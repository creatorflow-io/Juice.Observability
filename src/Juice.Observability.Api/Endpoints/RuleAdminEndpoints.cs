using System.Text.Json;
using System.Text.Json.Serialization;
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
            if (await db.Rules.AnyAsync(r => r.TenantId == tenantId && r.Key == definition!.Key, ct))
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Rule exists", $"A rule with the key '{definition!.Key}' exists.");
            }
            if (await db.Rules.CountAsync(r => r.TenantId == tenantId, ct) >= MaxRules)
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

        private static async Task<IResult> UpdateAsync(Guid tenantId, string key, JsonElement body, HttpContext http, PlatformDbContext db, TimeProvider clock,
            IEnumerable<IAlertNotifier> notifiers, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "rule.update", "rule");
            if (await mutation.RequireAsync(key, ct, Roles.Admin) is { } denied) return denied;
            var rule = await db.Rules.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key, ct);
            if (rule is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Rule not found", $"No rule '{key}'.");
            }
            if (rule.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(key, File, ct);
            if (http.Request.Headers.IfMatch.ToString() is { Length: > 0 } match && match.Trim('"') != rule.DefinitionHash)
            {
                return Mutation.Problem(StatusCodes.Status412PreconditionFailed, "Rule changed", "Someone changed this rule since you read it. Read it again.");
            }
            var (definition, problem) = Read(body, key);
            if (problem is not null) return problem;

            var hash = definition!.Hash();
            if (hash == rule.DefinitionHash)
            {
                return Results.Ok(ToDto(rule));
            }
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var closed = 0;
            if (!definition.Enabled)
            {
                closed = await RuleEvaluator.CloseOpenAlertsAsync(db, rule, RuleProvisioner.ClosedRuleDisabled, notifiers, now, ct);
            }
            else if (RuleProvisioner.ChangesWhatItWatches(RuleDefinition.FromJson(rule.Definition), definition))
            {
                closed = await RuleEvaluator.CloseOpenAlertsAsync(db, rule, RuleProvisioner.ClosedRuleChanged, notifiers, now, ct);
            }
            var wasEnabled = rule.Enabled;
            rule.Name = definition.Name;
            rule.Template = definition.Template;
            rule.Severity = definition.Severity;
            rule.Definition = definition.ToJson();
            rule.DefinitionHash = hash;
            rule.Enabled = definition.Enabled;
            rule.LoadedAt = now;
            var verb = wasEnabled == rule.Enabled ? "update" : rule.Enabled ? "enable" : "disable";
            var audit = Metadata(rule);
            if (closed > 0) audit["alertsClosed"] = closed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            mutation.Audit(AuditOutcome.Succeeded, key, audit);
            mutation.RecordChange($"Rule {key}: {verb}", now, new() { [ChangeEvent.RuleAttribute] = key });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(rule));
        }

        private static Dictionary<string, string> Metadata(Rule rule) => new() { ["hash"] = rule.DefinitionHash, ["template"] = rule.Template.ToString(), ["enabled"] = rule.Enabled ? "true" : "false" };
    }
}
