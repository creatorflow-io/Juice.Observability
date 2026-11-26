using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// Silences as holds in the pipeline (p5-d3): while a silence covers an open alert whose firing was sent, the alert's correlation is held, so an
    /// escalation step falling due waits, as legacy holds it; once no active silence covers it, it is released and what fell due runs if the alert
    /// is still open. <see cref="AlertHold"/> is the platform's record of each hold, and so of what to release (a hold outlives resolve and reopen,
    /// Q6). Overlapping silences: the hold moves to the one still covering, and is released only when none does.
    /// <para>Called in the platform's transaction that changes a silence (<c>SilenceEndpoints</c>), and by <see cref="SilenceHoldPass"/> for silences
    /// that start or end by time (within <see cref="PlatformNotificationsOptions.HoldPassInterval"/>, Q5). Each change is the client's call and its
    /// row together.</para>
    /// </summary>
    public sealed class SilenceHolds(PlatformDbContext db, ProducerAdapter adapter, IOptions<PlatformNotificationsOptions> pipeline, TimeProvider clock,
        ILogger<SilenceHolds> logger)
    {
        /// <summary>Whether the platform holds through the pipeline (<c>shadow</c> and <c>new</c>).</summary>
        public bool Enabled => pipeline.Value.ProducesRequests;

        /// <summary>
        /// Holds and releases so that the pipeline's holds match the silences active now, for one tenant or all; inside an open platform transaction
        /// (the client refuses otherwise). Returns how many holds it changed.
        /// </summary>
        public async Task<int> ReconcileAsync(Guid? tenantId, CancellationToken cancellationToken)
        {
            if (!Enabled)
            {
                return 0;
            }
            var now = clock.GetUtcNow();
            var active = (await NotificationWorker.ActiveAsync(db, now, cancellationToken)).Where(s => tenantId is null || s.TenantId == tenantId).ToList();
            var rows = await db.AlertHolds.AsNoTracking().Where(h => tenantId == null || h.TenantId == tenantId).ToListAsync(cancellationToken);
            var held = rows.Select(h => h.AlertId).ToList();
            var alerts = await db.Alerts.AsNoTracking()
                .Where(a => (tenantId == null || a.TenantId == tenantId) && (a.State == AlertState.Firing || held.Contains(a.Id))).ToListAsync(cancellationToken);
            var ruleIds = alerts.Select(a => a.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, cancellationToken);
            var changed = 0;
            foreach (var alert in alerts)
            {
                var covering = SilenceMatcher.Find(active.Where(s => s.TenantId == alert.TenantId), new AlertContext(rules[alert.RuleId], alert.SubjectKey, alert.Severity), now);
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
                    await adapter.ReleaseAlertAsync(alert.Id, cancellationToken);
                    await db.Database.ExecuteSqlAsync($"DELETE FROM alert_holds WHERE tenant_id = {alert.TenantId} AND alert_id = {alert.Id}", cancellationToken);
                    changed++;
                }
            }
            if (changed > 0)
            {
                logger.LogInformation("Silence holds: {Changed} held or released", changed);
            }
            return changed;
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
            do
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
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
