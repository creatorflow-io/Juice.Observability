using System.Diagnostics.Metrics;
using System.Globalization;
using Juice.Domain;
using Juice.MediatR;
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
    /// The alert lifecycle's changes, for every source of alerts (rules, SLO burn rates): a transition in the history, counted; a resolve; and the
    /// domain event that notifies. Only changes entities: the command's TransactionBehavior saves them and dispatches the events.
    /// </summary>
    public static class AlertChanges
    {
        private static readonly Counter<long> Transitions = RuleEvaluator.Meter.CreateCounter<long>("juice.observability.alerts.transitions", "{transition}",
            "Alert state transitions, by rule template and new state");

        /// <summary>A transition that notifies: the alert raises it, and whoever notifies hears of it through the outbox once it commits.</summary>
        public static void Raise(Alert alert, AlertTransition transition)
            => alert.AddDomainEvent<INotification>(new AlertTransitioned(alert.TenantId, alert.Id, transition.Id, transition.ToState));

        /// <summary>Records a state change in the alert's history, and counts it (<c>juice.observability.alerts.transitions</c>).</summary>
        public static AlertTransition Transition(PlatformDbContext db, Alert alert, AlertState? from, AlertState to, DateTimeOffset at,
            double? value, string reason, RuleTemplate template)
        {
            var transition = new AlertTransition
            {
                TenantId = alert.TenantId, AlertId = alert.Id, FromState = from, ToState = to, At = at, Value = value, Reason = reason,
            };
            db.AlertTransitions.Add(transition);
            Transitions.Add(1, new KeyValuePair<string, object?>("template", template.ToString()),
                new KeyValuePair<string, object?>("state", to.ToString()));
            return transition;
        }

        /// <summary>Resolves an open alert for <paramref name="reason"/>, notifying if it had fired.</summary>
        public static AlertTransition Resolve(PlatformDbContext db, Alert alert, string reason, DateTimeOffset at, RuleTemplate template)
        {
            var transition = Transition(db, alert, alert.State, AlertState.Resolved, at, alert.LastValue, reason, template);
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = at;
            alert.RecoveringSince = null;
            if (alert.FiredAt is not null)
            {
                Raise(alert, transition);
            }
            return transition;
        }
    }

    /// <summary>Evaluate one rule now. Its alert changes, and the notifications they raise, commit together through the TransactionBehavior.</summary>
    public sealed record EvaluateRule(Rule Rule) : MessageBase, IRequest<RuleEvaluation>;

    /// <summary>
    /// Runs a rule's evaluation as a command (<see cref="EvaluateRule"/>), so the mediator's TransactionBehavior saves the alerts, the domain events they raised
    /// and the outbox in one transaction. The scheduler and tests call this.
    /// </summary>
    public sealed class RuleEvaluator(IServiceScopeFactory scopes, ILogger<RuleEvaluator> logger)
    {
        internal static readonly Meter Meter = new("Juice.Observability");

        public async Task<RuleEvaluation> EvaluateAsync(Rule rule, CancellationToken cancellationToken)
        {
            using var context = PlatformMessageContext.Begin("rules");
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new EvaluateRule(rule), cancellationToken);
            if (result.Opened + result.Fired + result.Resolved > 0)
            {
                logger.LogInformation("Rule {Rule}: {Opened} opened, {Fired} fired, {Resolved} resolved", rule.Key, result.Opened, result.Fired, result.Resolved);
            }
            return result;
        }
    }

    /// <summary>
    /// Evaluates one rule: observes its template over the window ending <see cref="RulesOptions.Lag"/> before now,
    /// applies <see cref="AlertLifecycle"/> to every observed subject and every open alert (an open alert whose subject
    /// returned nothing is evaluated as no data), and changes alerts and their transitions. A transition that notifies raises
    /// <see cref="AlertTransitioned"/>; nothing here saves, begins or commits, or knows who is notified.
    /// </summary>
    public sealed class EvaluateRuleHandler(PlatformDbContext db, RuleTemplates templates, IOptions<RulesOptions> options,
        IOptions<PlatformOptions> platform, TimeProvider clock) : IRequestHandler<EvaluateRule, RuleEvaluation>
    {
        public async ValueTask<RuleEvaluation> Handle(EvaluateRule request, CancellationToken cancellationToken)
        {
            var rule = request.Rule;
            var definition = RuleDefinition.FromJson(rule.Definition);
            var now = clock.GetUtcNow();
            var at = new DateTimeOffset((now - options.Value.Lag).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            var from = at - definition.Window;
            var observations = (await templates.ObserveAsync(definition,
                new TelemetryWindow(platform.Value.TenantKey, from, at, options.Value.RowLimit), cancellationToken))
                .GroupBy(o => o.SubjectKey).ToDictionary(g => g.Key, g => g.First());

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
                    var transition = Transition(db, alert, alert.State, to, at, observation?.Value, decision.Reason, rule.Template);
                    alert.State = to;
                    if (to == AlertState.Firing) { alert.FiredAt = at; fired++; }
                    if (to == AlertState.Resolved) { alert.ResolvedAt = at; resolved++; }
                    if (to == AlertState.Firing || (to == AlertState.Resolved && alert.FiredAt is not null))
                    {
                        alert.LastValue = observation?.Value;
                        Raise(alert, transition);
                    }
                }
                alert.RecoveringSince = decision.RecoveringSince;
                alert.LastEvaluatedAt = now;
                alert.LastValue = observation?.Value;
                alert.LastSamples = observation?.Samples ?? 0;
                alert.NoData = condition == Condition.NoData;
                alert.Evidence = Evidence(definition, from, at, observation, condition);
            }
            return new RuleEvaluation(rule.Key, at, keys.Count, opened, fired, resolved);
        }

        public static string DedupKey(string ruleKey, string subjectKey) => $"{ruleKey}|{subjectKey}";

        private static void Raise(Alert alert, AlertTransition transition) => AlertChanges.Raise(alert, transition);

        private static AlertTransition Transition(PlatformDbContext db, Alert alert, AlertState? from, AlertState to, DateTimeOffset at,
            double? value, string reason, RuleTemplate template) => AlertChanges.Transition(db, alert, from, to, at, value, reason, template);

        /// <summary>
        /// Resolves a rule's open alerts because the rule no longer watches them: disabled, or its scope changed. Left alone they would be held as "no data"
        /// forever, firing and escalating with nothing able to end them. <paramref name="reason"/> says this isn't a measured recovery. Only changes them:
        /// it runs inside a command, whose TransactionBehavior saves them with the caller's change.
        /// </summary>
        public static async Task<int> CloseOpenAlertsAsync(PlatformDbContext db, Rule rule, string reason, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var open = await db.Alerts.Where(a => a.TenantId == rule.TenantId && a.RuleId == rule.Id && a.State != AlertState.Resolved).ToListAsync(cancellationToken);
            foreach (var alert in open)
            {
                AlertChanges.Resolve(db, alert, reason, now, rule.Template);
            }
            return open.Count;
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
