using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 M4: <c>GET /rules</c> says "never observes" for an enabled metric rule whose metric the tenant hasn't reported in 24 h.</summary>
    public class RuleListQuietTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static string DownConnection => LocalStack.ClickHouseReader.Replace("Port=8123", "Port=9");

        private static string Never(string metric) => $"metric {metric} has not been reported in the last 24 h";

        private static async Task AddRuleAsync(ScratchDatabase database, string key, string metric, bool enabled = true, TimeSpan? window = null)
        {
            var definition = new RuleDefinition
            {
                Key = key, Name = key, Template = RuleTemplate.MetricThreshold, Threshold = 10, Enabled = enabled, Window = window ?? TimeSpan.FromMinutes(5),
                Metric = new MetricSpec { Name = metric, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last },
            };
            await using var db = database.Context();
            db.Rules.Add(new Rule
            {
                TenantId = (await db.Tenants.SingleAsync(Ct)).Id, Key = key, Name = key, Template = definition.Template, Severity = definition.Severity,
                Definition = definition.ToJson(), DefinitionHash = definition.Hash(), Enabled = enabled, LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api,
            });
            await db.SaveChangesAsync(Ct);
        }

        private static async Task SendGaugeAsync(string metric, bool foreign = false)
        {
            var service = $"rq-{metric.GetHashCode():x}";
            if (foreign)
            {
                await LocalStack.ClickHouseAsync($"INSERT INTO otel.otel_metrics_gauge (ResourceAttributes, ServiceName, MetricName, Attributes, TimeUnix, Value) VALUES " +
                    $"(map('juice.platform_tenant_id', 'other-tenant', 'service.name', '{service}'), '{service}', '{metric}', map(), now() - 60, 1)");
                return;
            }
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", service)], metric, [], (DateTimeOffset.UtcNow.AddMinutes(-1), 1)));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}' AND JuiceTenant = '{LocalStack.TenantKey}'")) < 1; i++)
                await Task.Delay(200, Ct);
        }

        private static async Task<Dictionary<string, JsonElement>> ListAsync(Caller caller, Guid tenant)
        {
            var page = await Caller.JsonAsync(await caller.GetAsync($"/api/v1/tenants/{tenant}/rules"));
            return page.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
        }

        [Fact]
        public async Task List_unseen_metric_rule_quiet_never_observes_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"rq.{_run}.unseen";
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "unseen-rule", metric);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var rules = await ListAsync(viewer, tenant);

            Assert.Equal(Never(metric), rules["unseen-rule"].GetProperty("quiet").GetString());
        }

        [Fact]
        public async Task List_seen_or_disabled_no_quiet_Async()
        {
            LocalStack.RequireAvailable();
            var (seen, unseen) = ($"rq.{_run}.seen", $"rq.{_run}.disabled");
            await SendGaugeAsync(seen);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "seen-rule", seen);
            await AddRuleAsync(database, "disabled-rule", unseen, enabled: false);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var rules = await ListAsync(viewer, tenant);

            Assert.Equal(JsonValueKind.Null, rules["seen-rule"].GetProperty("quiet").ValueKind);
            Assert.Equal(JsonValueKind.Null, rules["disabled-rule"].GetProperty("quiet").ValueKind);
        }

        [Fact]
        public async Task List_other_tenants_metric_not_seen_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"rq.{_run}.foreign";
            await SendGaugeAsync(metric, foreign: true);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "foreign-rule", metric);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            Assert.Equal(Never(metric), (await ListAsync(viewer, tenant))["foreign-rule"].GetProperty("quiet").GetString());
        }

        [Fact]
        public async Task List_clickhouse_down_still_200_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "down-rule", $"rq.{_run}.down");
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["ClickHouse:ConnectionString"] = DownConnection });
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var response = await viewer.GetAsync($"/api/v1/tenants/{tenant}/rules");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rule = Assert.Single((await Caller.JsonAsync(response)).GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("quiet").ValueKind);
        }

        private sealed class Capture : Microsoft.Extensions.Logging.ILogger
        {
            public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Error)> Entries { get; } = [];
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
        }

        private static List<Juice.Observability.Api.Endpoints.RuleDto> Page(string metric, TimeSpan? window = null)
        {
            var definition = new RuleDefinition
            {
                Key = "page-rule", Name = "n", Template = RuleTemplate.MetricThreshold, Threshold = 10, Window = window ?? TimeSpan.FromMinutes(5),
                Metric = new MetricSpec { Name = metric, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last },
            };
            return [new("page-rule", "n", "MetricThreshold", "Warning", true, definition, DateTimeOffset.UtcNow, "api", "etag")];
        }

        [Fact]
        public async Task List_catalog_throws_anything_page_is_returned_without_marks_Async()
        {
            // An auth error, and an HttpClient timeout that isn't the request's own cancel.
            foreach (Exception error in new Exception[] { new InvalidOperationException("Authentication failed"), new TaskCanceledException("The request timed out", new TimeoutException()) })
            {
                var log = new Capture();
                var page = await Juice.Observability.Api.Endpoints.AlertEndpoints.MarkNeverObservedAsync(Page("m.x"), DateTimeOffset.UtcNow,
                    (_, _) => throw error, log, CancellationToken.None);

                Assert.Null(Assert.Single(page).Quiet);
                var entry = Assert.Single(log.Entries);
                Assert.Equal((Microsoft.Extensions.Logging.LogLevel.Warning, error), entry);
            }
        }

        [Fact]
        public async Task List_request_cancel_still_cancels_Async()
        {
            using var cancel = new CancellationTokenSource();
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Juice.Observability.Api.Endpoints.AlertEndpoints.MarkNeverObservedAsync(Page("m.x"), DateTimeOffset.UtcNow,
                (_, _) => throw new OperationCanceledException(cancel.Token), new Capture(), cancel.Token));
        }

        [Fact]
        public async Task List_catalog_auth_error_still_200_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "auth-rule", $"rq.{_run}.auth");
            var wrong = LocalStack.ClickHouseReader.Replace(LocalStack.Secret("CLICKHOUSE_PLATFORM_PASSWORD"), "wrong-password");
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["ClickHouse:ConnectionString"] = wrong });
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var rule = Assert.Single((await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/rules"))).GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, rule.GetProperty("quiet").ValueKind);
        }

        [Fact]
        public async Task List_looks_back_over_24h_and_the_window_Async()
        {
            LocalStack.RequireAvailable();
            var (metric, service) = ($"rq.{_run}.old", $"rq-{_run}-old");
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", service)], metric, [], (DateTimeOffset.UtcNow.AddHours(-26), 1)));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}' AND JuiceTenant = '{LocalStack.TenantKey}'")) < 1; i++)
                await Task.Delay(200, Ct);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await AddRuleAsync(database, "short-window", metric, window: TimeSpan.FromMinutes(5));
            await AddRuleAsync(database, "long-window", metric, window: TimeSpan.FromHours(6));
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var rules = await ListAsync(viewer, tenant);

            // One query, but each rule against its own lookback: last seen 26 h ago is inside 24 h + 6 h, outside 24 h + 5 min.
            Assert.Equal(JsonValueKind.Null, rules["long-window"].GetProperty("quiet").ValueKind);
            Assert.Equal(Never(metric), rules["short-window"].GetProperty("quiet").GetString());
        }

        [Fact]
        public async Task List_one_catalog_query_per_page_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            for (var i = 0; i < 12; i++) await AddRuleAsync(database, $"page-rule-{i}", $"rq.{_run}.m{i % 4}");
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);
            var started = await LocalStack.ClickHouseAsync("SELECT toUnixTimestamp64Micro(now64(6))");

            var rules = await ListAsync(viewer, tenant);

            Assert.Equal(12, rules.Count);
            Assert.All(rules.Values, r => Assert.NotNull(r.GetProperty("quiet").GetString()));
            // ClickHouse's own log (it holds the query with its values): the queries since the listing began that named this run's metrics.
            await LocalStack.ClickHouseAsync("SYSTEM FLUSH LOGS");
            Assert.Equal("1", await LocalStack.ClickHouseAsync($"SELECT count() FROM system.query_log WHERE type = 'QueryFinish' AND log_comment = 'juice:api:/rules' " +
                $"AND toUnixTimestamp64Micro(event_time_microseconds) >= {started} AND position(query, 'rq.{_run}.m') > 0"));
        }
    }
}
