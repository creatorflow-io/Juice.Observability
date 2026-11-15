using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The API's ClickHouse queries are bounded (Phase 11 R11): at most <c>ClickHouse:Api:MaxConcurrent</c> at once per replica, each with a tighter
    /// time and thread limit, and a refused or cut query is a 503 "Busy, try again". The workers' queries aren't bounded.
    /// </summary>
    public class TelemetryBusyTests
    {
        private static readonly TelemetryWindow Window = new(LocalStack.TenantKey, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 10);

        private static ClickHouseTelemetrySource Source(ClickHouseApiOptions api, string? connection = null)
            => new(Options.Create(new ClickHouseOptions { ConnectionString = connection ?? LocalStack.ClickHouseReader, Api = api }));

        private static Task<IReadOnlyList<long>> Run(ClickHouseTelemetrySource source, string caller, string sql)
        {
            PlatformCaller.Set(caller);
            return source.QueryAsync(sql, Window, r => Convert.ToInt64(r.GetValue(0)), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task An_api_query_past_the_bound_is_busy_and_a_workers_query_isnt_held_back_Async()
        {
            LocalStack.RequireAvailable();
            var source = Source(new ClickHouseApiOptions { MaxConcurrent = 1, QueueTimeout = TimeSpan.Zero });

            var running = Task.Run(() => Run(source, "api:/telemetry/traces", "SELECT sleepEachRow(0.5) FROM numbers(4)"), TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<TelemetryBusyException>(() => Run(source, "api:/telemetry/logs", "SELECT 1"));
            Assert.Equal([1L], await Run(source, "health", "SELECT 1"));
            await running;
            // The slot is free again.
            Assert.Equal([1L], await Run(source, "api:/telemetry/logs", "SELECT 1"));
        }

        [Fact]
        public async Task An_api_query_takes_the_tighter_time_limit_and_is_busy_when_cut_Async()
        {
            LocalStack.RequireAvailable();
            var source = Source(new ClickHouseApiOptions { MaxExecutionSeconds = 1, MaxThreads = 1 });

            var settings = await Run(source, "api:/telemetry/traces",
                "SELECT toInt64(getSetting('max_execution_time')) * 1000 + toInt64(getSetting('max_threads'))");
            Assert.Equal([1001L], settings);
            await Assert.ThrowsAsync<TelemetryBusyException>(() => Run(source, "api:/telemetry/traces", "SELECT sum(sleepEachRow(0.5)) FROM numbers(6) SETTINGS max_block_size = 1"));
            // A worker keeps the profile's limits.
            Assert.NotEqual([1001L], await Run(source, "health",
                "SELECT toInt64(getSetting('max_execution_time')) * 1000 + toInt64(getSetting('max_threads'))"));
        }

        [Fact]
        public async Task A_search_while_clickhouse_is_out_of_reach_is_a_503_not_a_500_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["ClickHouse:ConnectionString"] = "Host=127.0.0.1;Port=1;Username=platform_reader;Password=x;Database=otel",
            });
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = (await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();

            using var response = await ApiTests.Get(client, $"/api/v1/tenants/{tenant}/telemetry/traces", token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("15", response.Headers.GetValues("Retry-After").Single());
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("Telemetry store unavailable", problem.GetProperty("title").GetString());
            Assert.True(problem.TryGetProperty("correlationId", out _));
        }
    }
}
