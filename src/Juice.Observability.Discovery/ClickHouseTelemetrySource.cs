using System.Data.Common;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Discovery
{
    /// <summary>Reads what discovery needs from the Collector's ClickHouse tables.</summary>
    public interface ITelemetrySource
    {
        Task<IReadOnlyList<ResourceObservation>> ReadResourcesAsync(TelemetryWindow window, CancellationToken cancellationToken);
        Task<IReadOnlyList<ServiceLinkObservation>> ReadServiceLinksAsync(TelemetryWindow window, CancellationToken cancellationToken);
        Task<IReadOnlyList<ResourceUseObservation>> ReadResourceUsesAsync(TelemetryWindow window, CancellationToken cancellationToken);
    }

    /// <summary>A bounded, tenant-scoped read: [<see cref="From"/>, <see cref="To"/>), at most <see cref="Limit"/> rows.</summary>
    public sealed record TelemetryWindow(string TenantKey, DateTimeOffset From, DateTimeOffset To, int Limit)
    {
        /// <summary>
        /// Where span counts start (<see cref="ServiceLinkObservation.FreshSpans"/>): discovery reads an overlap before
        /// its checkpoint to find late telemetry, but counts only what no earlier pass counted. Defaults to <see cref="From"/>.
        /// </summary>
        public DateTimeOffset? CountFrom { get; init; }
    }

    /// <summary>
    /// Platform-owned query templates over the exporter's default tables (requirements §5.1). Every query filters on the
    /// Collector-stamped <c>ResourceAttributes['juice.platform_tenant_id']</c>, is bounded in time and rows, and takes
    /// values only as parameters. Connects as <c>platform_reader</c>, whose profile caps time and memory.
    /// </summary>
    public sealed class ClickHouseTelemetrySource(IOptions<ClickHouseOptions> options) : ITelemetrySource
    {
        public const string Tenant = "ResourceAttributes['juice.platform_tenant_id'] = {tenant:String}";
        public const string From = "{from:DateTime64(9)}";
        public const string To = "{to:DateTime64(9)}";
        public const string Limit = "{limit:UInt32}";
        /// <summary>A span's environment key, as rules and SLOs match it: lowercase and trimmed, <c>unspecified</c> when missing or blank.</summary>
        public const string EnvironmentKey = "if(trim(BOTH ' ' FROM ResourceAttributes['deployment.environment.name']) = '', 'unspecified', lower(trim(BOTH ' ' FROM ResourceAttributes['deployment.environment.name'])))";
        public const string CountFrom = "{count_from:DateTime64(9)}";

        /// <summary>One service (<c>{svc}</c>, <c>{ns}</c>) in one environment (<c>{env}</c>), in any of <see cref="ResourceTables"/>.</summary>
        public const string ServiceMatch = $"ServiceName = {{svc:String}} AND ResourceAttributes['service.namespace'] = {{ns:String}} AND {EnvironmentKey} = {{env:String}}";

        /// <summary><c>namespace/name</c> or <c>name</c> (no namespace), as rules and SLOs name a service.</summary>
        public static (string Namespace, string Name) SplitService(string service)
        {
            var slash = service.LastIndexOf('/');
            return slash < 0 ? ("", service) : (service[..slash], service[(slash + 1)..]);
        }

        /// <summary>Every table a service's telemetry lands in (in <c>otel</c>), with its time column.</summary>
        public static readonly IReadOnlyList<(string Table, string Time)> ResourceTables =
        [
            ("otel_traces", "Timestamp"),
            ("otel_logs", "Timestamp"),
            ("otel_metrics_sum", "TimeUnix"),
            ("otel_metrics_gauge", "TimeUnix"),
            ("otel_metrics_histogram", "TimeUnix"),
            ("otel_metrics_exponential_histogram", "TimeUnix"),
            ("otel_metrics_summary", "TimeUnix"),
        ];

        private const string ResourceColumns = """
            ResourceAttributes['service.namespace'] AS ns, ServiceName AS svc,
            ResourceAttributes['service.instance.id'] AS inst, ResourceAttributes['deployment.environment.name'] AS env,
            ResourceAttributes['service.version'] AS ver, ResourceAttributes['host.name'] AS host_name,
            ResourceAttributes['host.id'] AS host_id, ResourceAttributes['container.id'] AS container_id,
            ResourceAttributes['k8s.pod.uid'] AS pod_uid, ResourceAttributes['k8s.namespace.name'] AS pod_ns,
            ResourceAttributes['k8s.pod.name'] AS pod_name, ResourceAttributes['process.pid'] AS pid
            """;

        private const string ResourceKeys = "ns, svc, inst, env, ver, host_name, host_id, container_id, pod_uid, pod_ns, pod_name, pid";

        private static readonly string ResourcesQuery = $"""
            SELECT {ResourceKeys}, min(first_seen) AS first_seen, max(last_seen) AS last_seen, sum(records) AS records
            FROM (
            {string.Join("\nUNION ALL\n", ResourceTables.Select(t => $"""
                SELECT {ResourceColumns}, min({t.Time}) AS first_seen, max({t.Time}) AS last_seen, count() AS records
                FROM otel.{t.Table}
                WHERE {Tenant} AND {t.Time} >= {From} AND {t.Time} < {To}
                GROUP BY {ResourceKeys}
                """))}
            )
            GROUP BY {ResourceKeys}
            ORDER BY last_seen DESC
            LIMIT {Limit}
            """;

        // A client or producer span and its server or consumer child in another service. The parent may start up to
        // five minutes before the window (a long call or a queued message).
        private static readonly string ServiceLinksQuery = $"""
            SELECT p.ns AS src_ns, p.svc AS src, p.env AS env, c.ns AS dst_ns, c.svc AS dst, p.kind AS kind,
                   count() AS spans, min(c.ts) AS first_seen, max(c.ts) AS last_seen, countIf(c.ts >= {CountFrom}) AS fresh_spans
            FROM (
                SELECT TraceId, ParentSpanId, ServiceName AS svc, ResourceAttributes['service.namespace'] AS ns, Timestamp AS ts
                FROM otel.otel_traces
                WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To}
                  AND SpanKind IN ('Server', 'Consumer') AND ParentSpanId != ''
            ) AS c
            INNER JOIN (
                SELECT TraceId, SpanId, ServiceName AS svc, ResourceAttributes['service.namespace'] AS ns,
                       ResourceAttributes['deployment.environment.name'] AS env, toString(SpanKind) AS kind
                FROM otel.otel_traces
                WHERE {Tenant} AND Timestamp >= {From} - INTERVAL 5 MINUTE AND Timestamp < {To}
                  AND SpanKind IN ('Client', 'Producer')
            ) AS p ON c.TraceId = p.TraceId AND c.ParentSpanId = p.SpanId
            WHERE c.svc != p.svc OR c.ns != p.ns
            GROUP BY src_ns, src, env, dst_ns, dst, kind
            LIMIT {Limit}
            """;

        // Spans that name a messaging destination or a database. Older semantic conventions are read as fallbacks.
        private static readonly string ResourceUsesQuery = $"""
            SELECT ResourceAttributes['service.namespace'] AS ns, ServiceName AS svc,
                   ResourceAttributes['deployment.environment.name'] AS env, toString(SpanKind) AS kind,
                   SpanAttributes['messaging.system'] AS msys, SpanAttributes['messaging.destination.name'] AS mdest,
                   if(SpanAttributes['db.system.name'] != '', SpanAttributes['db.system.name'], SpanAttributes['db.system']) AS dbsys,
                   if(SpanAttributes['db.namespace'] != '', SpanAttributes['db.namespace'], SpanAttributes['db.name']) AS dbns,
                   SpanAttributes['server.address'] AS addr,
                   count() AS spans, min(Timestamp) AS first_seen, max(Timestamp) AS last_seen,
                   countIf(Timestamp >= {CountFrom}) AS fresh_spans
            FROM otel.otel_traces
            WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND ServiceName != ''
              AND ((SpanKind IN ('Producer', 'Consumer') AND SpanAttributes['messaging.destination.name'] != '')
                OR (SpanKind = 'Client' AND (SpanAttributes['db.system.name'] != '' OR SpanAttributes['db.system'] != '')))
            GROUP BY ns, svc, env, kind, msys, mdest, dbsys, dbns, addr
            LIMIT {Limit}
            """;

        public Task<IReadOnlyList<ResourceObservation>> ReadResourcesAsync(TelemetryWindow window, CancellationToken cancellationToken)
            => QueryAsync(ResourcesQuery, window, r => new ResourceObservation
            {
                ServiceNamespace = r.GetString(0),
                ServiceName = r.GetString(1),
                InstanceId = r.GetString(2),
                Environment = r.GetString(3),
                ServiceVersion = r.GetString(4),
                HostName = r.GetString(5),
                HostId = r.GetString(6),
                ContainerId = r.GetString(7),
                PodUid = r.GetString(8),
                PodNamespace = r.GetString(9),
                PodName = r.GetString(10),
                ProcessId = r.GetString(11),
                FirstSeen = Utc(r.GetValue(12)),
                LastSeen = Utc(r.GetValue(13)),
                Records = Convert.ToInt64(r.GetValue(14)),
            }, cancellationToken);

        public Task<IReadOnlyList<ServiceLinkObservation>> ReadServiceLinksAsync(TelemetryWindow window, CancellationToken cancellationToken)
            => QueryAsync(ServiceLinksQuery, window, r => new ServiceLinkObservation
            {
                SourceNamespace = r.GetString(0),
                SourceName = r.GetString(1),
                Environment = r.GetString(2),
                TargetNamespace = r.GetString(3),
                TargetName = r.GetString(4),
                SourceKind = r.GetString(5),
                Spans = Convert.ToInt64(r.GetValue(6)),
                FirstSeen = Utc(r.GetValue(7)),
                LastSeen = Utc(r.GetValue(8)),
                FreshSpans = Convert.ToInt64(r.GetValue(9)),
            }, cancellationToken);

        public Task<IReadOnlyList<ResourceUseObservation>> ReadResourceUsesAsync(TelemetryWindow window, CancellationToken cancellationToken)
            => QueryAsync(ResourceUsesQuery, window, r => new ResourceUseObservation
            {
                ServiceNamespace = r.GetString(0),
                ServiceName = r.GetString(1),
                Environment = r.GetString(2),
                SpanKind = r.GetString(3),
                MessagingSystem = r.GetString(4),
                MessagingDestination = r.GetString(5),
                DbSystem = r.GetString(6),
                DbNamespace = r.GetString(7),
                ServerAddress = r.GetString(8),
                Spans = Convert.ToInt64(r.GetValue(9)),
                FirstSeen = Utc(r.GetValue(10)),
                LastSeen = Utc(r.GetValue(11)),
                FreshSpans = Convert.ToInt64(r.GetValue(12)),
            }, cancellationToken);

        /// <summary>
        /// Runs a platform query template with the window's tenant, time bounds and row limit as parameters, plus any
        /// template-specific ones. Values are only ever passed as parameters, never formatted into the SQL.
        /// </summary>
        public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, TelemetryWindow window, Func<DbDataReader, T> map,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, object>? parameters = null)
        {
            await using var connection = new ClickHouseConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.AddParameter("tenant", window.TenantKey);
            command.AddParameter("from", window.From.UtcDateTime);
            command.AddParameter("to", window.To.UtcDateTime);
            command.AddParameter("limit", (uint)window.Limit);
            command.AddParameter("count_from", (window.CountFrom ?? window.From).UtcDateTime);
            foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
            {
                command.AddParameter(name, value);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }
            return rows;
        }

        public static DateTimeOffset Utc(object value) => value switch
        {
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
            DateTimeOffset o => o.ToUniversalTime(),
            _ => throw new InvalidCastException($"Unexpected ClickHouse time value {value.GetType()}"),
        };
    }
}
