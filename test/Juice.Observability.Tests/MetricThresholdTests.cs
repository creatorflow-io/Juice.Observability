using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 R1-R4, R6 for gauges and sums, against the stack: exact values, tenancy, no data, the lifecycle.</summary>
    public class MetricThresholdTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static RuleTemplates Templates()
            => new(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })));

        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        private static TelemetryWindow Window(int limit = 100) => new(LocalStack.TenantKey, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, limit);

        private static DateTimeOffset Ago(double minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes);

        private static RuleDefinition Rule(string metric, MetricKind kind, MetricAggregation aggregation, string service = "", Action<RuleDefinition>? configure = null,
            Action<MetricSpec>? spec = null)
        {
            var metricSpec = new MetricSpec { Name = metric, Kind = kind, Aggregation = aggregation };
            spec?.Invoke(metricSpec);
            var rule = new RuleDefinition { Key = $"metric-{Guid.NewGuid():N}"[..20], Name = "Metric", Template = RuleTemplate.MetricThreshold, Service = service, Threshold = 10, Metric = metricSpec };
            configure?.Invoke(rule);
            Assert.Empty(rule.Validate());
            return rule;
        }

        private static async Task SendAsync(JsonObject payload, string table, string metric, int rows)
        {
            var before = int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_{table} WHERE MetricName = '{metric}'"));
            await LocalStack.SendAsync("metrics", payload);
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_{table} WHERE MetricName = '{metric}'")) < before + rows; i++)
            {
                await Task.Delay(200, Ct);
            }
        }

        private static (string, string)[] Res(string service, string? host = null) => host is null ? [("service.name", service)] : [("service.name", service), ("host.name", host)];

        private static async Task<RuleObservation> ObserveOneAsync(RuleDefinition rule)
            => Assert.Single(await Templates().ObserveAsync(rule, Window(), Ct));

        // ─── gauges and sums ──────────────────────────────────────────────────

        [Fact]
        public async Task Gauge_last_avg_max_min_exact_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-gauge", $"mt.{_run}.gauge");
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [("q", "a")], (Ago(4), 10), (Ago(3), 30), (Ago(1), 20)), "gauge", metric, 3);
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [("q", "b")], (Ago(2), 40)), "gauge", metric, 1);

            Assert.Equal(30, (await ObserveOneAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Last, service))).Value);   // mean of 20 and 40
            Assert.Equal(25, (await ObserveOneAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Avg, service))).Value);
            Assert.Equal(40, (await ObserveOneAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Max, service))).Value);
            var min = await ObserveOneAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Min, service));
            Assert.Equal((10d, 4L), (min.Value, min.Samples));
            Assert.Equal($"service:{service}@unspecified", min.SubjectKey);
        }

        [Fact]
        public async Task UpDownCounter_last_sums_series_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-updown", $"mt.{_run}.updown");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, false, Ago(20), [("pool", "a")], (Ago(4), 3), (Ago(1), 5)), "sum", metric, 2);
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, false, Ago(20), [("pool", "b")], (Ago(2), 7)), "sum", metric, 1);

            Assert.Equal(12, (await ObserveOneAsync(Rule(metric, MetricKind.UpDownCounter, MetricAggregation.Last, service))).Value);
            // The kind picks the table rows: a monotonic reading of an up-down counter observes nothing.
            Assert.Empty(await Templates().ObserveAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service), Window(), Ct));
        }

        [Fact]
        public async Task UpDownCounter_delta_temporality_sums_the_deltas_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-updowndelta", $"mt.{_run}.updowndelta");
            // Changes of +4, then -1 and +4 again in the window: net 7, not the last delta (4).
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Delta, false, Ago(20), [], (Ago(4), 4), (Ago(3), -1), (Ago(1), 4)), "sum", metric, 3);
            Assert.Equal(7, (await ObserveOneAsync(Rule(metric, MetricKind.UpDownCounter, MetricAggregation.Last, service))).Value);
        }

        [Fact]
        public async Task Counter_rate_and_increase_exact_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-counter", $"mt.{_run}.counter");
            // 60 more than before the window; a second series whose first point (10) counts from zero.
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(20), [("w", "a")], (Ago(7), 100), (Ago(3), 130), (Ago(1), 160)), "sum", metric, 3);
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(2), [("w", "b")], (Ago(1), 10)), "sum", metric, 1);

            Assert.Equal(70, (await ObserveOneAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service))).Value);
            var window = Window();
            var rate = Assert.Single(await Templates().ObserveAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Rate, service), window, Ct));
            Assert.Equal(70 / (window.To - window.From).TotalSeconds, rate.Value!.Value, 6);
        }

        [Fact]
        public async Task Counter_first_point_counts_from_zero_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-first", $"mt.{_run}.first");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(2), [], (Ago(1), 9)), "sum", metric, 1);
            Assert.Equal(9, (await ObserveOneAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service))).Value);
        }

        /// <summary>A start time long before the window (a process or boot start) with no point before it: the increase is from its first point in the window, not the lifetime total.</summary>
        [Fact]
        public async Task Counter_started_long_before_the_window_with_no_earlier_point_counts_from_its_first_point_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-oldstart", $"mt.{_run}.oldstart");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(600), [], (Ago(3), 4000), (Ago(2), 4010), (Ago(1), 4015)), "sum", metric, 3);
            Assert.Equal(15, (await ObserveOneAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service))).Value);
        }

        /// <summary>One point in the window of a series that began before it, with none earlier, has nothing to measure from: no data, not an increase of 0.</summary>
        [Fact]
        public async Task Counter_old_series_with_a_single_point_in_the_window_is_no_data_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-single", $"mt.{_run}.single");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(600), [], (Ago(1), 4000)), "sum", metric, 1);
            Assert.Empty(await Templates().ObserveAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service), Window(), Ct));
        }

        [Fact]
        public async Task Counter_reset_is_restart_not_negative_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-reset", $"mt.{_run}.reset");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(30), [], (Ago(7), 100), (Ago(4), 150)), "sum", metric, 2);
            // The process restarted: a new start time, the value begins again.
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(3.5), [], (Ago(3), 5), (Ago(1), 12)), "sum", metric, 2);
            Assert.Equal(62, (await ObserveOneAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service))).Value);   // 50 + 12
        }

        [Fact]
        public async Task Counter_delta_temporality_summed_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-delta", $"mt.{_run}.delta");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Delta, true, Ago(20), [], (Ago(7), 100), (Ago(3), 4), (Ago(1), 6)), "sum", metric, 3);
            Assert.Equal(10, (await ObserveOneAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service))).Value);
        }

        [Fact]
        public async Task Series_without_point_in_window_is_no_data_never_zero_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, metric) = ($"mt-{_run}-gone", $"mt.{_run}.gone");
            await SendAsync(OtlpMetrics.Sum(Res(service), metric, OtlpMetrics.Cumulative, true, Ago(30), [], (Ago(10), 50)), "sum", metric, 1);
            Assert.Empty(await Templates().ObserveAsync(Rule(metric, MetricKind.Counter, MetricAggregation.Increase, service), Window(), Ct));

            // An open alert is held as no data: it neither resolves nor clears.
            var gauge = $"mt.{_run}.held";
            var clock = new Clock(DateTimeOffset.UtcNow);
            var rule = await AddRuleAsync(database, Rule(gauge, MetricKind.Gauge, MetricAggregation.Last, service, r => (r.ForDuration, r.RecoveryDuration) = (TimeSpan.Zero, TimeSpan.Zero)));
            await SendAsync(OtlpMetrics.Gauge(Res(service), gauge, [], (Ago(1), 99)), "gauge", gauge, 1);
            var evaluator = PlatformServices.Evaluator(database, clock);
            await evaluator.EvaluateAsync(rule, Ct);
            await evaluator.EvaluateAsync(rule, Ct);
            await using (var db = database.Context()) Assert.Equal(AlertState.Firing, (await db.Alerts.SingleAsync(Ct)).State);

            clock.Now += TimeSpan.FromMinutes(10);
            await evaluator.EvaluateAsync(rule, Ct);
            await using (var db = database.Context())
            {
                var alert = await db.Alerts.SingleAsync(Ct);
                Assert.Equal((AlertState.Firing, true), (alert.State, alert.NoData));
            }
        }

        [Fact]
        public async Task Filters_equals_and_in_select_series_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-filters", $"mt.{_run}.filters");
            foreach (var (q, v, j) in new[] { ("a", 10.0, "x"), ("b", 20.0, "x"), ("c", 40.0, "y") })
                await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [("q", q), ("j", j)], (Ago(1), v)), "gauge", metric, 1);

            MetricFilter Eq(string k, string v) => new() { Key = k, Op = FilterOp.Equals, Values = [v] };
            double? Max(params MetricFilter[] f) => Templates().ObserveAsync(
                Rule(metric, MetricKind.Gauge, MetricAggregation.Max, service, spec: m => m.Filters = [.. f]), Window(), Ct).GetAwaiter().GetResult().SingleOrDefault()?.Value;

            Assert.Equal(40, Max());
            Assert.Equal(10, Max(Eq("q", "a")));
            Assert.Equal(20, Max(new MetricFilter { Key = "q", Op = FilterOp.In, Values = ["a", "b"] }));
            Assert.Equal(20, Max(Eq("j", "x"), new MetricFilter { Key = "q", Op = FilterOp.In, Values = ["b", "c"] }));
            Assert.Null(Max(Eq("q", "zzz")));            // nothing matches: no data, not zero
            Assert.Null(Max(Eq("missing", "a")));
        }

        [Fact]
        public async Task Subject_host_groups_by_host_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"mt.{_run}.host";
            var (h1, h2) = ($"mt-{_run}-h1", $"mt-{_run}-h2");
            await SendAsync(OtlpMetrics.Gauge(Res("hosted", h1), metric, [], (Ago(1), 1)), "gauge", metric, 1);
            await SendAsync(OtlpMetrics.Gauge(Res("hosted", h2), metric, [], (Ago(1), 2)), "gauge", metric, 1);

            var all = await Templates().ObserveAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Last, spec: m => m.Subject = MetricSubject.Host), Window(), Ct);
            Assert.Equal([$"host:{h1}", $"host:{h2}"], all.Select(o => o.SubjectKey).Order());
            Assert.All(all, o => Assert.Equal(HealthSubject.Host, o.SubjectType));
            var one = Assert.Single(await Templates().ObserveAsync(
                Rule(metric, MetricKind.Gauge, MetricAggregation.Last, configure: r => r.Host = h2.ToUpperInvariant(), spec: m => m.Subject = MetricSubject.Host), Window(), Ct));
            Assert.Equal((2d, $"host:{h2}"), (one.Value, one.SubjectKey));
        }

        [Fact]
        public async Task Comparison_below_fires_and_recovers_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, metric) = ($"mt-{_run}-below", $"mt.{_run}.below");
            var clock = new Clock(DateTimeOffset.UtcNow);
            var rule = await AddRuleAsync(database, Rule(metric, MetricKind.Gauge, MetricAggregation.Last, service,
                r => (r.Threshold, r.RecoveryThreshold, r.ForDuration, r.RecoveryDuration) = (10, 20, TimeSpan.Zero, TimeSpan.Zero), m => m.Comparison = Comparison.Below));
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [], (Ago(1), 5)), "gauge", metric, 1);
            var evaluator = PlatformServices.Evaluator(database, clock);
            await evaluator.EvaluateAsync(rule, Ct);
            await evaluator.EvaluateAsync(rule, Ct);
            await using (var db = database.Context()) Assert.Equal(AlertState.Firing, (await db.Alerts.SingleAsync(Ct)).State);

            clock.Now += TimeSpan.FromMinutes(6);
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [], (clock.Now.AddMinutes(-1), 30)), "gauge", metric, 1);
            Assert.Equal(1, (await evaluator.EvaluateAsync(rule, Ct)).Resolved);
        }

        [Fact]
        public async Task Other_tenant_rows_never_observed_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"mt-{_run}-foreign", $"mt.{_run}.foreign");
            await LocalStack.ClickHouseAsync($"INSERT INTO otel.otel_metrics_gauge (ResourceAttributes, ServiceName, MetricName, Attributes, TimeUnix, Value) VALUES " +
                $"(map('juice.platform_tenant_id', 'other-tenant', 'service.name', '{service}'), '{service}', '{metric}', map(), now() - 60, 999)");
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [], (Ago(1), 1)), "gauge", metric, 2);   // ours too, so the other's row is in the table
            Assert.Equal(1, (await ObserveOneAsync(Rule(metric, MetricKind.Gauge, MetricAggregation.Max, service))).Value);
        }

        [Fact]
        public async Task Row_limit_bounds_subjects_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"mt.{_run}.limit";
            for (var i = 0; i < 3; i++)
                await SendAsync(OtlpMetrics.Gauge(Res($"mt-{_run}-lim{i}"), metric, [], (Ago(1), i)), "gauge", metric, 1);
            var rule = Rule(metric, MetricKind.Gauge, MetricAggregation.Last);
            Assert.Equal(3, (await Templates().ObserveAsync(rule, Window(10), Ct)).Count);
            Assert.Equal(2, (await Templates().ObserveAsync(rule, Window(2), Ct)).Count);
        }

        [Fact]
        public async Task Evidence_carries_metric_aggregation_filters_unit_window_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, metric) = ($"mt-{_run}-evidence", $"mt.{_run}.evidence");
            var rule = await AddRuleAsync(database, Rule(metric, MetricKind.Gauge, MetricAggregation.Max, service, spec: m =>
            {
                m.Filters.Add(new MetricFilter { Key = "q", Op = FilterOp.Equals, Values = ["a"] });
                m.Filters.Add(new MetricFilter { Key = "j", Op = FilterOp.In, Values = ["x", "y"] });
            }));
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [("q", "a"), ("j", "x")], (Ago(1), 50)), "gauge", metric, 1);
            await PlatformServices.Evaluator(database, null).EvaluateAsync(rule, Ct);

            await using var db = database.Context();
            var evidence = (await db.Alerts.SingleAsync(Ct)).Evidence;
            Assert.Equal(metric, evidence["metric"]);
            Assert.Equal("Gauge", evidence["kind"]);
            Assert.Equal("Max", evidence["aggregation"]);
            Assert.Equal("q=a; j in [x,y]", evidence["filters"]);
            Assert.Equal("00:05:00", evidence["window"]);
            Assert.True(evidence.ContainsKey("unit"));
            Assert.Equal("50", evidence["value"]);
            Assert.Equal("MetricThreshold", evidence["template"]);   // a detail never overrides the base keys
        }

        [Fact]
        public async Task Concurrent_evaluations_one_alert_per_subject_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, metric) = ($"mt-{_run}-concurrent", $"mt.{_run}.concurrent");
            var rule = await AddRuleAsync(database, Rule(metric, MetricKind.Gauge, MetricAggregation.Last, service));
            await SendAsync(OtlpMetrics.Gauge(Res(service), metric, [], (Ago(1), 99)), "gauge", metric, 1);
            await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { await PlatformServices.Evaluator(database, null).EvaluateAsync(rule, Ct); }
                catch (DbUpdateException) { }
            }));
            await PlatformServices.Evaluator(database, null).EvaluateAsync(rule, Ct);
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(Ct);
            Assert.Equal(AlertState.Pending, alert.State);
        }

        // ─── the SQL ──────────────────────────────────────────────────────────

        [Fact]
        public void Queries_hold_the_tenant_the_window_and_a_limit_in_every_subquery_and_no_rule_text()
        {
            IReadOnlyList<MetricFilter> filters =
            [
                new() { Key = "k'}", Op = FilterOp.Equals, Values = ["') OR 1=1 --"] },
                new() { Key = "j", Op = FilterOp.In, Values = ["{x:String}", "\n"] },
            ];
            foreach (var kind in new[] { MetricKind.Gauge, MetricKind.Counter, MetricKind.UpDownCounter })
            {
                foreach (var aggregation in RuleDefinition.AggregationsOf(kind))
                {
                    foreach (var subject in Enum.GetValues<MetricSubject>())
                    {
                        var sql = RuleTemplates.MetricQuery(kind, aggregation, subject, filters);
                        Assert.Equal(sql.Split("FROM otel.").Length - 1, Regex.Matches(sql, Regex.Escape("JuiceTenant = {tenant:String}")).Count);   // one per read
                        Assert.Contains("LIMIT {limit:UInt32}", sql);
                        Assert.Contains("{from:DateTime64(9)}", sql);
                        Assert.DoesNotContain("rule.", sql);
                        Assert.DoesNotContain("OR 1=1", sql);
                        Assert.DoesNotContain("k'", sql);
                        // Every brace is a typed parameter.
                        Assert.DoesNotContain("{", Regex.Replace(sql, @"\{[a-z_0-9]+:[A-Za-z0-9()]+\}", ""));
                    }
                }
            }
            Assert.DoesNotContain("rule.", MetricCatalog.Query);
            Assert.Contains("{metric:String}", MetricCatalog.Query);
            Assert.Equal(3, Regex.Matches(MetricCatalog.Query, Regex.Escape("JuiceTenant = {tenant:String}")).Count);
        }

        // ─── helpers ──────────────────────────────────────────────────────────

        private static async Task<Rule> AddRuleAsync(ScratchDatabase database, RuleDefinition definition)
        {
            await using var db = database.Context();
            var rule = new Rule
            {
                TenantId = (await db.Tenants.SingleAsync(Ct)).Id, Key = definition.Key, Name = definition.Name, Template = definition.Template,
                Severity = definition.Severity, Definition = definition.ToJson(), DefinitionHash = definition.Hash(), LoadedAt = DateTimeOffset.UtcNow,
            };
            db.Rules.Add(rule);
            await db.SaveChangesAsync(Ct);
            return rule;
        }

        private sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
