using System.Net;
using System.Text.Json;
using Juice.Observability.Api;
using Juice.Observability.Api.Modules;
using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>Composition of modules: what is refused, and that a refusal leaves the host as it was. No database.</summary>
    [Trait("Milestone", "C2-M1")]
    public sealed class ModuleCompositionTests
    {
        private static WebApplicationBuilder Core()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        private static ModuleCompositionException Refused<T>(WebApplicationBuilder? builder = null) where T : IObservabilityModule, new()
            => Assert.Throws<ModuleCompositionException>(() => (builder ?? Core()).AddObservabilityModule<T>());

        [Fact]
        public void Version_out_of_range_refused()
        {
            var e = Refused<OutOfRangeModule>();
            Assert.Contains("future", e.Message);
            Assert.Contains("[9.0.0,10.0.0)", e.Message);
            Assert.Contains(CoreVersion.Current.ToString(), e.Message);
        }

        [Fact]
        public void Unparseable_range_refused() => Assert.Contains("odd", Refused<UnparseableRangeModule>().Message);

        [Fact]
        public void Duplicate_name_refused()
        {
            var builder = Core();
            builder.AddObservabilityModule<TestModule>();
            Assert.Contains("sample", Refused<TestModule>(builder).Message);
            Assert.Contains("slos", Refused<SlosNamedModule>().Message);
        }

        [Fact]
        public void Reserved_segment_refused()
        {
            Assert.Contains("alerts", Refused<AlertsModule>().Message);
            Assert.Contains("telemetry", Refused<TelemetryModule>().Message);
        }

        [Fact]
        public void Every_real_tenant_root_segment_is_refused_as_a_module_name()
        {
            using var app = CoreHost.Build();
            foreach (var segment in CoreHost.TenantRootSegments(app))
            {
                NamedModule.Name = segment;
                Assert.Contains(segment, Refused<NamedModule>().Message);
            }
        }

        [Fact]
        public void AddModule_before_AddJuiceObservability_refused()
        {
            var builder = CoreHost.CreateBuilder();
            Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<TestModule>());
        }

        [Fact]
        public void Policy_collision_with_core_refused()
        {
            // Named platform, a module would pass the prefix rule for platform.read: the name is refused first, and the policy check stands behind it.
            Assert.Contains("platform", Refused<PlatformPolicyModule>().Message);
            var builder = Core();
            builder.AddObservabilityModule<TestModule>();
            Assert.Contains("sample.write", Refused<PolicyCollisionModule>(builder).Message);
        }

        [Fact]
        public void A_refused_module_leaves_no_services_behind()
        {
            var builder = Core();
            var before = builder.Services.Count;
            Refused<HalfwayModule>(builder);
            Assert.Equal(before, builder.Services.Count);
            Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(HalfwayMarker));
            // And the name is free again.
            builder.AddObservabilityModule<TestModule>();
        }

        [Fact]
        public void CoreRouteSegments_matches_real_tenant_root_segments()
        {
            using var app = CoreHost.Build();
            Assert.Equal(CoreHost.TenantRootSegments(app).Order(), CoreRouteSegments.All.Order());
            // And the contract's reserved names are a floor over them: every real segment is refused as a module name.
            Assert.All(CoreRouteSegments.All, s => Assert.NotNull(ModuleValidation.Name(s)));
        }

        [Fact]
        public void Module_composes_before_workers_and_after_the_core_hosted_services()
        {
            var builder = Core();
            var core = CoreHost.HostedServices(builder.Services);
            builder.AddObservabilityModule<TestModule>();
            var all = CoreHost.HostedServices(builder.Services);
            Assert.Equal(core, all.Take(core.Count));
            // The runner is registered by the integrations module, among the core's own.
            Assert.Equal([typeof(SampleWorker).FullName], all.Skip(core.Count));
        }

        [Fact]
        public async Task A_module_endpoint_that_allows_anonymous_is_refused_before_any_hosted_service_starts()
        {
            var builder = Core();
            builder.AddObservabilityModule<AnonymousModule>();
            builder.Services.AddHostedService<StartRecorder>();
            StartRecorder.Started = false;
            using var app = builder.Build();
            // Refused while the routes are mapped, so the host never gets to start.
            var e = Assert.Throws<ModuleCompositionException>(() => app.MapJuiceObservability());
            Assert.Contains("anon", e.Message);
            Assert.False(StartRecorder.Started);
            await Task.CompletedTask;
        }

        private sealed class StartRecorder : IHostedService
        {
            public static volatile bool Started;

            public Task StartAsync(CancellationToken cancellationToken)
            {
                Started = true;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Fact]
        public void A_refused_module_restores_the_descriptors_it_replaced_by_reference_and_order()
        {
            var builder = Core();
            var before = builder.Services.ToList();
            var original = Assert.Single(before, d => d.ServiceType == typeof(HeartbeatWriter));
            Refused<ReplacingModule>(builder);
            Assert.Equal(before.Count, builder.Services.Count);
            for (var i = 0; i < before.Count; i++)
            {
                Assert.Same(before[i], builder.Services[i]);
            }
            Assert.Same(original, Assert.Single(builder.Services, d => d.ServiceType == typeof(HeartbeatWriter)));
        }

        [Fact]
        public void Composition_core_routes_unchanged_by_a_module_except_under_its_name()
        {
            using var app = CoreHost.Build(b => b.AddObservabilityModule<TestModule>());
            var routes = CoreHost.Routes(app);
            var prefix = " " + CoreHost.TenantPrefix + "sample/";
            Assert.All(routes.Where(r => r.Contains(prefix)), r => Assert.Contains("platform.read", r.Split(" | ")[1]));
            Assert.Equal(5, routes.Count(r => r.Contains(prefix)));
            var rest = routes.Where(r => !r.Contains(prefix) && !r.Contains(" /api/v1/modules |"));
            CoreHost.AssertSnapshot("core-routes.snapshot.txt", rest);
        }

        // ─── /api/v1/modules ──────────────────────────────────────────────────

        [Fact]
        public async Task Modules_lists_core_slos_agents_then_external_in_order()
        {
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<TestModule>());
            using var client = host.As("viewer");
            var response = await client.GetAsync("/api/v1/modules", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
            Assert.Equal(CoreVersion.Current.ToString(), json.GetProperty("coreVersion").GetString());
            var items = json.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["core", "slos", "agents", "integrations", "sample"], items.Select(i => i.GetProperty("name").GetString()));
            Assert.Equal(["core", "module", "module", "module", "module"], items.Select(i => i.GetProperty("kind").GetString()));
            Assert.Equal("[2.0.0,3.0.0)", items[4].GetProperty("supportedCore").GetString());
        }

        [Fact]
        public async Task Modules_needs_authentication()
        {
            await using var host = await ModuleTestHost.StartAsync();
            using var client = host.Client;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/modules", TestContext.Current.CancellationToken)).StatusCode);
        }

        [Fact]
        public async Task Modules_answers_with_database_down()
        {
            // The connection string names a port nothing listens on (CoreHost): no database access is made.
            await using var host = await ModuleTestHost.StartAsync();
            using var client = host.As("viewer", tenant: null);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/modules", TestContext.Current.CancellationToken)).StatusCode);
        }
    }
}
