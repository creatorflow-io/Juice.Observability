using Juice.Observability.Discovery;

namespace Juice.Observability.Rules
{
    public sealed record MetricCatalogEntry(MetricKind Kind, string Unit);

    /// <summary>What a tenant's metric is: its kind and unit, from the last 24 h of telemetry.</summary>
    public static class MetricCatalog
    {
        // Stub in the first commit; the lookup follows.
        public static Task<MetricCatalogEntry?> FindAsync(ClickHouseTelemetrySource ch, string tenantKey, string metric, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult<MetricCatalogEntry?>(null);
    }
}
