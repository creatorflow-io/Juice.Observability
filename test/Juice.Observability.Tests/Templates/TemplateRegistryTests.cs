using System.Data.Common;
using System.Text.Json;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>Core 2.0 M2 slice A: the rule template registry, with the built-ins on it, and what a module may add to it. No database.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class TemplateRegistryTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static ClickHouseTelemetrySource ClickHouse() => new(Options.Create(new ClickHouseOptions { ConnectionString = "Host=127.0.0.1;Port=1" }));

        public sealed class DemoTemplate : IRuleTemplate
        {
            public static string Key = "demo.split";

            public RuleTemplateInfo Info => new(Key, "Demo split", TemplateSubjectKind.Service);

            public Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<TemplateObservation>>([new("service:a@prod", AlertSubjectKind.Service, "", "a", "prod", "", 1, 5)]);
        }

        public sealed class DemoProvider : IRuleThresholdProvider
        {
            public string Key => "demo.p95";

            public ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, JsonElement? settings, IReadOnlyCollection<string> subjectKeys,
                DateTimeOffset at, CancellationToken ct) => ValueTask.FromResult<IReadOnlyDictionary<string, RuleThreshold>>(new Dictionary<string, RuleThreshold>());
        }

        public sealed class DemoModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("demo", "Demo", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddRuleTemplate<DemoTemplate>().AddThresholdProvider<DemoProvider>();
        }

        [Fact]
        public void The_registry_holds_every_built_in_template_and_no_slo_burn()
        {
            var registry = new RuleTemplates(ClickHouse()).Registry;

            var expected = Enum.GetValues<RuleTemplate>().Where(t => t != RuleTemplate.SloBurn).Select(t => t.ToString()).ToList();
            Assert.Equal(10, expected.Count);
            Assert.Equal(expected.Order(StringComparer.Ordinal), registry.All.Select(i => i.Key).Order(StringComparer.Ordinal));
            Assert.False(registry.TryGet("SloBurn", out _));
            Assert.All(expected, key => Assert.True(registry.TryGet(key, out var template) && template.Info.Key == key));
        }

        [Fact]
        public void The_built_ins_say_what_they_watch_and_which_way_they_fire()
        {
            var all = new RuleTemplates(ClickHouse()).Registry.All.ToDictionary(i => i.Key);

            Assert.False(all["ThroughputAbsent"].FiresAbove);
            Assert.All(all.Values.Where(i => i.Key != "ThroughputAbsent"), i => Assert.True(i.FiresAbove));
            Assert.Equal(TemplateSubjectKind.Host, all["HostMemory"].Subject);
            Assert.Equal(TemplateSubjectKind.Host, all["HostCpu"].Subject);
            Assert.Equal(TemplateSubjectKind.Host, all["HostDisk"].Subject);
            Assert.Equal(TemplateSubjectKind.Either, all["MetricThreshold"].Subject);
            Assert.Equal(TemplateSubjectKind.Service, all["ErrorRate"].Subject);
            Assert.True(all["ErrorRate"].UsesSamples);
            Assert.False(all["MediatorErrors"].UsesSamples);
        }

        [Fact]
        public void Validation_through_the_registry_keeps_the_messages()
        {
            var registry = new RuleTemplates(ClickHouse()).Registry;
            var rule = new RuleDefinition { Key = "mem-rule", Name = "n", Template = RuleTemplate.HostMemory, Threshold = 90 };

            Assert.Contains(rule.Validate(null, registry), e => e == "rule 'mem-rule': Threshold and RecoveryThreshold are ratios from 0 to 1");
            Assert.Equal(rule.Validate(), rule.Validate(null, registry));
            rule.Template = RuleTemplate.ErrorRate;
            rule.Threshold = 0.1;
            rule.Metric = new MetricSpec { Name = "m", Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last };
            Assert.Contains(rule.Validate(null, registry), e => e == "rule 'mem-rule': only MetricThreshold rules take Metric");
            Assert.Contains(new RuleDefinition { Key = "lat-rule", Name = "n", Template = RuleTemplate.LatencyP95 }.Validate(null, registry),
                e => e.EndsWith("latency thresholds are positive milliseconds", StringComparison.Ordinal));
            Assert.Contains(new RuleDefinition { Key = "slo-rule", Name = "n", Template = RuleTemplate.SloBurn }.Validate(null, registry),
                e => e.Contains("SloBurn rules are the platform's own", StringComparison.Ordinal));
        }

        [Fact]
        public void A_module_template_is_registered_listed_and_found_after_the_built_ins()
        {
            var registry = new RuleTemplateRegistry(BuiltIns(), [new DemoTemplate()],
                [new DemoProvider()], [new ModuleRuleContribution("demo", typeof(DemoTemplate)), new ModuleRuleContribution("demo", typeof(DemoProvider))]);

            Assert.Equal("demo.split", registry.All[^1].Key);
            Assert.Equal(11, registry.All.Count);
            Assert.True(registry.TryGet("demo.split", out var template));
            Assert.IsType<DemoTemplate>(template);
            Assert.True(registry.TryGetProvider("demo.p95", out _));
            Assert.True(registry.TryGetProvider("fixed", out _));
        }

        [Fact]
        public void A_module_template_outside_its_prefix_or_a_built_in_name_or_twice_refuses_the_host()
        {
            RuleTemplateRegistry Build(string key, string module = "demo", IRuleTemplate[]? more = null)
            {
                DemoTemplate.Key = key;
                try
                {
                    return new RuleTemplateRegistry(BuiltIns(), [new DemoTemplate(), .. more ?? []], null, [new ModuleRuleContribution(module, typeof(DemoTemplate))]);
                }
                finally
                {
                    DemoTemplate.Key = "demo.split";
                }
            }

            Assert.Contains("must be 'demo.<name>'", Assert.Throws<ModuleCompositionException>(() => Build("other.split")).Message);
            Assert.Contains("must be 'demo.<name>'", Assert.Throws<ModuleCompositionException>(() => Build("ErrorRate")).Message);
            Assert.Contains("must be 'demo.<name>'", Assert.Throws<ModuleCompositionException>(() => Build("demo.Upper")).Message);
            Assert.Contains("must be 'demo.<name>'", Assert.Throws<ModuleCompositionException>(() => Build("demo.")).Message);
            Assert.Contains("already taken", Assert.Throws<ModuleCompositionException>(() => Build("demo.split", more: [new DemoTemplate()])).Message);
            Assert.Contains("was not registered by a module", Assert.Throws<ModuleCompositionException>(
                () => new RuleTemplateRegistry(BuiltIns(), [new DemoTemplate()], null, [])).Message);
        }

        [Fact]
        public void A_module_provider_outside_its_prefix_or_twice_refuses_the_host()
        {
            Assert.Contains("must be 'other.<name>'", Assert.Throws<ModuleCompositionException>(
                () => new RuleTemplateRegistry(BuiltIns(), null, [new DemoProvider()], [new ModuleRuleContribution("other", typeof(DemoProvider))])).Message);
            Assert.Throws<ModuleCompositionException>(() => new RuleTemplateRegistry(BuiltIns(), null, [new DemoProvider(), new DemoProvider()],
                [new ModuleRuleContribution("demo", typeof(DemoProvider))]));
        }

        [Fact]
        public void AddRuleTemplate_refuses_a_duplicate_or_abstract_type_or_a_wrong_key_when_the_module_is_composed()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            builder.AddObservabilityModule<DemoModule>();
            using var app = builder.Build();
            var registry = app.Services.GetRequiredService<IRuleTemplateRegistry>();

            Assert.True(registry.TryGet("demo.split", out _));
            Assert.Equal(11, registry.All.Count);
            Assert.Same(registry, app.Services.GetRequiredService<RuleTemplates>().Registry);

            Assert.Contains("more than once", Assert.Throws<ModuleCompositionException>(() => CoreBuilder().AddObservabilityModule<TwiceModule>()).Message);
            Assert.Contains("concrete class", Assert.Throws<ModuleCompositionException>(() => CoreBuilder().AddObservabilityModule<AbstractModule>()).Message);
            Assert.Contains("must be 'wrong.<name>'", Assert.Throws<ModuleCompositionException>(() => CoreBuilder().AddObservabilityModule<WrongKeyModule>()).Message);
        }

        private static WebApplicationBuilder CoreBuilder()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        public sealed class TwiceModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("demo", "Twice", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddRuleTemplate<DemoTemplate>().AddRuleTemplate<DemoTemplate>();
        }

        public abstract class AbstractTemplate : IRuleTemplate
        {
            public abstract RuleTemplateInfo Info { get; }

            public abstract Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct);
        }

        public sealed class AbstractModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("abstracted", "Abstract", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddRuleTemplate<AbstractTemplate>();
        }

        public sealed class WrongKeyModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("wrong", "Wrong", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddRuleTemplate<DemoTemplate>();
        }

        [Fact]
        public async Task A_template_reaches_telemetry_only_with_the_tenant_window_and_limit_placeholders_Async()
        {
            var telemetry = new TemplateTelemetry(ClickHouse(), new TelemetryWindow("tenant", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 10));
            var none = new Dictionary<string, object>();
            DbDataReader Unused(DbDataReader r) => r;

            var full = $"SELECT 1 FROM otel.otel_traces WHERE {TemplateSql.Tenant} AND Timestamp >= {TemplateSql.From} AND Timestamp < {TemplateSql.To} LIMIT {TemplateSql.Limit}";
            foreach (var missing in new[] { TemplateSql.Tenant, TemplateSql.From, TemplateSql.To, TemplateSql.Limit })
            {
                var e = await Assert.ThrowsAsync<ArgumentException>(() => telemetry.QueryAsync(full.Replace(missing, "1"), none, Unused, Ct));
                Assert.Contains(missing, e.Message);
            }
            foreach (var name in new[] { "tenant", "from", "to", "limit", "Tenant", "LIMIT" })
            {
                var e = await Assert.ThrowsAsync<ArgumentException>(() => telemetry.QueryAsync(full, new Dictionary<string, object> { [name] = "x" }, Unused, Ct));
                Assert.Contains("the host's", e.Message);
            }
        }

        [Fact]
        public async Task The_host_only_runs_a_built_in_through_its_own_telemetry_Async()
        {
            var registry = new RuleTemplates(ClickHouse()).Registry;
            Assert.True(registry.TryGet("ErrorRate", out var template));
            var spec = new RuleDefinition { Key = "err-rule", Name = "n", Template = RuleTemplate.ErrorRate, Threshold = 0.1 }.ToSpec();
            var window = new TemplateWindow(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 10);

            await Assert.ThrowsAsync<InvalidOperationException>(() => template.ObserveAsync(spec, new ForeignTelemetry(), window, Ct));
        }

        private sealed class ForeignTelemetry : ITemplateTelemetry
        {
            public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map, CancellationToken ct)
                => throw new NotSupportedException();
        }

        private static RuleTemplates Core() => new(ClickHouse());

        private static IEnumerable<IRuleTemplate> BuiltIns()
        {
            var registry = Core().Registry;
            return registry.All.Select(i => registry.TryGet(i.Key, out var t) ? t : throw new InvalidOperationException()).ToList();
        }
    }
}
