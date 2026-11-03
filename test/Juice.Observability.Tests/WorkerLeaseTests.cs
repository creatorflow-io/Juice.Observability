using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>Review finding 3: discovery, health and rules run in one replica at a time.</summary>
    public class WorkerLeaseTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        [Fact]
        public async Task One_replica_holds_a_worker_and_another_takes_over_when_it_stops_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            // Each replica is its own process with its own connection pool: the application name keeps the pools apart.
            WorkerLease Lease(string name, string replica) => new(
                new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = replica }.ConnectionString,
                $"{name}-{_run}", NullLogger.Instance);

            await using var second = Lease("discovery", "replica-b");
            await using var other = Lease("health", "replica-b");
            await using (var first = Lease("discovery", "replica-a"))
            {
                Assert.True(await first.TryHoldAsync(ct));
                Assert.False(await second.TryHoldAsync(ct));
                Assert.True(await first.TryHoldAsync(ct), "the holder keeps it pass after pass");
                Assert.True(await other.TryHoldAsync(ct), "each worker has its own lease");
            }

            Assert.True(await second.TryHoldAsync(ct), "the lock goes with the holder's connection");
        }

        [Fact]
        public async Task Replicas_starting_together_load_the_rules_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            var services = new ServiceCollection()
                .AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString))
                .BuildServiceProvider();
            var rules = Options.Create(new RulesOptions
            {
                Definitions = [new RuleDefinition { Key = $"errors-{_run}", Name = "Errors", Template = RuleTemplate.ErrorRate, Threshold = 0.05, RecoveryThreshold = 0.02 }],
            });
            RuleProvisioner Replica() => new(services.GetRequiredService<IServiceScopeFactory>(), rules,
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<RuleProvisioner>.Instance);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Replica().StartAsync(ct), ct)));

            await using var db = database.Context();
            Assert.Single(await db.Rules.Where(r => r.Key == $"errors-{_run}").ToListAsync(ct));
            Assert.Single(await db.AuditRecords.Where(a => a.TargetId == $"errors-{_run}").ToListAsync(ct));
        }
    }
}
