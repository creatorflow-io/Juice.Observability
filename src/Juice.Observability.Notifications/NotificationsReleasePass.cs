using System.Diagnostics.Metrics;
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
    /// What a silence held, once no silence covers it (phase-5-plan I65): each hold is decided as the legacy release decides a held delivery
    /// (<see cref="HeldNotification.Decide"/>), and its request is sent in the transaction that records the decision. One replica at a time
    /// (a lease), and the holds it decides are locked, so two passes never send one twice; the request's key would make a second one the same.
    /// <para>"Told" is per alert here (Q4): a resolved notice is sent if the alert's firing was sent, or held and then sent.</para>
    /// </summary>
    public sealed class NotificationsReleasePass(IServiceScopeFactory scopes, IOptions<PlatformNotificationsOptions> options, TimeProvider clock,
        ILogger<NotificationsReleasePass> logger) : BackgroundService
    {
        public const string Worker = "notifications-release";
        private const int Batch = 200;
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Released = Meter.CreateCounter<long>("juice.observability.notifications.released", "{notification}",
            "Notifications a silence held, decided once no silence covers them, by outcome (sent, dropped)");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PlatformCaller.Set(Worker, stoppingToken);
            WorkerPulse.Register(Worker, options.Value.ReleasePassInterval, stopping: stoppingToken);
            await using var lease = WorkerLease.For(scopes, Worker, logger);
            using var timer = new PeriodicTimer(options.Value.ReleasePassInterval);
            // The first pass is one interval after the start, not at it: the start has just migrated and provisioned.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        using var pass = WorkerPulse.Begin(Worker, stoppingToken);
                        while (await ReleaseOnceAsync(stoppingToken) >= Batch)
                        {
                        }
                        pass.Succeeded();
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Notification release pass failed; retrying next interval");
                }
            }
        }

        /// <summary>Replays the calls kept while the pipeline was down, then decides up to one batch of holds whose silence is over; returns how many it looked at.</summary>
        public async Task<int> ReleaseOnceAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var adapter = scope.ServiceProvider.GetRequiredService<ProducerAdapter>();
            using var context = PlatformMessageContext.Begin(Worker);
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Calls kept while the pipeline was down first, in order (an acknowledgement before the transitions kept after it).
            var parked = await db.NotificationParkedCalls
                .FromSql($"SELECT * FROM notification_parked_calls WHERE replayed_at IS NULL ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            foreach (var call in parked)
            {
                if (await EachAsync(transaction, () => adapter.ReplayAsync(call, cancellationToken), cancellationToken) is { } error)
                {
                    call.Attempts++;
                    call.LastError = Bounded(error);
                    if (call.Attempts >= NotificationHoldOutcomes.MaxAttempts)
                    {
                        logger.LogError("Kept call {Call} ({Kind}) given up after {Attempts} tries: {Error}", call.Id, call.Kind, call.Attempts, error);
                        call.ReplayedAt = now;
                    }
                    continue;
                }
                call.ReplayedAt = now;
            }
            var active = await NotificationPlan.ActiveAsync(db, now, cancellationToken);
            var activeIds = active.Select(s => s.Id).ToArray();
            var holds = await db.NotificationHolds
                .FromSql($"SELECT * FROM notification_holds WHERE decided_at IS NULL AND NOT (silence_id = ANY({activeIds})) ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            if (holds.Count == 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return parked.Count;
            }
            var alertIds = holds.Select(h => h.AlertId).Distinct().ToList();
            var alerts = await db.Alerts.AsNoTracking().Where(a => alertIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
            var ruleIds = alerts.Values.Select(a => a.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, cancellationToken);
            // Tracked: a firing decided in this batch counts for its resolved notice, decided after it (ids are in time order).
            var firings = await db.NotificationHolds.Where(h => alertIds.Contains(h.AlertId) && h.Event == NotificationPlan.Firing).ToListAsync(cancellationToken);
            // A silence being made now takes the same lock until it commits: the silences read below include it.
            foreach (var tenant in holds.Select(h => h.TenantId).Distinct().Order())
            {
                await SilenceHolds.LockTenantAsync(db, tenant, cancellationToken);
            }
            foreach (var hold in holds)
            {
                var alert = alerts[hold.AlertId];
                var alertContext = new AlertContext(rules[alert.RuleId], alert.SubjectKey, alert.Severity);
                // Another silence covers it now (read again, just before it would be sent): it stays held, under that one.
                var covering = SilenceMatcher.Find((await NotificationPlan.ActiveAsync(db, clock.GetUtcNow(), cancellationToken)).Where(s => s.TenantId == hold.TenantId),
                    alertContext, clock.GetUtcNow());
                if (covering is not null)
                {
                    hold.SilenceId = covering.Id;
                    continue;
                }
                var firing = firings.FirstOrDefault(f => f.AlertId == hold.AlertId);
                var told = firing is null || firing.Outcome == NotificationHoldOutcomes.Sent;
                // A held resolve of an alert that has fired again since is stale: its notice would tell the receiver the recurrence is over, and
                // its resolve would close the recurrence's escalation (review #13). The pipeline drops it on purpose (M46c).
                // Read once, so a retry decides the same.
                var stale = hold.Event == NotificationPlan.Resolved && await FiredSinceAsync(db, hold, alert, cancellationToken);
                var wouldSend = HeldNotification.Decide(hold.Event, alert.State, told, alert.AcknowledgedAt is not null) == HeldOutcome.Send;
                var decided = !stale && wouldSend;
                // Dropped because the alert fired again: only when it would have been sent otherwise (a hold dropped for another reason stays "dropped").
                var staleDropped = stale && wouldSend;
                var sent = false;
                var error = await EachAsync(transaction, async () =>
                {
                    // Its resolve first, if it wasn't asked at plan (the pipeline was down, whatever the reason it was kept: a silence wins when
                    // both apply, and it may have moved under a later silence), and never once the alert has fired again since.
                    if (hold.Event == NotificationPlan.Resolved && !hold.ResolveAsked && !stale)
                    {
                        await adapter.ResolveAlertAsync(hold.AlertId, null, cancellationToken);
                    }
                    sent = decided && await adapter.SendHeldAsync(hold, cancellationToken);
                }, cancellationToken);
                if (error is not null)
                {
                    // Only this hold's work is undone; it is tried again next pass, and given up after a few.
                    hold.Attempts++;
                    hold.LastError = Bounded(error);
                    if (hold.Attempts < NotificationHoldOutcomes.MaxAttempts)
                    {
                        continue;
                    }
                    logger.LogError("Held notification {Hold} given up after {Attempts} tries: {Error}", hold.Id, hold.Attempts, error);
                }
                hold.DecidedAt = now;
                hold.Outcome = error is not null ? NotificationHoldOutcomes.Failed : sent ? NotificationHoldOutcomes.Sent : staleDropped ? NotificationHoldOutcomes.DroppedStale : NotificationHoldOutcomes.Dropped;
                Released.Add(1, new KeyValuePair<string, object?>("outcome", hold.Outcome));
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return parked.Count + holds.Count;
        }

        /// <summary>
        /// One item's work under a savepoint of its own: an error undoes that item's writes only (PostgreSQL aborts the whole transaction on an error
        /// otherwise), so one bad item doesn't block the batch. Returns the error, or null.
        /// </summary>
        private async Task<string?> EachAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, Func<Task> work, CancellationToken cancellationToken)
        {
            await transaction.CreateSavepointAsync("item", cancellationToken);
            try
            {
                await work();
                await transaction.ReleaseSavepointAsync("item", cancellationToken);
                return null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await transaction.RollbackToSavepointAsync("item", cancellationToken);
                logger.LogWarning(ex, "A kept notification failed; the others go on: {Message}", ex.Message);
                return ex is Juice.Notifications.NotificationRefusedException refused ? $"{refused.Code}: {refused.Message}" : $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string Bounded(string error) => error.Length > 1000 ? error[..1000] : error;

        /// <summary>Whether the alert fired again after the held transition: firing now, or a later firing transition (resolved again since).</summary>
        private static async Task<bool> FiredSinceAsync(PlatformDbContext db, NotificationHold hold, Alert alert, CancellationToken cancellationToken)
        {
            if (alert.State == AlertState.Firing)
            {
                return true;
            }
            var at = await db.AlertTransitions.AsNoTracking().Where(t => t.TenantId == hold.TenantId && t.Id == hold.TransitionId).Select(t => t.At).SingleAsync(cancellationToken);
            return await db.AlertTransitions.AsNoTracking()
                .AnyAsync(t => t.TenantId == hold.TenantId && t.AlertId == hold.AlertId && t.ToState == AlertState.Firing && t.At > at, cancellationToken);
        }
    }
}
