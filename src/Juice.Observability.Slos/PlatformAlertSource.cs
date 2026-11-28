using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Slos
{
    /// <summary>
    /// The core's <see cref="IAlertSource"/> for one template: it opens, updates and resolves alerts of the platform-managed rules of that template, as the
    /// rule evaluator does for its own, so they notify, escalate, are silenced and reach incidents the same way (W-23). The tenant is the deployment's, never
    /// the caller's. Lives with the SLO burn alerts, its first user, so that burn evaluation needs nothing registered to run (the tests' bare containers).
    /// <para>
    /// <see cref="ApplyAsync"/> joins a transaction already open on the context, which is how a command's handler calls it: it changes entities, and the
    /// command's TransactionBehavior saves, dispatches and commits. Outside one it sends <see cref="ApplyAlertSignals"/>, so the alerts, their transitions
    /// and the outbox row commit together. Either way it serializes per template with <c>pg_advisory_xact_lock('alert-source:&lt;template&gt;')</c>.
    /// </para>
    /// </summary>
    internal sealed class PlatformAlertSource(string template, PlatformDbContext db, IServiceProvider services, IOptions<PlatformOptions> platform, TimeProvider clock) : IAlertSource
    {
        public string Template { get; } = template;

        private async Task<Guid> TenantAsync(CancellationToken ct)
            => await db.Tenants.AsNoTracking().Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(ct);

        /// <summary>The platform-managed rules of this template. None when the template is not one a rule can name yet (a module's own, until stored).</summary>
        private IQueryable<Rule> Rules(Guid tenantId)
        {
            var rules = db.Rules.Where(r => r.TenantId == tenantId && r.ManagedBy == ManagedBy.Platform);
            return Enum.TryParse<RuleTemplate>(Template, out var known) ? rules.Where(r => r.Template == known) : rules.Where(_ => false);
        }

        public async Task<IReadOnlyList<SourceAlert>> OpenAlertsAsync(CancellationToken ct)
        {
            var tenantId = await TenantAsync(ct);
            var open = await (from a in db.Alerts.AsNoTracking()
                              join r in Rules(tenantId).AsNoTracking() on a.RuleId equals r.Id
                              where a.TenantId == tenantId && a.State != AlertState.Resolved
                              select new { a.Id, RuleKey = r.Key, a.DedupKey, a.SubjectKey, a.State, a.FiredAt, a.LastValue }).ToListAsync(ct);
            return [.. open.Select(a => new SourceAlert(a.Id, a.RuleKey, a.DedupKey, a.SubjectKey, a.State.ToString(), a.FiredAt, a.LastValue))];
        }

        public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastResolvedAsync(IReadOnlyCollection<string> dedupKeys, string reason, CancellationToken ct)
        {
            var tenantId = await TenantAsync(ct);
            var keys = dedupKeys.ToList();
            // Fired and then resolved for the reason: only that kind of resolve says anything about the condition (a move or a retirement doesn't).
            // Only alerts of this template's platform-managed rules: another rule's history isn't the source's to read.
            var ruleIds = Rules(tenantId).AsNoTracking().Select(r => r.Id);
            var resolved = await db.AlertTransitions.AsNoTracking().Where(t => t.TenantId == tenantId && t.ToState == AlertState.Resolved && t.Reason == reason)
                .Join(db.Alerts.AsNoTracking().Where(a => keys.Contains(a.DedupKey) && a.FiredAt != null && ruleIds.Contains(a.RuleId)), t => t.AlertId, a => a.Id, (t, a) => new { a.DedupKey, t.At })
                .GroupBy(x => x.DedupKey).Select(g => new { g.Key, ResolvedAt = g.Max(x => x.At) }).ToListAsync(ct);
            return resolved.ToDictionary(x => x.Key, x => x.ResolvedAt);
        }

        public async Task<AlertSourceResult> ApplyAsync(IReadOnlyList<AlertSignal> signals, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(signals);
            if (signals.Count == 0)
            {
                return new(0, 0, 0);
            }
            // Inside a command: its TransactionBehavior saves, dispatches the domain events (AlertTransitioned) and commits after the handler, so join it.
            if (db.IsManaged)
            {
                return await ApplyInTransactionAsync(signals, ct);
            }
            // A transaction nobody manages would commit the alert without its domain events, and so without its notification: refused, never joined.
            if (db.HasActiveTransaction)
            {
                throw new InvalidOperationException("ApplyAsync runs inside a command, or outside any transaction; it was called in a transaction that no command manages.");
            }
            using var context = PlatformMessageContext.Begin("alert-source");
            return await services.GetRequiredService<IMediator>().Send(new ApplyAlertSignals(Template, signals), ct);
        }

        /// <summary>
        /// The signals in order, in the open transaction. Any foreign rule or tenant's id refuses the whole call before anything is changed
        /// (<see cref="ArgumentException"/>).
        /// </summary>
        internal async Task<AlertSourceResult> ApplyInTransactionAsync(IReadOnlyList<AlertSignal> signals, CancellationToken ct)
        {
            // A command's handler runs before its transaction begins (the behavior then joins one already open): the lock needs one, so it is
            // begun here, and given up if this fails, or the scope would keep a transaction nobody commits.
            var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            try
            {
                return await ApplyLockedAsync(signals, ct);
            }
            catch
            {
                if (own is not null)
                {
                    await own.DisposeAsync();
                }
                throw;
            }
        }

        private async Task<AlertSourceResult> ApplyLockedAsync(IReadOnlyList<AlertSignal> signals, CancellationToken ct)
        {
            var tenantId = await TenantAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WorkerLease.KeyOf($"alert-source:{Template}")})", ct);

            var ruleKeys = signals.Select(s => s.RuleKey).Distinct().ToList();
            var rules = await Rules(tenantId).AsNoTracking().Where(r => ruleKeys.Contains(r.Key)).ToDictionaryAsync(r => r.Key, ct);
            await ValidateAsync(tenantId, signals, rules, ct);

            var dedupKeys = signals.Select(s => s.DedupKey).Distinct().ToList();
            var open = await db.Alerts.Where(a => a.TenantId == tenantId && dedupKeys.Contains(a.DedupKey) && a.State != AlertState.Resolved).ToDictionaryAsync(a => a.DedupKey, ct);
            var now = clock.GetUtcNow();
            int fired = 0, resolved = 0, observed = 0;
            foreach (var signal in signals)
            {
                var rule = rules[signal.RuleKey];
                open.TryGetValue(signal.DedupKey, out var alert);
                if (alert is not null && alert.RuleId != rule.Id)
                {
                    // Validation read the same rows under the lock: only a concurrent change outside the source gets here.
                    throw new ArgumentException($"Dedup key '{signal.DedupKey}' belongs to an alert of another rule.", nameof(signals));
                }
                switch (signal.Kind)
                {
                    case AlertSignalKind.Fire:
                        if (alert is null)
                        {
                            var subject = signal.Subject!;
                            alert = new Alert
                            {
                                TenantId = tenantId, RuleId = rule.Id, DedupKey = signal.DedupKey, SubjectKey = subject.Key,
                                SubjectType = subject.Kind == AlertSubjectKind.Host ? HealthSubject.Host : HealthSubject.Service,
                                SubjectId = subject.SubjectId, EnvironmentId = subject.EnvironmentId, Severity = rule.Severity, State = AlertState.Pending,
                                BreachStartedAt = signal.At,
                            };
                            db.Alerts.Add(alert);
                            open[signal.DedupKey] = alert;
                            AlertChanges.Transition(db, alert, null, AlertState.Pending, signal.At, signal.Value, "breach", rule.Template);
                            // The source's own confirmation stands in for a rule's ForDuration: it fires at once.
                            var firing = AlertChanges.Transition(db, alert, AlertState.Pending, AlertState.Firing, signal.At, signal.Value, signal.Reason, rule.Template);
                            alert.State = AlertState.Firing;
                            alert.FiredAt = signal.At;
                            AlertChanges.Raise(alert, firing);
                            fired++;
                        }
                        Update(alert, signal, now);
                        break;
                    case AlertSignalKind.Resolve:
                        if (alert is not null)
                        {
                            AlertChanges.Resolve(db, alert, signal.Reason, signal.At, rule.Template);
                            open.Remove(signal.DedupKey);
                            resolved++;
                        }
                        break;
                    case AlertSignalKind.Observe:
                        if (alert is not null)
                        {
                            Update(alert, signal, now);
                            observed++;
                        }
                        break;
                }
            }
            return new(fired, resolved, observed);
        }

        /// <summary>What an evaluation leaves on an alert: when, the value (null keeps the last), the samples, whether there was data, and the evidence.</summary>
        private static void Update(Alert alert, AlertSignal signal, DateTimeOffset now)
        {
            alert.LastEvaluatedAt = now;
            alert.LastValue = signal.Value ?? alert.LastValue;
            alert.LastSamples = signal.Samples ?? alert.LastSamples;
            alert.NoData = signal.NoData;
            if (signal.Evidence is not null)
            {
                alert.Evidence = new Dictionary<string, string>(signal.Evidence);
            }
        }

        private async Task ValidateAsync(Guid tenantId, IReadOnlyList<AlertSignal> signals, IReadOnlyDictionary<string, Rule> rules, CancellationToken ct)
        {
            foreach (var signal in signals)
            {
                if (signal is null || !rules.ContainsKey(signal.RuleKey ?? ""))
                {
                    throw new ArgumentException($"Rule '{signal?.RuleKey}' is not a platform-managed rule of template '{Template}'.", nameof(signals));
                }
                if (string.IsNullOrEmpty(signal.DedupKey) || signal.DedupKey.Length > 600 || string.IsNullOrEmpty(signal.Reason) || signal.Reason.Length > 200)
                {
                    throw new ArgumentException("A signal needs a dedup key (up to 600 characters) and a reason (up to 200).", nameof(signals));
                }
                if (!Enum.IsDefined(signal.Kind))
                {
                    throw new ArgumentException($"Unknown signal kind {signal.Kind}.", nameof(signals));
                }
                if (signal.Kind == AlertSignalKind.Fire && (signal.Subject is not { Key.Length: > 0 and <= 512 } || !Enum.IsDefined(signal.Subject.Kind)))
                {
                    throw new ArgumentException("A fire signal needs a subject with a key of up to 512 characters.", nameof(signals));
                }
            }
            // Ids of another tenant, or of none: the tenant is the core's, and so is every row an alert names.
            var subjectIds = signals.Where(s => s.Subject?.SubjectId is not null).Select(s => (s.Subject!.Kind, Id: s.Subject.SubjectId!.Value)).Distinct().ToList();
            var serviceIds = subjectIds.Where(s => s.Kind == AlertSubjectKind.Service).Select(s => s.Id).ToList();
            var hostIds = subjectIds.Where(s => s.Kind == AlertSubjectKind.Host).Select(s => s.Id).ToList();
            var environmentIds = signals.Where(s => s.Subject?.EnvironmentId is not null).Select(s => s.Subject!.EnvironmentId!.Value).Distinct().ToList();
            var foundServices = serviceIds.Count == 0 ? [] : await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && serviceIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
            var foundHosts = hostIds.Count == 0 ? [] : await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && r.Type == ResourceType.Host && hostIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
            var foundEnvironments = environmentIds.Count == 0 ? [] : await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId && environmentIds.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
            if (serviceIds.Except(foundServices).Any() || hostIds.Except(foundHosts).Any() || environmentIds.Except(foundEnvironments).Any())
            {
                throw new ArgumentException("A subject or environment id is not one of the platform tenant's.", nameof(signals));
            }
            // A dedup key held by an open alert of another rule is not this signal's to touch.
            var dedupKeys = signals.Select(s => s.DedupKey).Distinct().ToList();
            var ruleIds = rules.Values.ToDictionary(r => r.Key, r => r.Id);
            var held = await db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId && dedupKeys.Contains(a.DedupKey) && a.State != AlertState.Resolved)
                .Select(a => new { a.DedupKey, a.RuleId }).ToListAsync(ct);
            foreach (var signal in signals)
            {
                if (held.Any(h => h.DedupKey == signal.DedupKey && h.RuleId != ruleIds[signal.RuleKey]))
                {
                    throw new ArgumentException($"Dedup key '{signal.DedupKey}' belongs to an alert of another rule.", nameof(signals));
                }
            }
        }
    }

    /// <summary>The signals of an alert source applied in a transaction of their own: the alerts, their transitions and the outbox commit together.</summary>
    internal sealed record ApplyAlertSignals(string Template, IReadOnlyList<AlertSignal> Signals) : MessageBase, IRequest<AlertSourceResult>;

    internal sealed class ApplyAlertSignalsHandler(PlatformDbContext db, IServiceProvider services, IOptions<PlatformOptions> platform, TimeProvider clock)
        : IRequestHandler<ApplyAlertSignals, AlertSourceResult>
    {
        public async ValueTask<AlertSourceResult> Handle(ApplyAlertSignals request, CancellationToken cancellationToken)
            => await new PlatformAlertSource(request.Template, db, services, platform, clock).ApplyInTransactionAsync(request.Signals, cancellationToken);
    }
}
