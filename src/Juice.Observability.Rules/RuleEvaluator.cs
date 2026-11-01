using System.Diagnostics.Metrics;
using System.Globalization;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Rules
{
    public sealed record RuleEvaluation(string RuleKey, DateTimeOffset At, int Subjects, int Opened, int Fired, int Resolved);

    /// <summary>
    /// Evaluates one rule: observes its template over the window ending <see cref="RulesOptions.Lag"/> before now,
    /// applies <see cref="AlertLifecycle"/> to every observed subject and every open alert (an open alert whose subject
    /// returned nothing is evaluated as no data), and writes alerts and their transitions in one transaction.
    /// </summary>
    public sealed class RuleEvaluator(IServiceScopeFactory scopes, RuleTemplates templates, IOptions<RulesOptions> options,
        IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<RuleEvaluator> logger)
    {
        internal static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("juice.observability.alerts.transitions", "{transition}",
            "Alert state transitions, by rule template and new state");

        public async Task<RuleEvaluation> EvaluateAsync(Rule rule, CancellationToken cancellationToken)
        {
            var definition = RuleDefinition.FromJson(rule.Definition);
            var now = clock.GetUtcNow();
            var at = new DateTimeOffset((now - options.Value.Lag).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            var from = at - definition.Window;
            var observations = (await templates.ObserveAsync(definition,
                new TelemetryWindow(platform.Value.TenantKey, from, at, options.Value.RowLimit), cancellationToken))
                .GroupBy(o => o.SubjectKey).ToDictionary(g => g.Key, g => g.First());

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var open = await db.Alerts.Where(a => a.TenantId == rule.TenantId && a.RuleId == rule.Id && a.State != AlertState.Resolved)
                .ToDictionaryAsync(a => a.SubjectKey, cancellationToken);
            var keys = observations.Keys.Union(open.Keys).ToList();
            var dedupKeys = keys.Select(k => DedupKey(rule.Key, k)).ToList();
            // Cooldown: when the last alert that fired for each subject resolved.
            var lastFiredResolved = await db.Alerts.AsNoTracking()
                .Where(a => a.TenantId == rule.TenantId && dedupKeys.Contains(a.DedupKey) && a.State == AlertState.Resolved && a.FiredAt != null)
                .GroupBy(a => a.DedupKey).Select(g => new { g.Key, ResolvedAt = g.Max(a => a.ResolvedAt) })
                .ToDictionaryAsync(x => x.Key, x => x.ResolvedAt, cancellationToken);
            var subjects = await SubjectsAsync(db, rule.TenantId, observations.Values, cancellationToken);

            int opened = 0, fired = 0, resolved = 0;
            foreach (var key in keys)
            {
                observations.TryGetValue(key, out var observation);
                open.TryGetValue(key, out var alert);
                var condition = observation is null ? Condition.NoData : AlertLifecycle.Classify(definition, observation.Value, observation.Samples);
                var cooldownUntil = lastFiredResolved.GetValueOrDefault(DedupKey(rule.Key, key)) is { } resolvedAt ? resolvedAt + definition.Cooldown : (DateTimeOffset?)null;
                var decision = AlertLifecycle.Next(definition, alert?.State, alert?.BreachStartedAt ?? at, alert?.RecoveringSince, condition, at, cooldownUntil);

                if (alert is null)
                {
                    if (decision.To != AlertState.Pending)
                    {
                        continue;
                    }
                    var (subjectId, environmentId) = subjects.GetValueOrDefault(key);
                    alert = new Alert
                    {
                        TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = DedupKey(rule.Key, key), SubjectKey = key,
                        SubjectType = observation!.SubjectType, SubjectId = subjectId, EnvironmentId = environmentId,
                        Severity = rule.Severity, State = AlertState.Pending, BreachStartedAt = at,
                    };
                    db.Alerts.Add(alert);
                    Transition(db, alert, null, AlertState.Pending, at, observation.Value, decision.Reason, rule.Template);
                    opened++;
                    // A rule with no ForDuration fires on the first breach.
                    decision = AlertLifecycle.Next(definition, AlertState.Pending, at, null, condition, at, cooldownUntil);
                }
                if (decision.To is { } to && to != alert.State)
                {
                    Transition(db, alert, alert.State, to, at, observation?.Value, decision.Reason, rule.Template);
                    alert.State = to;
                    if (to == AlertState.Firing) { alert.FiredAt = at; fired++; }
                    if (to == AlertState.Resolved) { alert.ResolvedAt = at; resolved++; }
                }
                alert.RecoveringSince = decision.RecoveringSince;
                alert.LastEvaluatedAt = now;
                alert.LastValue = observation?.Value;
                alert.LastSamples = observation?.Samples ?? 0;
                alert.NoData = condition == Condition.NoData;
                alert.Evidence = Evidence(definition, from, at, observation, condition);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (opened + fired + resolved > 0)
            {
                logger.LogInformation("Rule {Rule}: {Opened} opened, {Fired} fired, {Resolved} resolved", rule.Key, opened, fired, resolved);
            }
            return new RuleEvaluation(rule.Key, at, keys.Count, opened, fired, resolved);
        }

        public static string DedupKey(string ruleKey, string subjectKey) => $"{ruleKey}|{subjectKey}";

        private static void Transition(PlatformDbContext db, Alert alert, AlertState? from, AlertState to, DateTimeOffset at, double? value,
            string reason, RuleTemplate template)
        {
            db.AlertTransitions.Add(new AlertTransition
            {
                TenantId = alert.TenantId, AlertId = alert.Id, FromState = from, ToState = to, At = at, Value = value, Reason = reason,
            });
            Transitions.Add(1, new KeyValuePair<string, object?>("template", template.ToString()),
                new KeyValuePair<string, object?>("state", to.ToString()));
        }

        /// <summary>The evidence reference (requirements §8): enough to repeat the query and see the same numbers.</summary>
        private static Dictionary<string, string> Evidence(RuleDefinition rule, DateTimeOffset from, DateTimeOffset to,
            RuleObservation? observation, Condition condition)
        {
            var evidence = new Dictionary<string, string>
            {
                ["template"] = rule.Template.ToString(),
                ["from"] = from.ToString("O"),
                ["to"] = to.ToString("O"),
                ["threshold"] = rule.Threshold.ToString(CultureInfo.InvariantCulture),
                ["recoveryThreshold"] = rule.Recovery.ToString(CultureInfo.InvariantCulture),
                ["condition"] = condition.ToString(),
                ["samples"] = (observation?.Samples ?? 0).ToString(CultureInfo.InvariantCulture),
            };
            if (observation?.Value is { } value) evidence["value"] = value.ToString("G6", CultureInfo.InvariantCulture);
            return evidence;
        }

        /// <summary>The platform ids of the observed subjects, when discovery knows them.</summary>
        private static async Task<Dictionary<string, (Guid?, Guid?)>> SubjectsAsync(PlatformDbContext db, Guid tenantId,
            IEnumerable<RuleObservation> observations, CancellationToken cancellationToken)
        {
            var list = observations.ToList();
            var names = list.Where(o => o.SubjectType == HealthSubject.Service).Select(o => o.ServiceName).Distinct().ToList();
            var hosts = list.Where(o => o.SubjectType == HealthSubject.Host).Select(o => o.Host).Distinct().ToList();
            var services = await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && names.Contains(s.Name)).ToListAsync(cancellationToken);
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Key, e => e.Id, cancellationToken);
            var hostIds = await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && r.Type == ResourceType.Host && hosts.Contains(r.Key))
                .ToDictionaryAsync(r => r.Key, r => r.Id, cancellationToken);
            var result = new Dictionary<string, (Guid?, Guid?)>();
            foreach (var o in list)
            {
                result[o.SubjectKey] = o.SubjectType == HealthSubject.Host
                    ? (hostIds.TryGetValue(o.Host, out var h) ? h : null, null)
                    : (services.FirstOrDefault(s => s.Namespace == o.ServiceNamespace && s.Name == o.ServiceName)?.Id,
                       environments.TryGetValue(o.Environment, out var e) ? e : null);
            }
            return result;
        }
    }
}
