using System.Diagnostics.Metrics;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LegacyStatus = Juice.Observability.Data.DeliveryStatus;

namespace Juice.Observability.Notifications.Shadow
{
    /// <summary>Configuration section <c>Notifications:Shadow</c>: when the comparer looks, and what it allows.</summary>
    public sealed class ShadowOptions
    {
        public const string Section = "Notifications:Shadow";

        /// <summary>How long after a transition (or a step's first delivery) both pipelines have to settle before it is compared.</summary>
        public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(15);
        /// <summary>How long it waits for a side still sending (a retry, a silence) before comparing anyway, as <c>unsettled</c>.</summary>
        public TimeSpan MaxWait { get; set; } = TimeSpan.FromHours(2);
        /// <summary>How far apart the two pipelines' send times may be.</summary>
        public TimeSpan TimingTolerance { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
        public int Batch { get; set; } = 100;
        /// <summary>
        /// How far back a pass looks for units not yet compared, so each pass reads a bounded window. A unit older than this was never compared
        /// (the comparer was down that long); the dead-man's shadow cause says so while it is within it.
        /// </summary>
        public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(2);
        /// <summary>How far back the dead-man looks for a difference no rule explains (M49's shadow cause).</summary>
        public TimeSpan AlarmWindow { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// The shadow run's comparer (phase-5-plan I67, waves §6.1): a leased worker that, once both pipelines have settled on a transition (a grace
    /// period), matches what legacy sent (its <c>webhook_deliveries</c>, by endpoint) against the new pipeline's dry-run deliveries of the
    /// transition's request (<c>transition:{id}</c>, by the integration of the endpoint's key), and each escalation step by its number. Each unit
    /// is compared once: a <see cref="ShadowComparison"/>, its <see cref="ShadowDifference"/>s, and the metric
    /// <c>juice.observability.notifications.shadow.differences</c>. Runs in <c>shadow</c> only.
    /// </summary>
    public sealed class ShadowComparer(IServiceScopeFactory scopes, IOptions<ShadowOptions> options, IOptions<WebhookOptions> webhooks, TimeProvider clock,
        ILogger<ShadowComparer> logger) : BackgroundService
    {
        public const string Worker = "notifications-shadow";

        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Differences = Meter.CreateCounter<long>("juice.observability.notifications.shadow.differences", "{difference}",
            "How the new pipeline's dry run differed from what legacy sent, by kind and rule (unexplained: none names it)");
        private static readonly Counter<long> Compared = Meter.CreateCounter<long>("juice.observability.notifications.shadow.compared", "{unit}",
            "Transitions and escalation steps compared between the pipelines");

        /// <summary>How long before the first request a transition may have been planned and still be part of the shadow run.</summary>
        private static readonly TimeSpan PlanSlack = TimeSpan.FromMinutes(1);

        /// <summary>The rule tag of a difference no rule explains.</summary>
        public const string Unexplained = "unexplained";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PlatformCaller.Set(Worker, stoppingToken);
            WorkerPulse.Register(Worker, options.Value.Interval, stopping: stoppingToken);
            await using var lease = WorkerLease.For(scopes, Worker, logger);
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        using var pass = WorkerPulse.Begin(Worker, stoppingToken);
                        // Until nothing more is recorded: unsettled units wait for their next look, so this never spins on them.
                        while (await CompareOnceAsync(stoppingToken) > 0)
                        {
                        }
                        pass.Succeeded();
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Shadow comparison pass failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>
        /// Units still sending on a side when last looked at, and when to look again (review of #12): a pass skips them until then, so it never
        /// spins on the same unsettled batch. Lost on a restart (or a lease handed over), when they are simply looked at again.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Transition, int Step), DateTimeOffset> _deferred = new();

        /// <summary>When the shadow run began (the pipeline's first request), once known: it never moves back.</summary>
        private DateTimeOffset? _first;

        /// <summary>The embedded application's id in the pipeline, once known: its requests' keys are unique per (tenant, application).</summary>
        private Guid? _application;

        /// <summary>Compares up to one batch of units that are due; returns how many it recorded (0 when nothing more is due now).</summary>
        public async Task<int> CompareOnceAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var embedded = scope.ServiceProvider.GetRequiredService<NotificationsEmbeddedOptions>();
            var tenant = embedded.TenantId;
            var settings = options.Value;
            var now = clock.GetUtcNow();
            var due = now - settings.Grace;
            _application ??= await pipeline.Applications.AsNoTracking().Where(a => a.Key == embedded.ApplicationKey).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken);
            if (_application is not { } application)
            {
                return 0;
            }
            // The shadow run began with the new pipeline's first request: nothing before it was ever asked of the new side.
            _first ??= await pipeline.Requests.AsNoTracking().Where(r => r.TenantId == tenant && r.ApplicationId == application)
                .MinAsync(r => (DateTimeOffset?)r.AcceptedAt, cancellationToken);
            if (_first is not { } first)
            {
                return 0;
            }
            // Within the lookback: each pass reads a bounded window, however long the run has gone on.
            var since = first > now - settings.Lookback ? first : now - settings.Lookback;
            foreach (var key in _deferred.Where(d => d.Value <= now).Select(d => d.Key).ToList())
            {
                _deferred.TryRemove(key, out _);
            }
            var units = await DueAsync(db, pipeline, tenant, application, since, due, settings.Batch, [.. _deferred.Keys.Select(k => k.Transition).Distinct()], cancellationToken);
            var compare = new ShadowCompareOptions(settings.TimingTolerance, webhooks.Value.ConsoleUrl);
            var recorded = 0;
            foreach (var (platformTenant, transitionId, step, unitAt) in units)
            {
                var transition = await db.AlertTransitions.AsNoTracking().SingleAsync(t => t.Id == transitionId, cancellationToken);
                var unit = new ShadowUnit(platformTenant, transitionId, step, transition.AlertId,
                    step > 0 ? NotificationPlan.Escalated : transition.ToState == AlertState.Firing ? NotificationPlan.Firing : NotificationPlan.Resolved,
                    await db.AlertTransitions.AnyAsync(t => t.AlertId == transition.AlertId && t.ToState == AlertState.Resolved && t.At > transition.At, cancellationToken),
                    await db.NotificationHolds.AnyAsync(h => h.TransitionId == transitionId, cancellationToken),
                    // The release pass dropped it because the alert fired again (not merely: it fired again and it was dropped).
                    transition.ToState == AlertState.Resolved
                        && await db.NotificationHolds.AnyAsync(h => h.TransitionId == transitionId && h.Outcome == NotificationHoldOutcomes.DroppedStale, cancellationToken));
                var legacy = await LegacyAsync(db, transitionId, step, cancellationToken);
                var @new = await NewAsync(pipeline, tenant, application, transitionId, step, cancellationToken);
                var findings = ShadowCompare.Compare(unit, legacy, @new, compare);
                // Still sending on one side: looked at again after the next interval, until the wait runs out.
                if (findings.Any(f => f.Kind == ShadowDifferenceKinds.Unsettled) && now - unitAt < settings.MaxWait)
                {
                    var next = now + settings.Interval;
                    _deferred[(transitionId, step)] = next < unitAt + settings.MaxWait ? next : unitAt + settings.MaxWait;
                    continue;
                }
                await RecordAsync(db, unit, legacy, @new, findings, now, cancellationToken);
                recorded++;
            }
            return recorded;
        }

        /// <summary>
        /// Units due: transitions the platform planned since the shadow run began and at least the grace ago, and escalation steps either
        /// pipeline ran at least the grace ago, not yet compared and not deferred. Oldest first. The new side's steps come from its escalations
        /// (one row a notification, with the steps it ran), not from scanning its recipients.
        /// </summary>
        internal static async Task<List<(Guid Tenant, Guid Transition, int Step, DateTimeOffset At)>> DueAsync(PlatformDbContext db, NotificationsDbContext pipeline,
            Guid tenant, Guid application, DateTimeOffset since, DateTimeOffset due, int batch, List<Guid> deferred, CancellationToken ct)
        {
            var transitions = await db.AlertTransitions.AsNoTracking()
                // Planned once the shadow run began (the first request is accepted in the plan's transaction, a moment after its time).
                .Where(t => t.NotificationsPlannedAt >= since - PlanSlack && t.At <= due && (t.ToState == AlertState.Firing || t.ToState == AlertState.Resolved))
                .Where(t => !db.ShadowComparisons.Any(c => c.TenantId == t.TenantId && c.TransitionId == t.Id && c.EscalationStep == 0))
                .Where(t => !deferred.Contains(t.Id))
                .OrderBy(t => t.At).Take(batch)
                .Select(t => new { t.TenantId, t.Id, t.At }).ToListAsync(ct);
            var legacySteps = await db.WebhookDeliveries.AsNoTracking()
                .Where(d => d.EscalationStep > 0 && d.TransitionId != null && d.CreatedAt >= since && d.CreatedAt <= due)
                .Where(d => !db.ShadowComparisons.Any(c => c.TenantId == d.TenantId && c.TransitionId == d.TransitionId && c.EscalationStep == d.EscalationStep))
                .Where(d => !deferred.Contains(d.TransitionId!.Value))
                .GroupBy(d => new { d.TenantId, Transition = d.TransitionId!.Value, d.EscalationStep })
                .Select(g => new { g.Key.TenantId, g.Key.Transition, g.Key.EscalationStep, At = g.Min(d => d.CreatedAt) })
                .OrderBy(x => x.At).Take(batch).ToListAsync(ct);
            var units = transitions.Select(t => (t.TenantId, t.Id, 0, t.At))
                .Concat(legacySteps.Select(s => (s.TenantId, s.Transition, s.EscalationStep, s.At)))
                .ToList();
            // New-side steps legacy never sent (steps 1 up to the escalation's next): the transition's tenant is the platform's, read with it.
            // Read a page at a time, keyset-paged on (UpdatedAt, NotificationId): escalations already compared fill the oldest pages and must
            // not hide a newer one that wasn't (M46c). Comparisons live in another context, so it is paged here rather than filtered in SQL.
            var newOnly = new List<(Guid Tenant, Guid Transition, int Step, DateTimeOffset At)>();
            (DateTimeOffset At, Guid Id)? after = null;
            while (newOnly.Count < batch)
            {
                var page = await EscalationPageAsync(pipeline, tenant, application, since, due, after, batch, ct);
                var candidates = page
                    .SelectMany(e => Enumerable.Range(1, e.NextStep).Select(step =>
                        (Transition: Guid.TryParse(e.IdempotencyKey["transition:".Length..], out var id) ? id : Guid.Empty, Step: step, At: e.UpdatedAt)))
                    .Where(s => s.Transition != Guid.Empty && !deferred.Contains(s.Transition) && !units.Any(u => u.Item2 == s.Transition && u.Item3 == s.Step)).ToList();
                if (candidates.Count > 0)
                {
                    var ids = candidates.Select(s => s.Transition).Distinct().ToList();
                    var known = await db.AlertTransitions.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.TenantId }).ToDictionaryAsync(t => t.Id, t => t.TenantId, ct);
                    var compared = await db.ShadowComparisons.AsNoTracking().Where(c => ids.Contains(c.TransitionId)).Select(c => new { c.TransitionId, c.EscalationStep }).ToListAsync(ct);
                    newOnly.AddRange(candidates.Where(s => known.ContainsKey(s.Transition) && !compared.Any(c => c.TransitionId == s.Transition && c.EscalationStep == s.Step))
                        .Select(s => (known[s.Transition], s.Transition, s.Step, s.At)));
                }
                if (page.Count < batch)
                {
                    break;
                }
                after = (page[^1].UpdatedAt, page[^1].NotificationId);
            }
            units.AddRange(newOnly);
            return [.. units.OrderBy(u => u.Item4).Take(batch)];
        }

        private sealed record EscalationRow(string IdempotencyKey, int NextStep, DateTimeOffset UpdatedAt, Guid NotificationId);

        /// <summary>One page of the application's transition escalations in the window, oldest first, after the given (UpdatedAt, NotificationId).</summary>
        private static async Task<List<EscalationRow>> EscalationPageAsync(NotificationsDbContext pipeline, Guid tenant, Guid application, DateTimeOffset since,
            DateTimeOffset due, (DateTimeOffset At, Guid Id)? after, int batch, CancellationToken ct)
        {
            var query = from e in pipeline.Escalations.AsNoTracking()
                        join n in pipeline.Notifications.AsNoTracking() on new { e.TenantId, Id = e.NotificationId } equals new { n.TenantId, n.Id }
                        join q in pipeline.Requests.AsNoTracking() on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                        where e.TenantId == tenant && e.NextStep > 0 && e.UpdatedAt >= since && e.UpdatedAt <= due
                            && q.ApplicationId == application && q.IdempotencyKey.StartsWith("transition:")
                        select new { q.IdempotencyKey, e.NextStep, e.UpdatedAt, e.NotificationId };
            if (after is { } last)
            {
                query = query.Where(r => r.UpdatedAt > last.At || r.UpdatedAt == last.At && r.NotificationId.CompareTo(last.Id) > 0);
            }
            var rows = await query.OrderBy(r => r.UpdatedAt).ThenBy(r => r.NotificationId).Take(batch).ToListAsync(ct);
            return [.. rows.Select(r => new EscalationRow(r.IdempotencyKey, r.NextStep, r.UpdatedAt, r.NotificationId))];
        }

        /// <summary>What legacy sent for the unit, by endpoint: a grouped delivery with its group's body and send time.</summary>
        internal static async Task<List<LegacySent>> LegacyAsync(PlatformDbContext db, Guid transitionId, int step, CancellationToken ct)
        {
            var rows = await (from d in db.WebhookDeliveries.AsNoTracking()
                              join e in db.WebhookEndpoints.AsNoTracking() on d.EndpointId equals e.Id
                              where d.TransitionId == transitionId && d.EscalationStep == step
                              select new { Delivery = d, e.Key, e.Kind, e.GroupWindowSeconds, Recipients = e.Recipients.Length }).ToListAsync(ct);
            var groups = rows.Where(r => r.Delivery.GroupedInto is not null).Select(r => r.Delivery.GroupedInto!.Value).Distinct().ToList();
            var primaries = await db.WebhookDeliveries.AsNoTracking().Where(d => groups.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
            return [.. rows.Select(r =>
            {
                var d = r.Delivery;
                var group = d.GroupedInto is { } into && primaries.TryGetValue(into, out var primary) ? primary : null;
                return new LegacySent(r.Key, r.Kind, r.GroupWindowSeconds, group?.Payload ?? d.Payload,
                    group is null ? d.Status : group.Status == LegacyStatus.Delivered || group.Status == LegacyStatus.Failed ? LegacyStatus.Grouped : group.Status,
                    group?.LastError ?? d.LastError, d.CreatedAt, group?.FirstAttemptAt ?? d.FirstAttemptAt, r.Recipients);
            })];
        }

        /// <summary>What the new pipeline planned for the unit: the deliveries carrying the transition's request's recipients of that step.</summary>
        internal static async Task<List<NewSent>> NewAsync(NotificationsDbContext pipeline, Guid tenant, Guid application, Guid transitionId, int step, CancellationToken ct)
        {
            var key = ProducerAdapter.TransitionKey(transitionId);
            return await (from q in pipeline.Requests.AsNoTracking()
                          join i in pipeline.DeliveryItems.AsNoTracking() on new { q.TenantId, RequestId = q.Id } equals new { i.TenantId, i.RequestId }
                          join r in pipeline.Recipients.AsNoTracking() on new { i.TenantId, Id = i.RecipientId } equals new { r.TenantId, r.Id }
                          join d in pipeline.Deliveries.AsNoTracking() on new { i.TenantId, Id = i.DeliveryId } equals new { d.TenantId, d.Id }
                          join g in pipeline.Integrations.AsNoTracking() on new { d.TenantId, Id = d.IntegrationId } equals new { g.TenantId, g.Id }
                          where q.TenantId == tenant && q.ApplicationId == application && q.IdempotencyKey == key && r.EscalationStep == step
                          select new { d.Id, g.Key, d.Channel, d.Content, d.Status, d.CreatedAt, d.TemplateVersion })
                .Distinct().Select(x => new NewSent(x.Key, x.Channel, x.Content, x.Status, x.CreatedAt, x.TemplateVersion == 0)).ToListAsync(ct);
        }

        private static async Task RecordAsync(PlatformDbContext db, ShadowUnit unit, IReadOnlyList<LegacySent> legacy, IReadOnlyList<NewSent> @new,
            IReadOnlyList<ShadowFinding> findings, DateTimeOffset now, CancellationToken ct)
        {
            db.ShadowComparisons.Add(new ShadowComparison
            {
                TenantId = unit.TenantId, TransitionId = unit.TransitionId, EscalationStep = unit.EscalationStep, AlertId = unit.AlertId, Event = unit.Event,
                Legacy = legacy.Select(l => l.EndpointKey).Distinct().Count(), New = @new.Select(n => n.IntegrationKey).Distinct().Count(),
                Differences = findings.Count, Unexplained = findings.Count(f => f.Rule is null), ComparedAt = now,
            });
            db.ShadowDifferences.AddRange(findings.Select(f => new ShadowDifference
            {
                TenantId = unit.TenantId, TransitionId = unit.TransitionId, EscalationStep = unit.EscalationStep, AlertId = unit.AlertId, Event = unit.Event,
                EndpointKey = f.EndpointKey.Length > 100 ? f.EndpointKey[..100] : f.EndpointKey, Kind = f.Kind, Rule = f.Rule,
                Detail = f.Detail.Length > 2000 ? f.Detail[..2000] : f.Detail, DetectedAt = now,
            }));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // Another replica compared it first (a lease handed over mid-pass): its record stands.
                if (await db.ShadowComparisons.AsNoTracking().AnyAsync(c => c.TransitionId == unit.TransitionId && c.EscalationStep == unit.EscalationStep, ct))
                {
                    return;
                }
                throw;
            }
            Compared.Add(1, new KeyValuePair<string, object?>("event", unit.Event));
            foreach (var f in findings)
            {
                Differences.Add(1, new("kind", f.Kind), new("rule", f.Rule ?? Unexplained), new("explained", f.Rule is not null));
            }
        }
    }
}
