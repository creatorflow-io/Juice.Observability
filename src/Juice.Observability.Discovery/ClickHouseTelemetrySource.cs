using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using Juice.Observability.Data;
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

        // Phase 11 R4: every platform query, timed and sized per caller (PlatformCaller: a worker, or an API endpoint). The same caller is in
        // ClickHouse's own system.query_log as log_comment = juice:<caller>, the authoritative cross-check (the summary header the driver reads
        // its statistics from can be partial for a streamed result).
        private static readonly Meter Meter = new(WorkerPulse.MeterName);
        private static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>("juice.observability.clickhouse.query.duration", "s",
            "A platform query to ClickHouse, by caller and outcome (success, failure, timeout: cut by the caller's own deadline, canceled: the host or request stopping, busy: an API query refused for load, by the API or by ClickHouse, unavailable: ClickHouse not reached)");
        private static readonly Counter<long> QueryReadRows = Meter.CreateCounter<long>("juice.observability.clickhouse.query.read_rows", "{row}",
            "Rows ClickHouse read for platform queries, by caller");
        private static readonly Counter<long> QueryReadBytes = Meter.CreateCounter<long>("juice.observability.clickhouse.query.read_bytes", "By",
            "Bytes ClickHouse read for platform queries, by caller");

        // Phase 11 R11: the API's queries, at most ClickHouse:Api:MaxConcurrent at once on this replica. The workers' aren't bounded here.
        private readonly SemaphoreSlim apiSlots = new(options.Value.Api.MaxConcurrent, options.Value.Api.MaxConcurrent);
        private static readonly UpDownCounter<long> ApiRunning = Meter.CreateUpDownCounter<long>("juice.observability.clickhouse.api.running", "{query}",
            "API queries to ClickHouse running now on this replica, against ClickHouse:Api:MaxConcurrent");

        /// <summary>Whether a caller is the API answering a request (<c>api:</c> and its route), not a worker.</summary>
        public static bool IsApi(string caller) => caller.StartsWith("api:", StringComparison.Ordinal);

        // ClickHouse's codes for a query it refused or cut under load: TIMEOUT_EXCEEDED, TOO_MANY_SIMULTANEOUS_QUERIES, MEMORY_LIMIT_EXCEEDED.
        private static readonly int[] BusyCodes = [159, 202, 241];

        /// <summary>What a platform query is tagged with in ClickHouse's <c>system.query_log</c>: <c>juice:</c> and the caller.</summary>
        public static string LogComment(string caller) => $"juice:{caller}";

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
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, TelemetryWindow window, Func<DbDataReader, T> map,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, object>? parameters = null)
            => QueryCoreAsync(sql, window, map, parameters, takeSlot: true, cancellationToken);

        /// <summary>The newest slice a newest-first search reads first (Phase 11 R12).</summary>
        public static readonly TimeSpan NewestSlice = TimeSpan.FromMinutes(5);

        /// <summary>How much wider each next slice is: 5 minutes, 1 hour, 12 hours, then the whole window.</summary>
        public const int SliceGrowth = 12;

        /// <summary>
        /// A newest-first search, read in widening slices (Phase 11 R12). The template bounds its search below by
        /// <c>{slice_from:DateTime64(9)}</c> as well as <c>{from}</c>, and orders newest first. A slice ends at
        /// <paramref name="newest"/> (a cursor's position) or the window's end, starts <see cref="NewestSlice"/> before it, and
        /// widens <see cref="SliceGrowth"/> times until it returns <paramref name="enough"/> rows or covers the window. The
        /// answer is the same as one read of the whole window: when a slice already holds enough rows, nothing older can
        /// rank above them. An API caller holds one query slot for every slice.
        /// </summary>
        public async Task<IReadOnlyList<T>> QueryNewestFirstAsync<T>(string sql, TelemetryWindow window, Func<DbDataReader, T> map, int enough,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, object>? parameters = null, DateTimeOffset? newest = null)
        {
            var caller = PlatformCaller.Current;
            var holding = false;
            if (IsApi(caller))
            {
                var started = Stopwatch.GetTimestamp();
                if (!await apiSlots.WaitAsync(options.Value.Api.QueueTimeout, cancellationToken))
                {
                    QueryDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                        new KeyValuePair<string, object?>("caller", caller), new KeyValuePair<string, object?>("outcome", "busy"));
                    throw new TelemetryBusyException($"{options.Value.Api.MaxConcurrent} telemetry queries are already running on this replica");
                }
                holding = true;
                ApiRunning.Add(1);
            }
            try
            {
                var top = newest is { } at && at < window.To ? at : window.To;
                for (var slice = NewestSlice; ; slice *= SliceGrowth)
                {
                    var whole = top - slice <= window.From;
                    var sliced = new Dictionary<string, object>(parameters ?? new Dictionary<string, object>())
                    {
                        ["slice_from"] = (whole ? window.From : top - slice).UtcDateTime,
                    };
                    var rows = await QueryCoreAsync(sql, window, map, sliced, takeSlot: false, cancellationToken);
                    if (whole || rows.Count >= enough) return rows;
                }
            }
            finally
            {
                if (holding)
                {
                    ApiRunning.Add(-1);
                    apiSlots.Release();
                }
            }
        }

        private async Task<IReadOnlyList<T>> QueryCoreAsync<T>(string sql, TelemetryWindow window, Func<DbDataReader, T> map,
            IReadOnlyDictionary<string, object>? parameters, bool takeSlot, CancellationToken cancellationToken)
        {
            var caller = PlatformCaller.Current;
            var api = IsApi(caller);
            var started = Stopwatch.GetTimestamp();
            var outcome = "failure";
            ClickHouseCommand? command = null;
            var holding = false;
            try
            {
                if (api && takeSlot)
                {
                    if (!await apiSlots.WaitAsync(options.Value.Api.QueueTimeout, cancellationToken))
                    {
                        outcome = "busy";
                        throw new TelemetryBusyException($"{options.Value.Api.MaxConcurrent} telemetry queries are already running on this replica");
                    }
                    holding = true;
                    ApiRunning.Add(1);
                }
                var rows = await RunAsync(sql, window, map, parameters, caller, api, c => command = c, cancellationToken);
                outcome = "success";
                return rows;
            }
            catch (ClickHouseServerException e) when (api && BusyCodes.Contains(e.ErrorCode))
            {
                outcome = "busy";
                throw new TelemetryBusyException("ClickHouse refused or cut the query under load", e);
            }
            catch (HttpRequestException e) when (api && !cancellationToken.IsCancellationRequested)
            {
                outcome = "unavailable";
                throw new TelemetryUnavailableException("ClickHouse can't be reached", e);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled while what stops the caller (the host, or the request) isn't: the caller's own deadline cut it, a timeout. A caller
                // that didn't say what stops it is counted as canceled, the cautious reading.
                outcome = PlatformCaller.Stopping is { IsCancellationRequested: false } stopping && stopping.CanBeCanceled ? "timeout" : "canceled";
                throw;
            }
            finally
            {
                if (holding)
                {
                    ApiRunning.Add(-1);
                    apiSlots.Release();
                }
                var tag = new KeyValuePair<string, object?>("caller", caller);
                QueryDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag, new KeyValuePair<string, object?>("outcome", outcome));
                if (command?.QueryStats is { } stats)
                {
                    QueryReadRows.Add(stats.ReadRows, tag);
                    QueryReadBytes.Add(stats.ReadBytes, tag);
                }
            }
        }

        private async Task<IReadOnlyList<T>> RunAsync<T>(string sql, TelemetryWindow window, Func<DbDataReader, T> map,
            IReadOnlyDictionary<string, object>? parameters, string caller, bool api, Action<ClickHouseCommand> created, CancellationToken cancellationToken)
        {
            await using var connection = new ClickHouseConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            created(command);
            command.CommandText = sql;
            command.CustomSettings["log_comment"] = LogComment(caller);
            if (api)
            {
                // Tighter than the reader profile's, which caps every query (60 s): a search gives up sooner and takes fewer cores.
                command.CustomSettings["max_execution_time"] = options.Value.Api.MaxExecutionSeconds;
                command.CustomSettings["max_threads"] = options.Value.Api.MaxThreads;
            }
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
