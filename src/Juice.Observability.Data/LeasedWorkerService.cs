using Juice.Observability.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Data
{
    /// <summary>
    /// The loop of a worker that one replica runs at a time: name the caller, register the worker's pulse, take the lease (<see cref="WorkerLease"/>),
    /// and on every tick of the interval hold it, run one pass, and report its outcome. A failed pass is logged and tried again next tick.
    /// <para>The core's discovery, health and SLO counting derive from it, and a module's workers run on it as <c>LeasedWorkerHost&lt;T&gt;</c>.</para>
    /// <para><see cref="LeasedPass.LeaseLost"/> is cancelled while the pass runs if the lease is lost or taken again on a new connection (a new
    /// generation): another replica may have run in between, so what the pass is doing is stale. The next pass then reads <see cref="LeasedPass.Retaken"/>.</para>
    /// </summary>
    public abstract class LeasedWorkerService : BackgroundService
    {
        protected LeasedWorkerService(string worker, IServiceScopeFactory scopes, ILogger logger)
        {
            Worker = worker;
            Scopes = scopes;
            Logger = logger;
        }

        /// <summary>The worker's name: its lease, its pulse and the caller its queries are tagged with.</summary>
        public string Worker { get; }

        protected IServiceScopeFactory Scopes { get; }

        protected ILogger Logger { get; }

        /// <summary>What the lease's lock is keyed by, when not the worker's name (see <see cref="WorkerLease"/>). A core worker's is its name.</summary>
        protected virtual string? LeaseKeyName => null;

        /// <summary>The time between passes.</summary>
        protected abstract TimeSpan Interval { get; }

        /// <summary>How often a running pass checks that the lease is still held: the interval, at most a second.</summary>
        protected virtual TimeSpan LeaseCheckInterval => Interval < TimeSpan.FromSeconds(1) ? Interval : TimeSpan.FromSeconds(1);

        /// <summary>One pass, while this replica holds the lease. <see cref="LeasedPassOutcome.Idle"/> is a heartbeat only.</summary>
        protected abstract Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken cancellationToken);

        /// <summary>A pass that threw (not the host stopping): logged here, and tried again at the next tick.</summary>
        protected virtual void LogFailure(Exception exception)
            => Logger.LogError(exception, "{Worker} pass failed; retrying in {Interval}", Worker, Interval);

        /// <summary>At a tick where another replica holds the lease (or this one could not take it): for a worker that has work of its own to give up.</summary>
        protected virtual Task OnLeaseNotHeldAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = Interval;
            PlatformCaller.Set(Worker, stoppingToken);
            WorkerPulse.Register(Worker, interval, stopping: stoppingToken);
            await using var lease = WorkerLease.For(Scopes, Worker, Logger, LeaseKeyName);
            using var timer = new PeriodicTimer(interval);
            var generation = 0;
            do
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        // Taken (back), even within one call: another replica may have run meanwhile.
                        var retaken = lease.Generation != generation;
                        generation = lease.Generation;
                        using var pass = WorkerPulse.Begin(Worker, stoppingToken);
                        var outcome = await RunWatchedAsync(lease, new LeasedPass(Worker, generation, retaken, default), stoppingToken);
                        switch (outcome)
                        {
                            case LeasedPassOutcome.Succeeded:
                                pass.Succeeded();
                                break;
                            case LeasedPassOutcome.Partial:
                                pass.Partial();
                                break;
                            default:
                                pass.Idle();
                                break;
                        }
                    }
                    else
                    {
                        await OnLeaseNotHeldAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailure(ex);
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    // A pass that ended in an error because the host is stopping isn't a failure of the worker: don't fault the service for it.
                    return;
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        // Runs the pass with a watcher beside it that cancels LeaseLost when the lease goes, or comes back as a new generation.
        private async Task<LeasedPassOutcome> RunWatchedAsync(WorkerLease lease, LeasedPass pass, CancellationToken stoppingToken)
        {
            using var lost = new CancellationTokenSource();
            using var done = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var watcher = WatchAsync(lease, pass.Generation, lost, done.Token);
            try
            {
                return await RunOnceAsync(pass with { LeaseLost = lost.Token }, stoppingToken);
            }
            finally
            {
                await done.CancelAsync();
                await watcher;
            }
        }

        private async Task WatchAsync(WorkerLease lease, int generation, CancellationTokenSource lost, CancellationToken done)
        {
            try
            {
                using var timer = new PeriodicTimer(LeaseCheckInterval);
                while (await timer.WaitForNextTickAsync(done))
                {
                    if (!await lease.TryHoldAsync(done) || lease.Generation != generation)
                    {
                        await lost.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (done.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // The lease can't be confirmed: the pass is told it may be lost, and the next pass finds out.
                Logger.LogWarning(ex, "Could not check the {Worker} lease during a pass", Worker);
                await lost.CancelAsync();
            }
        }
    }
}
