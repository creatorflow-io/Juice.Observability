using Juice.MediatR;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>
    /// The <c>integrations</c> leased worker (decision 7). Every pass reads the active instances (Enabled, Degraded, Failed), fails the ones whose plugin is missing
    /// or whose config version isn't the plugin's, runs the due ones (at most <c>MaxConcurrentRuns</c> at once, each claimed first, in a scope of its own) and then
    /// reconciles the integration alerts. With no instance and no open alert it is one query and one cheap join, and returns Idle.
    /// </summary>
    internal sealed class IntegrationsWorker(IServiceProvider services, IntegrationsDbContext db, IIntegrationPluginRegistry registry, IntegrationRunner runner, IntegrationHealthState state,
        IOptions<IntegrationsOptions> options, TimeProvider clock, ILogger<IntegrationsWorker> logger) : ILeasedWorker
    {
        private static readonly IntegrationState[] Active = [IntegrationState.Enabled, IntegrationState.Degraded, IntegrationState.Failed];

        public TimeSpan Interval => options.Value.PollInterval;

        /// <summary>How long a stopping host waits for the runs it cancelled to end before letting go of the services they use.</summary>
        public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The pass starts the due runs and reconciles the alerts, and then, while any run is in flight, stays open and does the same at each poll interval, so a
        /// slow plugin holds back neither the other due instances nor the alerts. It stays open because only a pass has the lease watcher: the runs are cancelled
        /// (their token lives with the worker, not the pass) when the lease is lost, when the lease is retaken, and when the host stops, and a stopping host
        /// waits a short grace for them to end before its services are disposed.
        /// </summary>
        public async Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct)
        {
            if (pass.Retaken)
            {
                // Another replica may have run meanwhile: whatever this one still has in flight is stale.
                state.CancelRuns();
                state.RuleReady = false;
            }
            using var onLost = pass.LeaseLost.Register(state.CancelRuns);
            using var onStop = ct.Register(state.CancelRuns);
            var runs = state.RunToken;
            try
            {
                var outcome = await IterateAsync(runs, ct);
                while (state.HasRuns && !runs.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    await Task.WhenAny(state.WhenRunsDoneAsync(), Task.Delay(Interval, ct));
                    if (state.HasRuns && !runs.IsCancellationRequested)
                    {
                        await IterateAsync(runs, ct);
                    }
                }
                return outcome;
            }
            finally
            {
                if (ct.IsCancellationRequested || pass.LeaseLost.IsCancellationRequested)
                {
                    state.CancelRuns();
                    await Task.WhenAny(state.WhenRunsDoneAsync(), Task.Delay(StopGrace, CancellationToken.None));
                }
            }
        }

        private async Task<LeasedPassOutcome> IterateAsync(CancellationToken runs, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var active = await db.Instances.AsNoTracking().Where(i => Active.Contains(i.State)).ToListAsync(ct);
            var work = new List<IntegrationInstance>();
            foreach (var instance in active)
            {
                var due = instance.NextRunAt is null || instance.NextRunAt <= now;
                if (!registry.TryGet(instance.PluginKey, out var descriptor))
                {
                    if (instance.State != IntegrationState.Failed || due)
                    {
                        await FailAsync(instance, $"Plugin '{instance.PluginKey}' is not installed", now, ct);
                    }
                }
                else if (descriptor.Config.Version != instance.ConfigVersion)
                {
                    if (instance.State != IntegrationState.Failed || due)
                    {
                        await FailAsync(instance, $"Config version {instance.ConfigVersion}, plugin expects {descriptor.Config.Version}", now, ct);
                    }
                }
                else if (due)
                {
                    work.Add(instance);
                }
            }
            // The runs are started, not waited for: one slow plugin holds back neither the other due instances (next pass) nor the alert reconcile. An instance
            // whose previous run (or its plugin call, which may outlive the run's timeout) hasn't ended is not started again.
            foreach (var instance in work)
            {
                if (state.TryBegin(instance.Id))
                {
                    state.Track(StartRun(instance, runs));
                }
            }
            var reconciled = await ReconcileAsync(active.Count > 0, ct);
            // Memory for the freshness gauge: replaced whole, so a deleted instance's series ends with this pass. Only instances that are running well report.
            if (active.Count > 0 || reconciled)
            {
                var all = await db.Instances.AsNoTracking().Select(i => new { i.Id, i.PluginKey, i.LastSuccessAt, i.State }).ToListAsync(ct);
                state.Replace(all.Where(i => i.State is IntegrationState.Enabled or IntegrationState.Degraded).Select(i => (i.Id, i.PluginKey, i.LastSuccessAt)));
            }
            else
            {
                state.Replace([]);
            }
            return active.Count == 0 && !reconciled ? LeasedPassOutcome.Idle : LeasedPassOutcome.Succeeded;
        }

        private async Task StartRun(IntegrationInstance instance, CancellationToken leaseLost)
        {
            var slots = state.Slots(options.Value.MaxConcurrentRuns);
            var waited = false;
            try
            {
                await slots.WaitAsync(leaseLost);
                waited = true;
                if (await runner.ClaimAsync(instance, leaseLost) is not { } token)
                {
                    return;
                }
                await runner.RunAsync(instance, token, leaseLost);
            }
            catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                logger.LogError(e, "Integration run of {Plugin} instance {Instance} failed to record", instance.PluginKey, instance.Id.ToString("N"));
            }
            finally
            {
                if (waited)
                {
                    slots.Release();
                }
                state.End(instance.Id);
            }
        }

        /// <summary>A missing plugin or a config of another version: Failed, not run, and looked at again after the longest backoff.</summary>
        private async Task FailAsync(IntegrationInstance instance, string reason, DateTimeOffset now, CancellationToken ct)
        {
            var id = instance.Id;
            var version = instance.Version;
            var next = now + options.Value.MaxBackoff;
            var failures = instance.State == IntegrationState.Failed ? instance.ConsecutiveFailures : instance.ConsecutiveFailures + 1;
            var rows = await db.Instances.Where(i => i.Id == id && i.Version == version && Active.Contains(i.State))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.State, IntegrationState.Failed)
                    .SetProperty(i => i.StateReason, reason)
                    .SetProperty(i => i.ConsecutiveFailures, failures)
                    .SetProperty(i => i.LastOutcome, nameof(CollectionResult.Failed))
                    .SetProperty(i => i.LastFailure, nameof(IntegrationFailure.Configuration))
                    .SetProperty(i => i.LastFailureDetail, reason)
                    .SetProperty(i => i.LastRunAt, (DateTimeOffset?)now)
                    .SetProperty(i => i.NextRunAt, (DateTimeOffset?)next)
                    .SetProperty(i => i.RunToken, (Guid?)null), ct);
            // A failed run for the metrics too, so an instance the core failed shows up as failing: counted only when this write took effect.
            if (rows == 1)
            {
                state.Run(instance.PluginKey, id, nameof(CollectionResult.Failed).ToLowerInvariant(), IntegrationFailure.Configuration);
            }
        }

        /// <summary>True when there was something to reconcile (an instance in an active state, or an alert still open).</summary>
        private async Task<bool> ReconcileAsync(bool anyActive, CancellationToken ct)
        {
            var source = services.GetRequiredKeyedService<IAlertSource>(AlertSourceKeys.Key("integrations", IntegrationHealthRule.Template));
            var open = await source.OpenAlertsAsync(ct);
            if (!anyActive && open.Count == 0)
            {
                return false;
            }
            var rows = (await db.Instances.AsNoTracking().Select(i => new { i.Id, i.Name, i.PluginKey, i.State, i.LastFailure, i.ConsecutiveFailures }).ToListAsync(ct))
                .Select(i => new IntegrationAlerts.Row(i.Id, i.Name, i.PluginKey, i.State, i.LastFailure, i.ConsecutiveFailures)).ToList();
            var canFire = true;
            if (rows.Any(r => r.State == IntegrationState.Failed) && !state.RuleReady)
            {
                using var context = PlatformMessageContext.Begin("integrations");
                state.RuleReady = await services.GetRequiredService<IMediator>().Send(new EnsureIntegrationRule(), ct);
                if (!state.RuleReady && state.FirstConflict())
                {
                    logger.LogWarning("A rule that the platform doesn't manage holds the key '{Key}', so integration instances raise no alerts", IntegrationHealthRule.Key);
                }
                canFire = state.RuleReady;
            }
            var signals = IntegrationAlerts.Reconcile(rows, open, clock.GetUtcNow(), canFire);
            if (signals.Count > 0)
            {
                await source.ApplyAsync(signals, ct);
            }
            return true;
        }
    }
}
