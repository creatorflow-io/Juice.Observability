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

        public async Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct)
        {
            if (pass.Retaken)
            {
                state.RuleReady = false;
            }
            var now = clock.GetUtcNow();
            var active = await db.Instances.AsNoTracking().Where(i => Active.Contains(i.State)).ToListAsync(ct);
            var partial = false;
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
            using var slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentRuns));
            var runs = work.Select(async instance =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    if (await runner.ClaimAsync(instance, ct) is not { } token)
                    {
                        return;
                    }
                    await runner.RunAsync(instance, token, pass.LeaseLost);
                }
                catch (OperationCanceledException) when (pass.LeaseLost.IsCancellationRequested || ct.IsCancellationRequested)
                {
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    partial = true;
                    logger.LogError(e, "Integration run of {Plugin} instance {Instance} failed to record", instance.PluginKey, instance.Id.ToString("N"));
                }
                finally
                {
                    slots.Release();
                }
            }).ToList();
            await Task.WhenAll(runs);
            if (pass.LeaseLost.IsCancellationRequested)
            {
                return LeasedPassOutcome.Partial;
            }
            var reconciled = await ReconcileAsync(active.Count > 0, ct);
            // Memory for the freshness gauge: replaced whole, so a deleted instance's series ends with this pass.
            if (active.Count > 0 || reconciled)
            {
                var all = await db.Instances.AsNoTracking().Select(i => new { i.Id, i.PluginKey, i.LastSuccessAt }).ToListAsync(ct);
                state.Replace(all.Select(i => (i.Id, i.PluginKey, i.LastSuccessAt)));
            }
            else
            {
                state.Replace([]);
            }
            return active.Count == 0 && !reconciled ? LeasedPassOutcome.Idle : partial ? LeasedPassOutcome.Partial : LeasedPassOutcome.Succeeded;
        }

        /// <summary>A missing plugin or a config of another version: Failed, not run, and looked at again after the longest backoff.</summary>
        private async Task FailAsync(IntegrationInstance instance, string reason, DateTimeOffset now, CancellationToken ct)
        {
            var id = instance.Id;
            var version = instance.Version;
            var next = now + options.Value.MaxBackoff;
            var failures = instance.State == IntegrationState.Failed ? instance.ConsecutiveFailures : instance.ConsecutiveFailures + 1;
            await db.Instances.Where(i => i.Id == id && i.Version == version && Active.Contains(i.State))
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
