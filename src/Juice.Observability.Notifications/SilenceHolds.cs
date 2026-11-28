using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// Silences as holds in the pipeline (p5-d3): while a silence covers an open alert whose firing was sent, the alert's correlation is held, so an
    /// escalation step falling due waits; once no active silence covers it, it is released and what fell due runs if the alert
    /// is still open. <see cref="AlertHold"/> is the platform's record of each hold, and so of what to release (a hold outlives resolve and reopen,
    /// Q6). Overlapping silences: the hold moves to the one still covering, and is released only when none does.
    /// <para>Called in the platform's transaction that changes a silence (<c>SilenceEndpoints</c>), and by <see cref="SilenceHoldPass"/> for silences
    /// that start or end by time (within <see cref="PlatformNotificationsOptions.HoldPassInterval"/>, Q5). Each change is the client's call and its
    /// row together.</para>
    /// </summary>
    public sealed class SilenceHolds(PlatformDbContext db, ProducerAdapter adapter,
        IOptions<Juice.Observability.Discovery.PlatformOptions> platform, TimeProvider clock, ILogger<SilenceHolds> logger)
    {
        /// <summary>
        /// Holds and releases so that the pipeline's holds match the silences active now, for one tenant or all; inside an open platform transaction
        /// (the client refuses otherwise). Returns how many holds it changed.
        /// </summary>
        public async Task<int> ReconcileAsync(Guid? tenantId, CancellationToken cancellationToken)
        {
            // The pipeline didn't start: nothing is held or released, and nothing of the platform's record changes until it does.
            if (adapter.IsUnavailable)
            {
                return 0;
            }
            var tenants = tenantId is { } one ? [one] : await db.Tenants.AsNoTracking().Select(t => t.Id).OrderBy(t => t).ToListAsync(cancellationToken);
            var changed = 0;
            foreach (var tenant in tenants)
            {
                changed += await ReconcileTenantAsync(tenant, cancellationToken);
            }
            if (changed > 0)
            {
                logger.LogInformation("Silence holds: {Changed} held or released", changed);
            }
            return changed;
        }

        /// <summary>The advisory lock class that makes one tenant's reconciles take turns (a silence's change, the pass, another replica).</summary>
        private const int LockClass = 0x53484c44;

        /// <summary>
        /// Takes the tenant's silence lock until the transaction ends: a silence's change (its reconcile), the hold pass and the release pass take
        /// turns on it, so each reads the silences as the others left them.
        /// </summary>
        internal static async Task LockTenantAsync(PlatformDbContext db, Guid tenantId, CancellationToken cancellationToken)
        {
            var lockKey = BitConverter.ToInt32(tenantId.ToByteArray(), 0);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockClass}, {lockKey})", cancellationToken);
        }

        private async Task<int> ReconcileTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            // Serialized per tenant until this transaction ends: what is read below is what the holds are decided from.
            await LockTenantAsync(db, tenantId, cancellationToken);
            var now = clock.GetUtcNow();
            var active = (await NotificationPlan.ActiveAsync(db, now, cancellationToken)).Where(s => s.TenantId == tenantId).ToList();
            var rows = await db.AlertHolds.AsNoTracking().Where(h => h.TenantId == tenantId).ToListAsync(cancellationToken);
            var held = rows.Select(h => h.AlertId).ToList();
            var alerts = await db.Alerts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && (a.State == AlertState.Firing || held.Contains(a.Id))).ToListAsync(cancellationToken);
            var ruleIds = alerts.Select(a => a.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, cancellationToken);
            var changed = 0;
            foreach (var alert in alerts)
            {
                var covering = SilenceMatcher.Find(active, new AlertContext(rules[alert.RuleId], alert.SubjectKey, alert.Severity), now);
                var row = rows.FirstOrDefault(h => h.AlertId == alert.Id);
                if (covering is not null && (row is null ? alert.State == AlertState.Firing : row.SilenceId != covering.Id))
                {
                    // A new hold, or the one still covering takes it over (another reason replaces the hold's).
                    if (await adapter.HoldAlertAsync(alert.Id, covering.Id, cancellationToken))
                    {
                        await db.Database.ExecuteSqlAsync($"""
                            INSERT INTO alert_holds (tenant_id, alert_id, silence_id, held_at) VALUES ({alert.TenantId}, {alert.Id}, {covering.Id}, {now})
                            ON CONFLICT (tenant_id, alert_id) DO UPDATE SET silence_id = EXCLUDED.silence_id
                            """, cancellationToken);
                        changed++;
                    }
                }
                else if (covering is null && row is not null)
                {
                    // The record goes only with the hold: if the release didn't happen, the hold stands, and the next reconcile tries again.
                    if (await adapter.ReleaseAsync(ProducerAdapter.AlertCorrelation(alert.Id), cancellationToken) is HoldRelease.Released or HoldRelease.Gone)
                    {
                        await db.Database.ExecuteSqlAsync(
                            $"DELETE FROM alert_holds WHERE tenant_id = {alert.TenantId} AND alert_id = {alert.Id} AND silence_id = {row.SilenceId}", cancellationToken);
                        changed++;
                    }
                }
            }
            return changed + await HealAsync(tenantId, active, cancellationToken);
        }

        /// <summary>
        /// Holds the pipeline keeps for a silence that the platform has no record of (a record lost, or removed by hand): released, unless a
        /// silence still covers the alert, in which case the next reconcile records it.
        /// </summary>
        private async Task<int> HealAsync(Guid tenantId, IReadOnlyList<Silence> active, CancellationToken cancellationToken)
        {
            var pipelineTenant = Guid.Parse(platform.Value.TenantKey);
            var keys = await db.Database.SqlQuery<string>($"""
                SELECT c.key AS "Value" FROM notifications.correlations c JOIN notifications.applications a ON a.id = c.application_id
                WHERE a.key = {NotificationsRegistration.ApplicationKey} AND c.tenant_id = {pipelineTenant} AND c.held_at IS NOT NULL
                  AND c.hold_reason LIKE 'silence:%' AND c.key LIKE 'alert:%'
                  AND NOT EXISTS (SELECT 1 FROM alert_holds h WHERE h.tenant_id = {tenantId} AND 'alert:' || h.alert_id::text = c.key)
                """).ToListAsync(cancellationToken);
            var healed = 0;
            foreach (var key in keys)
            {
                if (!Guid.TryParse(key["alert:".Length..], out var alertId))
                {
                    continue;
                }
                var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, cancellationToken);
                if (alert is { State: AlertState.Firing })
                {
                    var rule = await db.Rules.AsNoTracking().Where(r => r.Id == alert.RuleId).Select(r => r.Key).SingleAsync(cancellationToken);
                    if (SilenceMatcher.Find(active, new AlertContext(rule, alert.SubjectKey, alert.Severity), clock.GetUtcNow()) is not null)
                    {
                        continue;
                    }
                }
                if (await adapter.ReleaseAsync(key, cancellationToken) is HoldRelease.Released)
                {
                    logger.LogWarning("Silence holds: {Key} was held for a silence with no record of it; released", key);
                    healed++;
                }
            }
            return healed;
        }
    }

    /// <summary>
    /// <see cref="SilenceHolds"/> every <see cref="PlatformNotificationsOptions.HoldPassInterval"/>, for silences that start or end by time (a future
    /// start, a weekly window, an end): one replica at a time, in a transaction of its own.
    /// </summary>
    public sealed class SilenceHoldPass(IServiceScopeFactory scopes, IOptions<PlatformNotificationsOptions> options, ILogger<SilenceHoldPass> logger) : BackgroundService
    {
        public const string Worker = "notifications-holds";

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PlatformCaller.Set(Worker, stoppingToken);
            WorkerPulse.Register(Worker, options.Value.HoldPassInterval, stopping: stoppingToken);
            await using var lease = WorkerLease.For(scopes, Worker, logger);
            using var timer = new PeriodicTimer(options.Value.HoldPassInterval);
            // The first pass is one interval after the start, not at it: the start has just migrated and provisioned.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        using var pass = WorkerPulse.Begin(Worker, stoppingToken);
                        await RunOnceAsync(stoppingToken);
                        pass.Succeeded();
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Silence hold pass failed; retrying next interval");
                }
            }
        }

        public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            using var context = PlatformMessageContext.Begin(Worker);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var changed = await scope.ServiceProvider.GetRequiredService<SilenceHolds>().ReconcileAsync(null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
    }
}
