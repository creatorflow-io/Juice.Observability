using Juice.Domain;
using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>
    /// The integration alert (decision 8). Each pass reconciles the instances with the open alerts of the <c>integration-health</c> rule: Failed instances fire,
    /// Degraded ones are observed, and an alert whose instance recovered, was disabled, changed or is gone resolves. Evidence is the core's: plugin, instance name,
    /// failure kind and failures in a row, never the plugin's detail.
    /// </summary>
    internal static class IntegrationAlerts
    {
        public const string AlertFired = "integration_failed";
        public const string Recovered = "recovered";
        public const string Disabled = "disabled";
        public const string Deleted = "deleted";
        public const string Reconfigured = "reconfigured";

        public sealed record Row(Guid Id, string Name, string PluginKey, IntegrationState State, string? LastFailure, int ConsecutiveFailures);

        /// <summary>The signals that bring the alerts in line with the instances. <paramref name="canFire"/> false (no rule of ours) leaves out the fires.</summary>
        public static List<AlertSignal> Reconcile(IReadOnlyList<Row> rows, IReadOnlyList<SourceAlert> open, DateTimeOffset now, bool canFire)
        {
            var signals = new List<AlertSignal>();
            var byDedup = rows.ToDictionary(r => IntegrationHealthRule.DedupKey(r.Id));
            var openDedup = open.Select(a => a.DedupKey).ToHashSet(StringComparer.Ordinal);
            foreach (var alert in open)
            {
                if (!byDedup.TryGetValue(alert.DedupKey, out var row))
                {
                    signals.Add(new(alert.RuleKey, alert.DedupKey, AlertSignalKind.Resolve, now, Deleted));
                    continue;
                }
                switch (row.State)
                {
                    case IntegrationState.Failed:
                    case IntegrationState.Degraded:
                        signals.Add(new(alert.RuleKey, alert.DedupKey, AlertSignalKind.Observe, now, AlertFired, Value: row.ConsecutiveFailures, Evidence: Evidence(row)));
                        break;
                    case IntegrationState.Enabled:
                        signals.Add(new(alert.RuleKey, alert.DedupKey, AlertSignalKind.Resolve, now, Recovered));
                        break;
                    case IntegrationState.Disabled:
                        signals.Add(new(alert.RuleKey, alert.DedupKey, AlertSignalKind.Resolve, now, Disabled));
                        break;
                    default:
                        signals.Add(new(alert.RuleKey, alert.DedupKey, AlertSignalKind.Resolve, now, Reconfigured));
                        break;
                }
            }
            if (canFire)
            {
                foreach (var row in rows.Where(r => r.State == IntegrationState.Failed && !openDedup.Contains(IntegrationHealthRule.DedupKey(r.Id))))
                {
                    signals.Add(new(IntegrationHealthRule.Key, IntegrationHealthRule.DedupKey(row.Id), AlertSignalKind.Fire, now, AlertFired,
                        new AlertSubject(AlertSubjectKind.Integration, IntegrationHealthRule.SubjectKey(row.Id)), row.ConsecutiveFailures, Evidence: Evidence(row)));
                }
            }
            return signals;
        }

        private static Dictionary<string, string> Evidence(Row row) => new()
        {
            ["plugin"] = row.PluginKey,
            ["instance"] = row.Name,
            ["failure"] = row.LastFailure ?? "",
            ["consecutiveFailures"] = row.ConsecutiveFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        public static RuleDefinition Definition() => new()
        {
            Key = IntegrationHealthRule.Key, Name = "Integration instance failing", Template = RuleTemplate.IntegrationHealth, Severity = AlertSeverity.Warning, Threshold = 1,
            RecoveryThreshold = 1, Window = TimeSpan.FromMinutes(5), Schedule = TimeSpan.FromSeconds(30), ForDuration = TimeSpan.Zero, RecoveryDuration = TimeSpan.Zero,
            Cooldown = TimeSpan.Zero, MinimumSamples = 1,
        };
    }

    /// <summary>Makes sure the platform's <c>integration-health</c> rule exists, the first time an alert must fire. True when it is the platform's; false when another owner holds the key.</summary>
    internal sealed record EnsureIntegrationRule : MessageBase, IRequest<bool>;

    internal sealed class EnsureIntegrationRuleHandler(PlatformDbContext db, IOptions<PlatformOptions> platform, TimeProvider clock) : IRequestHandler<EnsureIntegrationRule, bool>
    {
        public const string Actor = "platform";

        public async ValueTask<bool> Handle(EnsureIntegrationRule request, CancellationToken cancellationToken)
        {
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            var definition = IntegrationAlerts.Definition();
            var hash = definition.Hash();
            var now = clock.GetUtcNow();
            var rule = await db.Rules.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == IntegrationHealthRule.Key, cancellationToken);
            string operation;
            if (rule is null)
            {
                rule = new Rule
                {
                    TenantId = tenantId, Key = IntegrationHealthRule.Key, Name = definition.Name, Template = RuleTemplate.IntegrationHealth, Severity = definition.Severity,
                    Definition = definition.ToJson(), DefinitionHash = hash, Enabled = true, LoadedAt = now, ManagedBy = ManagedBy.Platform,
                };
                db.Rules.Add(rule);
                operation = "rule.create";
            }
            else if (rule.ManagedBy != ManagedBy.Platform)
            {
                return false;
            }
            else if (rule.DefinitionHash != hash || rule.Template != RuleTemplate.IntegrationHealth || !rule.Enabled)
            {
                rule.Template = RuleTemplate.IntegrationHealth;
                rule.Enabled = true;
                rule.Name = definition.Name;
                rule.Severity = definition.Severity;
                rule.Definition = definition.ToJson();
                rule.DefinitionHash = hash;
                rule.LoadedAt = now;
                operation = "rule.update";
            }
            else
            {
                return true;
            }
            db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = "rule", TargetId = rule.Key, Outcome = AuditOutcome.Succeeded,
                Metadata = new() { ["hash"] = hash, ["template"] = nameof(RuleTemplate.IntegrationHealth) },
            });
            db.ChangeEvents.Add(new ChangeEvent
            {
                TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = Actor,
                Summary = $"Rule {rule.Key}: {operation.Replace("rule.", "", StringComparison.Ordinal)}", Attributes = new() { [ChangeEvent.RuleAttribute] = rule.Key },
                OccurredAt = now, RecordedAt = now,
            });
            return true;
        }
    }
}
