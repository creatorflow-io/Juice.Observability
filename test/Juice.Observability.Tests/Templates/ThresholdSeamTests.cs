using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>Core 2.0 M2 slice A: the threshold seam. Thresholds reach the evaluator only through RuleThresholds; bad values are no data. No database.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ThresholdSeamTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly DateTimeOffset At = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Gives what it is told to, and counts how often it was asked.</summary>
        internal sealed class Provider(Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, RuleThreshold>> resolve) : IRuleThresholdProvider
        {
            public int Calls;
            public JsonElement? SeenSettings;
            public Func<RuleSpec, JsonElement?, IEnumerable<string>> Refuse { get; init; } = (_, _) => [];

            public string Key => "demo.p95";

            public IEnumerable<string> Validate(RuleSpec rule, JsonElement? settings) => Refuse(rule, settings);

            public ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, JsonElement? settings, IReadOnlyCollection<string> subjectKeys,
                DateTimeOffset at, CancellationToken ct)
            {
                Calls++;
                SeenSettings = settings;
                return ValueTask.FromResult(resolve(subjectKeys));
            }
        }

        internal static RuleThresholds Thresholds(Provider provider)
            => new(new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [provider], [new ModuleRuleContribution("demo", typeof(Provider))]));

        internal static RuleDefinition Rule(Action<RuleDefinition>? configure = null)
        {
            var rule = new RuleDefinition { Key = "err-rule", Name = "Errors", Template = RuleTemplate.ErrorRate, Threshold = 0.05, RecoveryThreshold = 0.02 };
            configure?.Invoke(rule);
            return rule;
        }

        private static RuleDefinition WithSource(Action<RuleDefinition>? configure = null, string json = """{"percentile":95}""")
            => Rule(r =>
            {
                r.ThresholdSource = new RuleThresholdSource { Provider = "demo.p95", Settings = JsonDocument.Parse(json).RootElement.Clone() };
                configure?.Invoke(r);
            });

        [Fact]
        public void Existing_rule_hash_unchanged_without_threshold_source()
        {
            // As the 1.4 release wrote them to the rules table, before ThresholdSource existed.
            string[] stored =
            [
                """{"Key":"k-rule","Name":"n","Template":"ErrorRate","Severity":"Warning","Service":"","Environment":"","Host":"","Schedule":"00:01:00","Window":"00:05:00","Threshold":0,"RecoveryThreshold":null,"MinimumSamples":10,"ForDuration":"00:02:00","RecoveryDuration":"00:02:00","Cooldown":"00:10:00","Enabled":true}""",
                """{"Key":"m-rule","Name":"M","Template":"MetricThreshold","Severity":"Critical","Service":"shop/orders","Environment":"prod","Host":"","Schedule":"00:00:30","Window":"00:05:00","Threshold":10,"RecoveryThreshold":8,"MinimumSamples":10,"ForDuration":"00:02:00","RecoveryDuration":"00:02:00","Cooldown":"00:10:00","Enabled":true,"Metric":{"Name":"queue.depth","Kind":"Gauge","Aggregation":"Max","Filters":[{"Key":"q","Op":"Equals","Values":["a"]}],"Subject":"Service","Comparison":"Above"}}""",
            ];

            foreach (var json in stored)
            {
                var rule = RuleDefinition.FromJson(json);

                Assert.Null(rule.ThresholdSource);
                Assert.Equal(json, rule.ToJson());
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))), rule.Hash());
                Assert.Empty(rule.Validate());
            }
        }

        [Fact]
        public void The_settings_limit_is_in_utf8_bytes_of_the_stored_json()
        {
            var known = new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [new Provider(_ => new Dictionary<string, RuleThreshold>())],
                [new ModuleRuleContribution("demo", typeof(Provider))]);
            // stored, an accented letter is escaped to 6 bytes: 600 are 3,600 bytes, and 2,100 are over the limit with far fewer than 4,096 characters.
            var fits = $$"""{"x":"{{new string('é', 600)}}"}""";
            var over = $$"""{"x":"{{new string('é', 2100)}}"}""";
            Assert.True(over.Length < RuleDefinition.MaxThresholdSettingsLength);

            Assert.Empty(WithSource(json: fits).Validate(null, known));
            Assert.Contains(WithSource(json: over).Validate(null, known), e => e.Contains("Settings is at most 4096 bytes", StringComparison.Ordinal));
        }

        [Fact]
        public void A_threshold_source_round_trips_and_changes_the_hash()
        {
            var rule = WithSource();

            var back = RuleDefinition.FromJson(rule.ToJson());

            Assert.Equal("demo.p95", back.ThresholdSource!.Provider);
            Assert.Equal(95, back.ThresholdSource.Settings!.Value.GetProperty("percentile").GetInt32());
            Assert.NotEqual(Rule().Hash(), rule.Hash());
            Assert.Equal(rule.Hash(), back.Hash());
        }

        [Fact]
        public void Classify_against_given_numbers_and_against_the_rules_own_agree_for_fixed()
        {
            var rule = Rule();
            foreach (var value in new double?[] { null, double.NaN, 0, 0.02, 0.03, 0.05, 0.06 })
            {
                Assert.Equal(AlertLifecycle.Classify(rule, value, 100), AlertLifecycle.Classify(rule, RuleThresholds.Fixed(rule), value, 100));
            }
            Assert.Equal(Condition.Breach, AlertLifecycle.Classify(rule, new RuleThreshold(0.5, 0.4), 0.6, 100));
            Assert.Equal(Condition.Band, AlertLifecycle.Classify(rule, new RuleThreshold(0.5, 0.4), 0.45, 100));
            Assert.Equal(Condition.Recovered, AlertLifecycle.Classify(rule, new RuleThreshold(0.5, 0.4), 0.4, 100));
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(rule, new RuleThreshold(0.5, 0.4), 0.6, 9));      // fewer samples than MinimumSamples
        }

        [Fact]
        public void Classify_below_fires_under_the_given_threshold()
        {
            var rule = new RuleDefinition { Key = "t", Name = "n", Template = RuleTemplate.ThroughputAbsent, Threshold = 5, RecoveryThreshold = 10 };

            Assert.Equal(Condition.Breach, AlertLifecycle.Classify(rule, new RuleThreshold(50, 60), 40, 0));
            Assert.Equal(Condition.Recovered, AlertLifecycle.Classify(rule, new RuleThreshold(50, 60), 61, 0));
        }

        [Fact]
        public async Task A_rule_without_a_source_has_its_own_numbers_for_every_subject_and_asks_nobody_Async()
        {
            var provider = new Provider(_ => throw new InvalidOperationException("never asked"));
            var thresholds = Thresholds(provider);

            var resolved = await thresholds.ResolveAsync(Rule(), ["a", "b"], At, Ct);

            Assert.Equal(new RuleThreshold(0.05, 0.02), resolved["a"]);
            Assert.Equal(new RuleThreshold(0.05, 0.02), resolved["b"]);
            Assert.Equal(0, provider.Calls);
        }

        [Fact]
        public async Task A_provider_gives_numbers_and_gets_the_settings_Async()
        {
            var provider = new Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.5, 0.4)));

            var resolved = await Thresholds(provider).ResolveAsync(WithSource(), ["a"], At, Ct);

            Assert.Equal(new RuleThreshold(0.5, 0.4), resolved["a"]);
            Assert.Equal(95, provider.SeenSettings!.Value.GetProperty("percentile").GetInt32());
        }

        [Fact]
        public async Task Provider_nan_or_inverted_is_nodata_Async()
        {
            var provider = new Provider(_ => new Dictionary<string, RuleThreshold>
            {
                ["ok"] = new(0.5, 0.4),
                ["nan"] = new(double.NaN, 0.4),
                ["inf"] = new(double.PositiveInfinity, 0.4),
                ["recovery-nan"] = new(0.5, double.NaN),
                ["inverted"] = new(0.5, 0.6),
                ["unasked"] = new(0.5, 0.4),
            });

            var resolved = await Thresholds(provider).ResolveAsync(WithSource(), ["ok", "nan", "inf", "recovery-nan", "inverted", "missing"], At, Ct);

            Assert.Equal(["ok"], resolved.Keys);
        }

        [Fact]
        public async Task For_a_rule_that_fires_below_inverted_means_recovery_under_the_threshold_Async()
        {
            var provider = new Provider(_ => new Dictionary<string, RuleThreshold> { ["good"] = new(50, 60), ["bad"] = new(50, 40) });
            var rule = WithSource(r => (r.Template, r.Threshold, r.RecoveryThreshold) = (RuleTemplate.ThroughputAbsent, 5, 10));

            var resolved = await Thresholds(provider).ResolveAsync(rule, ["good", "bad"], At, Ct);

            Assert.Equal(["good"], resolved.Keys);
        }

        [Fact]
        public async Task A_provider_that_throws_fails_the_resolution_Async()
        {
            var provider = new Provider(_ => throw new TimeoutException("boom"));

            await Assert.ThrowsAsync<TimeoutException>(async () => await Thresholds(provider).ResolveAsync(WithSource(), ["a"], At, Ct));
        }

        [Fact]
        public async Task A_provider_that_is_not_registered_fails_the_resolution_Async()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await RuleThresholds.Default.ResolveAsync(WithSource(), ["a"], At, Ct));
        }

        [Fact]
        public async Task The_fixed_provider_may_be_named_Async()
        {
            var rule = Rule(r => r.ThresholdSource = new RuleThresholdSource { Provider = "fixed" });

            var resolved = await RuleThresholds.Default.ResolveAsync(rule, ["a"], At, Ct);

            Assert.Equal(new RuleThreshold(0.05, 0.02), resolved["a"]);
            Assert.Empty(rule.Validate());
        }

        [Fact]
        public void Validate_refuses_unknown_provider_and_template_with_registry()
        {
            var known = new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [new Provider(_ => new Dictionary<string, RuleThreshold>())],
                [new ModuleRuleContribution("demo", typeof(Provider))]);

            Assert.Empty(WithSource().Validate(null, known));
            Assert.Contains(WithSource(r => r.Template = (RuleTemplate)999).Validate(null, known), e => e.Contains("unknown Template", StringComparison.Ordinal));
            Assert.Contains(WithSource().Validate(), e => e.Contains("'demo.p95' is not a known provider", StringComparison.Ordinal));
            Assert.Contains(WithSource(r => r.ThresholdSource!.Provider = "no spaces").Validate(null, known), e => e.Contains("must be 'fixed' or '<module>.<name>'", StringComparison.Ordinal));
            Assert.Contains(WithSource(r => r.ThresholdSource!.Provider = "").Validate(null, known), e => e.Contains("must be 'fixed' or '<module>.<name>'", StringComparison.Ordinal));
            Assert.Contains(WithSource(r => r.ThresholdSource!.Provider = "Fixed").Validate(null, known), e => e.Contains("must be 'fixed'", StringComparison.Ordinal));

            var big = $$"""{"x":"{{new string('a', RuleDefinition.MaxThresholdSettingsLength)}}"}""";
            Assert.Contains(WithSource(json: big).Validate(null, known), e => e.Contains("Settings is at most 4096", StringComparison.Ordinal));
            var edge = $$"""{"x":"{{new string('a', RuleDefinition.MaxThresholdSettingsLength - 8)}}"}""";
            Assert.Equal(RuleDefinition.MaxThresholdSettingsLength, System.Text.Encoding.UTF8.GetByteCount(JsonDocument.Parse(edge).RootElement.GetRawText()));
            Assert.Empty(WithSource(json: edge).Validate(null, known));
        }

        [Fact]
        public void The_provider_may_refuse_its_own_settings()
        {
            var provider = new Provider(_ => new Dictionary<string, RuleThreshold>()) { Refuse = (rule, s) => s is { } settings && settings.TryGetProperty("percentile", out var percentile) ? [] :["percentile is required"] };
            var known = new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [provider], [new ModuleRuleContribution("demo", typeof(Provider))]);

            Assert.Empty(WithSource().Validate(null, known));
            Assert.Contains(WithSource(json: "{}").Validate(null, known), e => e == "rule 'err-rule': ThresholdSource: percentile is required");
        }
    }
}
