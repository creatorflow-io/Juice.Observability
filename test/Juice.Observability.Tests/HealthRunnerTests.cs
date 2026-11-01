using System.Diagnostics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Roadmap Phase 2 gate, end to end: telemetry through the Collector, discovery, then health evaluation from
    /// ClickHouse evidence into PostgreSQL states and transitions.
    /// </summary>
    public class HealthRunnerTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        [Fact]
        public async Task Partial_instance_failure_degrades_the_service_and_its_application_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"health-{_run}-orders";
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            foreach (var (instance, failed) in new[] { ("a", 0), ("b", 0), ("c", 1) })
            {
                await LocalStack.SendAsync("traces", Otlp.Traces(
                    [("service.name", service), ("service.namespace", $"shop-{_run}"), ("service.instance.id", instance), ("deployment.environment.name", "production")],
                    Otlp.Requests(10, failed, at)));
            }
            await LocalStack.WaitForSpansAsync(service, 30);

            await Discover(database);
            await Health(database).RunOnceAsync(Ct);

            await using var db = database.Context();
            var svc = await db.Services.SingleAsync(s => s.Name == service);
            var instances = await db.Instances.Where(i => i.ServiceId == svc.Id).ToDictionaryAsync(i => i.InstanceKey, i => i.Id);
            var states = await db.HealthSnapshots.ToListAsync();
            Assert.Equal(HealthState.Healthy, states.Single(h => h.SubjectId == instances["a"]).State);
            var failing = states.Single(h => h.SubjectId == instances["c"]);
            Assert.Equal((HealthState.Unhealthy, "error_rate_high"), (failing.State, failing.ReasonCodes[0])); // 1 of 10

            var serviceState = states.Single(h => h.SubjectType == HealthSubject.Service && h.SubjectId == svc.Id);
            Assert.Equal(HealthState.Degraded, serviceState.State); // 1 of 3 instances, 1 error in 30
            Assert.Contains("partial_instance_failure", serviceState.ReasonCodes);
            Assert.Contains("error_rate_elevated", serviceState.ReasonCodes);
            Assert.Equal(new HealthPolicy().Version, serviceState.PolicyVersion);
            Assert.NotNull(serviceState.LastEvidenceAt);
            Assert.Equal(HealthState.Degraded, states.Single(h => h.SubjectType == HealthSubject.Application && h.SubjectId == svc.ApplicationId).State);

            var transition = await db.HealthTransitions.SingleAsync(t => t.SubjectId == svc.Id);
            Assert.Equal((null, HealthState.Degraded), (transition.FromState, transition.ToState));
        }

        [Fact]
        public async Task Failing_critical_dependency_makes_the_caller_critical_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (orders, billing) = ($"health-{_run}-orders", $"health-{_run}-billing");
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", orders), ("service.instance.id", "o1")], Otlp.Requests(10, 0, at)));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", billing), ("service.instance.id", "b1")], Otlp.Requests(10, 10, at)));
            await LocalStack.WaitForSpansAsync(billing, 10);
            await LocalStack.WaitForSpansAsync(orders, 10);

            await Discover(database);
            await Health(database, p => p.CriticalDependencies.Add(new CriticalDependency { Service = orders, DependsOn = billing })).RunOnceAsync(Ct);

            await using var db = database.Context();
            var ordersState = await ServiceStateAsync(db, orders);
            Assert.Equal(HealthState.Critical, ordersState.State);
            Assert.Equal($"critical_dependency_failed:{billing}", ordersState.ReasonCodes[0]);
            Assert.Equal(HealthState.Unhealthy, (await ServiceStateAsync(db, billing)).State);
        }

        [Fact]
        public async Task Silent_subjects_are_offline_and_never_seen_ones_unknown_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var silent = $"health-{_run}-silent";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", silent), ("service.instance.id", "s1")],
                Otlp.Requests(5, 0, DateTimeOffset.UtcNow.AddMinutes(-10))));
            await LocalStack.WaitForSpansAsync(silent, 5);
            await Discover(database);
            var never = $"health-{_run}-never";
            await using (var seed = database.Context())
            {
                var tenant = await seed.Tenants.SingleAsync();
                seed.Services.Add(new Service { TenantId = tenant.Id, Namespace = "", Name = never, FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
                await seed.SaveChangesAsync();
            }

            await Health(database).RunOnceAsync(Ct);

            await using var db = database.Context();
            var offline = await ServiceStateAsync(db, silent);
            Assert.Equal((HealthState.Offline, "no_active_instances"), (offline.State, offline.ReasonCodes[0]));
            var unknown = await ServiceStateAsync(db, never);
            Assert.Equal((HealthState.Unknown, "never_seen", (Guid?)null), (unknown.State, unknown.ReasonCodes[0], unknown.EnvironmentId));
        }

        [Fact]
        public async Task Same_evidence_and_policy_never_writes_a_second_transition_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"health-{_run}-replay";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "r1")],
                Otlp.Requests(10, 0, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 10);
            await Discover(database);
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var runner = Health(database, clock: clock);

            var first = await runner.RunOnceAsync(Ct);
            Assert.True(first.Transitions > 0);
            var transitions = await CountAsync(database);

            Assert.Equal(0, (await runner.RunOnceAsync(Ct)).Transitions);          // unchanged state
            await using (var db = database.Context())
            {
                await db.HealthSnapshots.ExecuteDeleteAsync(Ct);                     // lost snapshots: every state "changes"
            }
            Assert.Equal(0, (await runner.RunOnceAsync(Ct)).Transitions);          // same window and policy: already recorded
            Assert.Equal(transitions, await CountAsync(database));

            // A new window with this service's state unchanged writes it no transition. (Other services the tenant
            // holds may legitimately change as the window moves.)
            clock.Now += TimeSpan.FromMinutes(1);
            await runner.RunOnceAsync(Ct);
            await using var check = database.Context();
            var id = (await check.Services.SingleAsync(s => s.Name == service)).Id;
            Assert.Equal(1, await check.HealthTransitions.CountAsync(t => t.SubjectId == id));
        }

        [Fact]
        public async Task ClickHouse_outage_keeps_the_last_states_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"health-{_run}-outage";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "x1")],
                Otlp.Requests(10, 0, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 10);
            await Discover(database);
            await Health(database).RunOnceAsync(Ct);
            await using var before = database.Context();
            var evaluated = await before.HealthSnapshots.AsNoTracking().ToDictionaryAsync(h => h.Id, h => (h.State, h.EvaluatedAt));

            var unreachable = Health(database, clickHouse: LocalStack.ClickHouseReader.Replace("Password=", "Password=wrong"));
            await Assert.ThrowsAnyAsync<Exception>(() => unreachable.RunOnceAsync(Ct));

            await using var after = database.Context();
            Assert.Equal(evaluated, await after.HealthSnapshots.AsNoTracking().ToDictionaryAsync(h => h.Id, h => (h.State, h.EvaluatedAt)));
        }

        /// <summary>
        /// Health freshness (requirements §9): p95 evidence-to-visible ≤ 60 s. A known healthy service starts failing;
        /// the evaluator runs on a short loop, so the measured time is the evidence lag plus ingestion. With the default
        /// 30 s interval the bound is lag (10 s) + interval (30 s) + ingestion (p95 1.4 s), about 42 s.
        /// </summary>
        [Fact]
        public async Task A_new_failure_is_visible_within_the_freshness_target_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"health-{_run}-fresh";
            var resource = new (string, string)[] { ("service.name", service), ("service.instance.id", "f1") };
            await LocalStack.SendAsync("traces", Otlp.Traces(resource, Otlp.Requests(10, 0, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 10);
            await Discover(database);
            var runner = Health(database);
            await runner.RunOnceAsync(Ct);
            await using (var db = database.Context())
            {
                Assert.Equal(HealthState.Healthy, (await ServiceStateAsync(db, service)).State);
            }

            var failure = Stopwatch.StartNew();
            await LocalStack.SendAsync("traces", Otlp.Traces(resource, Otlp.Requests(20, 20, DateTimeOffset.UtcNow)));
            HealthState state;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
                await runner.RunOnceAsync(Ct);
                await using var db = database.Context();
                state = (await ServiceStateAsync(db, service)).State;
            }
            while (state == HealthState.Healthy && failure.Elapsed < TimeSpan.FromSeconds(60));

            TestContext.Current.SendDiagnosticMessage($"evidence-to-visible with a 1 s evaluation loop: {failure.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(HealthState.Unhealthy, state);
            Assert.True(failure.Elapsed < new HealthPolicy().Lag + TimeSpan.FromSeconds(10),
                $"took {failure.Elapsed}, expected about the evidence lag ({new HealthPolicy().Lag}) plus ingestion");
        }

        // ─── helpers ──────────────────────────────────────────────────────────

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<HealthSnapshot> ServiceStateAsync(PlatformDbContext db, string name)
        {
            var id = (await db.Services.SingleAsync(s => s.Name == name)).Id;
            return await db.HealthSnapshots.SingleAsync(h => h.SubjectType == HealthSubject.Service && h.SubjectId == id);
        }

        private static async Task<int> CountAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.HealthTransitions.CountAsync();
        }

        private static ServiceProvider Services(ScratchDatabase database)
            => new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();

        private static async Task Discover(ScratchDatabase database)
            => await new DiscoveryRunner(Services(database).GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })),
                Options.Create(new DiscoveryOptions { Lag = TimeSpan.Zero, InitialLookback = TimeSpan.FromMinutes(30) }),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System,
                NullLogger<DiscoveryRunner>.Instance).RunOnceAsync(Ct);

        private static HealthRunner Health(ScratchDatabase database, Action<HealthPolicy>? configure = null, TimeProvider? clock = null,
            string? clickHouse = null)
        {
            // The evidence window must cover telemetry sent "a minute ago"; the default lag (10 s) is kept.
            var policy = new HealthPolicy();
            configure?.Invoke(policy);
            return new HealthRunner(Services(database).GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = clickHouse ?? LocalStack.ClickHouseReader })),
                Options.Create(policy), Options.Create(new DiscoveryOptions()), Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }),
                clock ?? TimeProvider.System, NullLogger<HealthRunner>.Instance);
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
