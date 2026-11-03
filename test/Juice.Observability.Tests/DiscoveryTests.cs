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
            // A row stored for another tenant (as a shared deployment would hold): discovery for local-dev must skip it.
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
