using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// MetricThreshold over explicit-bucket histograms against the local stack: the per-bucket increase (cumulative and delta, with resets
    /// and first points), percentiles, Avg and Rate, the sample count, mixed layouts, and what is never read.
    /// </summary>
    public class HistogramRuleTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private const string Metric = "test.latency";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A window of ten minutes that ended ten minutes ago; points are placed relative to its start.
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private static readonly DateTimeOffset From = new DateTimeOffset(Now.Year, Now.Month, Now.Day, Now.Hour, Now.Minute, 0, TimeSpan.Zero).AddMinutes(-20);
        private static DateTimeOffset At(int minutes) => From.AddMinutes(minutes);
        private static TelemetryWindow Window(string tenant = LocalStack.TenantKey) => new(tenant, From, From.AddMinutes(10), 100);

        private static readonly RuleTemplates Templates = new(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })));

        private static RuleDefinition Rule(string service, MetricAggregation aggregation, params MetricFilter[] filters) => new()
        {
            Key = "h", Name = "h", Template = RuleTemplate.MetricThreshold, Service = service, MinimumSamples = 10,
            Metric = new MetricSpec { Name = Metric, Kind = MetricKind.Histogram, Aggregation = aggregation, Filters = [.. filters] },
        };

        private static (string, string)[] Resource(string service) => [("service.name", service)];

        private static async Task WaitAsync(string table, string service, int rows)
        {
            for (var i = 0; i < 100; i++)
            {
                if (int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.{table} WHERE ServiceName = '{service}'")) >= rows) return;
                await Task.Delay(200, Ct);
            }
            Assert.Fail($"{rows} rows of {service} did not reach {table}");
        }

        private static async Task<RuleObservation> ObserveAsync(RuleDefinition rule)
            => Assert.Single(await Templates.ObserveAsync(rule, Window(), Ct));

        private static readonly double[] Bounds = [1, 2];

        [Fact]
        public async Task Histogram_percentiles_from_cumulative_bucket_increase_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-cum";
            // Before the window [5,5,0]; at its end [10,25,5]: the window's increase is [5,20,5].
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 5), (At(2), [10, 15, 5], 20), (At(8), [10, 25, 5], 30)));
            await WaitAsync("otel_metrics_histogram", service, 3);

            var p50 = await ObserveAsync(Rule(service, MetricAggregation.P50));
            Assert.Equal(1.5, p50.Value!.Value, 9);      // rank 15 of 30: 10 of the 20 in (1, 2]
            Assert.Equal(30, p50.Samples);
            var p99 = await ObserveAsync(Rule(service, MetricAggregation.P99));
            Assert.Equal("2", p99.Detail!["bucketLower"]);   // rank 30 of 30 is in the overflow bucket
            Assert.Equal("+Inf", p99.Detail["bucketUpper"]);
            Assert.Equal(2, p99.Value!.Value);
        }

        [Fact]
        public async Task Histogram_reset_and_first_point_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-reset";
            // One series from before the window grows by 1; it restarts (a new start time) at 2 inside it, counting from zero, never negative.
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 5), (At(2), [6, 5, 0], 6)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(4), [], Bounds,
                (At(5), [2, 0, 0], 1)));
            await WaitAsync("otel_metrics_histogram", service, 3);

            var p50 = await ObserveAsync(Rule(service, MetricAggregation.P50));
            Assert.Equal(3, p50.Samples);                       // 1 + 2, not 1 + (2 - 6)
            Assert.Equal(2d / 3, p50.Value!.Value, 9);          // rank 2 of 3 in (0, 1]
        }

        [Fact]
        public async Task Histogram_delta_summed_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-delta";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(-5), [100, 0, 0], 100), (At(1), [1, 1, 1], 4), (At(3), [0, 2, 0], 3)));
            await WaitAsync("otel_metrics_histogram", service, 3);

            var p50 = await ObserveAsync(Rule(service, MetricAggregation.P50));
            Assert.Equal(5, p50.Samples);                       // the point before the window isn't counted
            Assert.Equal(1 + 2d / 3, p50.Value!.Value, 9);      // [1,3,1]: rank 3 is the 2nd of 3 in (1, 2]
        }

        [Fact]
        public async Task Histogram_avg_and_rate_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-avg";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 10), (At(8), [10, 25, 5], 40)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            Assert.Equal(1.0, (await ObserveAsync(Rule(service, MetricAggregation.Avg))).Value!.Value, 9);   // 30 of sum over 30 observations
            var rate = await ObserveAsync(Rule(service, MetricAggregation.Rate));
            Assert.Equal(30d / 600, rate.Value!.Value, 9);                                                  // observations per second
        }

        [Fact]
        public async Task Histogram_below_min_samples_is_no_data_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-few";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [0, 0, 0], 0), (At(8), [3, 2, 0], 6)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            var rule = Rule(service, MetricAggregation.P95);
            var observed = await ObserveAsync(rule);
            Assert.Equal(5, observed.Samples);
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(rule, observed.Value, observed.Samples));
            // Rate doesn't use the sample count.
            var rateRule = Rule(service, MetricAggregation.Rate);
            var rate = await ObserveAsync(rateRule);
            Assert.NotEqual(Condition.NoData, AlertLifecycle.Classify(rateRule, rate.Value, rate.Samples));
        }

        [Fact]
        public async Task Histogram_with_no_observations_in_window_is_no_value_not_zero_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-idle";
            // Reporting, but nothing observed: Avg and the percentile have no value; Rate is a true zero.
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 10), (At(8), [5, 5, 0], 10)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            Assert.Null((await ObserveAsync(Rule(service, MetricAggregation.P95))).Value);
            Assert.Null((await ObserveAsync(Rule(service, MetricAggregation.Avg))).Value);
            Assert.Equal(0, (await ObserveAsync(Rule(service, MetricAggregation.Rate))).Value);

            // A series with no point in the window isn't observed at all.
            var gone = $"hist-{_run}-gone";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(gone), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 10)));
            await WaitAsync("otel_metrics_histogram", gone, 1);
            Assert.Empty(await Templates.ObserveAsync(Rule(gone, MetricAggregation.Rate), Window(), Ct));
        }

        [Fact]
        public async Task Mixed_bounds_uses_largest_layout_flagged_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-mixed";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [("pod", "a")], Bounds,
                (At(2), [10, 20, 0], 30)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [("pod", "b")], [10, 20],
                (At(2), [0, 0, 5], 100)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            var p50 = await ObserveAsync(Rule(service, MetricAggregation.P50));
            Assert.Equal("true", p50.Detail!["mixedBounds"]);
            Assert.Equal(30, p50.Samples);
            Assert.Equal("1", p50.Detail["bucketLower"]);
            Assert.Equal("2", p50.Detail["bucketUpper"]);
            // Avg and Rate count every layout.
            Assert.Equal(35d / 600, (await ObserveAsync(Rule(service, MetricAggregation.Rate))).Value!.Value, 9);
        }

        [Fact]
        public async Task Filters_select_series_and_stay_literal_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-filter";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [("route", "/a")], Bounds,
                (At(2), [10, 0, 0], 5)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [("route", "/b")], Bounds,
                (At(2), [0, 20, 0], 30)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            var a = await ObserveAsync(Rule(service, MetricAggregation.Rate, new MetricFilter { Key = "route", Op = FilterOp.Equals, Values = ["/a"] }));
            Assert.Equal(10d / 600, a.Value!.Value, 9);
            var both = await ObserveAsync(Rule(service, MetricAggregation.Rate, new MetricFilter { Key = "route", Op = FilterOp.In, Values = ["/a", "/b"] }));
            Assert.Equal(30d / 600, both.Value!.Value, 9);
            // Quotes, braces and SQL are values, nothing more.
            Assert.Empty(await Templates.ObserveAsync(Rule(service, MetricAggregation.Rate,
                new MetricFilter { Key = "route", Op = FilterOp.Equals, Values = ["') OR 1=1 --"] }), Window(), Ct));
            Assert.Empty(await Templates.ObserveAsync(Rule(service, MetricAggregation.Rate,
                new MetricFilter { Key = "ro'ute}", Op = FilterOp.In, Values = ["{x:String}", "\\"] }), Window(), Ct));
        }

        [Fact]
        public async Task Histogram_without_sum_has_no_avg_and_negative_sums_agree_Async()
        {
            LocalStack.RequireAvailable();
            // No sum (stored as 0) behind 30 observations: the percentile is there, the average is not.
            var nosum = $"hist-{_run}-nosum";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(nosum), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], 0), (At(8), [10, 25, 5], 0)));
            // Negative sums: cumulative -5 to -35, and deltas -10 and -20, are both -30 over 30 observations.
            var cumulative = $"hist-{_run}-negcum";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(cumulative), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [5, 5, 0], -5), (At(8), [10, 25, 5], -35)));
            var delta = $"hist-{_run}-negdelta";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(delta), Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(1), [5, 10, 0], -10), (At(5), [5, 10, 0], -20)));
            foreach (var s in new[] { nosum, cumulative, delta }) await WaitAsync("otel_metrics_histogram", s, 2);

            Assert.Null((await ObserveAsync(Rule(nosum, MetricAggregation.Avg))).Value);
            Assert.NotNull((await ObserveAsync(Rule(nosum, MetricAggregation.P50))).Value);
            Assert.Equal(-1, (await ObserveAsync(Rule(cumulative, MetricAggregation.Avg))).Value!.Value, 9);
            Assert.Equal(-1, (await ObserveAsync(Rule(delta, MetricAggregation.Avg))).Value!.Value, 9);
        }

        [Fact]
        public async Task Histogram_evidence_carries_the_unit_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-unit";
            await LocalStack.SendAsync("metrics", OtlpMetrics.WithUnit(OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(2), [10, 20, 0], 30)), "ms"));
            await WaitAsync("otel_metrics_histogram", service, 1);

            foreach (var aggregation in new[] { MetricAggregation.P50, MetricAggregation.Avg, MetricAggregation.Rate })
            {
                Assert.Equal("ms", (await ObserveAsync(Rule(service, aggregation))).Detail!["unit"]);
            }
        }

        [Fact]
        public async Task Row_limit_never_cuts_a_subject_s_layouts_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-limit";
            (string, string)[] Env(string env) => [("service.name", service), ("deployment.environment.name", env)];
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Env("dev"), Metric, OtlpMetrics.Delta, At(-30), [], Bounds, (At(2), [10, 20, 0], 30)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Env("prod"), Metric, OtlpMetrics.Delta, At(-30), [("pod", "a")], Bounds, (At(2), [10, 20, 0], 30)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Env("prod"), Metric, OtlpMetrics.Delta, At(-30), [("pod", "b")], [10, 20], (At(2), [1, 0, 0], 1)));
            await WaitAsync("otel_metrics_histogram", service, 3);

            var rule = Rule(service, MetricAggregation.Rate);
            Assert.Equal(2, (await Templates.ObserveAsync(rule, Window(), Ct)).Count);
            // Three rows (dev, prod x2) with a limit of 2: prod is cut mid-subject, so only dev is reported.
            var cut = await Templates.ObserveAsync(rule, Window() with { Limit = 2 }, Ct);
            Assert.EndsWith("@dev", Assert.Single(cut).SubjectKey);
            // Rows that just fit are not a cut: both subjects are reported, the last one whole.
            var exact = await Templates.ObserveAsync(rule, Window() with { Limit = 3 }, Ct);
            Assert.Equal(2, exact.Count);
            Assert.Equal(31d / 600, exact.Single(o => o.SubjectKey.EndsWith("@prod")).Value!.Value, 9);
        }

        [Fact]
        public async Task One_subject_s_layouts_are_capped_so_it_cannot_fill_the_limit_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-layouts";
            var layouts = RuleTemplates.MaxHistogramLayoutsPerSubject + 2;
            for (var i = 1; i <= layouts; i++)
            {
                // Layout i has i observations, so the two least observed are the ones left out.
                await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [("l", i.ToString())],
                    [10 * i, 10 * i + 1], (At(2), [i, 0, 0], i)));
            }
            await WaitAsync("otel_metrics_histogram", service, layouts);

            // A limit of the cap holds the subject; without the cap its 10 rows would exceed it and the subject would be dropped.
            var observed = Assert.Single(await Templates.ObserveAsync(Rule(service, MetricAggregation.Rate),
                Window() with { Limit = RuleTemplates.MaxHistogramLayoutsPerSubject }, Ct));
            Assert.Equal(Enumerable.Range(3, layouts - 2).Sum() / 600d, observed.Value!.Value, 9);

            // A percentile says its layouts were cut.
            var p50 = Assert.Single(await Templates.ObserveAsync(Rule(service, MetricAggregation.P50),
                Window() with { Limit = RuleTemplates.MaxHistogramLayoutsPerSubject }, Ct));
            Assert.Equal("true", p50.Detail!["mixedBounds"]);
            Assert.Equal("2", p50.Detail["layoutsOmitted"]);
        }

        [Fact]
        public async Task Sum_of_zero_is_missing_only_when_a_bucket_beyond_zero_holds_observations_Async()
        {
            LocalStack.RequireAvailable();
            // Everything in the first bucket (which holds 0): a sum of 0 is a real average of 0.
            var zeros = $"hist-{_run}-zeros";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(zeros), Metric, OtlpMetrics.Delta, At(-30), [], Bounds, (At(2), [10, 0, 0], 0)));
            // Observations in (1, 2] can't sum to 0: the sum is missing.
            var missing = $"hist-{_run}-missing";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(missing), Metric, OtlpMetrics.Delta, At(-30), [], Bounds, (At(2), [0, 10, 0], 0)));
            // One series reports its sum, another doesn't: the average is the first one's alone (20 over 10, not 20 over 40).
            var mixed = $"hist-{_run}-sums";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(mixed), Metric, OtlpMetrics.Delta, At(-30), [("s", "a")], Bounds, (At(2), [0, 10, 0], 20)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(mixed), Metric, OtlpMetrics.Delta, At(-30), [("s", "b")], Bounds, (At(2), [0, 30, 0], 0)));
            await WaitAsync("otel_metrics_histogram", zeros, 1);
            await WaitAsync("otel_metrics_histogram", missing, 1);
            await WaitAsync("otel_metrics_histogram", mixed, 2);

            Assert.Equal(0, (await ObserveAsync(Rule(zeros, MetricAggregation.Avg))).Value);
            Assert.Null((await ObserveAsync(Rule(missing, MetricAggregation.Avg))).Value);
            Assert.Equal(2, (await ObserveAsync(Rule(mixed, MetricAggregation.Avg))).Value!.Value, 9);
            Assert.Equal(40d / 600, (await ObserveAsync(Rule(mixed, MetricAggregation.Rate))).Value!.Value, 9);
        }

        [Fact]
        public async Task Cumulative_reset_under_the_same_start_counts_from_zero_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-samestart";
            // 40 observations and a sum of 100 before the window; inside it the counts fall (a restart that kept its start time) and grow to 8.
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Cumulative, At(-30), [], Bounds,
                (At(-5), [10, 25, 5], 100), (At(2), [3, 0, 0], 1.5), (At(6), [6, 2, 0], 5)));
            await WaitAsync("otel_metrics_histogram", service, 3);

            var avg = await ObserveAsync(Rule(service, MetricAggregation.Avg));
            Assert.Equal(5d / 8, avg.Value!.Value, 9);                       // never negative
            Assert.Equal(8, (await ObserveAsync(Rule(service, MetricAggregation.P50))).Samples);
        }

        [Fact]
        public async Task Subject_host_groups_by_host_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-onhost";
            var (one, two) = ($"hist-{_run}-h1", $"hist-{_run}-h2");
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram([("service.name", service), ("host.name", one)], Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(2), [10, 20, 0], 30)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram([("service.name", service), ("host.name", two)], Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(2), [5, 0, 0], 3)));
            await WaitAsync("otel_metrics_histogram", service, 2);

            var rule = Rule(service, MetricAggregation.Rate);
            rule.Metric!.Subject = MetricSubject.Host;
            rule.Service = "";
            rule.Host = one;
            var observed = Assert.Single(await Templates.ObserveAsync(rule, Window(), Ct));
            Assert.Equal($"host:{one}", observed.SubjectKey);
            Assert.Equal(HealthSubject.Host, observed.SubjectType);
            Assert.Equal(30d / 600, observed.Value!.Value, 9);

            rule.Metric.Aggregation = MetricAggregation.P50;
            rule.Host = two;
            var p50 = Assert.Single(await Templates.ObserveAsync(rule, Window(), Ct));
            Assert.Equal($"host:{two}", p50.SubjectKey);
            Assert.Equal(5, p50.Samples);
        }

        [Fact]
        public async Task Exponential_histogram_never_read_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-exp";
            JsonNode Nano(DateTimeOffset t) => (t.ToUnixTimeMilliseconds() * 1_000_000).ToString();
            var body = new JsonObject
            {
                ["resourceMetrics"] = new JsonArray(new JsonObject
                {
                    ["resource"] = new JsonObject { ["attributes"] = new JsonArray(new JsonObject { ["key"] = "service.name", ["value"] = new JsonObject { ["stringValue"] = service } }) },
                    ["scopeMetrics"] = new JsonArray(new JsonObject
                    {
                        ["scope"] = new JsonObject { ["name"] = "Juice.Observability.Tests" },
                        ["metrics"] = new JsonArray(new JsonObject
                        {
                            ["name"] = Metric,
                            ["exponentialHistogram"] = new JsonObject
                            {
                                ["aggregationTemporality"] = 1,
                                ["dataPoints"] = new JsonArray(new JsonObject
                                {
                                    ["startTimeUnixNano"] = Nano(At(-30)), ["timeUnixNano"] = Nano(At(2)), ["count"] = "20", ["sum"] = 10.0, ["scale"] = 0,
                                    ["positive"] = new JsonObject { ["offset"] = 0, ["bucketCounts"] = new JsonArray("10", "10") },
                                }),
                            },
                        }),
                    }),
                }),
            };
            await LocalStack.SendAsync("metrics", body);
            await WaitAsync("otel_metrics_exponential_histogram", service, 1);

            foreach (var aggregation in new[] { MetricAggregation.P95, MetricAggregation.Avg, MetricAggregation.Rate })
            {
                Assert.Empty(await Templates.ObserveAsync(Rule(service, aggregation), Window(), Ct));
            }
        }

        [Fact]
        public async Task Other_tenant_histogram_never_observed_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-tenant";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(2), [10, 20, 0], 30)));
            await WaitAsync("otel_metrics_histogram", service, 1);

            Assert.Single(await Templates.ObserveAsync(Rule(service, MetricAggregation.P50), Window(), Ct));
            Assert.Empty(await Templates.ObserveAsync(Rule(service, MetricAggregation.P50), Window("0193f0aa-0000-7000-8000-00000000dead"), Ct));
        }

        [Fact]
        public async Task Evidence_has_bucket_bounds_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"hist-{_run}-evidence";
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram(Resource(service), Metric, OtlpMetrics.Delta, At(-30), [], Bounds,
                (At(2), [10, 20, 0], 30)));
            await WaitAsync("otel_metrics_histogram", service, 1);

            var p90 = await ObserveAsync(Rule(service, MetricAggregation.P90));
            Assert.Equal("1", p90.Detail!["bucketLower"]);
            Assert.Equal("2", p90.Detail["bucketUpper"]);
            Assert.DoesNotContain("mixedBounds", p90.Detail.Keys);
        }

        [Fact]
        public void Histogram_query_holds_no_rule_text()
        {
            // The SQL is built from platform constants and parameter placeholders only.
            var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Juice.Observability.Rules", "RuleTemplates.Histogram.cs")));
            Assert.DoesNotContain("{rule.", source);
            Assert.DoesNotContain("{spec.", source);
        }
    }
}
