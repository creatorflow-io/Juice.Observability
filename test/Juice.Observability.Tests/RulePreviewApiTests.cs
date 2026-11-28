using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 M4 (W-22): <c>POST /rules/preview</c>, a dry evaluation over 24 h: nothing written, tenant-bound, bounded in cost.</summary>
    public class RulePreviewApiTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static string DownConnection => LocalStack.ClickHouseReader.Replace("Port=8123", "Port=9");
        private static DateTimeOffset Ago(double minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes);
        private static string PreviewUrl(Guid tenant) => $"/api/v1/tenants/{tenant}/rules/preview";

        // Schedule 15 min and window 30 min: a step of 15 min, and every sample is inside two points' windows.
        private object MetricBody(string metric, string? kind, string service, string aggregation = "Last", double threshold = 50, string? key = "preview-rule",
            string? name = "Preview rule", object? filters = null, string forDuration = "00:00:00")
            => new
            {
                key, name, template = "MetricThreshold", threshold, service, schedule = "00:15:00", window = "00:30:00", forDuration,
                metric = new { name = metric, kind, aggregation, filters = filters ?? Array.Empty<object>() },
            };

        /// <summary>A rule that evaluates nothing against ClickHouse before it is refused: an error rate over 1.</summary>
        private static object Invalid() => new { key = "bad-rule", name = "Bad", template = "ErrorRate", threshold = 5 };

        /// <summary>A cheap valid preview: a host rule over a 6 h window (8 points).</summary>
        private static object Host() => new { key = "host-mem", name = "Host", template = "HostMemory", threshold = 0.99, schedule = "01:00:00", window = "06:00:00" };

        private static async Task SendGaugeAsync(string service, string metric, params (DateTimeOffset At, double Value)[] points)
        {
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", service)], metric, [], points));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}' AND ServiceName = '{service}' AND JuiceTenant = '{LocalStack.TenantKey}'")) < points.Length; i++)
                await Task.Delay(200, Ct);
        }

        private static async Task<(int Rules, int Alerts, int Audits, int Changes, int Idempotency)> CountsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return (await db.Rules.CountAsync(Ct), await db.Alerts.CountAsync(Ct), await db.AuditRecords.CountAsync(Ct), await db.ChangeEvents.CountAsync(Ct),
                await db.IdempotencyRecords.CountAsync(Ct));
        }

        private static async Task<(ScratchDatabase Database, ApiFactory Factory, Caller Viewer, Caller Operator, Caller Admin, Guid Tenant)> StartAsync(
            Dictionary<string, string>? settings = null)
        {
            var database = await LocalStack.CreateDatabaseAsync();
            var factory = new ApiFactory(database, settings: settings);
            var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            return (database, factory, viewer, @operator, admin, tenant);
        }

        // ─── what a preview shows ─────────────────────────────────────────────

        [Fact]
        public async Task Preview_gauge_matches_seeded_values_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"pv-{_run}-gauge", $"pv.{_run}.gauge");
            await SendGaugeAsync(service, metric, (Ago(180), 90), (Ago(60), 10));
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, service)));

            Assert.True(preview.GetProperty("complete").GetBoolean());
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("cut").ValueKind);
            Assert.Equal(TimeSpan.FromMinutes(15), TimeSpan.Parse(preview.GetProperty("step").GetString()!));
            Assert.Equal(96, preview.GetProperty("points").GetInt32());
            Assert.Equal(96, preview.GetProperty("pointsEvaluated").GetInt32());
            Assert.Equal("Gauge", preview.GetProperty("definition").GetProperty("metric").GetProperty("kind").GetString());
            var subject = Assert.Single(preview.GetProperty("subjects").EnumerateArray());
            var points = subject.GetProperty("points").EnumerateArray().ToList();
            Assert.Equal(96, points.Count);
            // A sample is in the window of the two points that follow it, 15 minutes apart: each value is seen twice, the rest is no data.
            Assert.Equal(2, points.Count(p => p.GetProperty("value").ValueKind == JsonValueKind.Number && p.GetProperty("value").GetDouble() == 90));
            Assert.Equal(2, points.Count(p => p.GetProperty("value").ValueKind == JsonValueKind.Number && p.GetProperty("value").GetDouble() == 10));
            Assert.All(points.Where(p => p.GetProperty("value").ValueKind == JsonValueKind.Null), p => Assert.Equal("NoData", p.GetProperty("condition").GetString()));
            Assert.Equal(2, subject.GetProperty("breaches").GetInt32());
            Assert.Equal(92, subject.GetProperty("noData").GetInt32());
            Assert.Equal(1, subject.GetProperty("wouldFire").GetInt32());
            Assert.Equal(10, subject.GetProperty("last").GetDouble());
            Assert.Equal(10, subject.GetProperty("min").GetDouble());
            Assert.Equal(90, subject.GetProperty("max").GetDouble());
            var first = subject.GetProperty("firstFiredAt").GetDateTimeOffset();
            Assert.InRange(first, Ago(181), Ago(149));   // the first point whose window holds the sample
        }

        [Fact]
        public async Task Preview_reads_back_past_24h_by_the_window_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"pv-{_run}-old", $"pv.{_run}.old");
            await SendGaugeAsync(service, metric, (Ago(25 * 60), 90));   // 25 h ago: outside 24 h, inside the first point's 6 h window (from 27 h)
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            var body = new
            {
                key = "old-rule", name = "Old", template = "MetricThreshold", threshold = 50, service, schedule = "01:00:00", window = "06:00:00", forDuration = "00:00:00",
                metric = new { name = metric, aggregation = "Last" },
            };

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), body));

            Assert.Equal(JsonValueKind.Null, preview.GetProperty("quiet").ValueKind);
            var subject = Assert.Single(preview.GetProperty("subjects").EnumerateArray());
            Assert.Equal(90, subject.GetProperty("max").GetDouble());
        }

        [Fact]
        public void Rate_bucket_sweep_drops_only_idle_buckets_and_is_amortised()
        {
            var (idle, busy) = (Guid.NewGuid(), Guid.NewGuid());
            Assert.True(PreviewLimits.TryTakeRate(idle, "a", 7, out _));
            Assert.True(PreviewLimits.TryTakeRate(busy, "a", 7, out _));
            Assert.True(PreviewLimits.HasBucket(idle, "a", 7));
            PreviewLimits.Sweep(System.Diagnostics.Stopwatch.GetTimestamp());   // nothing is idle yet
            Assert.True(PreviewLimits.HasBucket(idle, "a", 7));
            PreviewLimits.Sweep(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 900);   // 15 min on
            Assert.False(PreviewLimits.HasBucket(idle, "a", 7));
            Assert.False(PreviewLimits.HasBucket(busy, "a", 7));

            // With many buckets, a burst of requests sweeps once, not once each.
            for (var i = 0; i < 2100; i++) PreviewLimits.TryTakeRate(Guid.NewGuid(), "bulk", 7, out _);
            var before = PreviewLimits.Sweeps;
            for (var i = 0; i < 50; i++) PreviewLimits.TryTakeRate(Guid.NewGuid(), "bulk", 7, out _);
            Assert.True(PreviewLimits.Sweeps - before <= 1);
        }

        [Fact]
        public async Task Preview_histogram_last_detail_has_bucket_bounds_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"pv-{_run}-hist", $"pv.{_run}.hist");
            await LocalStack.SendAsync("metrics", OtlpMetrics.BucketHistogram([("service.name", service)], metric, OtlpMetrics.Cumulative, Ago(300), [], [1, 2],
                (Ago(60), [5, 20, 5], 30)));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_histogram WHERE MetricName = '{metric}'")) < 1; i++)
                await Task.Delay(200, Ct);
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, service, "P95", 100)));

            Assert.Equal("Histogram", preview.GetProperty("definition").GetProperty("metric").GetProperty("kind").GetString());
            var detail = Assert.Single(preview.GetProperty("subjects").EnumerateArray()).GetProperty("lastDetail");
            Assert.True(detail.TryGetProperty("bucketLower", out _));
            Assert.True(detail.TryGetProperty("bucketUpper", out _));
        }

        [Fact]
        public async Task Preview_unseen_metric_needs_kind_then_quiet_never_observes_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"pv.{_run}.unseen";
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var without = await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, $"pv-{_run}"));
            Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
            Assert.Contains("Metric.Kind is required", await without.Content.ReadAsStringAsync(Ct));

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, "Gauge", $"pv-{_run}")));
            Assert.Equal($"metric {metric} has not been reported in the last 24 h", preview.GetProperty("quiet").GetString());
            Assert.Empty(preview.GetProperty("subjects").EnumerateArray());
            Assert.True(preview.GetProperty("complete").GetBoolean());
        }

        [Fact]
        public async Task Preview_writes_nothing_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"pv-{_run}-nothing", $"pv.{_run}.nothing");
            await SendGaugeAsync(service, metric, (Ago(90), 90));
            var (database, factory, _, @operator, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            var created = await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", MetricBody(metric, null, service)));
            var etag = created.GetProperty("etag").GetString();
            var before = await CountsAsync(database);

            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, service, threshold: 1))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await @operator.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, service, key: null, name: null))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(PreviewUrl(tenant), Invalid())).StatusCode);

            Assert.Equal(before, await CountsAsync(database));
            // The rule's hash (its etag) is as it was made. Not read through GET /rules, whose catalog query the list tests count.
            await using var db = database.Context();
            Assert.Equal(etag, (await db.Rules.SingleAsync(Ct)).DefinitionHash);
        }

        [Fact]
        public async Task Preview_existing_key_no_409_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            var body = MetricBody($"pv.{_run}.exists", "Gauge", $"pv-{_run}");
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", body)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", body)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(PreviewUrl(tenant), body)).StatusCode);
        }

        [Fact]
        public async Task Preview_empty_key_and_name_accepted_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), new { template = "HostMemory", threshold = 0.99, schedule = "01:00:00", window = "06:00:00" }));

            Assert.Equal("preview", preview.GetProperty("definition").GetProperty("key").GetString());
            Assert.Equal("preview", preview.GetProperty("definition").GetProperty("name").GetString());
            Assert.Equal("HostMemory", preview.GetProperty("definition").GetProperty("template").GetString());
        }

        [Fact]
        public async Task Preview_sloburn_400_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var response = await admin.PostAsync(PreviewUrl(tenant), new { key = "burn-rule", name = "Burn", template = "SloBurn", threshold = 1 });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("SloBurn", await response.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task Preview_invalid_same_detail_as_create_with_errors_array_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            // Several things wrong at once: a window below the schedule, an aggregation that doesn't fit, a sixth filter.
            var body = MetricBody($"pv.{_run}.bad", "Gauge", $"pv-{_run}", "P95", filters: Enumerable.Range(0, 6).Select(i => new { key = $"k{i}", op = "Equals", values = new[] { "v" } }).ToArray());

            var create = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", body);
            var preview = await admin.PostAsync(PreviewUrl(tenant), body);

            Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
            var (c, p) = (JsonDocument.Parse(await create.Content.ReadAsStringAsync(Ct)).RootElement, JsonDocument.Parse(await preview.Content.ReadAsStringAsync(Ct)).RootElement);
            Assert.Equal(c.GetProperty("detail").GetString(), p.GetProperty("detail").GetString());
            var errors = p.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(c.GetProperty("errors").EnumerateArray().Select(e => e.GetString()), errors);
            Assert.Contains(errors, e => e.Contains("Metric.Aggregation P95"));
            Assert.Contains(errors, e => e.Contains("Metric.Filters has at most"));
            Assert.Equal(string.Join(" ", errors), p.GetProperty("detail").GetString());
        }

        // ─── who may ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Viewer_403_denial_audited_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, viewer, _, _, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(PreviewUrl(tenant), Host())).StatusCode);

            await using var db = database.Context();
            var audit = await db.AuditRecords.SingleAsync(a => a.Operation == "rule.preview", Ct);
            Assert.Equal(AuditOutcome.Denied, audit.Outcome);
        }

        [Fact]
        public async Task Operator_200_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, @operator, _, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            Assert.Equal(HttpStatusCode.OK, (await @operator.PostAsync(PreviewUrl(tenant), Host())).StatusCode);
            // Saving stays the admin's.
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/rules", Host())).StatusCode);
        }

        [Fact]
        public async Task Admin_200_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(PreviewUrl(tenant), Host())).StatusCode);
        }

        // ─── the tenant ───────────────────────────────────────────────────────

        [Fact]
        public async Task Other_tenant_route_403_before_any_clickhouse_query_Async()
        {
            LocalStack.RequireAvailable();
            // ClickHouse is out of reach: a preview that got past the tenant filter would be a 503, not its 403.
            var (database, factory, _, _, admin, _) = await StartAsync(new() { ["ClickHouse:ConnectionString"] = DownConnection });
            await using var _d = database; await using var _f = factory;

            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(PreviewUrl(Guid.NewGuid()), MetricBody("m.x", "Gauge", "s"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(PreviewUrl(Guid.NewGuid()), Host())).StatusCode);
        }

        [Fact]
        public async Task Other_tenant_route_leaves_no_query_in_the_log_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, _) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            var started = await LocalStack.ClickHouseAsync("SELECT toUnixTimestamp64Micro(now64(6))");

            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(PreviewUrl(Guid.NewGuid()), MetricBody("m.x", "Gauge", "s"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(PreviewUrl(Guid.NewGuid()), Host())).StatusCode);

            // This class's tests run one at a time, so a query of the preview route since the start is one of these requests'.
            await LocalStack.ClickHouseAsync("SYSTEM FLUSH LOGS");
            Assert.Equal("0", await LocalStack.ClickHouseAsync($"SELECT count() FROM system.query_log WHERE log_comment = 'juice:api:/rules/preview' " +
                $"AND toUnixTimestamp64Micro(event_time_microseconds) >= {started}"));
        }

        [Fact]
        public async Task Other_tenant_rows_never_previewed_Async()
        {
            LocalStack.RequireAvailable();
            var (service, metric) = ($"pv-{_run}-foreign", $"pv.{_run}.foreign");
            await LocalStack.ClickHouseAsync($"INSERT INTO otel.otel_metrics_gauge (ResourceAttributes, ServiceName, MetricName, Attributes, TimeUnix, Value) VALUES " +
                $"(map('juice.platform_tenant_id', 'other-tenant', 'service.name', '{service}'), '{service}', '{metric}', map(), now() - 3600, 999)");
            await SendGaugeAsync(service, metric, (Ago(60), 7));   // ours too, so the other's row is in the table
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, service)));

            var subject = Assert.Single(preview.GetProperty("subjects").EnumerateArray());
            Assert.Equal(7, subject.GetProperty("max").GetDouble());
        }

        // ─── what it costs ────────────────────────────────────────────────────

        [Fact]
        public async Task Preview_row_limit_bounds_subjects_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"pv.{_run}.limit";
            for (var i = 0; i < 5; i++) await SendGaugeAsync($"pv-{_run}-lim{i}", metric, (Ago(60), i));
            var (database, factory, _, _, admin, tenant) = await StartAsync(new() { ["Rules:RowLimit"] = "3" });
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), MetricBody(metric, null, "")));

            Assert.InRange(preview.GetProperty("subjects").GetArrayLength(), 1, 3);
        }

        [Fact]
        public async Task Preview_injection_inert_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync(new() { ["Rules:Preview:PerActorPerMinute"] = "1000" });
            await using var _d = database; await using var _f = factory;
            var service = $"pv-{_run}-inj";
            foreach (var text in MetricQueryInjectionTests.Hostile)
            {
                // As a name or a filter key: refused by validation, with the errors array.
                var named = await admin.PostAsync(PreviewUrl(tenant), MetricBody(text, "Gauge", service));
                Assert.Equal(HttpStatusCode.BadRequest, named.StatusCode);
                var keyed = await admin.PostAsync(PreviewUrl(tenant), MetricBody("ok.metric", "Gauge", service, filters: new[] { new { key = text, op = "Equals", values = new[] { "v" } } }));
                Assert.Equal(HttpStatusCode.BadRequest, keyed.StatusCode);
                // As a filter value: matched literally, never read as SQL (the metric is unseen, so nothing is asked of ClickHouse for it, and a seen one is below).
                var valued = await admin.PostAsync(PreviewUrl(tenant), MetricBody("ok.metric", "Gauge", service, filters: new[] { new { key = "q", op = "Equals", values = new[] { text } } }));
                Assert.Equal(HttpStatusCode.OK, valued.StatusCode);
            }
            // A seen metric reads the values from ClickHouse: no syntax error, so no 500.
            var (seenService, seen) = ($"pv-{_run}-inj-seen", $"pv.{_run}.inj");
            await SendGaugeAsync(seenService, seen, (Ago(60), 1));
            foreach (var text in MetricQueryInjectionTests.Hostile)
            {
                var valued = await admin.PostAsync(PreviewUrl(tenant), new
                {
                    key = "inj-rule", name = "n", template = "MetricThreshold", threshold = 5, service = seenService, schedule = "01:00:00", window = "06:00:00",
                    metric = new { name = seen, aggregation = "Last", filters = new[] { new { key = "q", op = "In", values = new[] { text, "plain" } } } },
                });
                Assert.Equal(HttpStatusCode.OK, valued.StatusCode);
            }
        }

        [Fact]
        public async Task Rate_limit_11th_in_a_minute_429_retry_after_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            for (var i = 0; i < 10; i++)
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(PreviewUrl(tenant), Invalid())).StatusCode);   // each takes a token, valid or not
            var refused = await admin.PostAsync(PreviewUrl(tenant), Invalid());

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);
            // A refusal isn't audited.
            await using var db = database.Context();
            Assert.Equal(0, await db.AuditRecords.CountAsync(a => a.Operation == "rule.preview", Ct));
        }

        [Fact]
        public async Task Rate_limit_other_actor_and_other_tenant_unaffected_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, @operator, admin, tenant) = await StartAsync(new() { ["Rules:Preview:PerActorPerMinute"] = "2" });
            await using var _d = database; await using var _f = factory;
            for (var i = 0; i < 2; i++) await admin.PostAsync(PreviewUrl(tenant), Invalid());
            Assert.Equal(HttpStatusCode.TooManyRequests, (await admin.PostAsync(PreviewUrl(tenant), Invalid())).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync(PreviewUrl(tenant), Invalid())).StatusCode);

            // Another tenant's bucket for the same actor, and an actor the token doesn't name, are their own.
            Assert.True(PreviewLimits.TryTakeRate(Guid.NewGuid(), "alice", 2, out _));
            Assert.True(PreviewLimits.TryTakeRate(tenant, "someone-else", 2, out _));
            var anonymous = Guid.NewGuid();
            Assert.True(PreviewLimits.TryTakeRate(anonymous, "unknown", 1, out _));
            Assert.False(PreviewLimits.TryTakeRate(anonymous, "unknown", 1, out var retry));
            Assert.InRange(retry, 1, 60);
        }

        [Fact]
        public async Task Concurrent_third_preview_same_tenant_429_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            using (var first = PreviewLimits.TryEnter(tenant, 2))
            using (var second = PreviewLimits.TryEnter(tenant, 2))
            {
                Assert.True(first.IsAcquired && second.IsAcquired);
                var refused = await admin.PostAsync(PreviewUrl(tenant), Host());
                Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
                Assert.True(refused.Headers.Contains("Retry-After"));
                // Another tenant's previews don't count against it.
                using var other = PreviewLimits.TryEnter(Guid.NewGuid(), 2);
                Assert.True(other.IsAcquired);
            }
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(PreviewUrl(tenant), Host())).StatusCode);
        }

        [Fact]
        public async Task ClickHouse_down_503_retry_after_no_partial_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync(new() { ["ClickHouse:ConnectionString"] = DownConnection });
            await using var _d = database; await using var _f = factory;

            var response = await admin.PostAsync(PreviewUrl(tenant), Host());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.Contains("Retry-After"));
            Assert.DoesNotContain("subjects", await response.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task Api_slots_full_503_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, @operator, admin, tenant) = await StartAsync(new() { ["ClickHouse:Api:MaxConcurrent"] = "1", ["ClickHouse:Api:QueueTimeout"] = "00:00:00" });
            await using var _d = database; await using var _f = factory;
            // 96 queries each, one slot, no waiting: a query of one finds the other's in the slot.
            var body = new { key = "host-mem", name = "Host", template = "HostMemory", threshold = 0.99 };

            var responses = await Task.WhenAll(admin.PostAsync(PreviewUrl(tenant), body), @operator.PostAsync(PreviewUrl(tenant), body));

            var busy = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
            Assert.NotEmpty(busy);
            Assert.All(busy, r => Assert.True(r.Headers.Contains("Retry-After")));
        }

        [Fact]
        public async Task Budget_cut_returns_partial_complete_false_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync(new() { ["Rules:Preview:BudgetSeconds"] = "0" });
            await using var _d = database; await using var _f = factory;

            var preview = await Caller.JsonAsync(await admin.PostAsync(PreviewUrl(tenant), Host()));

            Assert.False(preview.GetProperty("complete").GetBoolean());
            Assert.Equal("stopped after 0 of 8 points", preview.GetProperty("cut").GetString());
            Assert.Equal(0, preview.GetProperty("pointsEvaluated").GetInt32());
            Assert.Equal(8, preview.GetProperty("points").GetInt32());
        }

        [Fact]
        public async Task Aborted_request_cancels_queries_Async()
        {
            LocalStack.RequireAvailable();
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;
            var queries = 0;
            // 96 points (a 1 min schedule and a 5 min window): the client gives up once the third query has run.
            using var abort = new CancellationTokenSource();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "juice.observability.clickhouse.query.duration") l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "caller", Value: "api:/rules/preview" } && Interlocked.Increment(ref queries) == 3) abort.Cancel();
                }
            });
            listener.Start();

            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, PreviewUrl(tenant))
            {
                Content = JsonContent.Create(new { key = "host-mem", name = "Host", template = "HostMemory", threshold = 0.99 }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LocalStack.TokenAsync("alice"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.SendAsync(request, abort.Token));
            await Task.Delay(1500, Ct);
            var settled = Volatile.Read(ref queries);
            await Task.Delay(1500, Ct);

            Assert.InRange(settled, 3, 95);
            Assert.Equal(settled, Volatile.Read(ref queries));   // none went on after the abort
        }

        // ─── the catalog ──────────────────────────────────────────────────────

        [Fact]
        public async Task Metrics_catalog_marks_monotonic_Async()
        {
            LocalStack.RequireAvailable();
            var service = $"pv-{_run}-cat";
            var (counter, updown, gauge) = ($"pv.{_run}.counter", $"pv.{_run}.updown", $"pv.{_run}.cgauge");
            await LocalStack.SendAsync("metrics", OtlpMetrics.Sum([("service.name", service)], counter, OtlpMetrics.Cumulative, true, Ago(30), [], (Ago(2), 3)));
            await LocalStack.SendAsync("metrics", OtlpMetrics.Sum([("service.name", service)], updown, OtlpMetrics.Cumulative, false, Ago(30), [], (Ago(2), 3)));
            await SendGaugeAsync(service, gauge, (Ago(2), 1));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_sum WHERE ServiceName = '{service}'")) < 2; i++)
                await Task.Delay(200, Ct);
            var (database, factory, _, _, admin, tenant) = await StartAsync();
            await using var _d = database; await using var _f = factory;

            // The last few minutes only: the page of a shared ClickHouse is in name order and bounded.
            var from = Uri.EscapeDataString(Ago(5).ToString("O"));
            var page = await Caller.JsonAsync(await admin.GetAsync($"/api/v1/tenants/{tenant}/telemetry/metrics?from={from}"));

            var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
            Assert.True(items[counter].GetProperty("monotonic").GetBoolean());
            Assert.False(items[updown].GetProperty("monotonic").GetBoolean());
            Assert.Equal(JsonValueKind.Null, items[gauge].GetProperty("monotonic").ValueKind);
            Assert.Equal("sum", items[counter].GetProperty("type").GetString());
        }
    }
}
