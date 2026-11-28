using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>The core's sinks: no members until the collection sinks arrive (I2, I3).</summary>
    internal sealed class CollectionSinks : ICollectionSinks;

    /// <summary>
    /// One run of one instance (decision 7): the claim, the plugin's collection in a scope of its own, and the outcome written only if the claim still holds.
    /// The tenant of the run context is the instance's. Everything the plugin says is scrubbed of the secrets it resolved, and capped, before it is stored.
    /// </summary>
    internal sealed class IntegrationRunner(IServiceScopeFactory scopes, IOptions<IntegrationsOptions> options, TimeProvider clock, IntegrationHealthState state, ILogger<IntegrationRunner> logger)
    {
        private static readonly IntegrationState[] Active = [IntegrationState.Enabled, IntegrationState.Degraded, IntegrationState.Failed];

        private IntegrationsOptions Options => options.Value;

        /// <summary>
        /// Takes the run: only one replica or pass wins an instance, and only while it is as it was read (not run since, not changed by an admin, still
        /// active). Gives the token the outcome must carry, or null.
        /// </summary>
        public async Task<Guid?> ClaimAsync(IntegrationInstance seen, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var token = Guid.NewGuid();
            var until = clock.GetUtcNow() + Options.RunTimeout;
            var id = seen.Id;
            var nextRunAt = seen.NextRunAt;
            var version = seen.Version;
            var rows = await db.Instances.Where(i => i.Id == id && i.NextRunAt == nextRunAt && i.Version == version && Active.Contains(i.State))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.RunToken, (Guid?)token).SetProperty(i => i.NextRunAt, (DateTimeOffset?)until), ct);
            return rows == 1 ? token : null;
        }

        /// <summary>Runs the claimed instance and writes its outcome. Returns false when nothing was written (the lease was lost, or the claim no longer held).</summary>
        public async Task<bool> RunAsync(IntegrationInstance instance, Guid token, CancellationToken leaseLost)
        {
            var (outcome, pending, resolved) = await ExecuteAsync(instance, leaseLost);
            if (leaseLost.IsCancellationRequested)
            {
                return false;
            }
            var decision = IntegrationOutcomes.Decide(outcome, instance.ConsecutiveFailures, Options.FailedAfter);
            var detail = IntegrationScrubber.Clean(outcome.Detail, resolved);
            var now = clock.GetUtcNow();
            var delay = IntegrationBackoff.Delay(decision, TimeSpan.FromSeconds(instance.IntervalSeconds), Options.MaxBackoff, Random.Shared.NextDouble() * 2 - 1);
            var written = await WriteAsync(instance, token, decision, detail, pending, now, now + delay, leaseLost);
            if (written)
            {
                state.Run(instance.PluginKey, instance.Id, decision.Result.ToString().ToLowerInvariant(), decision.Failure);
                if (decision.Success)
                {
                    state.Succeeded(instance.Id, instance.PluginKey, now);
                }
            }
            return written;
        }

        private async Task<(CollectionOutcome Outcome, IReadOnlyDictionary<string, IntegrationCheckpoint> Pending, IReadOnlyCollection<string> Resolved)> ExecuteAsync(
            IntegrationInstance instance, CancellationToken leaseLost)
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<IntegrationsDbContext>();
            var gate = new SemaphoreSlim(1);
            var id = instance.Id;
            var checkpoints = new RunCheckpoints(async (stream, ct) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var row = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(c => c.InstanceId == id && c.Stream == stream, ct);
                    return row is null ? null : new IntegrationCheckpoint(row.Stream, row.Watermark, row.Cursor);
                }
                finally
                {
                    gate.Release();
                }
            });
            IntegrationContext? context = null;
            IReadOnlyCollection<string> Resolved() => context is null ? [] : IntegrationContextFactory.ResolvedValues(context);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(leaseLost);
            timeout.CancelAfter(Options.RunTimeout);
            try
            {
                context = IntegrationContextFactory.Create(instance, services, checkpoints);
                var plugin = services.GetRequiredService<IIntegrationPluginRegistry>().Resolve(services, instance.PluginKey);
                CollectionOutcome outcome;
                if (plugin is IIntegrationCollector collector)
                {
                    var call = collector.CollectAsync(context, new CollectionSinks(), timeout.Token);
                    state.Attach(id, call);
                    outcome = await call.WaitAsync(timeout.Token);
                }
                else
                {
                    // A plugin with no collection is run as its health check.
                    var check = plugin.ValidateAsync(context, timeout.Token);
                    state.Attach(id, check);
                    var report = await check.WaitAsync(timeout.Token);
                    outcome = report.Passed ? new(CollectionResult.Succeeded)
                        : new(CollectionResult.Failed, IntegrationFailure.Remote, "Failed checks: " + string.Join(", ", report.Checks.Where(c => !c.Passed).Select(c => c.Name)));
                }
                if (!Enum.IsDefined(outcome.Result) || !Enum.IsDefined(outcome.Failure))
                {
                    outcome = new(CollectionResult.Failed, IntegrationFailure.Internal, "The plugin returned an unknown outcome");
                }
                return (outcome, checkpoints.Pending, Resolved());
            }
            catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
            {
                return (new(CollectionResult.Failed, IntegrationFailure.Internal, "Lease lost"), checkpoints.Pending, Resolved());
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return (new(CollectionResult.Failed, IntegrationFailure.Timeout, $"The run took longer than {Options.RunTimeout}"), checkpoints.Pending, Resolved());
            }
            catch (IntegrationSecretException e)
            {
                return (new(CollectionResult.Failed, IntegrationFailure.Configuration, e.Message), checkpoints.Pending, Resolved());
            }
            catch (Exception e)
            {
                logger.LogWarning("Integration {Plugin} instance {Instance} threw {Type}", instance.PluginKey, instance.Id.ToString("N"), e.GetType().Name);
                return (new(CollectionResult.Failed, IntegrationFailure.Internal, $"{e.GetType().Name}: {e.Message}"), checkpoints.Pending, Resolved());
            }
        }

        private async Task<bool> WriteAsync(IntegrationInstance instance, Guid token, RunDecision decision, string? detail,
            IReadOnlyDictionary<string, IntegrationCheckpoint> pending, DateTimeOffset now, DateTimeOffset next, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var id = instance.Id;
            var version = instance.Version;
            var newState = decision.State;
            var reason = decision.Reason;
            var failures = decision.Failures;
            var result = decision.Result.ToString();
            string? failure = decision.Success && decision.Failure == IntegrationFailure.None ? null : decision.Failure.ToString();
            var failureDetail = decision.Success ? null : detail;
            var succeeded = decision.Success;
            // Only the worker writes it, and only under the claim, so the value read with the instance is current.
            var lastSuccess = succeeded ? now : instance.LastSuccessAt;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // The claim, the version and the state: a disable, a change by an admin or a newer claim since the run began each drop the whole outcome.
            var rows = await db.Instances.Where(i => i.Id == id && i.RunToken == token && i.Version == version && Active.Contains(i.State))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.State, newState)
                    .SetProperty(i => i.StateReason, reason)
                    .SetProperty(i => i.ConsecutiveFailures, failures)
                    .SetProperty(i => i.LastOutcome, result)
                    .SetProperty(i => i.LastFailure, failure)
                    .SetProperty(i => i.LastFailureDetail, failureDetail)
                    .SetProperty(i => i.LastRunAt, (DateTimeOffset?)now)
                    .SetProperty(i => i.LastSuccessAt, (DateTimeOffset?)lastSuccess)
                    .SetProperty(i => i.NextRunAt, (DateTimeOffset?)next)
                    .SetProperty(i => i.RunToken, (Guid?)null), ct);
            if (rows != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            // The streams the plugin set commit with an accepted outcome that collected something, or not at all.
            if (succeeded && pending.Count > 0)
            {
                var streams = pending.Keys.ToList();
                var existing = await db.Checkpoints.Where(c => c.InstanceId == id && streams.Contains(c.Stream)).ToDictionaryAsync(c => c.Stream, ct);
                foreach (var (stream, set) in pending)
                {
                    if (!existing.TryGetValue(stream, out var row))
                    {
                        db.Checkpoints.Add(new IntegrationCheckpointRow { InstanceId = id, Stream = stream, Watermark = set.Watermark, Cursor = set.Cursor, UpdatedAt = now });
                    }
                    else
                    {
                        row.Watermark = set.Watermark;
                        row.Cursor = set.Cursor;
                        row.UpdatedAt = now;
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return true;
        }
    }
}
