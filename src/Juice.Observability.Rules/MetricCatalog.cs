using Juice.Observability.Discovery;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    public sealed record MetricCatalogEntry(MetricKind Kind, string Unit);

    /// <summary>What a tenant's metric is: its kind and unit, from the last 24 h of telemetry. Explicit-bucket histograms only.</summary>
    public static class MetricCatalog
    {
        public static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

        private const string MetricParam = "{metric:String}";

        // The metric name is a parameter; the tenant, window and limit are in every part.
        internal const string Query = $"""
            SELECT type, mono, unit, n FROM (
                SELECT 'gauge' AS type, toUInt8(0) AS mono, any(MetricUnit) AS unit, count() AS n
                FROM otel.otel_metrics_gauge WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = {MetricParam}
                UNION ALL
                SELECT 'sum', toUInt8(IsMonotonic), any(MetricUnit), count()
                FROM otel.otel_metrics_sum WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = {MetricParam}
                GROUP BY IsMonotonic
                UNION ALL
                SELECT 'histogram', toUInt8(0), any(MetricUnit), count()
                FROM otel.otel_metrics_histogram WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = {MetricParam}
            ) WHERE n > 0 ORDER BY n DESC LIMIT {Limit}
            """;

        private const string NamesParam = "{names:Array(String)}";

        // The names are one array parameter; the tenant, window and limit are in every part.
        internal const string SeenQuery = $"""
            SELECT MetricName FROM (
                SELECT MetricName FROM otel.otel_metrics_gauge WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam}
                UNION ALL
                SELECT MetricName FROM otel.otel_metrics_sum WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam}
                UNION ALL
                SELECT MetricName FROM otel.otel_metrics_histogram WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam}
            ) GROUP BY MetricName LIMIT {Limit}
            """;

        /// <summary>
        /// Which of <paramref name="names"/> the tenant reported in the last 24 h, in one query for all of them (a rule list's page). The names are
        /// a typed array parameter, never part of the SQL.
        /// </summary>
        public static async Task<IReadOnlySet<string>> SeenAsync(ClickHouseTelemetrySource ch, string tenantKey, IReadOnlyCollection<string> names,
            DateTimeOffset now, CancellationToken ct)
        {
            var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length == 0) return new HashSet<string>();
            var rows = await ch.QueryAsync(SeenQuery, new TelemetryWindow(tenantKey, now - Lookback, now, distinct.Length),
                r => r.GetString(0), ct, new Dictionary<string, object> { ["names"] = distinct });
            return new HashSet<string>(rows, StringComparer.Ordinal);
        }

        /// <summary>The metric's kind in the tenant's last 24 h (the commonest, should it be reported as more than one), or null when it has not been reported.</summary>
        public static async Task<MetricCatalogEntry?> FindAsync(ClickHouseTelemetrySource ch, string tenantKey, string metric, DateTimeOffset now, CancellationToken ct)
        {
            var rows = await ch.QueryAsync(Query, new TelemetryWindow(tenantKey, now - Lookback, now, 10),
                r => (Type: r.GetString(0), Monotonic: Convert.ToInt32(r.GetValue(1)) != 0, Unit: r.GetString(2)), ct,
                new Dictionary<string, object> { ["metric"] = metric });
            if (rows.Count == 0) return null;
            var (type, monotonic, unit) = rows[0];
            return new MetricCatalogEntry(type switch
            {
                "gauge" => MetricKind.Gauge,
                "histogram" => MetricKind.Histogram,
                _ => monotonic ? MetricKind.Counter : MetricKind.UpDownCounter,
            }, unit);
        }
    }
}
