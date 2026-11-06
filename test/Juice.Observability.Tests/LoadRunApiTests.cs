using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>A load test marks its window through the API, and the results are read from the telemetry of that window.</summary>
    public class LoadRunApiTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static Task<HttpResponseMessage> Post(HttpClient client, string path, string token, object? body = null, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (body is not null) request.Content = JsonContent.Create(body);
            return client.SendAsync(request);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }

        private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }

        [Fact]
        public async Task A_run_is_marked_with_roles_audit_and_idempotency_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, tester) = (await LocalStack.TokenAsync("victor"), await LocalStack.TokenAsync("erin"));
            var tenant = (await Json(await Get(client, "/api/v1/tenants", viewer))).GetProperty("items")[0].GetProperty("id").GetGuid();
            var runs = $"/api/v1/tenants/{tenant}/load-runs";

            using (var denied = await Post(client, runs, viewer, new { name = "first" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, runs, tester, new { name = " " })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, runs, tester, new { name = new string('x', 129) })).StatusCode);

            var key = Guid.NewGuid().ToString("N");
            var started = await Post(client, runs, tester, new { name = "first", environment = "Production" }, key);
            Assert.Equal(HttpStatusCode.Created, started.StatusCode);
            var run = await Json(started);
            var id = run.GetProperty("id").GetGuid();
            Assert.Equal(("first", "production"), (run.GetProperty("name").GetString(), run.GetProperty("environment").GetString()));
            Assert.Equal(JsonValueKind.Null, run.GetProperty("endedAt").ValueKind);

            // The test's retry gets the same run, and the same key can't start a different one.
            var replay = await Post(client, runs, tester, new { name = "first", environment = "production" }, key);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(id, (await Json(replay)).GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(client, runs, tester, new { name = "other" }, key)).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, $"{runs}/{id}/end", viewer)).StatusCode);
            var ended = await Json(await Post(client, $"{runs}/{id}/end", tester));
            var endedAt = ended.GetProperty("endedAt").GetDateTimeOffset();
            // Ending it again changes nothing. (The database keeps microseconds.)
            Assert.Equal(endedAt, (await Json(await Post(client, $"{runs}/{id}/end", tester))).GetProperty("endedAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
            Assert.Equal(HttpStatusCode.NotFound, (await Post(client, $"{runs}/{Guid.NewGuid()}/end", tester)).StatusCode);

            var listed = Assert.Single((await Json(await Get(client, $"{runs}?environment=production", viewer))).GetProperty("items").EnumerateArray());
            Assert.Equal(id, listed.GetProperty("id").GetGuid());
            Assert.Empty((await Json(await Get(client, $"{runs}?environment=staging", viewer))).GetProperty("items").EnumerateArray());

            // Another tenant's run can't be read, ended or listed from this tenant.
            await using var db = database.Context();
            var other = new PlatformTenant { Key = $"other-{_run}", DisplayName = "Other" };
            db.Tenants.Add(other);
            var foreign = new LoadRun { TenantId = other.Id, Name = "foreign", StartedAt = DateTimeOffset.UtcNow, StartedBy = "test" };
            db.LoadRuns.Add(foreign);
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await Post(client, $"{runs}/{foreign.Id}/end", tester)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"{runs}/{foreign.Id}/results", viewer)).StatusCode);
            Assert.DoesNotContain((await Json(await Get(client, runs, viewer))).GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == foreign.Id);

            var audit = await db.AuditRecords.AsNoTracking().Where(a => a.TargetType == "load-run").ToListAsync();
            Assert.Equal(2, audit.Count(a => a.Outcome == AuditOutcome.Denied));
            Assert.Single(audit, a => a.Operation == "load-run.start" && a.Outcome == AuditOutcome.Succeeded);
            Assert.Single(audit, a => a.Operation == "load-run.end" && a.Outcome == AuditOutcome.Succeeded);
        }

        [Fact]
        public async Task Results_come_from_the_telemetry_of_the_window_and_find_the_limit_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"run-{_run}-orders";
            var environment = $"run-{_run}";
            var resource = new[] { ("service.name", service), ("service.instance.id", $"{service}-1"), ("deployment.environment.name", environment) };
            var now = DateTimeOffset.UtcNow;
            var first = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(-10);

            // Six minutes, so 15 s steps, with 1, 2, ... 6 requests a step; latency jumps from 10 ms to 300 ms in the fourth minute.
            // A minute of load before the run, far slower, must not count.
            var spans = new List<System.Text.Json.Nodes.JsonObject>();
            spans.AddRange(Otlp.Requests(50, 0, first.AddMinutes(-1).AddSeconds(5)).Select(s => Otlp.Lasting(s, TimeSpan.FromSeconds(5))));
            for (var minute = 0; minute < 6; minute++)
            {
                var latency = TimeSpan.FromMilliseconds(minute >= 3 ? 300 : 10);
                for (var step = 0; step < 4; step++)
                {
                    spans.AddRange(Otlp.Requests(minute + 1, 0, first.AddMinutes(minute).AddSeconds(step * 15 + 1)).Select(s => Otlp.Lasting(s, latency)));
                }
            }
            await LocalStack.SendAsync("traces", Otlp.Traces(resource, [.. spans]));
            await LocalStack.WaitForSpansAsync(service, spans.Count);
            var queue = Enumerable.Range(0, 6).Select(m => (first.AddMinutes(m).AddSeconds(30), m == 5 ? 7L : 0L)).ToArray();
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "dotnet.thread_pool.queue.length", first, [], queue));

            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var viewer = await LocalStack.TokenAsync("victor");
            var tenant = (await Json(await Get(client, "/api/v1/tenants", viewer))).GetProperty("items")[0].GetProperty("id").GetGuid();
            Guid runId;
            await using (var db = database.Context())
            {
                var run = new LoadRun { TenantId = tenant, Name = "rising", Environment = environment, StartedAt = first, EndedAt = first.AddMinutes(6), StartedBy = "test" };
                db.LoadRuns.Add(run);
                await db.SaveChangesAsync();
                runId = run.Id;
            }
            var results = $"/api/v1/tenants/{tenant}/load-runs/{runId}/results";

            JsonElement result = default;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                result = await Json(await Get(client, results, viewer));
                if (result.GetProperty("services").EnumerateArray().Any(s => s.GetProperty("points").EnumerateArray().Any(p => p.GetProperty("queue").ValueKind == JsonValueKind.Number && p.GetProperty("queue").GetDouble() == 7))) break;
                await Task.Delay(1000);
            }

            Assert.Equal("rising", result.GetProperty("run").GetProperty("name").GetString());
            var orders = Assert.Single(result.GetProperty("services").EnumerateArray());
            Assert.Equal(service, orders.GetProperty("name").GetString());
            Assert.Equal(15, result.GetProperty("stepSeconds").GetInt32());
            Assert.Equal(6d / 15, orders.GetProperty("peakPerSecond").GetDouble(), 6);
            Assert.Equal(300, orders.GetProperty("p95AtPeakMs").GetDouble(), 0);
            var limit = orders.GetProperty("limit");
            Assert.Equal("latency", limit.GetProperty("reason").GetString());
            Assert.Equal(first.AddMinutes(3), limit.GetProperty("at").GetDateTimeOffset());
            Assert.Equal(4d / 15, limit.GetProperty("perSecond").GetDouble(), 6);

            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"/api/v1/tenants/{tenant}/load-runs/{Guid.NewGuid()}/results", viewer)).StatusCode);
        }
    }
}
