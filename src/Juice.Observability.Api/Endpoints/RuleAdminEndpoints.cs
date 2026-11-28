using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Juice.Observability.Signals;
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
            tenant.MapRulePreviewEndpoints();
            return tenant;
        }

        /// <summary>A rule's 400: the same <c>detail</c> as ever, and <c>errors</c>, one message each, for the Console to place by field.</summary>
        internal static IResult Invalid(IEnumerable<string> errors)
        {
            var list = errors.ToArray();
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: string.Join(" ", list),
                extensions: new Dictionary<string, object?> { ["errors"] = list });
        }

        internal static RuleDto ToDto(Rule r) => new(r.Key, r.Name, r.Template.ToString(), r.Severity.ToString(), r.Enabled, RuleDefinition.FromJson(r.Definition), r.LoadedAt, r.ManagedBy, r.DefinitionHash);

        /// <summary>The definition from the body; a missing template or threshold is an error, since their defaults would fire on everything.</summary>
        /// <summary>
        /// The definition from the body, and for a MetricThreshold rule its metric's kind from the route tenant's catalog (the last 24 h). A kind that
        /// conflicts with the catalog is refused; a metric not yet reported needs its kind and is accepted (<c>Unseen</c> says so, for the response's Quiet).
        /// When the catalog can't be read, a rule that gives its kind is accepted as it is, and one that doesn't is a 503.
        /// </summary>
        /// <param name="body">The create or update body.</param>
        /// <param name="key">The route's key (an update), or null.</param>
        /// <param name="http">The request, for the route tenant's key.</param>
        /// <param name="clock">The time the catalog's 24 h ends at.</param>
        /// <param name="ct">The request's cancellation.</param>
        /// <param name="preview">
        /// For the preview of a draft: an empty key or name is filled with <c>preview</c> before validation, and Enabled is taken as true (a preview is of
        /// the rule at work). The metric's catalog entry is returned too, for its unit.
        /// </param>
        internal static async Task<(RuleDefinition? Definition, IResult? Problem, string? Unseen, MetricCatalogEntry? Entry)> ReadAsync(JsonElement body, string? key,
            HttpContext http, TimeProvider clock, CancellationToken ct, bool preview = false)
        {
            try
            {
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("template", out _) || !body.TryGetProperty("threshold", out _))
                {
                    return (null, Invalid(["template and threshold are required."]), null, null);
                }
                var definition = body.Deserialize<RuleDefinition>(Json)!;
                if (key is not null)
                {
                    if (definition.Key.Length > 0 && definition.Key != key)
                    {
                        return (null, Invalid(["A rule's key can't change."]), null, null);
                    }
                    definition.Key = key;
                }
                if (preview)
                {
                    if (string.IsNullOrEmpty(definition.Key)) definition.Key = "preview";
                    if (string.IsNullOrEmpty(definition.Name)) definition.Name = "preview";
                    definition.Enabled = true;
                }
                string? unseen = null;
                MetricCatalogEntry? catalog = null;
                if (definition is { Template: RuleTemplate.MetricThreshold, Metric: { } metric } && !string.IsNullOrEmpty(metric.Name)
                    && System.Text.RegularExpressions.Regex.IsMatch(metric.Name, MetricSpec.NamePattern) && !metric.Name.EndsWith('\n'))
                {
                    var tenantKey = (string)http.Items[TelemetryEndpoints.TenantKeyItem]!;
                    try
                    {
                        var entry = await MetricCatalog.FindAsync(http.RequestServices.GetRequiredService<Discovery.ClickHouseTelemetrySource>(), tenantKey,
                            metric.Name, clock.GetUtcNow(), ct,
                            // A preview reads back to the first point's window: 24 h and the rule's window (validated to 6 h at most, so clamped here).
                            preview ? MetricCatalog.Lookback + TimeSpan.FromTicks(Math.Clamp(definition.Window.Ticks, 0, TimeSpan.FromHours(6).Ticks)) : null);
                        if (entry is null)
                        {
                            if (metric.Kind is not null) unseen = $"metric {metric.Name} has not been reported in the last 24 h";
                        }
                        else if (metric.Kind is { } given && given != entry.Kind)
                        {
                            return (null, Invalid([$"rule '{definition.Key}': Metric.Kind {given} conflicts with the catalog: {metric.Name} is a {entry.Kind}."]), null, null);
                        }
                        else
                        {
                            metric.Kind = entry.Kind;
                            catalog = entry;
                        }
                    }
                    catch (Exception e) when (e is Discovery.TelemetryBusyException or Discovery.TelemetryUnavailableException && metric.Kind is not null)
                    {
                        // Accepted as given: the kind is checked against the aggregation, and the rule observes nothing if it is wrong.
                    }
                }
                var errors = definition.Validate(http.RequestServices.GetRequiredService<SignalPacks>()).ToList();
                return errors.Count > 0 ? (null, Invalid(errors), null, null) : (definition, null, unseen, catalog);
            }
            catch (JsonException e)
            {
                return (null, Invalid([$"Unreadable definition at {e.Path}."]), null, null);
            }
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, JsonElement body, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "rule.create", "rule");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            var (definition, problem, unseen, _) = await ReadAsync(body, null, http, clock, ct);
            if (problem is not null) return problem;
            if (await QuietAsync(db, tenantId, definition!, ct) is { } quiet) return quiet;
            if (RuleDefinition.IsReservedKey(definition!.Key)) return Invalid([RuleDefinition.ReservedKeyError(definition.Key)]);
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
            return Results.Created($"{http.Request.Path}/{rule.Key}", ToDto(rule) with { Quiet = unseen });
        }

        private static async Task<IResult> UpdateAsync(Guid tenantId, string key, JsonElement body, HttpContext http, PlatformDbContext db, IMediator mediator,
            TimeProvider clock, CancellationToken ct)
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
            var (definition, problem, unseen, _) = await ReadAsync(body, key, http, clock, ct);
            if (problem is not null) return problem;
            if (await QuietAsync(db, tenantId, definition!, ct) is { } quiet) return quiet;

            if (definition!.Hash() == rule.DefinitionHash)
            {
                return Results.Ok(ToDto(rule) with { Quiet = unseen });
            }
            using var context = PlatformMessageContext.Begin("api", CorrelationId.Get(http));
            return Results.Ok((await mediator.Send(new ChangeRule(tenantId, rule.Id, definition, mutation.Actor, CorrelationId.Get(http)), ct)) with { Quiet = unseen });
        }

        /// <summary>An enabled rule that could never observe its service is refused (<see cref="SignalCoverage"/>); a disabled one isn't watching anything.</summary>
        private static async Task<IResult?> QuietAsync(PlatformDbContext db, Guid tenantId, RuleDefinition definition, CancellationToken ct)
            => definition.Enabled && await SignalCoverage.QuietReasonAsync(db, tenantId, definition, ct) is { } reason
                ? Invalid([$"rule '{definition.Key}': {reason}"]) : null;

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
