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

        /// <summary>
        /// Phase 9 milestone 4: a lease lost and taken back within one call still says so. Its connection ends (as when the database restarts or
        /// a newer replica ends it), so the next call takes the lease again and returns true as before; the generation tells the worker another
        /// replica may have held it in between.
        /// </summary>
        [Fact]
        public async Task A_lease_taken_back_within_one_call_has_a_new_generation_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            await using var lease = new WorkerLease(database.ConnectionString, $"counter-{_run}", NullLogger.Instance);
            Assert.Equal(0, lease.Generation);
            Assert.True(await lease.TryHoldAsync(ct));
            Assert.Equal(1, lease.Generation);
            Assert.True(await lease.TryHoldAsync(ct));
            Assert.Equal(1, lease.Generation);   // held all along

            await using (var admin = new Npgsql.NpgsqlConnection(LocalStack.PostgresConnection("juice_observability")))
            {
                await admin.OpenAsync(ct);
                await using var end = new Npgsql.NpgsqlCommand(
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Name}' AND application_name LIKE 'juice-worker:counter-{_run}%'", admin);
                await end.ExecuteNonQueryAsync(ct);
            }
            Assert.True(await lease.TryHoldAsync(ct));   // lost and taken back in one call
            Assert.Equal(2, lease.Generation);
        }

        /// <summary>
        /// Phase 5's limit, seen for real: a host left on an older version held discovery and recorded no version changes. A newer replica now
        /// takes a lease over from an older holder; replicas of the same version don't, and an older one still takes a free lease.
        /// </summary>
        [Fact]
        public async Task A_newer_version_takes_a_lease_over_from_an_older_holder_and_only_then_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            WorkerLease Lease(string version) => new(database.ConnectionString, $"discovery-{_run}", NullLogger.Instance, version);

            await using var old = Lease("1.0.0");
            await using var sameAsOld = Lease("1.0.0");
            await using var newer = Lease("1.1.0-alpha.2");
            await using var newest = Lease("1.1.0");

            Assert.True(await old.TryHoldAsync(ct));
            Assert.False(await sameAsOld.TryHoldAsync(ct), "the same version took the lease over");
            Assert.True(await newer.TryHoldAsync(ct), "a newer version didn't take the lease over");
            Assert.False(await old.TryHoldAsync(ct), "the older holder didn't notice it lost the lease");
            Assert.True(await newer.TryHoldAsync(ct), "the new holder lost it again");
            Assert.True(await newest.TryHoldAsync(ct), "the release didn't take over from its prerelease");
            Assert.False(await newer.TryHoldAsync(ct));
            Assert.False(await old.TryHoldAsync(ct), "an older version took a held lease");

            await newest.DisposeAsync();
            Assert.True(await old.TryHoldAsync(ct), "an older version can't take a free lease: going back a version would stop the worker");
        }

        [Theory]
        [InlineData("1.0.0", "1.0.1", -1)]
        [InlineData("1.10.0", "1.9.0", 1)]
        [InlineData("1.1.0-alpha.3", "1.1.0", -1)]
        [InlineData("1.1.0-alpha.10", "1.1.0-alpha.9", 1)]
        [InlineData("1.1.0-alpha.3", "1.1.0-beta.1", -1)]
        [InlineData("1.1.0-alpha", "1.1.0-alpha.1", -1)]
        [InlineData("1.1.0-1", "1.1.0-alpha", -1)]
        [InlineData("1.1.0+abc", "1.1.0+def", 0)]
        [InlineData("1.1.0-local.20261108.1", "1.1.0-local.20261109.1", -1)]
        [InlineData("not a version", "0.0.1", -1)]
        public void Versions_are_ordered_as_semantic_versioning_orders_them(string a, string b, int expected)
        {
            Assert.Equal(expected, Math.Sign(PlatformVersion.Compare(a, b)));
            Assert.Equal(-expected, Math.Sign(PlatformVersion.Compare(b, a)));
        }

        [Fact]
        public async Task Replicas_starting_together_load_the_rules_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            var rules = Options.Create(new RulesOptions
            {
                Definitions = [new RuleDefinition { Key = $"errors-{_run}", Name = "Errors", Template = RuleTemplate.ErrorRate, Threshold = 0.05, RecoveryThreshold = 0.02 }],
            });
            // Each replica its own services, as separate processes would have.
            RuleProvisioner Replica() => new(PlatformServices.Build(database).GetRequiredService<IServiceScopeFactory>(), rules, NullLogger<RuleProvisioner>.Instance);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Replica().StartAsync(ct), ct)));

            await using var db = database.Context();
            Assert.Single(await db.Rules.Where(r => r.Key == $"errors-{_run}").ToListAsync(ct));
            Assert.Single(await db.AuditRecords.Where(a => a.TargetId == $"errors-{_run}").ToListAsync(ct));
        }
    }
}
