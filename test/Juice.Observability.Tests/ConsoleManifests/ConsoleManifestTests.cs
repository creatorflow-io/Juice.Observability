using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Api;
using Juice.Observability.Api.Modules;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests.ConsoleManifests
{
    /// <summary>Console manifests of the core's own modules and the rules an external module's manifest is held to (core 2.0 M3 A). No database.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed class ConsoleManifestTests
    {
        private static readonly ModuleInfo Info = new("manifest-sample", "Manifest sample", "1.0.0", "[2.0.0,3.0.0)");
        private static readonly IReadOnlySet<string> NoPolicies = new HashSet<string>();
        private const string OwnPackage = "@juice-js/observability-manifest-sample";

        private static ConsoleManifest External(IEnumerable<ConsoleRoute>? routes = null, IEnumerable<ConsoleMenuEntry>? menu = null, string package = OwnPackage)
            => new(package, (routes ?? [new ConsoleRoute("things", null)]).ToList(), (menu ?? [new ConsoleMenuEntry("things", "Things", "things", "list", null, 10)]).ToList());

        private static readonly ConsoleManifest[] Internal = [InternalConsoleManifests.Core, InternalConsoleManifests.Slos, InternalConsoleManifests.Agents];

        // ─── packages ─────────────────────────────────────────────────────────

        [Fact]
        public void Internal_manifests_valid_core_library_package()
        {
            foreach (var manifest in Internal)
            {
                Assert.Equal("@juice-js/observability", manifest.Package);
            }
            Assert.Null(ModuleValidation.Manifest(InternalConsoleManifests.Core, new ModuleInfo("core", "Core", "2.0.0", "[2.0.0]"), NoPolicies, isInternal: true));
            Assert.Null(ModuleValidation.Manifest(InternalConsoleManifests.Slos, new ModuleInfo("slos", "Slos", "2.0.0", "[2.0.0]"), NoPolicies, isInternal: true));
            Assert.Null(ModuleValidation.Manifest(InternalConsoleManifests.Agents, new ModuleInfo("agents", "Agents", "2.0.0", "[2.0.0]"), NoPolicies, isInternal: true));
        }

        [Fact]
        public void External_package_own_name_unique_core_library_refused()
        {
            // The core's library is refused for an external module, by the validator and at composition; its own name is accepted.
            Assert.NotNull(ModuleValidation.Manifest(External(package: "@juice-js/observability"), Info, NoPolicies));
            Assert.Null(ModuleValidation.Manifest(External(), Info, NoPolicies));
            Assert.Contains("must be @juice-js/observability-manifest-sample", Refused<CorePackageModule>().Message);
            // A package is its module name, so two external modules can never share one.
            var builder = Core();
            builder.AddObservabilityModule<FirstModule>();
            Assert.Contains("must be @juice-js/observability-manifest-second", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<SamePackageModule>()).Message);
        }

        [Fact]
        public void Empty_route_internal_only()
        {
            var overview = External([new ConsoleRoute("", null)], [new ConsoleMenuEntry("home", "Home", "", null, null, 1)]);
            var internalInfo = new ModuleInfo("core", "Core", "2.0.0", "[2.0.0]");
            Assert.NotNull(ModuleValidation.Manifest(overview, Info, NoPolicies));
            Assert.NotNull(ModuleValidation.Manifest(External([new ConsoleRoute("", null)]), Info, NoPolicies));
            Assert.Null(ModuleValidation.Manifest(overview with { Package = "@juice-js/observability" }, internalInfo, NoPolicies, isInternal: true));
            Assert.Contains("must be relative", Refused<EmptyRouteModule>().Message);
        }

        // ─── groups ───────────────────────────────────────────────────────────

        [Fact]
        public void Menu_group_must_be_core_group()
        {
            Assert.Equal(["automation", "configure", "explore"], ConsoleMenuGroups.All.Order(StringComparer.Ordinal));
            foreach (var group in new string?[] { null, "explore", "automation", "configure" })
            {
                var entry = new ConsoleMenuEntry("things", "Things", "things", null, null, 1) { Group = group };
                Assert.Null(ModuleValidation.Manifest(External(menu: [entry]), Info, NoPolicies));
            }
            foreach (var group in new[] { "", "Explore", "mine", "observability-explore" })
            {
                var entry = new ConsoleMenuEntry("things", "Things", "things", null, null, 1) { Group = group };
                Assert.Contains("group", ModuleValidation.Manifest(External(menu: [entry]), Info, NoPolicies));
            }
            Assert.Contains("group", Refused<BadGroupModule>().Message);
        }

        [Fact]
        public void Modules_json_has_no_group_key_when_null()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var top = JsonSerializer.Serialize(new ConsoleMenuEntry("things", "Things", "things", null, null, 1), options);
            Assert.DoesNotContain("group", top);
            var grouped = JsonSerializer.Serialize(new ConsoleMenuEntry("things", "Things", "things", null, null, 1) { Group = "explore" }, options);
            Assert.Contains("\"group\":\"explore\"", grouped);
            // positional members are unchanged
            Assert.Equal(["key", "label", "route", "icon", "policy", "order"], JsonDocument.Parse(top).RootElement.EnumerateObject().Select(p => p.Name));
        }

        // ─── route collisions ─────────────────────────────────────────────────

        [Fact]
        public void Route_first_segment_collision_refused()
        {
            Assert.Contains("a core page uses", Refused<CoreSegmentModule>().Message);     // alerts/x
            Assert.Contains("a core page uses", Refused<SlosSegmentModule>().Message);     // slos
            Assert.Contains("a core page uses", Refused<AuthSegmentModule>().Message);     // auth: the sign-in callback
            // another module's name, and the first segment of another module's routes
            var builder = Core();
            builder.AddObservabilityModule<FirstModule>();
            Assert.Contains("which module 'manifest-first' uses", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<TakesFirstsSegmentModule>()).Message);
            Assert.Contains("name of another module", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<TakesModuleNameModule>()).Message);
            // a refusal leaves the host as it was: the module composes after the fix
            var services = builder.Services.Count;
            Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<TakesFirstsSegmentModule>());
            Assert.Equal(services, builder.Services.Count);
        }

        [Fact]
        public void Internal_manifests_do_not_collide_with_each_other()
        {
            // Internal against internal is pinned, not checked at composition: no first segment belongs to two of the three, and no key or order repeats.
            var owners = Internal.SelectMany((m, i) => m.Routes.Select(r => (Segment: InternalConsoleManifests.FirstSegment(r.Path), Owner: i)))
                .GroupBy(t => t.Segment).ToList();
            Assert.All(owners, g => Assert.Single(g.Select(t => t.Owner).Distinct()));
            var entries = Internal.SelectMany(m => m.Menu).ToList();
            Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
            Assert.Equal(entries.Count, entries.Select(e => e.Order).Distinct().Count());
            // every entry's route is one of its own manifest's routes
            foreach (var manifest in Internal)
            {
                Assert.All(manifest.Menu, e => Assert.Contains(e.Route, manifest.Routes.Select(r => r.Path)));
            }
        }

        // ─── the served answer ────────────────────────────────────────────────

        [Fact]
        public async Task Core_only_host_serves_the_internal_manifests()
        {
            await using var host = await ModuleTestHost.StartAsync();
            using var client = host.As("viewer", tenant: null);
            var response = await client.GetAsync("/api/v1/modules", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var items = (await response.Content.ReadFromJsonAsync<ModulesDto>(TestContext.Current.CancellationToken))!.Items;
            Assert.Equal(["core", "slos", "agents"], items.Select(i => i.Name));
            Assert.Equal(Internal.Select(m => m.Package), items.Select(i => i.Console!.Package));
        }

        [Fact]
        public async Task Builtin_manifests_json_equals_served_modules()
        {
            await using var host = await ModuleTestHost.StartAsync();
            using var client = host.As("viewer", tenant: null);
            var served = JsonNode.Parse(await client.GetStringAsync("/api/v1/modules", TestContext.Current.CancellationToken))!["items"]!.AsArray();
            // What the Console keeps: no version, which is the build's, so the file doesn't go stale at every release.
            var expected = new JsonArray(served.Select(item => (JsonNode)new JsonObject
            {
                ["name"] = item!["name"]!.DeepClone(), ["displayName"] = item["displayName"]!.DeepClone(), ["kind"] = item["kind"]!.DeepClone(), ["console"] = item["console"]!.DeepClone(),
            }).ToArray());
            var text = new JsonObject { ["items"] = expected }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
            var path = BuiltinPath();
            if (Environment.GetEnvironmentVariable("JUICE_WRITE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(text)),
                "builtin-manifests.json differs from the served /modules; rerun with JUICE_WRITE_SNAPSHOTS=1 to rewrite it.");
        }

        private static string BuiltinPath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "console", "projects", "juice-js", "observability", "src", "lib", "modules", "builtin-manifests.json"));

        // ─── test modules ─────────────────────────────────────────────────────

        private static WebApplicationBuilder Core()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        private static ModuleCompositionException Refused<T>() where T : IObservabilityModule, new()
            => Assert.Throws<ModuleCompositionException>(() => Core().AddObservabilityModule<T>());

        private abstract class ManifestModule(string name, ConsoleManifest manifest) : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new(name, name, "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.SetConsoleManifest(manifest);
        }

        private static ConsoleManifest Of(string name, params string[] routes)
            => new($"@juice-js/observability-{name}", routes.Select(r => new ConsoleRoute(r, null)).ToList(),
                routes.Select((r, i) => new ConsoleMenuEntry($"k{i}", "Entry", r, null, null, i)).ToList());

        private sealed class FirstModule() : ManifestModule("manifest-first", Of("manifest-first", "firsts", "firsts/:id"));
        private sealed class SamePackageModule() : ManifestModule("manifest-second", Of("manifest-first", "seconds"));
        private sealed class CorePackageModule() : ManifestModule("manifest-sample", External(package: "@juice-js/observability"));
        private sealed class EmptyRouteModule() : ManifestModule("manifest-sample", External([new ConsoleRoute("", null)], []));
        private sealed class BadGroupModule() : ManifestModule("manifest-sample", External(menu: [new ConsoleMenuEntry("things", "Things", "things", null, null, 1) { Group = "mine" }]));
        private sealed class CoreSegmentModule() : ManifestModule("manifest-core-seg", Of("manifest-core-seg", "alerts/x"));
        private sealed class SlosSegmentModule() : ManifestModule("manifest-slos-seg", Of("manifest-slos-seg", "slos"));
        private sealed class AuthSegmentModule() : ManifestModule("manifest-auth-seg", Of("manifest-auth-seg", "auth/callback"));
        private sealed class TakesFirstsSegmentModule() : ManifestModule("manifest-third", Of("manifest-third", "firsts/other"));
        private sealed class TakesModuleNameModule() : ManifestModule("manifest-name-seg", Of("manifest-name-seg", "manifest-first"));
    }
}
