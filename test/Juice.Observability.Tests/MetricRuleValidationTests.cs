using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.Observability.Data;
using Juice.Observability.Rules;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 R1-R3: a MetricThreshold rule's definition, what it refuses, and that nothing an existing rule serialises changed.</summary>
    public class MetricRuleValidationTests
    {
        private static RuleDefinition Rule(Action<RuleDefinition>? configure = null, Action<MetricSpec>? metric = null)
        {
            var spec = new MetricSpec { Name = "sample.queue.depth", Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last };
            metric?.Invoke(spec);
            var rule = new RuleDefinition { Key = "queue-depth", Name = "Queue depth", Template = RuleTemplate.MetricThreshold, Threshold = 100, Metric = spec };
            configure?.Invoke(rule);
            return rule;
        }

        private static string Errors(RuleDefinition rule) => string.Join("\n", rule.Validate());

        [Fact]
        public void A_valid_metric_rule_has_no_errors() => Assert.Empty(Rule().Validate());

        // ─── compatibility ────────────────────────────────────────────────────

        public static TheoryData<RuleTemplate> OldTemplates() => new(
            RuleTemplate.ErrorRate, RuleTemplate.LatencyP95, RuleTemplate.ThroughputAbsent, RuleTemplate.DeliveryFailureRate,
            RuleTemplate.HostMemory, RuleTemplate.MediatorErrors, RuleTemplate.ConsumerFailures, RuleTemplate.SloBurn);

        /// <summary>The JSON each of the first 8 templates serialised to before Phase 12, written out here: the hash and the ETag are of these bytes.</summary>
        [Theory]
        [MemberData(nameof(OldTemplates))]
        public void Existing_definitions_hash_unchanged(RuleTemplate template)
        {
            var golden = "{\"Key\":\"golden-rule\",\"Name\":\"Golden\",\"Template\":\"" + template + "\",\"Severity\":\"Warning\",\"Service\":\"shop/orders\","
                + "\"Environment\":\"\",\"Host\":\"\",\"Schedule\":\"00:01:00\",\"Window\":\"00:05:00\",\"Threshold\":0.05,\"RecoveryThreshold\":0.02,"
                + "\"MinimumSamples\":10,\"ForDuration\":\"00:02:00\",\"RecoveryDuration\":\"00:02:00\",\"Cooldown\":\"00:10:00\",\"Enabled\":true}";
            var rule = new RuleDefinition
            {
                Key = "golden-rule", Name = "Golden", Template = template, Service = "shop/orders", Threshold = 0.05, RecoveryThreshold = 0.02,
            };
            Assert.Equal(golden, rule.ToJson());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(golden))), rule.Hash());
            Assert.Equal(golden, RuleDefinition.FromJson(golden).ToJson());

            // The API's body for the definition (RuleDto.Definition) gains no field either.
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            Assert.DoesNotContain("metric", JsonSerializer.Serialize(rule, web), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Template_values_are_appended_so_stored_names_still_read()
        {
            Assert.Equal(["ErrorRate", "LatencyP95", "ThroughputAbsent", "DeliveryFailureRate", "HostMemory", "MediatorErrors", "ConsumerFailures", "SloBurn",
                "MetricThreshold", "HostCpu", "HostDisk"], Enum.GetNames<RuleTemplate>());
            Assert.Equal(8, (int)RuleTemplate.MetricThreshold);
        }

        [Fact]
        public void A_metric_rule_round_trips_and_its_hash_follows_the_metric()
        {
            var rule = Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "queue", Op = FilterOp.In, Values = ["a", "b"] }));
            var json = rule.ToJson();
            Assert.Contains("\"Kind\":\"Gauge\"", json);
            Assert.Contains("\"Op\":\"In\"", json);
            Assert.Equal(json, RuleDefinition.FromJson(json).ToJson());
            Assert.NotEqual(rule.Hash(), Rule(metric: m => m.Aggregation = MetricAggregation.Max).Hash());
        }

        [Fact]
        public void Changing_the_metric_changes_what_a_rule_watches_a_threshold_does_not()
        {
            Assert.True(RuleProvisioner.ChangesWhatItWatches(Rule(), Rule(metric: m => m.Name = "other.metric")));
            Assert.True(RuleProvisioner.ChangesWhatItWatches(Rule(), Rule(metric: m => m.Aggregation = MetricAggregation.Avg)));
            Assert.True(RuleProvisioner.ChangesWhatItWatches(Rule(), Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Values = ["v"] }))));
            Assert.False(RuleProvisioner.ChangesWhatItWatches(Rule(), Rule(r => r.Threshold = 5)));
            Assert.False(RuleProvisioner.ChangesWhatItWatches(Rule(), Rule()));
        }

        // ─── Validate ─────────────────────────────────────────────────────────

        [Fact]
        public void Validate_missing_metric() => Assert.Contains("Metric is required", Errors(Rule(r => r.Metric = null)));

        [Fact]
        public void Validate_metric_on_another_template()
            => Assert.Contains("only MetricThreshold rules take Metric", Errors(Rule(r => r.Template = RuleTemplate.ErrorRate)));

        [Theory]
        [InlineData("")]
        [InlineData("1starts.with.digit")]
        [InlineData("has space")]
        [InlineData("quote'name")]
        [InlineData("brace{name}")]
        [InlineData("new\nline")]
        [InlineData("trailing\n")]
        public void Validate_bad_name(string name) => Assert.Contains("Metric.Name", Errors(Rule(metric: m => m.Name = name)));

        [Fact]
        public void Validate_name_length() => Assert.Contains("Metric.Name", Errors(Rule(metric: m => m.Name = new string('a', 256))));

        [Theory]
        [InlineData("")]
        [InlineData("9key")]
        [InlineData("has space")]
        [InlineData("a'b")]
        [InlineData("a}")]
        [InlineData("key\n")]
        public void Validate_bad_filter_key(string key)
            => Assert.Contains("Key must match", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = key, Values = ["v"] }))));

        [Fact]
        public void Validate_filter_key_length()
            => Assert.Contains("Key must match", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = new string('k', 129), Values = ["v"] }))));

        [Fact]
        public void Validate_value_length()
        {
            Assert.Contains("each value is 1 to 256", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Values = [""] }))));
            Assert.Contains("each value is 1 to 256", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Values = [new string('v', 257)] }))));
            Assert.Empty(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Values = [new string('v', 256)] })).Validate());
        }

        [Fact]
        public void Validate_more_than_5_filters()
        {
            var six = Enumerable.Range(0, 6).Select(i => new MetricFilter { Key = $"k{i}", Values = ["v"] }).ToList();
            Assert.Contains("at most 5 filters", Errors(Rule(metric: m => m.Filters = six)));
            Assert.Empty(Rule(metric: m => m.Filters = [.. six.Take(5)]).Validate());
        }

        [Fact]
        public void Validate_more_than_20_In_values()
        {
            string[] Values(int n) => [.. Enumerable.Range(0, n).Select(i => $"v{i}")];
            Assert.Contains("In takes 1 to 20", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Op = FilterOp.In, Values = [.. Values(21)] }))));
            Assert.Contains("In takes 1 to 20", Errors(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Op = FilterOp.In, Values = [] }))));
            Assert.Empty(Rule(metric: m => m.Filters.Add(new MetricFilter { Key = "k", Op = FilterOp.In, Values = [.. Values(20)] })).Validate());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void Validate_Equals_takes_exactly_one_value(int count)
            => Assert.Contains("exactly one value", Errors(Rule(metric: m => m.Filters.Add(
                new MetricFilter { Key = "k", Op = FilterOp.Equals, Values = [.. Enumerable.Range(0, count).Select(i => $"v{i}")] }))));

        [Fact]
        public void Validate_duplicate_key()
            => Assert.Contains("a key more than once", Errors(Rule(metric: m =>
            {
                m.Filters.Add(new MetricFilter { Key = "k", Values = ["a"] });
                m.Filters.Add(new MetricFilter { Key = "k", Op = FilterOp.In, Values = ["b"] });
            })));

        [Fact]
        public void Validate_kind_is_required_in_the_definition()
            => Assert.Contains("Metric.Kind is required", Errors(Rule(metric: m => m.Kind = null)));

        /// <summary>R3's table: every aggregation against every kind.</summary>
        [Fact]
        public void Validate_every_aggregation_against_every_kind()
        {
            var table = new Dictionary<MetricKind, MetricAggregation[]>
            {
                [MetricKind.Gauge] = [MetricAggregation.Last, MetricAggregation.Avg, MetricAggregation.Max, MetricAggregation.Min],
                [MetricKind.Counter] = [MetricAggregation.Rate, MetricAggregation.Increase],
                [MetricKind.UpDownCounter] = [MetricAggregation.Last],
                [MetricKind.Histogram] = [MetricAggregation.P50, MetricAggregation.P90, MetricAggregation.P95, MetricAggregation.P99, MetricAggregation.Avg, MetricAggregation.Rate],
            };
            foreach (var kind in Enum.GetValues<MetricKind>())
            {
                foreach (var aggregation in Enum.GetValues<MetricAggregation>())
                {
                    var errors = Errors(Rule(metric: m => (m.Kind, m.Aggregation) = (kind, aggregation)));
                    if (table[kind].Contains(aggregation)) Assert.True(errors.Length == 0, $"{kind} {aggregation}: {errors}");
                    else Assert.Contains($"Metric.Aggregation {aggregation} doesn't fit a {kind}", errors);
                }
            }
        }

        [Fact]
        public void Validate_scope_follows_the_subject()
        {
            Assert.Contains("host rules take Host, not Service", Errors(Rule(r => r.Service = "shop/orders", metric: m => m.Subject = MetricSubject.Host)));
            Assert.Contains("only host rules take Host", Errors(Rule(r => r.Host = "h1")));
            Assert.Empty(Rule(r => r.Host = "h1", metric: m => m.Subject = MetricSubject.Host).Validate());
            Assert.Empty(Rule(r => r.Service = "shop/orders").Validate());
        }

        [Fact]
        public void Validate_Below_with_a_recovery_under_the_threshold()
        {
            Assert.Contains("must not be below Threshold", Errors(Rule(r => (r.Threshold, r.RecoveryThreshold) = (10, 5), m => m.Comparison = Comparison.Below)));
            Assert.Empty(Rule(r => (r.Threshold, r.RecoveryThreshold) = (10, 20), m => m.Comparison = Comparison.Below).Validate());
            // Above keeps its rule: recovery not above the threshold.
            Assert.Contains("must not be above Threshold", Errors(Rule(r => (r.Threshold, r.RecoveryThreshold) = (10, 20))));
        }

        [Fact]
        public void Validate_thresholds_are_finite()
            => Assert.Contains("finite", Errors(Rule(r => r.Threshold = double.PositiveInfinity)));

        [Fact]
        public void Comparison_sets_which_side_fires_and_the_lifecycle_follows()
        {
            var above = Rule(r => (r.Threshold, r.RecoveryThreshold) = (10, 8));
            var below = Rule(r => (r.Threshold, r.RecoveryThreshold) = (10, 12), m => m.Comparison = Comparison.Below);
            Assert.True(above.FiresAbove);
            Assert.False(below.FiresAbove);
            Assert.Equal(Condition.Breach, AlertLifecycle.Classify(above, 11, 0));
            Assert.Equal(Condition.Recovered, AlertLifecycle.Classify(above, 8, 0));
            Assert.Equal(Condition.Breach, AlertLifecycle.Classify(below, 10, 0));
            Assert.Equal(Condition.Band, AlertLifecycle.Classify(below, 11, 0));
            Assert.Equal(Condition.Recovered, AlertLifecycle.Classify(below, 13, 0));
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(below, null, 0));
        }

        private static RuleDefinition FromBody(string metricJson)
            => RuleDefinition.FromJson("{\"Key\":\"queue-depth\",\"Name\":\"Q\",\"Template\":\"MetricThreshold\",\"Threshold\":1,\"Metric\":" + metricJson + "}");

        [Fact]
        public void Null_filters_are_read_and_stored_as_an_empty_list()
        {
            var rule = FromBody("{\"Name\":\"m.x\",\"Kind\":\"Gauge\",\"Aggregation\":\"Last\",\"Filters\":null}");
            Assert.Empty(rule.Validate());
            Assert.NotNull(rule.Metric!.Filters);
            Assert.Contains("\"Filters\":[]", rule.ToJson());
            // The query helpers don't throw on a list that was somehow null.
            var parameters = new Dictionary<string, object>();
            MetricFilters.AddParameters(null!, parameters);
            Assert.Equal("", MetricFilters.Sql(null!));
        }

        [Fact]
        public void An_undefined_op_with_null_values_is_an_error_not_an_exception()
        {
            var rule = FromBody("{\"Name\":\"m.x\",\"Kind\":\"Gauge\",\"Aggregation\":\"Last\",\"Filters\":[{\"Key\":\"k\",\"Op\":9,\"Values\":null}]}");
            Assert.Contains("Op is Equals or In", Errors(rule));
            var nullValues = FromBody("{\"Name\":\"m.x\",\"Kind\":\"Gauge\",\"Aggregation\":\"Last\",\"Filters\":[{\"Key\":\"k\",\"Op\":\"In\",\"Values\":null}]}");
            Assert.Contains("In takes 1 to 20", Errors(nullValues));
        }

        [Fact]
        public void Only_histogram_percentiles_and_average_use_the_minimum_samples()
        {
            Assert.False(Rule().UsesSamples);
            Assert.False(Rule(metric: m => (m.Kind, m.Aggregation) = (MetricKind.Counter, MetricAggregation.Rate)).UsesSamples);
            Assert.False(Rule(metric: m => (m.Kind, m.Aggregation) = (MetricKind.Histogram, MetricAggregation.Rate)).UsesSamples);
            Assert.True(Rule(metric: m => (m.Kind, m.Aggregation) = (MetricKind.Histogram, MetricAggregation.P95)).UsesSamples);
            Assert.True(Rule(metric: m => (m.Kind, m.Aggregation) = (MetricKind.Histogram, MetricAggregation.Avg)).UsesSamples);
        }

        [Fact]
        public void Host_rules_are_host_scoped_and_take_ratios()
        {
            Assert.True(new RuleDefinition { Template = RuleTemplate.HostCpu }.IsHostRule);
            Assert.True(new RuleDefinition { Template = RuleTemplate.HostDisk }.IsHostRule);
            Assert.True(new RuleDefinition { Template = RuleTemplate.HostMemory }.IsHostRule);
            Assert.False(new RuleDefinition { Template = RuleTemplate.ErrorRate }.IsHostRule);
            Assert.Contains("ratios from 0 to 1", Errors(new RuleDefinition { Key = "cpu-rule", Name = "n", Template = RuleTemplate.HostCpu, Threshold = 90 }));
            Assert.Empty(new RuleDefinition { Key = "cpu-rule", Name = "n", Template = RuleTemplate.HostCpu, Threshold = 0.9, Host = "h1" }.Validate());
        }
    }
}
