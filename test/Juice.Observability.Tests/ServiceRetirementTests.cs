using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Services that stop reporting: discovery retires them after Discovery:ServiceRetireAfter and restores them when they report again;
    /// health stops evaluating them; the inventory leaves them out unless asked; an admin can forget a retired one.
    /// </summary>
    public class ServiceRetirementTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static ResourceObservation Seen(string service, DateTimeOffset at, string instance = "a")
            => new() { ServiceNamespace = "shop", ServiceName = service, InstanceId = instance, Environment = "production", FirstSeen = at, LastSeen = at, Records = 1 };

        private static async Task<ReconcileResult> ApplyAsync(ScratchDatabase database, DateTimeOffset readThrough, TimeSpan? retireAfter = null,
            params ResourceObservation[] seen)
        {
            await using var db = database.Context();
            var options = new DiscoveryOptions();
            if (retireAfter is { } after) options.ServiceRetireAfter = after;
            return await new Reconciler(db, new Normalizer([]), options, TimeProvider.System)
                .ApplyAsync((await db.Tenants.SingleAsync(Ct)).Id, seen, [], [], Ct, readThrough);
        }

        private static async Task<DateTimeOffset?> RetiredAtAsync(ScratchDatabase database, string service)
        {
            await using var db = database.Context();
            return await db.Services.AsNoTracking().Where(s => s.Name == service).Select(s => s.RetiredAt).SingleAsync(Ct);
        }

        [Fact]
        public async Task A_silent_service_is_retired_and_comes_back_when_it_reports_again_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var now = DateTimeOffset.UtcNow;
            // Measured from what discovery has read: catching up after an outage, a service whose newer telemetry is still unread stays.
            Assert.Equal(0, (await ApplyAsync(database, now.AddDays(-10), null, Seen("old", now.AddDays(-20)), Seen("recent", now.AddDays(-3)))).Retired);
            Assert.Null(await RetiredAtAsync(database, "old"));

            var result = await ApplyAsync(database, now);
            Assert.Equal(1, result.Retired);
            Assert.NotNull(await RetiredAtAsync(database, "old"));
            Assert.Null(await RetiredAtAsync(database, "recent"));
            Assert.Equal(0, (await ApplyAsync(database, now)).Retired); // already retired: nothing new

            // The overlap re-reading what it had already seen doesn't bring it back; newer telemetry does.
            Assert.Equal(0, (await ApplyAsync(database, now, null, Seen("old", now.AddDays(-20)))).Restored);
            Assert.NotNull(await RetiredAtAsync(database, "old"));
            var back = await ApplyAsync(database, now, null, Seen("old", now.AddMinutes(-1)));
            Assert.Equal((1, 0), (back.Restored, back.Retired));
            Assert.Null(await RetiredAtAsync(database, "old"));
        }

        [Fact]
        public async Task Zero_never_retires_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var now = DateTimeOffset.UtcNow;
            await ApplyAsync(database, now, TimeSpan.Zero, Seen("ancient", now.AddDays(-400)));
            Assert.Equal(0, (await ApplyAsync(database, now, TimeSpan.Zero)).Retired);
            Assert.Null(await RetiredAtAsync(database, "ancient"));
        }

        [Fact]
        public async Task Health_leaves_out_retired_services_and_drops_their_states_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);

            await Health(database).RunOnceAsync(Ct);

            await using var db = database.Context();
            var snapshots = await db.HealthSnapshots.AsNoTracking().ToListAsync(Ct);
            Assert.DoesNotContain(snapshots, h => h.SubjectId == seed.Retired || h.SubjectId == seed.RetiredInstance || h.SubjectId == seed.RetiredApplication);
            Assert.Contains(snapshots, h => h.SubjectId == seed.Live && h.SubjectType == HealthSubject.Service);
            Assert.Contains(snapshots, h => h.SubjectId == seed.LiveApplication && h.SubjectType == HealthSubject.Application);
            // History stays.
            Assert.True(await db.HealthTransitions.AnyAsync(t => t.SubjectId == seed.Retired, Ct));
        }

        [Fact]
        public async Task The_inventory_leaves_out_retired_services_unless_asked_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = $"/api/v1/tenants/{seed.Tenant}";

            var services = await JsonAsync(await Get(client, $"{tenant}/resources?type=service", token));
            Assert.Equal([seed.Live], services.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            var all = (await JsonAsync(await Get(client, $"{tenant}/resources?type=service&retired=true", token))).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, all.Count);
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, all.Single(i => i.GetProperty("id").GetGuid() == seed.Retired).GetProperty("retiredAt").ValueKind);

            // Its detail is still there, saying so.
            var detail = await JsonAsync(await Get(client, $"{tenant}/resources/{seed.Retired}", token));
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, detail.GetProperty("retiredAt").ValueKind);

            var applications = (await JsonAsync(await Get(client, $"{tenant}/applications", token))).GetProperty("items").EnumerateArray()
                .ToDictionary(a => a.GetProperty("key").GetString()!, a => (a.GetProperty("services").GetInt32(), a.GetProperty("retiredServices").GetInt32()));
            Assert.Equal((0, 1), applications["legacy"]);
            Assert.Equal((1, 0), applications["shop"]);

            // The call from the live service to the retired one is left out, with the retired node.
            var topology = await JsonAsync(await Get(client, $"{tenant}/topology?environment=production", token));
            Assert.Equal(0, topology.GetProperty("edges").GetArrayLength());
        }

        [Fact]
        public async Task Only_an_admin_forgets_and_only_a_retired_service_without_open_alerts_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, _) = await Caller.AllAsync(client, database);
            var resources = $"/api/v1/tenants/{seed.Tenant}/resources";

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Retired}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Retired}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Live}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Host}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{Guid.CreateVersion7()}")).StatusCode);

            // An open alert on it: resolve it first.
            await using (var db = database.Context())
            {
                var rule = new Rule { TenantId = seed.Tenant, Key = "legacy-errors", Name = "Legacy errors", Definition = "{}", DefinitionHash = "x" };
                db.Rules.Add(rule);
                db.Alerts.Add(new Alert
                {
                    TenantId = seed.Tenant, RuleId = rule.Id, DedupKey = "legacy", SubjectKey = "legacy/billing", SubjectType = HealthSubject.Service,
                    SubjectId = seed.Retired, State = AlertState.Firing, BreachStartedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }
            Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Retired}")).StatusCode);
            await using (var db = database.Context())
            {
                await db.Alerts.ExecuteUpdateAsync(a => a.SetProperty(x => x.State, AlertState.Resolved), Ct);
            }

            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Retired}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Delete, $"{resources}/{seed.Retired}")).StatusCode);

            await using var check = database.Context();
            Assert.False(await check.Services.AnyAsync(s => s.Id == seed.Retired, Ct));
            Assert.False(await check.Instances.AnyAsync(i => i.ServiceId == seed.Retired, Ct));
            Assert.False(await check.Dependencies.AnyAsync(d => d.TargetServiceId == seed.Retired, Ct));
            Assert.False(await check.HealthSnapshots.AnyAsync(h => h.SubjectId == seed.Retired || h.SubjectId == seed.RetiredInstance, Ct));
            Assert.False(await check.HealthTransitions.AnyAsync(h => h.SubjectId == seed.Retired, Ct));
            // Its application had nothing else, so it goes too; the live one stays.
            Assert.False(await check.Applications.AnyAsync(a => a.Id == seed.RetiredApplication, Ct));
            Assert.True(await check.Services.AnyAsync(s => s.Id == seed.Live, Ct));
            // History stays: the alert, the audit trail and a configuration change on the timeline.
            Assert.True(await check.Alerts.AnyAsync(a => a.SubjectId == seed.Retired, Ct));
            var audits = await check.AuditRecords.AsNoTracking().Where(a => a.Operation == "service.forget").ToListAsync(Ct);
            Assert.Equal(2, audits.Count(a => a.Outcome == AuditOutcome.Denied));
            Assert.Equal(3, audits.Count(a => a.Outcome == AuditOutcome.Failed)); // in use, not a service, open alerts; a 404 is not audited
            var forgotten = audits.Single(a => a.Outcome == AuditOutcome.Succeeded);
            Assert.Equal(("alice", "legacy/billing"), (forgotten.Actor, forgotten.Metadata["service"]));
            Assert.True(await check.ChangeEvents.AnyAsync(c => c.Summary == "Service legacy/billing: forgotten", Ct));
        }

        private sealed record Seed(Guid Tenant, Guid Live, Guid LiveApplication, Guid Retired, Guid RetiredInstance, Guid RetiredApplication, Guid Host);

        /// <summary>
        /// shop/orders in use, calling legacy/billing, retired 3 days ago with an instance, an old Offline state and a transition; a host.
        /// </summary>
        private static async Task<Seed> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = await db.Tenants.SingleAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-20);
            var production = new DeploymentEnvironment { TenantId = tenant.Id, Key = "production", Type = EnvironmentType.Production, FirstSeen = old, LastSeen = now };
            var shop = new Application { TenantId = tenant.Id, Key = "shop", DisplayName = "shop", FirstSeen = old, LastSeen = now };
            var legacy = new Application { TenantId = tenant.Id, Key = "legacy", DisplayName = "legacy", FirstSeen = old, LastSeen = old };
            var orders = new Service { TenantId = tenant.Id, Namespace = "shop", Name = "orders", ApplicationId = shop.Id, FirstSeen = old, LastSeen = now };
            var billing = new Service { TenantId = tenant.Id, Namespace = "legacy", Name = "billing", ApplicationId = legacy.Id, FirstSeen = old, LastSeen = old, RetiredAt = now.AddDays(-3) };
            var instance = new ServiceInstance { TenantId = tenant.Id, ServiceId = billing.Id, EnvironmentId = production.Id, InstanceKey = "b1", State = InstanceState.Inactive, FirstSeen = old, LastSeen = old };
            var host = new Resource { TenantId = tenant.Id, Type = ResourceType.Host, Key = "web-1", DisplayName = "web-1", FirstSeen = old, LastSeen = now };
            db.AddRange(production, shop, legacy, orders, billing, instance, host,
                new ServiceEnvironment { TenantId = tenant.Id, ServiceId = orders.Id, EnvironmentId = production.Id, FirstSeen = old, LastSeen = now },
                new ServiceEnvironment { TenantId = tenant.Id, ServiceId = billing.Id, EnvironmentId = production.Id, FirstSeen = old, LastSeen = old },
                new Dependency { TenantId = tenant.Id, EnvironmentId = production.Id, SourceServiceId = orders.Id, Relation = DependencyRelation.Calls, TargetServiceId = billing.Id, Confidence = DependencyConfidence.Observed, SpanCount = 3, FirstSeen = old, LastSeen = old });
            foreach (var (type, id) in new[] { (HealthSubject.Service, billing.Id), (HealthSubject.Instance, instance.Id), (HealthSubject.Application, legacy.Id) })
            {
                db.HealthSnapshots.Add(new HealthSnapshot { TenantId = tenant.Id, SubjectType = type, SubjectId = id, EnvironmentId = production.Id, State = HealthState.Offline, PolicyVersion = "old", EvaluatedAt = now, Since = old });
            }
            db.HealthTransitions.Add(new HealthTransition { TenantId = tenant.Id, SubjectType = HealthSubject.Service, SubjectId = billing.Id, EnvironmentId = production.Id, ToState = HealthState.Offline, PolicyVersion = "old", EvaluatedAt = old });
            await db.SaveChangesAsync(Ct);
            return new Seed(tenant.Id, orders.Id, shop.Id, billing.Id, instance.Id, legacy.Id, host.Id);
        }

        private static HealthRunner Health(ScratchDatabase database)
            => new(new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider()
                    .GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })),
                Options.Create(new HealthPolicy()), Options.Create(new DiscoveryOptions()), Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }),
                TimeProvider.System, NullLogger<HealthRunner>.Instance);
    }
}
