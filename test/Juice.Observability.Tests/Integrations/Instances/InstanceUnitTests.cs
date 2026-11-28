using System.Text.Json;
using Juice.Observability.Api;
using Juice.Observability.Api.Integrations;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Integrations.Sample;
using Juice.Observability.Tests.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>Descriptor rules, config validation, secret references and scrubbing: no database.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class InstanceUnitTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class NotACollector : IIntegrationPlugin
        {
            public Task<ValidationReport> ValidateAsync(IntegrationContext context, CancellationToken ct) => Task.FromResult(new ValidationReport(true, []));
        }

        private static string? Check(IntegrationDescriptor d, Type? plugin = null, params string[] taken)
            => IntegrationPluginComposer.Check("sample", d, plugin ?? typeof(SampleJobsPlugin), taken);

        [Fact]
        public void Descriptor_rules()
        {
            var good = SampleJobsPlugin.Descriptor();
            Assert.Null(Check(good));
            // key prefix, name pattern, duplicate
            Assert.NotNull(Check(good with { Key = "other.jobs" }));
            Assert.NotNull(Check(good with { Key = "sample.Jobs" }));
            Assert.NotNull(Check(good with { Key = "sample." }));
            Assert.NotNull(Check(good, null, "sample.jobs"));
            // SemVer, capabilities
            Assert.NotNull(Check(good with { Version = "1.0" }));
            Assert.NotNull(Check(good with { Capabilities = new HashSet<IntegrationCapability>() }));
            Assert.NotNull(Check(good with { Capabilities = new HashSet<IntegrationCapability> { IntegrationCapability.Health, IntegrationCapability.Actions } }));
            // a collection capability needs a collector; the others don't
            Assert.NotNull(Check(good, typeof(NotACollector)));
            Assert.Null(Check(good with { Capabilities = new HashSet<IntegrationCapability> { IntegrationCapability.Health } }, typeof(NotACollector)));
            // fields and secrets
            Assert.NotNull(Check(good with { Config = good.Config with { Fields = [new("1bad", IntegrationFieldKind.String, "x")] } }));
            Assert.NotNull(Check(good with { Config = good.Config with { Fields = [new("a", IntegrationFieldKind.String, "x"), new("a", IntegrationFieldKind.String, "y")] } }));
            Assert.NotNull(Check(good with { Config = good.Config with { Fields = [.. Enumerable.Range(0, 51).Select(i => new IntegrationConfigField($"f{i}", IntegrationFieldKind.String, "x"))] } }));
            Assert.NotNull(Check(good with { Secrets = [.. Enumerable.Range(0, 11).Select(i => new IntegrationSecretSpec($"s{i}", "x"))] }));
            Assert.NotNull(Check(good with { Secrets = [new("bad name", "x")] }));
            // a pattern that doesn't compile
            Assert.NotNull(Check(good with { Config = good.Config with { Fields = [new("a", IntegrationFieldKind.String, "x", Pattern: "([")] } }));
            // intervals
            Assert.NotNull(Check(good with { MinInterval = TimeSpan.FromSeconds(30) }));
            Assert.NotNull(Check(good with { DefaultInterval = TimeSpan.FromSeconds(90), MinInterval = TimeSpan.FromMinutes(2) }));
            Assert.NotNull(Check(good with { DefaultInterval = TimeSpan.FromHours(25) }));
        }

        public sealed class BadKeyModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("badkey", "Bad key", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module)
                => module.AddIntegrationPlugin<SampleJobsPlugin>(SampleJobsPlugin.Descriptor() with { Key = "sample.jobs" });
        }

        [Fact]
        public void Descriptor_refusal_is_a_composition_exception_naming_the_module()
        {
            var e = Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b => b.AddObservabilityModule<BadKeyModule>()));
            Assert.Contains("badkey", e.Message);
        }

        [Fact]
        public void The_same_key_twice_is_refused()
        {
            Assert.Throws<ModuleCompositionException>(() => CoreHost.Build(b =>
            {
                b.AddObservabilityModule<SampleModule>();
                b.AddObservabilityModule<TwiceModule>();
            }));
        }

        public sealed class TwiceModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("twice", "Twice", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddIntegrationPlugin<SampleJobsPlugin>(SampleJobsPlugin.Descriptor());
        }

        private static Dictionary<string, List<string>> Validate(IntegrationConfigSchema schema, string json, int max = IntegrationConfigValidator.MaxConfigBytes)
            => IntegrationConfigValidator.Validate(schema, JsonDocument.Parse(json).RootElement, "config", max);

        [Fact]
        public void Config_validation_by_field_kind_unknown_property_and_size()
        {
            var schema = SampleJobsPlugin.Descriptor().Config;
            Assert.Empty(Validate(schema, """{"baseUrl":"https://x.example","batchSize":5,"mode":"fast","outcome":"Failed","ratio":0.5,"dryRun":true,"timeout":"00:05:00"}"""));
            Assert.Empty(Validate(schema, """{"baseUrl":"http://x","timeout":"PT5M"}"""));
            Assert.Contains("config.baseUrl", Validate(schema, "{}").Keys);                                  // required
            Assert.Contains("config.baseUrl", Validate(schema, """{"baseUrl":"ftp://x"}""").Keys);          // pattern
            Assert.Contains("config.baseUrl", Validate(schema, """{"baseUrl":5}""").Keys);                  // kind
            Assert.Contains("config.baseUrl", Validate(schema, $$"""{"baseUrl":"http://{{new string('a', 300)}}"}""").Keys);  // length
            Assert.Contains("config.batchSize", Validate(schema, """{"baseUrl":"http://x","batchSize":0}""").Keys);        // range
            Assert.Contains("config.batchSize", Validate(schema, """{"baseUrl":"http://x","batchSize":1.5}""").Keys);      // integer
            Assert.Contains("config.mode", Validate(schema, """{"baseUrl":"http://x","mode":"warp"}""").Keys);             // enum
            Assert.Contains("config.ratio", Validate(schema, """{"baseUrl":"http://x","ratio":2}""").Keys);
            Assert.Contains("config.dryRun", Validate(schema, """{"baseUrl":"http://x","dryRun":"yes"}""").Keys);
            Assert.Contains("config.timeout", Validate(schema, """{"baseUrl":"http://x","timeout":"soon"}""").Keys);
            Assert.Contains("config.timeout", Validate(schema, """{"baseUrl":"http://x","timeout":"01:00:00"}""").Keys);   // over Max seconds
            Assert.Contains("config.surprise", Validate(schema, """{"baseUrl":"http://x","surprise":1}""").Keys);          // unknown property
            Assert.Contains("config", Validate(schema, "[]").Keys);
            Assert.Contains("config", Validate(schema, $$"""{"baseUrl":"http://x","pad":"{{new string('a', 17000)}}"}""").Keys);  // size
            var scope = SampleJobsPlugin.Descriptor().Scope;
            Assert.Empty(IntegrationConfigValidator.Validate(scope, JsonDocument.Parse("""{"projects":["a","b"]}""").RootElement, "scope", 4096));
            Assert.Contains("scope.projects", IntegrationConfigValidator.Validate(scope, JsonDocument.Parse($"{{\"projects\":[{string.Join(',', Enumerable.Range(0, 11).Select(i => $"\"p{i}\""))}]}}").RootElement, "scope", 4096).Keys);
            Assert.Contains("scope", IntegrationConfigValidator.Validate(scope, JsonDocument.Parse($$"""{"projects":["{{new string('a', 5000)}}"]}""").RootElement, "scope", 4096).Keys);
        }

        [Fact]
        public void Secret_ref_outside_prefix_or_file_without_directory_refused()
        {
            var folder = Path.Combine(Path.GetTempPath(), "juice-secrets-" + Guid.NewGuid().ToString("N"));
            Assert.True(SecretReference.IsAllowedFromApi("env:JUICE_INTEGRATION_TOKEN", "JUICE_INTEGRATION_", null));
            Assert.False(SecretReference.IsAllowedFromApi("env:PATH", "JUICE_INTEGRATION_", null));
            Assert.False(SecretReference.IsAllowedFromApi("env:JUICE_INTEGRATION_", "JUICE_INTEGRATION_", null));
            Assert.False(SecretReference.IsAllowedFromApi("env:juice_integration_token", "JUICE_INTEGRATION_", null));
            Assert.False(SecretReference.IsAllowedFromApi("env:ConnectionStrings__Platform", "", null));
            Assert.False(SecretReference.IsAllowedFromApi("env:JUICE_INTEGRATION_A B", "JUICE_INTEGRATION_", null));
            // no directory configured: no file reference
            Assert.False(SecretReference.IsAllowedFromApi("file:" + Path.Combine(folder, "token"), "JUICE_INTEGRATION_", null));
            Assert.True(SecretReference.IsAllowedFromApi("file:" + Path.Combine(folder, "token"), "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi("file:" + Path.Combine(folder, "..", "other"), "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi("file:" + folder + "-sibling" + Path.DirectorySeparatorChar + "token", "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi("file:" + folder, "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi("file:relative/token", "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi("http://elsewhere", "JUICE_INTEGRATION_", folder));
            Assert.False(SecretReference.IsAllowedFromApi(null, "JUICE_INTEGRATION_", folder));
        }

        [Fact]
        public async Task Secrets_resolve_only_declared_names_exception_has_no_value()
        {
            const string variable = "JUICE_INTEGRATION_UNIT_TEST_VALUE";
            Environment.SetEnvironmentVariable(variable, "unit-secret-value-1");
            try
            {
                var options = new IntegrationsOptions();
                var secrets = new InstanceSecrets(new Dictionary<string, string> { ["token"] = "env:" + variable, ["other"] = "env:" + variable, ["missing"] = "env:JUICE_INTEGRATION_NOT_SET_ANYWHERE" },
                    new HashSet<string> { "token", "missing" }, options);
                Assert.Equal("unit-secret-value-1", await secrets.GetAsync("token", Ct));
                Assert.Equal(["unit-secret-value-1"], secrets.Values);
                // declared names only, even when a reference exists
                var undeclared = await Assert.ThrowsAsync<IntegrationSecretException>(async () => await secrets.GetAsync("other", Ct));
                Assert.Equal("other", undeclared.SecretName);
                var missing = await Assert.ThrowsAsync<IntegrationSecretException>(async () => await secrets.GetAsync("missing", Ct));
                Assert.DoesNotContain("unit-secret-value-1", missing.Message);
                Assert.Contains("missing", missing.Message);
                // a reference the policy no longer allows is not resolved either
                var strict = new InstanceSecrets(new Dictionary<string, string> { ["token"] = "env:" + variable }, new HashSet<string> { "token" }, new IntegrationsOptions { SecretEnvPrefix = "OTHER_" });
                await Assert.ThrowsAsync<IntegrationSecretException>(async () => await strict.GetAsync("token", Ct));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Fact]
        public void Scrubber_removes_every_secret_value()
        {
            string[] values = ["s3cr3t", "pa ss&word\"1"];
            Assert.Equal("a *** b *** c", IntegrationScrubber.Scrub("a s3cr3t b s3cr3t c", values));
            Assert.Equal("x *** y", IntegrationScrubber.Scrub($"x {Uri.EscapeDataString("pa ss&word\"1")} y", values));
            Assert.DoesNotContain("pa ss", IntegrationScrubber.Scrub(JsonSerializer.Serialize(new { v = "pa ss&word\"1" }), values));
            Assert.DoesNotContain("s3cr3t", IntegrationScrubber.Scrub(Convert.ToBase64String("s3cr3t"u8.ToArray()), values));
            Assert.Null(IntegrationScrubber.Scrub(null, values));
            Assert.Equal("kept", IntegrationScrubber.Scrub("kept", [""]));
            // capped at 500 after scrubbing: a cut can't leave the front of a value behind
            var cleaned = IntegrationScrubber.Clean(new string('a', 497) + "s3cr3t", values)!;
            Assert.Equal(500, cleaned.Length);
            Assert.DoesNotContain("s3c", cleaned);
        }

        [Fact]
        public void Hash_covers_plugin_version_config_scope_and_refs_and_ignores_key_order()
        {
            IntegrationInstance Make(string config = """{"a":1,"b":2}""", string scope = "{}", string refs = """{"token":"env:X"}""", int version = 1, string plugin = "sample.jobs")
                => new() { PluginKey = plugin, Config = config, Scope = scope, SecretRefs = refs, ConfigVersion = version };
            var baseline = IntegrationHash.Of(Make());
            Assert.Equal(baseline, IntegrationHash.Of(Make(config: """{ "b": 2.0, "a": 1 }""")));
            Assert.NotEqual(baseline, IntegrationHash.Of(Make(config: """{"a":1,"b":3}""")));
            Assert.NotEqual(baseline, IntegrationHash.Of(Make(scope: """{"p":1}""")));
            Assert.NotEqual(baseline, IntegrationHash.Of(Make(refs: """{"token":"env:Y"}""")));
            Assert.NotEqual(baseline, IntegrationHash.Of(Make(version: 2)));
            Assert.NotEqual(baseline, IntegrationHash.Of(Make(plugin: "sample.other")));
        }

        [Fact]
        public async Task Builds_and_composes_with_no_plugin()
        {
            using var app = CoreHost.Build();
            Assert.Empty(app.Services.GetRequiredService<IIntegrationPluginRegistry>().All);
            Assert.Contains("integrations", CoreHost.TenantRootSegments(app));
            await Task.CompletedTask;
        }

        [Fact]
        public void With_the_sample_module_the_registry_lists_the_plugin()
        {
            using var app = CoreHost.Build(b => b.AddObservabilityModule<SampleModule>());
            var registry = app.Services.GetRequiredService<IIntegrationPluginRegistry>();
            Assert.Equal(["sample.jobs"], registry.All.Select(d => d.Key));
            Assert.True(registry.TryGet("sample.jobs", out _));
            Assert.False(registry.TryGet("sample.none", out _));
        }
    }
}
