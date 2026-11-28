using System.Net;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 R1-R3 through the API: the kind from the route tenant's catalog, roles, tenancy, concurrency, and the file.</summary>
    public class MetricRuleApiTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private object Body(string metric, string? kind, string aggregation = "Last", double threshold = 10, string key = "metric-rule")
            => new
            {
                key, name = "Metric rule", template = "MetricThreshold", threshold, service = $"mr-{_run}",
                metric = new { name = metric, kind, aggregation, filters = Array.Empty<object>() },
            };

        private static string DownConnection => LocalStack.ClickHouseReader.Replace("Port=8123", "Port=9");

        private static async Task SendGaugeAsync(string metric)
        {
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", "catalog-svc")], metric, [], (DateTimeOffset.UtcNow.AddMinutes(-1), 1)));
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_gauge WHERE MetricName = '{metric}'")) < 1; i++)
                await Task.Delay(200, Ct);
        }

        private static async Task<int> AuditCountAsync(ScratchDatabase database, string operation)
        {
            await using var db = database.Context();
            return await db.AuditRecords.CountAsync(a => a.Operation == operation && a.Outcome == AuditOutcome.Succeeded, Ct);
        }

        [Fact]
        public async Task Create_fills_kind_from_catalog_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"api.{_run}.gauge";
            await SendGaugeAsync(metric);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);

            var created = await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body(metric, null)));
            var definition = created.GetProperty("definition").GetProperty("metric");
            Assert.Equal("Gauge", definition.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, created.GetProperty("quiet").ValueKind);
        }

        [Fact]
        public async Task Create_kind_conflict_400_names_field_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"api.{_run}.conflict";
            await SendGaugeAsync(metric);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);

            var response = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body(metric, "Counter", "Rate"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Metric.Kind Counter conflicts", await response.Content.ReadAsStringAsync(Ct));
            // An aggregation that doesn't fit the kind names its field too.
            var badAggregation = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body(metric, null, "P95"));
            Assert.Equal(HttpStatusCode.BadRequest, badAggregation.StatusCode);
            Assert.Contains("Metric.Aggregation P95 doesn't fit a Gauge", await badAggregation.Content.ReadAsStringAsync(Ct));
            await using var db = database.Context();
            Assert.False(await db.Rules.AnyAsync(Ct));
        }

        [Fact]
        public async Task Create_unseen_metric_needs_kind_then_quiet_never_observes_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"api.{_run}.unseen";
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";

            var without = await admin.PostAsync(rules, Body(metric, null));
            Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
            Assert.Contains("Metric.Kind is required", await without.Content.ReadAsStringAsync(Ct));

            var created = await Caller.JsonAsync(await admin.PostAsync(rules, Body(metric, "Gauge")));
            Assert.Equal($"metric {metric} has not been reported in the last 24 h", created.GetProperty("quiet").GetString());
        }

        [Fact]
        public async Task Create_catalog_down_with_kind_accepted_without_kind_503_Async()
        {
            LocalStack.RequireAvailable();
            var metric = $"api.{_run}.down";
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["ClickHouse:ConnectionString"] = DownConnection });
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";

            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsync(rules, Body(metric, null))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, Body(metric, "Gauge"))).StatusCode);
        }

        [Fact]
        public async Task Viewer_and_Operator_403_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(rules, Body("m.x", "Gauge"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(rules, Body("m.x", "Gauge"))).StatusCode);
        }

        [Fact]
        public async Task Other_tenant_route_403_and_catalog_not_queried_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // ClickHouse is out of reach: a catalog read would be a 503, not the 403 the tenant filter gives first.
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["ClickHouse:ConnectionString"] = DownConnection });
            using var client = factory.CreateClient();
            var (_, _, admin, _) = await Caller.AllAsync(client, database);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/v1/tenants/{Guid.NewGuid()}/rules", Body("m.x", null))).StatusCode);
        }

        [Fact]
        public async Task Put_if_match_stale_412_and_metric_change_closes_open_alerts_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";
            var created = await Caller.JsonAsync(await admin.PostAsync(rules, Body("m.put", "Gauge")));
            var etag = created.GetProperty("etag").GetString()!;
            var url = $"{rules}/metric-rule";

            Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.PutAsync(url, Body("m.put", "Gauge", threshold: 20), "stale")).StatusCode);

            // Same definition: no change, no audit.
            var before = await AuditCountAsync(database, "rule.update");
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(url, Body("m.put", "Gauge"), etag)).StatusCode);
            Assert.Equal(before, await AuditCountAsync(database, "rule.update"));

            // An open alert, then a change of the metric closes it; a change of the threshold alone doesn't.
            await using (var db = database.Context())
            {
                var rule = await db.Rules.SingleAsync(Ct);
                db.Alerts.Add(new Alert
                {
                    TenantId = rule.TenantId, RuleId = rule.Id, SubjectKey = "service:x@e", SubjectType = HealthSubject.Service, State = AlertState.Pending,
                    DedupKey = $"{rule.Key}|service:x@e", BreachStartedAt = DateTimeOffset.UtcNow, LastEvaluatedAt = DateTimeOffset.UtcNow,
                    Severity = rule.Severity, Evidence = [],
                });
                await db.SaveChangesAsync(Ct);
            }
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(url, Body("m.put", "Gauge", threshold: 30))).StatusCode);
            await using (var db = database.Context()) Assert.Equal(AlertState.Pending, (await db.Alerts.SingleAsync(Ct)).State);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(url, Body("m.other", "Gauge", threshold: 30))).StatusCode);
            await using (var db = database.Context()) Assert.Equal(AlertState.Resolved, (await db.Alerts.SingleAsync(Ct)).State);
        }

        [Fact]
        public async Task Put_same_definition_is_noop_no_audit_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";
            var created = await Caller.JsonAsync(await admin.PostAsync(rules, Body("m.same", "Gauge")));
            var etag = created.GetProperty("etag").GetString()!;
            await using var count = database.Context();
            var audits = await count.AuditRecords.CountAsync(Ct);

            var put = await Caller.JsonAsync(await admin.PutAsync($"{rules}/metric-rule", Body("m.same", "Gauge"), etag));

            Assert.Equal(etag, put.GetProperty("etag").GetString());
            await using var after = database.Context();
            Assert.Equal(audits, await after.AuditRecords.CountAsync(Ct));
        }

        [Fact]
        public async Task Rules_json_metric_rule_provisioned_and_restart_idempotent_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var file = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "queue-depth", ["Rules:Definitions:0:Name"] = "Queue depth", ["Rules:Definitions:0:Template"] = "MetricThreshold",
                ["Rules:Definitions:0:Threshold"] = "100", ["Rules:Definitions:0:Metric:Name"] = "sample.queue.depth", ["Rules:Definitions:0:Metric:Kind"] = "Gauge",
                ["Rules:Definitions:0:Metric:Aggregation"] = "Last", ["Rules:Definitions:0:Metric:Filters:0:Key"] = "queue",
                ["Rules:Definitions:0:Metric:Filters:0:Op"] = "In", ["Rules:Definitions:0:Metric:Filters:0:Values:0"] = "a", ["Rules:Definitions:0:Metric:Filters:0:Values:1"] = "b",
            };
            string? hash = null;
            for (var start = 0; start < 2; start++)
            {
                await using var factory = new ApiFactory(database, settings: file);
                factory.CreateClient().Dispose();
                await using var db = database.Context();
                var rule = await db.Rules.SingleAsync(Ct);
                hash ??= rule.DefinitionHash;
                Assert.Equal(hash, rule.DefinitionHash);
                Assert.Equal("MetricThreshold", rule.Template.ToString());
            }
            Assert.Equal(1, await AuditCountAsync(database, "rule.create"));
            Assert.Equal(0, await AuditCountAsync(database, "rule.update"));
        }

        [Fact]
        public async Task Rules_json_without_a_kind_stops_the_start_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var file = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "queue-depth", ["Rules:Definitions:0:Name"] = "Queue depth", ["Rules:Definitions:0:Template"] = "MetricThreshold",
                ["Rules:Definitions:0:Threshold"] = "100", ["Rules:Definitions:0:Metric:Name"] = "sample.queue.depth", ["Rules:Definitions:0:Metric:Aggregation"] = "Last",
            };
            await using var factory = new ApiFactory(database, settings: file);
            var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains("Metric.Kind is required", error.ToString());
        }
    }
}
