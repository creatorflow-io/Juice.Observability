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
                        while (await CompareOnceAsync(stoppingToken) >= options.Value.Batch)
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

        /// <summary>Compares up to one batch of units that are due; returns how many it looked at.</summary>
        public async Task<int> CompareOnceAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<NotificationsEmbeddedOptions>().TenantId;
            var settings = options.Value;
            var now = clock.GetUtcNow();
            var due = now - settings.Grace;
            // The shadow run began with the new pipeline's first request: nothing before it was ever asked of the new side.
            var first = await pipeline.Requests.AsNoTracking().Where(r => r.TenantId == tenant).MinAsync(r => (DateTimeOffset?)r.AcceptedAt, cancellationToken);
            if (first is null)
            {
                return 0;
            }
            // Within the lookback: each pass reads a bounded window, however long the run has gone on.
            var since = first.Value > now - settings.Lookback ? first.Value : now - settings.Lookback;
            var units = await DueAsync(db, pipeline, tenant, since, due, settings.Batch, cancellationToken);
            var compare = new ShadowCompareOptions(settings.TimingTolerance, webhooks.Value.ConsoleUrl);
            foreach (var (platformTenant, transitionId, step, unitAt) in units)
            {
                var transition = await db.AlertTransitions.AsNoTracking().SingleAsync(t => t.Id == transitionId, cancellationToken);
                var unit = new ShadowUnit(platformTenant, transitionId, step, transition.AlertId,
                    step > 0 ? NotificationPlan.Escalated : transition.ToState == AlertState.Firing ? NotificationPlan.Firing : NotificationPlan.Resolved,
                    await db.AlertTransitions.AnyAsync(t => t.AlertId == transition.AlertId && t.ToState == AlertState.Resolved && t.At > transition.At, cancellationToken),
                    await db.NotificationHolds.AnyAsync(h => h.TransitionId == transitionId, cancellationToken));
                var legacy = await LegacyAsync(db, transitionId, step, cancellationToken);
                var @new = await NewAsync(pipeline, tenant, transitionId, step, cancellationToken);
                var findings = ShadowCompare.Compare(unit, legacy, @new, compare);
                // Still sending on one side: looked at again next pass, until the wait runs out.
                if (findings.Any(f => f.Kind == ShadowDifferenceKinds.Unsettled) && now - unitAt < settings.MaxWait)
                {
                    continue;
                }
                await RecordAsync(db, unit, legacy, @new, findings, now, cancellationToken);
            }
            return units.Count;
        }

        /// <summary>
        /// Units due: transitions the platform planned since the shadow run began and at least the grace ago, and escalation steps either
        /// pipeline sent at least the grace ago, not yet compared. Oldest first.
        /// </summary>
        private static async Task<List<(Guid Tenant, Guid Transition, int Step, DateTimeOffset At)>> DueAsync(PlatformDbContext db, NotificationsDbContext pipeline,
            Guid tenant, DateTimeOffset since, DateTimeOffset due, int batch, CancellationToken ct)
        {
            var transitions = await db.AlertTransitions.AsNoTracking()
                // Planned once the shadow run began (the first request is accepted in the plan's transaction, a moment after its time).
                .Where(t => t.NotificationsPlannedAt >= since - PlanSlack && t.At <= due && (t.ToState == AlertState.Firing || t.ToState == AlertState.Resolved))
                .Where(t => !db.ShadowComparisons.Any(c => c.TenantId == t.TenantId && c.TransitionId == t.Id && c.EscalationStep == 0))
                .OrderBy(t => t.At).Take(batch)
                .Select(t => new { t.TenantId, t.Id, t.At }).ToListAsync(ct);
            var legacySteps = await db.WebhookDeliveries.AsNoTracking()
                .Where(d => d.EscalationStep > 0 && d.TransitionId != null && d.CreatedAt >= since && d.CreatedAt <= due)
                .Where(d => !db.ShadowComparisons.Any(c => c.TenantId == d.TenantId && c.TransitionId == d.TransitionId && c.EscalationStep == d.EscalationStep))
                .GroupBy(d => new { d.TenantId, Transition = d.TransitionId!.Value, d.EscalationStep })
                .Select(g => new { g.Key.TenantId, g.Key.Transition, g.Key.EscalationStep, At = g.Min(d => d.CreatedAt) })
                .OrderBy(x => x.At).Take(batch).ToListAsync(ct);
            var newSteps = await (from r in pipeline.Recipients.AsNoTracking()
                                  join n in pipeline.Notifications.AsNoTracking() on new { r.TenantId, Id = r.NotificationId } equals new { n.TenantId, n.Id }
                                  join q in pipeline.Requests.AsNoTracking() on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                                  where r.TenantId == tenant && r.EscalationStep > 0 && r.CreatedAt >= since && r.CreatedAt <= due && q.IdempotencyKey.StartsWith("transition:")
                                  group r by new { q.IdempotencyKey, r.EscalationStep } into g
                                  select new { g.Key.IdempotencyKey, g.Key.EscalationStep, At = g.Min(x => x.CreatedAt) })
                .OrderBy(x => x.At).Take(batch).ToListAsync(ct);
            var units = transitions.Select(t => (t.TenantId, t.Id, 0, t.At))
                .Concat(legacySteps.Select(s => (s.TenantId, s.Transition, s.EscalationStep, s.At)))
                .ToList();
            // New-side steps legacy never sent: the transition's tenant is the platform's, read with the transition.
            var newOnly = newSteps.Select(s => (Transition: Guid.TryParse(s.IdempotencyKey["transition:".Length..], out var id) ? id : Guid.Empty, s.EscalationStep, s.At))
                .Where(s => s.Transition != Guid.Empty && !units.Any(u => u.Item2 == s.Transition && u.Item3 == s.EscalationStep)).ToList();
            if (newOnly.Count > 0)
            {
                var ids = newOnly.Select(s => s.Transition).Distinct().ToList();
                var known = await db.AlertTransitions.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.TenantId }).ToDictionaryAsync(t => t.Id, t => t.TenantId, ct);
                var compared = await db.ShadowComparisons.AsNoTracking().Where(c => ids.Contains(c.TransitionId)).Select(c => new { c.TransitionId, c.EscalationStep }).ToListAsync(ct);
                units.AddRange(newOnly.Where(s => known.ContainsKey(s.Transition) && !compared.Any(c => c.TransitionId == s.Transition && c.EscalationStep == s.EscalationStep))
                    .Select(s => (known[s.Transition], s.Transition, s.EscalationStep, s.At)));
            }
            return [.. units.OrderBy(u => u.Item4).Take(batch)];
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
        internal static async Task<List<NewSent>> NewAsync(NotificationsDbContext pipeline, Guid tenant, Guid transitionId, int step, CancellationToken ct)
        {
            var key = ProducerAdapter.TransitionKey(transitionId);
            return await (from q in pipeline.Requests.AsNoTracking()
                          join i in pipeline.DeliveryItems.AsNoTracking() on new { q.TenantId, RequestId = q.Id } equals new { i.TenantId, i.RequestId }
                          join r in pipeline.Recipients.AsNoTracking() on new { i.TenantId, Id = i.RecipientId } equals new { r.TenantId, r.Id }
                          join d in pipeline.Deliveries.AsNoTracking() on new { i.TenantId, Id = i.DeliveryId } equals new { d.TenantId, d.Id }
                          join g in pipeline.Integrations.AsNoTracking() on new { d.TenantId, Id = d.IntegrationId } equals new { g.TenantId, g.Id }
                          where q.TenantId == tenant && q.IdempotencyKey == key && r.EscalationStep == step
                          select new { d.Id, g.Key, d.Channel, d.Content, d.Status, d.CreatedAt })
                .Distinct().Select(x => new NewSent(x.Key, x.Channel, x.Content, x.Status, x.CreatedAt)).ToListAsync(ct);
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
