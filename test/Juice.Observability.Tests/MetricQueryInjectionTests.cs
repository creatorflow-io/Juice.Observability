using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>D5 for MetricThreshold: names, keys and values are refused by validation or matched literally, never read as SQL.</summary>
    public class MetricQueryInjectionTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public static readonly string[] Hostile =
        [
            "a'b", "a\\b", "a}b", "{x:String}", "') OR 1=1 --", "line\nbreak", "ünï-💥-日本", "\\'; DROP TABLE otel.otel_metrics_gauge; --", "{f0v:String}",
        ];

        [Fact]
        public void Hostile_names_and_keys_are_refused_by_validation()
        {
            foreach (var text in Hostile)
            {
                var rule = new RuleDefinition
                {
                    Key = "inj-rule", Name = "n", Template = RuleTemplate.MetricThreshold, Threshold = 1,
                    Metric = new MetricSpec { Name = text, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last },
                };
                Assert.Contains(rule.Validate(), e => e.Contains("Metric.Name"));
                rule.Metric.Name = "ok.metric";
                rule.Metric.Filters.Add(new MetricFilter { Key = text, Values = ["v"] });
                Assert.Contains(rule.Validate(), e => e.Contains("Key must match"));
            }
        }

        [Fact]
        public async Task Injection_inert_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"inj-{_run}", $"inj.{_run}.gauge");
            var clickHouse = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));
            var templates = new RuleTemplates(clickHouse);
            var window = new TelemetryWindow(LocalStack.TenantKey, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 100);

            // One series per hostile value (value i), plus a plain one (value 1000).
            for (var i = 0; i < Hostile.Length; i++)
                await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", service)], metric, [("q", Hostile[i])], (DateTimeOffset.UtcNow.AddMinutes(-1), i)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", service)], metric, [("q", "plain")], (DateTimeOffset.UtcNow.AddMinutes(-1), 1000)));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}'")) < Hostile.Length + 1; i++)
                await Task.Delay(200, Ct);

            async Task<IReadOnlyList<RuleObservation>> Observe(params MetricFilter[] filters)
            {
                var rule = new RuleDefinition
                {
                    Key = "inj-rule", Name = "n", Template = RuleTemplate.MetricThreshold, Threshold = 1, Service = service,
                    Metric = new MetricSpec { Name = metric, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Max, Filters = [.. filters] },
                };
                Assert.Empty(rule.Validate());
                return await templates.ObserveAsync(rule, window, Ct);   // no ClickHouse syntax error
            }

            for (var i = 0; i < Hostile.Length; i++)
            {
                var equals = Assert.Single(await Observe(new MetricFilter { Key = "q", Op = FilterOp.Equals, Values = [Hostile[i]] }));
                Assert.Equal((double)i, equals.Value);   // matched literally: only its own series
            }
            var any = Assert.Single(await Observe(new MetricFilter { Key = "q", Op = FilterOp.In, Values = [.. Hostile] }));
            Assert.Equal((double)(Hostile.Length - 1), any.Value);               // the plain series (1000) is not among them
            Assert.Empty(await Observe(new MetricFilter { Key = "q", Op = FilterOp.Equals, Values = ["' OR '1'='1"] }));
            Assert.True(int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}'")) >= Hostile.Length + 1);
        }
    }
}
