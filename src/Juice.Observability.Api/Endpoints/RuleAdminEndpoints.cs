using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// Creating and changing rules (admin). Rules stay typed templates: the body is a <see cref="RuleDefinition"/>, validated as the deployment file's are, and no query
    /// can be supplied. A rule is disabled, never deleted, since alerts keep their rule. <c>If-Match</c> with the <c>etag</c> refuses a change made over someone else's.
    /// </summary>
    public static class RuleAdminEndpoints
    {
        public const int MaxRules = 200;
        private const string File = "the rules file";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public static RouteGroupBuilder MapRuleAdminEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapPost("/rules", CreateAsync);
            tenant.MapPut("/rules/{key}", UpdateAsync);
            return tenant;
        }

        internal static RuleDto ToDto(Rule r) => new(r.Key, r.Name, r.Template.ToString(), r.Severity.ToString(), r.Enabled, RuleDefinition.FromJson(r.Definition), r.LoadedAt, r.ManagedBy, r.DefinitionHash);

        /// <summary>The definition from the body; a missing template or threshold is an error, since their defaults would fire on everything.</summary>
        private static (RuleDefinition? Definition, IResult? Problem) Read(JsonElement body, string? key)
        {
            try
            {
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("template", out _) || !body.TryGetProperty("threshold", out _))
                {
                    return (null, Mutation.Invalid(["template and threshold are required."]));
                }
                var definition = body.Deserialize<RuleDefinition>(Json)!;
                if (key is not null)
                {
                    if (definition.Key.Length > 0 && definition.Key != key)
                    {
                        return (null, Mutation.Invalid(["A rule's key can't change."]));
                    }
                    definition.Key = key;
                }
                var errors = definition.Validate().ToList();
                return errors.Count > 0 ? (null, Mutation.Invalid(errors)) : (definition, null);
            }
            catch (JsonException e)
            {
                return (null, Mutation.Invalid([$"Unreadable definition at {e.Path}."]));
            }
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, JsonElement body, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "rule.create", "rule");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            var (definition, problem) = Read(body, null);
            if (problem is not null) return problem;
            if (RuleDefinition.IsReservedKey(definition!.Key)) return Mutation.Invalid([RuleDefinition.ReservedKeyError(definition.Key)]);
            if (await db.Rules.AnyAsync(r => r.TenantId == tenantId && r.Key == definition!.Key, ct))
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Rule exists", $"A rule with the key '{definition!.Key}' exists.");
            }
            // The platform's burn rules run no query, so they don't count against the cap.
            if (await db.Rules.CountAsync(r => r.TenantId == tenantId && r.ManagedBy != ManagedBy.Platform, ct) >= MaxRules)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Too many rules", $"A tenant has at most {MaxRules} rules: each one runs queries on its schedule.");
            }
            var now = clock.GetUtcNow();
            var rule = new Rule
            {
                TenantId = tenantId, Key = definition!.Key, Name = definition.Name, Template = definition.Template, Severity = definition.Severity,
                Definition = definition.ToJson(), DefinitionHash = definition.Hash(), Enabled = definition.Enabled, LoadedAt = now, ManagedBy = ManagedBy.Api,
            };
            db.Rules.Add(rule);
            mutation.Audit(AuditOutcome.Succeeded, rule.Key, Metadata(rule));
            mutation.RecordChange($"Rule {rule.Key}: create", now, new() { [ChangeEvent.RuleAttribute] = rule.Key });
            await db.SaveChangesAsync(ct);
            return Results.Created($"{http.Request.Path}/{rule.Key}", ToDto(rule));
        }

        private static async Task<IResult> UpdateAsync(Guid tenantId, string key, JsonElement body, HttpContext http, PlatformDbContext db, IMediator mediator,
            CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "rule.update", "rule");
            if (await mutation.RequireAsync(key, ct, Roles.Admin) is { } denied) return denied;
            var rule = await db.Rules.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key, ct);
            if (rule is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Rule not found", $"No rule '{key}'.");
            }
            if (rule.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(key, File, ct);
            if (rule.ManagedBy == ManagedBy.Platform)
            {
                return await mutation.ManagedByPlatformAsync(key, $"'{key}' raises SLO burn alerts and changes with the SLOs. Route or silence its alerts instead.", ct);
            }
            if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } match && match.Trim('"') != rule.DefinitionHash)
            {
                return Mutation.Problem(StatusCodes.Status412PreconditionFailed, "Rule changed", "Someone changed this rule since you read it. Read it again.");
            }
            var (definition, problem) = Read(body, key);
            if (problem is not null) return problem;

            if (definition!.Hash() == rule.DefinitionHash)
            {
                return Results.Ok(ToDto(rule));
            }
            using var context = PlatformMessageContext.Begin("api", CorrelationId.Get(http));
            return Results.Ok(await mediator.Send(new ChangeRule(tenantId, rule.Id, definition, mutation.Actor, CorrelationId.Get(http)), ct));
        }

        internal static Dictionary<string, string> Metadata(Rule rule) => new() { ["hash"] = rule.DefinitionHash, ["template"] = rule.Template.ToString(), ["enabled"] = rule.Enabled ? "true" : "false" };
    }

    /// <summary>Change an API-owned rule, already checked by the endpoint (role, owner, <c>If-Match</c>, a valid and different definition).</summary>
    internal sealed record ChangeRule(Guid PlatformTenantId, Guid RuleId, RuleDefinition Definition, string Actor, string? CorrelationId) : MessageBase, IRequest<RuleDto>;

    /// <summary>
    /// Changes the rule, resolves the open alerts it no longer watches, and writes the audit record and change event. Only changes entities: the
    /// TransactionBehavior saves them, and the notifications the resolved alerts raise, in one transaction.
    /// </summary>
    internal sealed class ChangeRuleHandler(PlatformDbContext db, TimeProvider clock) : IRequestHandler<ChangeRule, RuleDto>
    {
        public async ValueTask<RuleDto> Handle(ChangeRule request, CancellationToken cancellationToken)
        {
            var definition = request.Definition;
            var rule = await db.Rules.SingleAsync(r => r.TenantId == request.PlatformTenantId && r.Id == request.RuleId, cancellationToken);
            var now = clock.GetUtcNow();
            var closed = 0;
            if (!definition.Enabled)
            {
                closed = await EvaluateRuleHandler.CloseOpenAlertsAsync(db, rule, RuleProvisioner.ClosedRuleDisabled, now, cancellationToken);
            }
            else if (RuleProvisioner.ChangesWhatItWatches(RuleDefinition.FromJson(rule.Definition), definition))
            {
                closed = await EvaluateRuleHandler.CloseOpenAlertsAsync(db, rule, RuleProvisioner.ClosedRuleChanged, now, cancellationToken);
            }
            var wasEnabled = rule.Enabled;
            rule.Name = definition.Name;
            rule.Template = definition.Template;
            rule.Severity = definition.Severity;
            rule.Definition = definition.ToJson();
            rule.DefinitionHash = definition.Hash();
            rule.Enabled = definition.Enabled;
            rule.LoadedAt = now;
            var verb = wasEnabled == rule.Enabled ? "update" : rule.Enabled ? "enable" : "disable";
            var audit = RuleAdminEndpoints.Metadata(rule);
            if (closed > 0) audit["alertsClosed"] = closed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            db.AuditRecords.Add(new AuditRecord
            {
                TenantId = request.PlatformTenantId, Actor = request.Actor, Operation = "rule.update", TargetType = "rule", TargetId = rule.Key,
                Outcome = AuditOutcome.Succeeded, CorrelationId = request.CorrelationId, Metadata = audit,
            });
            db.ChangeEvents.Add(new ChangeEvent
            {
                TenantId = request.PlatformTenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = request.Actor,
                Summary = $"Rule {rule.Key}: {verb}", Attributes = new() { [ChangeEvent.RuleAttribute] = rule.Key }, OccurredAt = now, RecordedAt = now,
            });
            return RuleAdminEndpoints.ToDto(rule);
        }
    }
}
