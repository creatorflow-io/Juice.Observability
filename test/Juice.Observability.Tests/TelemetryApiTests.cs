using System.Net;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Telemetry exploration for Juice Console (Console requirements §5–§13, §21): the investigation journey through the
    /// Juice API alone, on real telemetry sent through the Collector; tenant isolation; and input bounds.
    /// </summary>
    public class TelemetryApiTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        [Fact]
        public async Task Service_operation_trace_span_dependency_logs_and_metrics_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (shop, payments) = ($"tel-{_run}-shop", $"tel-{_run}-payments");
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            // shop POST /checkout → client call → payments POST /charge (fails) → database client span.
            var (trace, root, call, charge) = (Otlp.TraceId(), Otlp.SpanId(), Otlp.SpanId(), Otlp.SpanId());
            (string, string)[] shopResource = [("service.name", shop), ("service.instance.id", "shop-1"), ("deployment.environment.name", "production")];
            (string, string)[] payResource = [("service.name", payments), ("service.instance.id", "pay-2"), ("deployment.environment.name", "production")];
            await LocalStack.SendAsync("traces", Otlp.Traces(shopResource,
                Otlp.Failed(Otlp.Span(trace, root, null, "POST /checkout", Otlp.Server, at)),
                Otlp.Failed(Otlp.Span(trace, call, root, "POST payments/charge", Otlp.Client, at, ("http.request.method", "POST")))));
            await LocalStack.SendAsync("traces", Otlp.Traces(payResource,
                [Otlp.Failed(Otlp.Lasting(Otlp.Span(trace, charge, call, "POST /charge", Otlp.Server, at), TimeSpan.FromMilliseconds(90))),
                 Otlp.Span(trace, Otlp.SpanId(), charge, "INSERT charges", Otlp.Client, at,
                     ("db.system.name", "postgresql"), ("server.address", "payments-db"), ("db.namespace", "payments")),
                 .. Otlp.Requests(5, 0, at)]));
            await LocalStack.SendAsync("logs", Otlp.Logs(payResource,
                (at, 17, "card declined by issuer", trace, charge), (at, 9, "charge started", null, null)));
            await LocalStack.SendAsync("metrics", Otlp.Sum(payResource, $"tel_{_run}_charges", at.AddMinutes(-1), [],
                (at.AddSeconds(-60), 10), (at.AddSeconds(-30), 40), (at, 70)));
            await LocalStack.WaitForSpansAsync(payments, 7);
            await LocalStack.WaitForSpansAsync(shop, 2);
            for (var i = 0; i < 50 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_logs WHERE ServiceName = '{payments}'")) < 2; i++)
            {
                await Task.Delay(200, Ct);
            }
            await DiscoverAsync(database);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            async Task<JsonElement> Api(string path) => await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}{path}", token));
            Guid paymentsId, shopId, callsId, databaseId;
            await using (var db = database.Context())
            {
                paymentsId = (await db.Services.SingleAsync(s => s.Name == payments)).Id;
                shopId = (await db.Services.SingleAsync(s => s.Name == shop)).Id;
                callsId = (await db.Dependencies.SingleAsync(d => d.SourceServiceId == shopId && d.Relation == DependencyRelation.Calls)).Id;
                databaseId = (await db.Dependencies.SingleAsync(d => d.SourceServiceId == paymentsId && d.Relation == DependencyRelation.UsesDatabase)).Id;
            }
            var window = $"from={Uri.EscapeDataString(at.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";

            Assert.Contains((await Api("/environments")).GetProperty("items").EnumerateArray(),
                e => e.GetProperty("key").GetString() == "production" && e.GetProperty("type").GetString() == "Production");

            // 1–3. The service's operations, busiest first: GET /orders healthy, POST /charge failing.
            var operations = (await Api($"/telemetry/operations?serviceId={paymentsId}&{window}")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal([("GET /orders", 5L, 0.0), ("POST /charge", 1L, 1.0)], operations.Select(o =>
                (o.GetProperty("name").GetString(), o.GetProperty("count").GetInt64(), o.GetProperty("errorRate").GetDouble())));
            Assert.Equal(90, operations[1].GetProperty("p95Ms").GetDouble());

            // 4. A representative error trace for that operation.
            var traces = (await Api($"/telemetry/traces?serviceId={paymentsId}&operation={Uri.EscapeDataString("POST /charge")}&status=error&{window}")).GetProperty("items");
            var summary = Assert.Single(traces.EnumerateArray());
            Assert.Equal(trace, summary.GetProperty("traceId").GetString());
            Assert.Equal(shop, summary.GetProperty("rootService").GetString());
            Assert.Equal("POST /checkout", summary.GetProperty("rootOperation").GetString());
            Assert.Equal("shop-1", summary.GetProperty("rootInstance").GetString());
            Assert.Equal(4, summary.GetProperty("spanCount").GetInt64());

            // 5–6. The waterfall: hierarchy, the failing downstream span with its instance.
            var spans = (await Api($"/telemetry/traces/{trace}")).GetProperty("spans").EnumerateArray().ToList();
            Assert.Equal(4, spans.Count);
            // A W3C traceparent, as Juice.Audit stored before 10.4, opens the same trace.
            var traceParent = $"00-{trace}-{charge}-01";
            Assert.Equal(trace, (await Api($"/telemetry/traces/{traceParent}")).GetProperty("traceId").GetString());
            var failing = spans.Single(s => s.GetProperty("spanId").GetString() == charge);
            Assert.Equal((call, payments, "pay-2", "Error", "production"), (failing.GetProperty("parentSpanId").GetString(),
                failing.GetProperty("service").GetString(), failing.GetProperty("instance").GetString(), failing.GetProperty("status").GetString(),
                failing.GetProperty("environment").GetString()));
            Assert.Equal(90, failing.GetProperty("durationMs").GetDouble());
            Assert.Equal(paymentsId, failing.GetProperty("serviceId").GetGuid());
            // The span names its instance, so the trace page can read that instance's metrics.
            var instanceId = failing.GetProperty("instanceId").GetGuid();
            Assert.NotEqual(Guid.Empty, instanceId);
            // From the span to the dependency: the call reaches payments, the INSERT the database discovery recorded.
            var callTarget = spans.Single(s => s.GetProperty("spanId").GetString() == call).GetProperty("target");
            Assert.Equal(("service", paymentsId), (callTarget.GetProperty("kind").GetString(), callTarget.GetProperty("id").GetGuid()));
            var dbTarget = spans.Single(s => s.GetProperty("name").GetString() == "INSERT charges").GetProperty("target");
            Assert.Equal(("resource", "payments-db/payments"), (dbTarget.GetProperty("kind").GetString(), dbTarget.GetProperty("name").GetString()));
            Assert.Equal(JsonValueKind.Null, failing.GetProperty("target").ValueKind);

            // 7. The dependency: evidence from the caller's spans, and its traces.
            var calls = await Api($"/telemetry/dependencies/{callsId}?{window}");
            Assert.Equal(("Calls", "Observed", 1L, 1.0), (calls.GetProperty("relation").GetString(), calls.GetProperty("confidence").GetString(),
                calls.GetProperty("count").GetInt64(), calls.GetProperty("errorRate").GetDouble()));
            Assert.Equal([trace], calls.GetProperty("errorTraces").EnumerateArray().Select(t => t.GetString()));
            var uses = await Api($"/telemetry/dependencies/{databaseId}?{window}");
            Assert.Equal(("Inferred", 1L), (uses.GetProperty("confidence").GetString(), uses.GetProperty("count").GetInt64()));
            // Every edge's figures at once, for the graph: the same as each edge's evidence, and 0 calls outside the window.
            var stats = (await Api($"/telemetry/dependencies?{window}")).GetProperty("items").EnumerateArray()
                .ToDictionary(s => s.GetProperty("dependencyId").GetGuid());
            Assert.Equal((1L, 1.0), (stats[callsId].GetProperty("count").GetInt64(), stats[callsId].GetProperty("errorRate").GetDouble()));
            Assert.Equal((1L, 0.0), (stats[databaseId].GetProperty("count").GetInt64(), stats[databaseId].GetProperty("errorRate").GetDouble()));
            var before = $"from={Uri.EscapeDataString(at.AddHours(-2).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}";
            var quiet = (await Api($"/telemetry/dependencies?{before}")).GetProperty("items").EnumerateArray().ToDictionary(s => s.GetProperty("dependencyId").GetGuid());
            Assert.Equal((0L, 0L), (quiet[callsId].GetProperty("count").GetInt64(), quiet[databaseId].GetProperty("count").GetInt64()));
            Assert.Equal(trace, Assert.Single((await Api($"/telemetry/traces?dependencyId={callsId}&{window}")).GetProperty("items").EnumerateArray())
                .GetProperty("traceId").GetString());
            Assert.Single((await Api($"/telemetry/traces?attribute=db.system.name%3Dpostgresql&serviceId={paymentsId}&{window}")).GetProperty("items").EnumerateArray());

            // 8. Logs of the span, of the trace, and of the service by severity.
            var spanLogs = (await Api($"/telemetry/logs?spanId={charge}&{window}")).GetProperty("items");
            Assert.Equal("card declined by issuer", Assert.Single(spanLogs.EnumerateArray()).GetProperty("body").GetString());
            Assert.Single((await Api($"/telemetry/logs?traceId={trace}&{window}")).GetProperty("items").EnumerateArray());
            Assert.Equal(2, (await Api($"/telemetry/logs?serviceId={paymentsId}&{window}")).GetProperty("items").GetArrayLength());
            Assert.Single((await Api($"/telemetry/logs?serviceId={paymentsId}&minSeverity=17&q=DECLINED&{window}")).GetProperty("items").EnumerateArray());

            // 9. Metrics of the service: the counter as a rate (30 per 30 s), and the service's request stats.
            var metric = $"tel_{_run}_charges";
            Assert.Contains((await Api($"/telemetry/metrics?serviceId={paymentsId}&{window}")).GetProperty("items").EnumerateArray(),
                m => m.GetProperty("name").GetString() == metric && m.GetProperty("type").GetString() == "sum");
            var series = await Api($"/telemetry/metrics/series?name={metric}&serviceId={paymentsId}&stepSeconds=30&{window}");
            Assert.Equal("rate", series.GetProperty("aggregation").GetString());
            // A level that its host reports as a counter, as .NET does its thread pool queue length; and by the span's instance.
            var level = await Api($"/telemetry/metrics/series?name={metric}&instanceId={instanceId}&level=true&stepSeconds=30&{window}");
            Assert.Equal("avg", level.GetProperty("aggregation").GetString());
            Assert.NotEmpty(level.GetProperty("points").EnumerateArray());
            var points = series.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("value")).ToList();
            Assert.Contains(points, v => v.ValueKind == JsonValueKind.Number && Math.Abs(v.GetDouble() - 1.0) < 1e-9);
            // The first bucket has nothing to compare with: no data, never a rate of zero.
            Assert.Equal(JsonValueKind.Null, points[0].ValueKind);
            Assert.DoesNotContain(points, v => v.ValueKind == JsonValueKind.Number && v.GetDouble() == 0);
            Assert.NotEmpty((await Api($"/telemetry/service-stats?serviceId={paymentsId}&{window}")).GetProperty("points").EnumerateArray());

            // 10. Back to the trace directly: global search by trace id, span id and service name.
            Assert.Equal("trace", Assert.Single((await Api($"/search?q={trace.ToUpperInvariant()}")).GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());
            Assert.Equal(trace, Assert.Single((await Api($"/search?q=00-{trace}-{charge}-01")).GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
            Assert.Equal(trace, Assert.Single((await Api($"/search?q={charge}")).GetProperty("items").EnumerateArray()).GetProperty("traceId").GetString());
            Assert.Equal(paymentsId.ToString(), Assert.Single((await Api($"/search?q={Uri.EscapeDataString($"{_run}-pay")}")).GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        }

        [Fact]
        public async Task Another_tenants_telemetry_is_never_returned_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (trace, span, foreign) = (Otlp.TraceId(), Otlp.SpanId(), $"tel-{_run}-foreign");
            // Rows stored for another tenant, as a shared ClickHouse would hold them.
            await LocalStack.ClickHouseAsync($"""
                INSERT INTO otel.otel_traces (Timestamp, TraceId, SpanId, ServiceName, SpanName, SpanKind, StatusCode, ResourceAttributes)
                VALUES (now64(9) - INTERVAL 1 MINUTE, '{trace}', '{span}', '{foreign}', 'secret op', 'Server', 'Error',
                        map('juice.platform_tenant_id', 'other-tenant'))
                """);
            await LocalStack.ClickHouseAsync($"""
                INSERT INTO otel.otel_logs (Timestamp, TraceId, SpanId, ServiceName, Body, SeverityNumber, ResourceAttributes)
                VALUES (now64(9) - INTERVAL 1 MINUTE, '{trace}', '{span}', '{foreign}', 'secret body', 17, map('juice.platform_tenant_id', 'other-tenant'))
                """);
            await LocalStack.WaitForSpansAsync(foreign, 1);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Task<HttpResponseMessage> Raw(string path) => Get(client, $"/api/v1/tenants/{tenant}{path}", token);

            Assert.Equal(HttpStatusCode.NotFound, (await Raw($"/telemetry/traces/{trace}")).StatusCode);
            Assert.Empty((await JsonAsync(await Raw($"/telemetry/traces?operation={Uri.EscapeDataString("secret op")}"))).GetProperty("items").EnumerateArray());
            Assert.Empty((await JsonAsync(await Raw($"/telemetry/logs?traceId={trace}"))).GetProperty("items").EnumerateArray());
            Assert.Empty((await JsonAsync(await Raw("/telemetry/logs?q=secret%20body"))).GetProperty("items").EnumerateArray());
            Assert.Empty((await JsonAsync(await Raw($"/search?q={span}"))).GetProperty("items").EnumerateArray());
        }

        [Fact]
        public async Task Paging_returns_every_trace_and_log_once_even_with_equal_timestamps_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, operation, marker) = ($"tel-{_run}-paged", $"paged op {_run}", $"paged-{_run}");
            var traces = Enumerable.Range(0, 5).Select(_ => Otlp.TraceId()).ToList();
            // Same timestamp for every row: only the tie-breaker orders them.
            foreach (var trace in traces)
            {
                await LocalStack.ClickHouseAsync($"""
                    INSERT INTO otel.otel_traces (Timestamp, TraceId, SpanId, ServiceName, SpanName, SpanKind, StatusCode, ResourceAttributes)
                    VALUES (toDateTime64(now() - 60, 9), '{trace}', '{Otlp.SpanId()}', '{service}', '{operation}', 'Server', 'Ok',
                            map('juice.platform_tenant_id', '{LocalStack.TenantKey}'))
                    """);
                await LocalStack.ClickHouseAsync($"""
                    INSERT INTO otel.otel_logs (Timestamp, TraceId, SpanId, ServiceName, Body, SeverityNumber, ResourceAttributes)
                    VALUES (toDateTime64(now() - 60, 9), '{trace}', '', '{service}', '{marker} {trace}', 9,
                            map('juice.platform_tenant_id', '{LocalStack.TenantKey}'))
                    """);
            }
            await LocalStack.WaitForSpansAsync(service, 5);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            var window = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";
            async Task<List<string>> AllAsync(string path, Func<JsonElement, string> id)
            {
                var (seen, cursor, pages) = (new List<string>(), (string?)null, 0);
                do
                {
                    var page = await JsonAsync(await Get(client,
                        $"/api/v1/tenants/{tenant}{path}&{window}&limit=2{(cursor is null ? "" : $"&cursor={cursor}")}", token));
                    seen.AddRange(page.GetProperty("items").EnumerateArray().Select(id));
                    cursor = page.GetProperty("nextCursor").GetString();
                    Assert.Equal(cursor is not null, page.GetProperty("truncated").GetBoolean());
                }
                while (cursor is not null && ++pages < 10);
                return seen;
            }

            var pagedTraces = await AllAsync($"/telemetry/traces?operation={Uri.EscapeDataString(operation)}", t => t.GetProperty("traceId").GetString()!);
            Assert.Equal(traces.Order(), pagedTraces.Order());
            var pagedLogs = await AllAsync($"/telemetry/logs?q={marker}", l => l.GetProperty("traceId").GetString()!);
            Assert.Equal(traces.Order(), pagedLogs.Order());

            using var bad = await Get(client, $"/api/v1/tenants/{tenant}/telemetry/traces?cursor=not-a-cursor", token);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        [Theory]
        [InlineData("/telemetry/traces/not-a-trace", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?spanId=xyz", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?attribute=bad%20key%3Dx", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?attribute=a%3D1&attribute=b%3D1&attribute=c%3D1&attribute=d%3D1&attribute=e%3D1&attribute=f%3D1", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?from=2026-01-01T00:00:00Z&to=2026-01-03T00:00:00Z", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?limit=201", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?status=maybe", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/traces?serviceId=01a19643-0000-7000-8000-000000000000", HttpStatusCode.NotFound)]
        [InlineData("/telemetry/logs?minSeverity=99", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/metrics/series?name=drop%20table", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/metrics/series?name=x&stepSeconds=1", HttpStatusCode.BadRequest)]
        [InlineData("/telemetry/operations", HttpStatusCode.BadRequest)]
        [InlineData("/search?q=", HttpStatusCode.BadRequest)]
        public async Task Requests_outside_the_bounds_are_refused_Async(string path, HttpStatusCode expected)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            using var response = await Get(client, $"/api/v1/tenants/{tenant}{path}", token);
            Assert.Equal(expected, response.StatusCode);
        }

        [Fact]
        public async Task Rates_are_spans_over_the_period_and_keep_requests_and_messages_apart_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"tel-{_run}-rates";
            var now = DateTimeOffset.UtcNow;
            var at = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(-2).AddSeconds(30);
            var (from, to) = (at.AddSeconds(-30).AddMinutes(-8), at.AddSeconds(-30).AddMinutes(2));   // 600 s
            var messages = Enumerable.Range(0, 6).Select(_ => Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), Otlp.SpanId(), "process orders", Otlp.Consumer, at));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service)], [.. Otlp.Requests(30, 0, at), .. messages]));
            await LocalStack.WaitForSpansAsync(service, 36);
            await DiscoverAsync(database);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Guid id;
            await using (var db = database.Context())
            {
                id = (await db.Services.SingleAsync(s => s.Name == service)).Id;
            }
            var query = $"serviceId={id}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
            async Task<JsonElement> Api(string path) => await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}{path}", token));

            // The whole window: 30 requests and 6 messages over 600 s.
            var operations = (await Api($"/telemetry/operations?{query}")).GetProperty("items").EnumerateArray()
                .ToDictionary(o => o.GetProperty("kind").GetString()!, o => o.GetProperty("requestsPerSecond").GetDouble());
            Assert.Equal(0.05, operations["Server"], 6);
            Assert.Equal(0.01, operations["Consumer"], 6);

            // One bucket of 60 s holds the same spans, and neither kind is counted in the other's rate.
            var point = Assert.Single((await Api($"/telemetry/service-stats?{query}&stepSeconds=60")).GetProperty("points").EnumerateArray());
            Assert.Equal(0.5, point.GetProperty("requestsPerSecond").GetDouble(), 6);
            Assert.Equal(0.1, point.GetProperty("messagesPerSecond").GetDouble(), 6);
        }

        [Fact]
        public async Task Capacity_measures_each_instance_from_real_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, sampled) = ($"tel-{_run}-capacity", $"tel-{_run}-sampled");
            var (resource, sampledResource) = (new[] { ("service.name", service), ("service.instance.id", $"{service}-1"), ("host.name", "bench-1") },
                new[] { ("service.name", sampled), ("service.instance.id", $"{sampled}-1") });
            var now = DateTimeOffset.UtcNow;
            var first = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(-14);
            const int Minutes = 12;
            var start = first.AddMinutes(-2);

            // A minute before the first bucket starts the counters. Then 6, 12, ... 72 requests a minute of 1 s each, on a
            // process using 0.1 core idle plus 0.5 s of CPU per request, and memory of 100 MB plus 50 MB per request in flight.
            var spans = new List<System.Text.Json.Nodes.JsonObject>();
            var cpu = new List<(DateTimeOffset, long)> { (first.AddSeconds(-30), 1000) };
            var (memory, cpuCount, queue) = (new List<(DateTimeOffset, long)>(), new List<(DateTimeOffset, long)>(), new List<(DateTimeOffset, long)>());
            var (http, used) = (new List<(DateTimeOffset, long, double)> { (first.AddSeconds(-30), 500, 50d) }, 1000L);
            for (var minute = 0; minute < Minutes; minute++)
            {
                var requests = (minute + 1) * 6;
                var at = first.AddMinutes(minute);
                spans.AddRange(Otlp.Requests(requests, 0, at.AddSeconds(10)).Select(s => Otlp.Lasting(s, TimeSpan.FromSeconds(1))));
                used += 6 + requests / 2;
                cpu.Add((at.AddSeconds(30), used));
                memory.Add((at.AddSeconds(30), 100_000_000 + requests * 50_000_000L / 60));
                cpuCount.Add((at.AddSeconds(30), 4));
                queue.Add((at.AddSeconds(30), minute == Minutes - 1 ? 7 : 0));
                http.Add((at.AddSeconds(30), 500 + (minute + 1) * 60, 50 + (minute + 1) * 6d));
            }
            // An outbox worker polling: root client spans, many times a second, that are not work.
            var polls = Enumerable.Range(0, 600).Select(i => Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "SELECT outbox", Otlp.Client, first.AddMinutes(Minutes - 1).AddMilliseconds(i * 10)));
            await LocalStack.SendAsync("traces", Otlp.Traces(resource, [.. spans, .. polls,
                Otlp.Lasting(Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), Otlp.SpanId(), "process orders", Otlp.Consumer, first.AddMinutes(Minutes - 1).AddSeconds(10)), TimeSpan.FromSeconds(1))]));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "dotnet.process.cpu.time", start, [], [.. cpu]));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "dotnet.process.memory.working_set", start, [], [.. memory]));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "dotnet.process.cpu.count", start, [], [.. cpuCount]));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "dotnet.thread_pool.queue.length", start, [], [.. queue]));
            await LocalStack.WaitForSpansAsync(service, spans.Count + 1 + 600);

            // Sampled traces: one span a minute, while the host's metric counts 60 requests a minute lasting 0.1 s each.
            await LocalStack.SendAsync("traces", Otlp.Traces(sampledResource,
                Enumerable.Range(0, Minutes).SelectMany(m => Otlp.Requests(1, 0, first.AddMinutes(m).AddSeconds(10))).ToArray()));
            await LocalStack.SendAsync("metrics", Otlp.Histogram(sampledResource, "http.server.request.duration", start, [.. http]));
            await LocalStack.WaitForSpansAsync(sampled, Minutes);
            await DiscoverAsync(database);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Guid id, sampledId;
            await using (var db = database.Context())
            {
                id = (await db.Services.SingleAsync(s => s.Name == service)).Id;
                sampledId = (await db.Services.SingleAsync(s => s.Name == sampled)).Id;
            }
            // A window cutting the last bucket short: that bucket, at the highest load, must not count.
            var window = $"from={Uri.EscapeDataString(first.ToString("O"))}&to={Uri.EscapeDataString(first.AddMinutes(Minutes).ToString("O"))}";
            var cut = $"from={Uri.EscapeDataString(first.ToString("O"))}&to={Uri.EscapeDataString(first.AddMinutes(Minutes - 1).AddSeconds(30).ToString("O"))}";
            Task<HttpResponseMessage> Capacity(string query) => Get(client, $"/api/v1/tenants/{tenant}/telemetry/capacity?{query}", token);

            // Metrics are stored apart from spans: wait until the last point of each has arrived.
            JsonElement result = default, viaMetric = default;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                result = await JsonAsync(await Capacity($"serviceId={id}&{window}"));
                viaMetric = await JsonAsync(await Capacity($"serviceId={sampledId}&{window}"));
                if (result.GetProperty("cpuModel").GetString() == "fitted" && result.GetProperty("memoryModel").GetString() == "fitted"
                    && result.GetProperty("peakQueue").ValueKind == JsonValueKind.Number && result.GetProperty("coresPerInstance").ValueKind == JsonValueKind.Number
                    && viaMetric.GetProperty("requestSource").GetString() == "metrics" && viaMetric.GetProperty("samples").GetInt32() == Minutes - 1) break;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Assert.Equal("spans", result.GetProperty("requestSource").GetString());
            Assert.Equal("fitted", result.GetProperty("cpuModel").GetString());
            Assert.Equal(1, result.GetProperty("instances").GetInt32());
            Assert.Equal(["bench-1"], result.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()!));
            Assert.Equal(4, result.GetProperty("coresPerInstance").GetInt32());
            Assert.Equal(0.5, result.GetProperty("cpuSecondsPerUnit").GetDouble(), 1);
            Assert.Equal(0.1, result.GetProperty("idleCores").GetDouble(), 1);
            Assert.Equal(7, result.GetProperty("peakQueue").GetDouble());
            Assert.Equal(1000, result.GetProperty("responseMs").GetDouble(), 0);
            // The busiest bucket: 72 requests and one message over 60 s, and the message took its own second.
            Assert.Equal(73d / 60, result.GetProperty("peakPerSecond").GetDouble(), 6);
            Assert.Equal(100e6, result.GetProperty("idleMemoryBytes").GetDouble(), tolerance: 2e6);
            Assert.Equal(50e6, result.GetProperty("memoryBytesPerInFlight").GetDouble(), tolerance: 2e6);

            var cutShort = await JsonAsync(await Capacity($"serviceId={id}&{cut}"));
            Assert.Equal(66d / 60, cutShort.GetProperty("peakPerSecond").GetDouble(), 6);
            Assert.Equal(1d / 60, result.GetProperty("peakMessagesPerSecond").GetDouble(), 6);
            Assert.Equal(1d / 60, result.GetProperty("peakMessagesInFlight").GetDouble(), 6);

            // The metric's count wins over sampled spans: 1 request/s lasting 0.1 s, not the single span a minute. The first
            // bucket has no earlier value in the window, and its sampled span doesn't stand in for the count.
            Assert.Equal("metrics", viaMetric.GetProperty("requestSource").GetString());
            Assert.Equal(Minutes - 1, viaMetric.GetProperty("samples").GetInt32());
            Assert.Equal(1, viaMetric.GetProperty("peakPerSecond").GetDouble(), 6);
            Assert.Equal(100, viaMetric.GetProperty("responseMs").GetDouble(), 3);
            Assert.Equal("none", viaMetric.GetProperty("cpuModel").GetString());

            Assert.Equal(HttpStatusCode.BadRequest, (await Capacity(window)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Capacity($"serviceId={Guid.NewGuid()}&{window}")).StatusCode);
        }

        [Fact]
        public async Task A_background_poll_is_not_a_request_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"tel-{_run}-polls";
            var resource = new[] { ("service.name", service), ("service.instance.id", $"{service}-1") };
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            await LocalStack.SendAsync("traces", Otlp.Traces(resource,
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "GET /orders", Otlp.Server, at),
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "SELECT outbox", Otlp.Client, at),
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "SELECT outbox", Otlp.Client, at),
                Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "nightly job", Otlp.Internal, at)));
            await LocalStack.WaitForSpansAsync(service, 4);
            await DiscoverAsync(database);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Guid id;
            await using (var db = database.Context()) id = (await db.Services.SingleAsync(s => s.Name == service)).Id;
            var query = $"serviceId={id}&from={Uri.EscapeDataString(at.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";

            var operations = (await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/telemetry/operations?{query}", token))).GetProperty("items")
                .EnumerateArray().Select(o => o.GetProperty("name").GetString()).Order().ToList();
            Assert.Equal(["GET /orders", "nightly job"], operations);
            var points = (await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/telemetry/service-stats?{query}&stepSeconds=60", token))).GetProperty("points").EnumerateArray();
            Assert.Equal(2d / 60, points.Sum(p => p.GetProperty("requestsPerSecond").GetDouble()), 6);
        }

        [Fact]
        public async Task Capacity_compares_outbox_messages_created_and_delivered_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"tel-{_run}-outbox";
            var resource = new[] { ("service.name", service), ("service.instance.id", $"{service}-1") };
            var now = DateTimeOffset.UtcNow;
            var first = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(-3);

            // 30 messages created in the window's first minute. 10 delivered, 20 s after each was created; 1 failed to send;
            // the rest still waiting. A message with two routes counts once, and "create" spans of other libraries don't count.
            var spans = new List<System.Text.Json.Nodes.JsonObject>();
            for (var i = 0; i < 30; i++)
            {
                var (trace, create, id, at) = (Otlp.TraceId(), Otlp.SpanId(), $"message-{i}", first.AddSeconds(1 + i));
                spans.Add(Otlp.Span(trace, create, null, "create OrderPlaced", Otlp.Producer, at, ("messaging.message.id", id)));
                if (i >= 11) continue;
                var outcome = i == 10 ? "failed" : "published";
                spans.Add(Otlp.Span(trace, Otlp.SpanId(), create, "deliver rabbitmq", Otlp.Internal, at.AddSeconds(20),
                    ("messaging.message.id", id), ("juice.delivery.outcome", outcome)));
                if (i == 0) spans.Add(Otlp.Span(trace, Otlp.SpanId(), create, "deliver second-route", Otlp.Internal, at.AddSeconds(20),
                    ("messaging.message.id", id), ("juice.delivery.outcome", "published")));
            }
            var juice = Otlp.Traces(resource, [.. spans]);
            juice["resourceSpans"]![0]!["scopeSpans"]![0]!["scope"]!["name"] = "Juice";
            await LocalStack.SendAsync("traces", juice);
            await LocalStack.SendAsync("traces", Otlp.Traces(resource, Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "create bucket", Otlp.Producer, first.AddSeconds(5))));
            await LocalStack.WaitForSpansAsync(service, spans.Count + 1);
            await DiscoverAsync(database);

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Guid id2;
            await using (var db = database.Context()) id2 = (await db.Services.SingleAsync(s => s.Name == service)).Id;

            var window = $"from={Uri.EscapeDataString(first.ToString("O"))}&to={Uri.EscapeDataString(first.AddMinutes(2).ToString("O"))}";
            var outbox = (await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/telemetry/capacity?serviceId={id2}&{window}", token))).GetProperty("outbox");

            Assert.Equal(20, outbox.GetProperty("undelivered").GetDouble());
            Assert.Equal(30d / 60, outbox.GetProperty("createdPerSecond").GetDouble(), 6);
            Assert.Equal(10d / 60, outbox.GetProperty("deliveredPerSecond").GetDouble(), 6);
            Assert.Equal(20, outbox.GetProperty("recentDelaySeconds").GetDouble(), 1);
            Assert.True(outbox.GetProperty("fallingBehind").GetBoolean());
        }

        private static Task DiscoverAsync(ScratchDatabase database)
        {
            var services = new ServiceCollection()
                .AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString))
                .BuildServiceProvider();
            return new DiscoveryRunner(services.GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })),
                Options.Create(new DiscoveryOptions { Lag = TimeSpan.Zero, InitialLookback = TimeSpan.FromMinutes(30) }),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System,
                NullLogger<DiscoveryRunner>.Instance).RunOnceAsync(Ct);
        }
    }
}
