using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Roadmap Phase 1 gate: resources appear from real telemetry, sent through the Collector, without manual
    /// registration; restarts and churn reconcile without duplicates or cross-tenant merges.
    /// </summary>
    public class DiscoveryTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        [Fact]
        public async Task A_new_version_is_a_change_but_a_rollout_and_a_replay_are_not_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"disc-{_run}-versions";
            var sent = 0;
            // Spans of now: discovery reads forward from where it stopped, as it does for real telemetry.
            async Task SendAsync(string instance, string version)
            {
                await LocalStack.SendAsync("traces", Otlp.Traces(
                    [("service.name", service), ("service.instance.id", instance), ("deployment.environment.name", "production"), ("service.version", version)],
                    Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, DateTimeOffset.UtcNow)));
                await LocalStack.WaitForSpansAsync(service, ++sent);
            }
            async Task<List<(string? From, string? To)>> DiscoverAsync()
            {
                await Runner(database).RunOnceAsync(TestContext.Current.CancellationToken);
                await using var db = database.Context();
                return (await db.ChangeEvents.Where(c => c.Source == ChangeEvent.TelemetrySource && c.Summary.Contains(service)).OrderBy(c => c.OccurredAt).ToListAsync()).Select(c => (c.FromVersion, c.ToVersion)).ToList();
            }

            // The first version seen isn't a change: nothing says what came before.
            await SendAsync("v1-a", "1.0");
            Assert.Empty(await DiscoverAsync());

            // A new instance on a new version is one.
            await SendAsync("v2-a", "1.1");
            Assert.Equal([("1.0", "1.1")], await DiscoverAsync());

            // The old instance still reporting 1.0 during the rollout, in a later round, isn't a step back.
            await SendAsync("v1-a", "1.0");
            Assert.Equal([("1.0", "1.1")], await DiscoverAsync());

            // More of the same version: nothing new. Running discovery again over what it has seen: nothing new.
            await SendAsync("v2-b", "1.1");
            Assert.Equal([("1.0", "1.1")], await DiscoverAsync());
            Assert.Equal([("1.0", "1.1")], await DiscoverAsync());

            // A rollback is a change too.
            await SendAsync("v1-b", "1.0");
            Assert.Equal([("1.0", "1.1"), ("1.1", "1.0")], await DiscoverAsync());

            await using var check = database.Context();
            var change = await check.ChangeEvents.Where(c => c.ToVersion == "1.1" && c.Summary.Contains(service)).SingleAsync();
            Assert.Equal((ChangeKind.Version, null, "production"), (change.Kind, change.Actor, change.Environment));
            Assert.Equal((await check.Services.SingleAsync(s => s.Name == service)).Id, change.ServiceId);
            Assert.Contains("moved from 1.0 to 1.1", change.Summary);
        }

        [Theory(DisplayName = "An install from before versions were tracked doesn't lose the first change after upgrading")]
        [InlineData("same", "A restart on a new version")]
        [InlineData("another", "A new instance on a new version")]
        public async Task The_first_change_after_upgrading_isnt_lost_Async(string instance, string _)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var start = DateTimeOffset.UtcNow.AddMinutes(-20);
            async Task ApplyAsync(string key, int minutes)
            {
                await using var db = database.Context();
                var at = start.AddMinutes(minutes);
                await new Reconciler(db, new Normalizer([]), new DiscoveryOptions(), TimeProvider.System).ApplyAsync((await db.Tenants.SingleAsync()).Id,
                    [new ResourceObservation { ServiceName = "orders", InstanceId = key, Environment = "production", ServiceVersion = minutes == 0 ? "1.0" : "1.1", FirstSeen = at, LastSeen = at, Records = 1 }],
                    [], [], TestContext.Current.CancellationToken);
            }
            await ApplyAsync("same", 0);
            await using var check = database.Context();
            // As an upgraded install finds it: instances know their version, the service doesn't yet.
            await check.ServiceEnvironments.ExecuteUpdateAsync(s => s.SetProperty(e => e.CurrentVersion, (string?)null).SetProperty(e => e.CurrentVersionAt, (DateTimeOffset?)null), TestContext.Current.CancellationToken);

            await ApplyAsync(instance, 5);

            var change = await check.ChangeEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(("1.0", "1.1"), (change.FromVersion, change.ToVersion));
        }

        [Fact]
        public async Task An_old_instance_reporting_later_is_not_a_step_back_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var start = DateTimeOffset.UtcNow.AddMinutes(-20);
            async Task<List<(string? From, string? To)>> ApplyAsync(string instance, string version, int minutes)
            {
                await using var db = database.Context();
                var tenant = (await db.Tenants.SingleAsync()).Id;
                var at = start.AddMinutes(minutes);
                await new Reconciler(db, new Normalizer([]), new DiscoveryOptions(), TimeProvider.System).ApplyAsync(tenant,
                    [new ResourceObservation { ServiceName = "orders", InstanceId = instance, Environment = "production", ServiceVersion = version, FirstSeen = at, LastSeen = at, Records = 1 }],
                    [], [], TestContext.Current.CancellationToken);
                return (await db.ChangeEvents.OrderBy(c => c.OccurredAt).ToListAsync()).Select(c => (c.FromVersion, c.ToVersion)).ToList();
            }

            Assert.Empty(await ApplyAsync("old", "1.0", 0));
            Assert.Equal([("1.0", "1.1")], await ApplyAsync("new", "1.1", 5));
            // This window holds only the old instance's later spans, so nothing in its times gives it away.
            Assert.Equal([("1.0", "1.1")], await ApplyAsync("old", "1.0", 10));
            // A new instance on the old version is a rollback.
            Assert.Equal([("1.0", "1.1"), ("1.1", "1.0")], await ApplyAsync("back", "1.0", 12));
            // An instance updated in place is a change too.
            Assert.Equal([("1.0", "1.1"), ("1.1", "1.0"), ("1.0", "1.2")], await ApplyAsync("back", "1.2", 14));
        }

        [Fact]
        public async Task Services_instances_environments_and_hosts_appear_from_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"disc-{_run}-a";
            var ns = $"shop-{_run}";
            var host = $"web-{_run}";
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            foreach (var (instance, version) in new[] { ("i1", "1.0"), ("i2", "1.1") })
            {
                await LocalStack.SendAsync("traces", Otlp.Traces(
                    [("service.name", service), ("service.namespace", ns), ("service.instance.id", instance),
                     ("deployment.environment.name", "Production"), ("host.name", host), ("service.version", version)],
                    Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, at)));
            }
            // No service.instance.id: the instance key is derived from host and process id.
            await LocalStack.SendAsync("traces", Otlp.Traces(
                [("service.name", service), ("service.namespace", ns), ("deployment.environment.name", "Production"),
                 ("host.name", host), ("process.pid", "77")],
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, at)));
            await LocalStack.WaitForSpansAsync(service, 3);

            await Runner(database).RunOnceAsync(TestContext.Current.CancellationToken);

            await using var db = database.Context();
            var svc = await db.Services.SingleAsync(s => s.Name == service);
            Assert.Equal(ns, svc.Namespace);
            var application = await db.Applications.SingleAsync(a => a.Id == svc.ApplicationId);
            Assert.Equal(ns, application.Key);
            var environment = await db.Environments.SingleAsync(e => e.Key == "production");
            Assert.Equal(EnvironmentType.Production, environment.Type);
            var hostResource = await db.Resources.SingleAsync(r => r.Type == ResourceType.Host && r.Key == host);
            var instances = await db.Instances.Where(i => i.ServiceId == svc.Id).OrderBy(i => i.InstanceKey).ToListAsync();
            Assert.Equal(["i1", "i2", $"{host}:77"], instances.Select(i => i.InstanceKey));
            Assert.Equal([InstanceKeySource.Observed, InstanceKeySource.Observed, InstanceKeySource.Derived], instances.Select(i => i.KeySource));
            Assert.All(instances, i => Assert.Equal(environment.Id, i.EnvironmentId));
            Assert.All(instances, i => Assert.Equal(hostResource.Id, i.HostResourceId));
            Assert.All(instances, i => Assert.Equal(InstanceState.Active, i.State));
            Assert.Equal("1.1", instances[1].ServiceVersion);
        }

        /// <summary>
        /// Juice names the RabbitMQ exchange on a send span and the queue on a process span. They are two resources; the link
        /// between the services comes from the consumer span's parent, so it does not depend on them matching.
        /// </summary>
        [Fact]
        public async Task A_broker_message_links_services_when_exchange_and_queue_differ_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (orders, billing) = ($"disc-{_run}-sender", $"disc-{_run}-receiver");
            var (exchange, queue) = ($"x-{_run}", $"q-{_run}");
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            var (trace, send) = (Otlp.TraceId(), Otlp.SpanId());
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", orders)],
                Otlp.Span(trace, send, null, $"send {exchange}", Otlp.Producer, at, ("messaging.system", "rabbitmq"), ("messaging.destination.name", exchange))));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", billing)],
                Otlp.Span(trace, Otlp.SpanId(), send, $"process {queue}", Otlp.Consumer, at, ("messaging.system", "rabbitmq"), ("messaging.destination.name", queue))));
            await LocalStack.WaitForSpansAsync(billing, 1);
            await LocalStack.WaitForSpansAsync(orders, 1);

            await Runner(database).RunOnceAsync(TestContext.Current.CancellationToken);

            await using var db = database.Context();
            var ordersId = (await db.Services.SingleAsync(s => s.Name == orders)).Id;
            var billingId = (await db.Services.SingleAsync(s => s.Name == billing)).Id;
            var exchangeId = (await db.Resources.SingleAsync(r => r.Key == $"rabbitmq:{exchange}")).Id;
            var queueId = (await db.Resources.SingleAsync(r => r.Key == $"rabbitmq:{queue}")).Id;
            var dependencies = await db.Dependencies.Where(d => d.SourceServiceId == ordersId || d.SourceServiceId == billingId).ToListAsync();

            Assert.Contains(dependencies, d => d.SourceServiceId == ordersId && d.Relation == DependencyRelation.PublishesTo && d.TargetServiceId == billingId && d.Confidence == DependencyConfidence.Observed);
            Assert.Contains(dependencies, d => d.SourceServiceId == ordersId && d.Relation == DependencyRelation.PublishesTo && d.TargetResourceId == exchangeId);
            Assert.Contains(dependencies, d => d.SourceServiceId == billingId && d.Relation == DependencyRelation.ConsumesFrom && d.TargetResourceId == queueId);
        }

        [Fact]
        public async Task Dependencies_are_inferred_from_spans_with_their_confidence_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (orders, billing) = ($"disc-{_run}-orders", $"disc-{_run}-billing");
            var destination = $"orders-{_run}";
            var dbName = $"billing-{_run}";
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            var trace = Otlp.TraceId();
            var (call, publish) = (Otlp.SpanId(), Otlp.SpanId());
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", orders)],
                Otlp.Span(trace, call, null, "GET /billing", Otlp.Client, at),
                Otlp.Span(trace, publish, null, "send OrderPlaced", Otlp.Producer, at,
                    ("messaging.system", "rabbitmq"), ("messaging.destination.name", destination))));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", billing)],
                Otlp.Span(trace, Otlp.SpanId(), call, "GET /billing", Otlp.Server, at),
                Otlp.Span(trace, Otlp.SpanId(), publish, "process OrderPlaced", Otlp.Consumer, at,
                    ("messaging.system", "rabbitmq"), ("messaging.destination.name", destination)),
                Otlp.Span(trace, Otlp.SpanId(), null, "INSERT", Otlp.Client, at,
                    ("db.system.name", "microsoft.sql_server"), ("server.address", "sql"), ("db.namespace", dbName))));
            await LocalStack.WaitForSpansAsync(billing, 3);
            await LocalStack.WaitForSpansAsync(orders, 2);

            await Runner(database).RunOnceAsync(TestContext.Current.CancellationToken);

            await using var db = database.Context();
            var ordersId = (await db.Services.SingleAsync(s => s.Name == orders)).Id;
            var billingId = (await db.Services.SingleAsync(s => s.Name == billing)).Id;
            var queue = await db.Resources.SingleAsync(r => r.Type == ResourceType.MessagingDestination && r.Key == $"rabbitmq:{destination}");
            var sql = await db.Resources.SingleAsync(r => r.Type == ResourceType.Database && r.DisplayName == $"sql/{dbName}");
            var dependencies = await db.Dependencies.Where(d => d.SourceServiceId == ordersId || d.SourceServiceId == billingId).ToListAsync();

            void Has(Guid source, DependencyRelation relation, Guid target, DependencyConfidence confidence)
                => Assert.Single(dependencies, d => d.SourceServiceId == source && d.Relation == relation
                    && (d.TargetServiceId ?? d.TargetResourceId) == target && d.Confidence == confidence && d.SpanCount == 1);

            Has(ordersId, DependencyRelation.Calls, billingId, DependencyConfidence.Observed);
            Has(ordersId, DependencyRelation.PublishesTo, billingId, DependencyConfidence.Observed);
            Has(ordersId, DependencyRelation.PublishesTo, queue.Id, DependencyConfidence.Inferred);
            Has(billingId, DependencyRelation.ConsumesFrom, queue.Id, DependencyConfidence.Inferred);
            Has(billingId, DependencyRelation.UsesDatabase, sql.Id, DependencyConfidence.Inferred);
            Assert.Equal(5, dependencies.Count);
        }

        [Fact]
        public async Task Reapplying_telemetry_creates_no_duplicates_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"disc-{_run}-again";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "i1"), ("host.name", $"h-{_run}")],
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, DateTimeOffset.UtcNow.AddMinutes(-2))));
            await LocalStack.WaitForSpansAsync(service, 1);
            var runner = Runner(database);
            var ct = TestContext.Current.CancellationToken;

            await runner.RunOnceAsync(ct);
            var counts = await CountsAsync(database);
            var second = await runner.RunOnceAsync(ct);
            Assert.Equal(0, second.Totals.Services + second.Totals.Instances + second.Totals.Resources);

            // Reading the same window again (checkpoint lost) upserts by natural key: same records.
            await using (var db = database.Context())
            {
                await db.Checkpoints.ExecuteDeleteAsync(ct);
            }
            var replay = await runner.RunOnceAsync(ct);
            Assert.Equal(0, replay.Totals.Services + replay.Totals.Instances + replay.Totals.Resources + replay.Totals.Dependencies);
            Assert.Equal(counts, await CountsAsync(database));
        }

        [Fact]
        public async Task Telemetry_stored_after_its_window_was_read_is_found_without_counting_twice_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (orders, billing, late) = ($"disc-{_run}-orders", $"disc-{_run}-billing", $"disc-{_run}-late");
            var ct = TestContext.Current.CancellationToken;
            async Task CallAsync(string to, DateTimeOffset at)
            {
                var (trace, call) = (Otlp.TraceId(), Otlp.SpanId());
                await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", orders)], Otlp.Span(trace, call, null, "GET", Otlp.Client, at)));
                await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", to), ("service.instance.id", "i1")],
                    Otlp.Span(trace, Otlp.SpanId(), call, "GET", Otlp.Server, at)));
            }
            await CallAsync(billing, DateTimeOffset.UtcNow.AddMinutes(-2));
            await LocalStack.WaitForSpansAsync(billing, 1);
            var runner = Runner(database);
            await runner.RunOnceAsync(ct);

            // Stored after that pass, but timed before its checkpoint: a long span, or a backlog the Collector held.
            await CallAsync(late, DateTimeOffset.UtcNow.AddMinutes(-3));
            await CallAsync(billing, DateTimeOffset.UtcNow.AddMinutes(-3));
            await LocalStack.WaitForSpansAsync(late, 1);
            await LocalStack.WaitForSpansAsync(billing, 2);
            await runner.RunOnceAsync(ct);

            await using var db = database.Context();
            var ordersId = (await db.Services.SingleAsync(s => s.Name == orders, ct)).Id;
            var lateService = await db.Services.SingleAsync(s => s.Name == late, ct);
            Assert.Single(await db.Instances.Where(i => i.ServiceId == lateService.Id).ToListAsync(ct));
            var calls = await db.Dependencies.Where(d => d.SourceServiceId == ordersId).ToListAsync(ct);
            // A dependency first seen in the overlap counts its spans; a known one does not count the overlap again,
            // so its earlier span is not counted twice (and its late one, before the checkpoint, not at all).
            Assert.Equal(1, calls.Single(d => d.TargetServiceId == lateService.Id).SpanCount);
            Assert.Equal(1, calls.Single(d => d.TargetServiceId != lateService.Id).SpanCount);
        }

        [Fact]
        public async Task Restarted_service_keeps_one_identity_and_old_instances_go_inactive_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"disc-{_run}-restart";
            // Each process start reports a new service.instance.id (the .NET SDK default).
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "before")],
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, DateTimeOffset.UtcNow.AddMinutes(-20))));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "after")],
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 2);

            await Runner(database, o => o.InstanceInactiveAfter = TimeSpan.FromMinutes(15)).RunOnceAsync(TestContext.Current.CancellationToken);

            await using var db = database.Context();
            var svc = await db.Services.SingleAsync(s => s.Name == service);
            var states = await db.Instances.Where(i => i.ServiceId == svc.Id).ToDictionaryAsync(i => i.InstanceKey, i => i.State);
            Assert.Equal(InstanceState.Inactive, states["before"]);
            Assert.Equal(InstanceState.Active, states["after"]);
        }

        [Fact]
        public async Task Telemetry_of_another_tenant_is_never_read_and_forged_tenants_are_restamped_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var foreign = $"disc-{_run}-foreign";
            var forged = $"disc-{_run}-forged";
            // A row stored for another tenant (as a shared deployment would hold): discovery for this tenant must skip it.
            await LocalStack.ClickHouseAsync($"""
                INSERT INTO otel.otel_traces (Timestamp, TraceId, SpanId, ServiceName, SpanName, SpanKind, ResourceAttributes)
                VALUES (now64(9) - INTERVAL 2 MINUTE, '{Otlp.TraceId()}', '{Otlp.SpanId()}', '{foreign}', 'work', 'Internal',
                        map('juice.platform_tenant_id', 'other-tenant', 'service.instance.id', 'x'))
                """);
            // A client claiming another tenant: the Collector replaces it with the trusted one.
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", forged), ("juice.platform_tenant_id", "other-tenant")],
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "work", Otlp.Internal, DateTimeOffset.UtcNow.AddMinutes(-2))));
            await LocalStack.WaitForSpansAsync(foreign, 1);
            await LocalStack.WaitForSpansAsync(forged, 1);

            await Runner(database).RunOnceAsync(TestContext.Current.CancellationToken);

            await using var db = database.Context();
            Assert.False(await db.Services.AnyAsync(s => s.Name == foreign));
            Assert.True(await db.Services.AnyAsync(s => s.Name == forged));
            Assert.Equal(LocalStack.TenantKey, (await db.Tenants.SingleAsync()).Key);
        }

        private static async Task<(int, int, int, int, int)> CountsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return (await db.Services.CountAsync(), await db.Instances.CountAsync(), await db.Resources.CountAsync(),
                await db.Dependencies.CountAsync(), await db.Environments.CountAsync());
        }

        private static DiscoveryRunner Runner(ScratchDatabase database, Action<DiscoveryOptions>? configure = null)
        {
            var options = new DiscoveryOptions { Lag = TimeSpan.Zero, InitialLookback = TimeSpan.FromMinutes(30) };
            configure?.Invoke(options);
            var services = new ServiceCollection()
                .AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString))
                .BuildServiceProvider();
            return new DiscoveryRunner(services.GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })),
                Options.Create(options), Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }),
                TimeProvider.System, NullLogger<DiscoveryRunner>.Instance);
        }
    }
}
