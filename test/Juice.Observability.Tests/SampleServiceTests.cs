namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 12 R8: the Python sample service (<c>deploy/local/tests/sample-service</c>, <c>docker compose --profile sample up -d --build sample-service</c>)
    /// is discovered with spans and four metrics of the four types, with no Juice code. Skipped unless the sample profile is up.
    /// </summary>
    public class SampleServiceTests
    {
        private const string Service = "sample-orders";

        [Fact]
        public async Task SampleService_discovered_with_spans_and_four_metrics_typed_Async()
        {
            LocalStack.RequireAvailable();
            var tenant = LocalStack.Secret("JUICE_PLATFORM_TENANT_ID");
            string Where(string time) => $"JuiceTenant = '{tenant}' AND ServiceName = '{Service}' AND {time} > now() - INTERVAL 3 MINUTE";

            var spans = int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_traces WHERE {Where("Timestamp")}"));
            Assert.SkipUnless(spans > 0, "the sample profile is not up (docker compose --profile sample up -d --build sample-service)");

            // The catalog's types: gauge, sum (monotonic), sum (non-monotonic), histogram.
            var types = await LocalStack.ClickHouseAsync($"""
                SELECT arrayStringConcat(arraySort(groupArray(concat(MetricName, '=', kind))), ',') FROM (
                    SELECT MetricName, 'gauge' AS kind FROM otel.otel_metrics_gauge WHERE {Where("TimeUnix")} GROUP BY MetricName
                    UNION ALL
                    SELECT MetricName, if(IsMonotonic, 'sum', 'updown') FROM otel.otel_metrics_sum WHERE {Where("TimeUnix")} GROUP BY MetricName, IsMonotonic
                    UNION ALL
                    SELECT MetricName, 'histogram' FROM otel.otel_metrics_histogram WHERE {Where("TimeUnix")} GROUP BY MetricName
                )
                """);
            Assert.Equal("sample.inflight=updown,sample.order.duration=histogram,sample.orders.processed=sum,sample.queue.depth=gauge", types);

            // Plain OpenTelemetry Python: no Juice instrumentation scope, no juice.* attribute from the service.
            Assert.Equal("python", await LocalStack.ClickHouseAsync(
                $"SELECT any(ResourceAttributes['telemetry.sdk.language']) FROM otel.otel_traces WHERE {Where("Timestamp")}"));
            Assert.Equal("0", await LocalStack.ClickHouseAsync($"""
                SELECT count() FROM otel.otel_traces
                WHERE {Where("Timestamp")} AND (ScopeName ILIKE 'Juice%' OR arrayExists(k -> k ILIKE 'juice.%' AND k NOT IN ('juice.platform_tenant_id'), mapKeys(SpanAttributes)))
                """));

            // Discovery reads Server and Consumer spans for a service's traffic, and its environment from the resource.
            Assert.Equal("1", await LocalStack.ClickHouseAsync($"""
                SELECT toUInt8(countIf(SpanKind = 'Consumer') > 0 AND anyIf(ResourceAttributes['deployment.environment.name'], SpanKind = 'Consumer') = 'local')
                FROM otel.otel_traces WHERE {Where("Timestamp")}
                """));
        }
    }
}
