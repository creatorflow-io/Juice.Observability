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
            SELECT MetricName, max(t) FROM (
                SELECT MetricName, max(TimeUnix) AS t FROM otel.otel_metrics_gauge WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam} GROUP BY MetricName
                UNION ALL
                SELECT MetricName, max(TimeUnix) AS t FROM otel.otel_metrics_sum WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam} GROUP BY MetricName
                UNION ALL
                SELECT MetricName, max(TimeUnix) AS t FROM otel.otel_metrics_histogram WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName IN {NamesParam} GROUP BY MetricName
            ) GROUP BY MetricName LIMIT {Limit}
            """;

        /// <summary>
        /// When the tenant last reported each of <paramref name="names"/> within the last 24 h (or <c>lookback</c>), in one query for all of them (a rule
        /// list's page); a name not reported is left out. The names are a typed array parameter, never part of the SQL.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastSeenAsync(ClickHouseTelemetrySource ch, string tenantKey,
            IReadOnlyCollection<string> names, DateTimeOffset now, CancellationToken ct, TimeSpan? lookback = null)
        {
            var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
            if (distinct.Length == 0) return new Dictionary<string, DateTimeOffset>();
            var rows = await ch.QueryAsync(SeenQuery, new TelemetryWindow(tenantKey, now - (lookback ?? Lookback), now, distinct.Length),
                r => (Name: r.GetString(0), At: ClickHouseTelemetrySource.Utc(r.GetValue(1))), ct, new Dictionary<string, object> { ["names"] = distinct });
            return rows.ToDictionary(r => r.Name, r => r.At, StringComparer.Ordinal);
        }

        /// <summary>Which of <paramref name="names"/> the tenant reported in the last 24 h (or <c>lookback</c>): <see cref="LastSeenAsync"/>'s names.</summary>
        public static async Task<IReadOnlySet<string>> SeenAsync(ClickHouseTelemetrySource ch, string tenantKey, IReadOnlyCollection<string> names,
            DateTimeOffset now, CancellationToken ct, TimeSpan? lookback = null)
            => new HashSet<string>((await LastSeenAsync(ch, tenantKey, names, now, ct, lookback)).Keys, StringComparer.Ordinal);

        /// <summary>The metric's kind in the tenant's last 24 h (the commonest, should it be reported as more than one), or null when it has not been reported.</summary>
        public static async Task<MetricCatalogEntry?> FindAsync(ClickHouseTelemetrySource ch, string tenantKey, string metric, DateTimeOffset now, CancellationToken ct,
            TimeSpan? lookback = null)
        {
            var rows = await ch.QueryAsync(Query, new TelemetryWindow(tenantKey, now - (lookback ?? Lookback), now, 10),
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
