using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Observability.Tests.Workers
{
    /// <summary>The leased loop tells a worker at a tick where another replica holds the lease (<c>OnLeaseNotHeldAsync</c>), and runs no pass then.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class LeaseNotHeldTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class Probe(string name, IServiceScopeFactory scopes, ILogger logger) : LeasedWorkerService(name, scopes, logger)
        {
            public int NotHeld, Passes;

            protected override TimeSpan Interval => TimeSpan.FromMilliseconds(100);

            protected override Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Passes);
                return Task.FromResult(LeasedPassOutcome.Idle);
            }

            protected override Task OnLeaseNotHeldAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref NotHeld);
                return Task.CompletedTask;
            }
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(100, Ct);
            Assert.True(condition(), "the condition was not met in 15 s");
        }

        [Fact]
        public async Task A_worker_is_told_when_another_replica_holds_the_lease_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var name = "hook-" + Guid.NewGuid().ToString("N")[..8];
            var services = new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();
            using var probe = new Probe(name, services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

            await using var other = new WorkerLease(database.ConnectionString, name, NullLogger.Instance);
            Assert.True(await other.TryHoldAsync(Ct));
            await probe.StartAsync(Ct);
            try
            {
                await WaitAsync(() => Volatile.Read(ref probe.NotHeld) >= 2);
                Assert.Equal(0, Volatile.Read(ref probe.Passes));

                await other.DisposeAsync();
                await WaitAsync(() => Volatile.Read(ref probe.Passes) > 0);
                var told = Volatile.Read(ref probe.NotHeld);
                await Task.Delay(500, Ct);
                Assert.Equal(told, Volatile.Read(ref probe.NotHeld));   // holding it, it is not told
            }
            finally
            {
                await probe.StopAsync(Ct);
            }
        }
    }
}
