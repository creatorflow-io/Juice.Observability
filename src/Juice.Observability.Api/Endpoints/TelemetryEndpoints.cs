using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record TraceSummaryDto(string TraceId, DateTimeOffset StartedAt, double DurationMs, string RootService,
        string RootOperation, string? Environment, string? RootInstance, string? RootHost, long SpanCount, long ErrorCount,
        IReadOnlyList<string> Services);

    public sealed record SpanEventDto(string Name, DateTimeOffset At, IReadOnlyDictionary<string, string> Attributes);

    public sealed record SpanLinkDto(string TraceId, string SpanId);

    /// <summary>What a client, producer or consumer span reaches: a service (seen running the child span) or a resource.</summary>
    public sealed record SpanTargetDto(string Kind, Guid Id, string Name);

    public sealed record SpanDto(string SpanId, string? ParentSpanId, Guid? ServiceId, string Service, string? Environment, string? Instance,
        string? Host, string Name, string Kind, DateTimeOffset StartedAt, double DurationMs, string Status, string? StatusMessage,
        string? Scope, string? ScopeVersion, IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<SpanEventDto> Events,
        IReadOnlyList<SpanLinkDto> Links, SpanTargetDto? Target, Guid? InstanceId = null);

    public sealed record TraceDto(string TraceId, IReadOnlyList<SpanDto> Spans, bool Truncated);

    public sealed record LogDto(DateTimeOffset Timestamp, string Severity, int SeverityNumber, string Service, string? Environment,
        string? Instance, string Body, bool BodyTruncated, string? TraceId, string? SpanId, IReadOnlyDictionary<string, string> Attributes);

    public sealed record OperationDto(string Name, string Kind, long Count, double RequestsPerSecond, double ErrorRate,
        double P50Ms, double P95Ms, double P99Ms, DateTimeOffset LastSeen);

    public sealed record MetricInfoDto(string Name, string Type, string Unit, string Description);

    public sealed record PointDto(DateTimeOffset At, double? Value);

    public sealed record SeriesDto(string Name, string Type, string Unit, string Aggregation, int StepSeconds, IReadOnlyList<PointDto> Points);

    public sealed record ServiceStatsPointDto(DateTimeOffset At, double RequestsPerSecond, double MessagesPerSecond, double ErrorRate, double? P95Ms);

    public sealed record ServiceStatsDto(int StepSeconds, IReadOnlyList<ServiceStatsPointDto> Points);

    /// <summary>One edge's traffic in a window.</summary>
    public sealed record DependencyStatsDto(Guid DependencyId, long Count, double ErrorRate);

    /// <summary>The traffic of the newest edges; <c>Truncated</c> means others were left out.</summary>
    public sealed record DependencyStatsPageDto(IReadOnlyList<DependencyStatsDto> Items, bool Truncated);

    public sealed record DependencyEvidenceDto(Guid DependencyId, string Relation, string Confidence, long Count, double ErrorRate,
        double? P95Ms, DateTimeOffset? LastSeen, IReadOnlyList<string> SlowestTraces, IReadOnlyList<string> ErrorTraces);

    public sealed record SearchHitDto(string Kind, string Id, string Title, string? TraceId);

    /// <summary>A bounded page of telemetry. Trace and log searches also give <c>NextCursor</c> when there is more.</summary>
    public sealed record TelemetryPage<T>(IReadOnlyList<T> Items, bool Truncated, string? NextCursor = null);

    /// <summary>
    /// Telemetry exploration for Juice Console (UI architecture §3.3, Console requirements §5–§13): traces, logs, metrics,
    /// operations and dependency evidence through the Juice API, never ClickHouse directly. Platform-owned query templates
    /// only: every query filters on the Collector-stamped tenant, is bounded in time and rows, takes values only as
    /// parameters, and runs as <c>platform_reader</c> with its caps. Services, instances, resources and dependencies are
    /// named by their platform ids; the API maps them to telemetry attributes.
    /// </summary>
    public static class TelemetryEndpoints
    {
        public const string TenantKeyItem = "juice.tenant_key";
        public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(1);
        public static readonly TimeSpan MaxSearchRange = TimeSpan.FromHours(24);
        public static readonly TimeSpan MaxSeriesRange = TimeSpan.FromDays(7);
        public const int MaxSpans = 1000;
        public const int MaxPoints = 500;
        public const int MaxBody = 4096;
        public const int MaxAttributes = 64;
        public const int MaxText = 256;
        public const int MaxAttributeFilters = 5;

        private static readonly Regex TraceIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex SpanIdPattern = new("^[0-9a-f]{16}$", RegexOptions.Compiled);
        // A W3C traceparent, "00-<trace id>-<span id>-<flags>": what Juice.Audit before 10.4 stored as a record's trace id.
        private static readonly Regex TraceParentPattern = new("^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.Compiled);
        private static readonly Regex AttributeKeyPattern = new(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.Compiled);
        private static readonly Regex MetricNamePattern = new(@"^[A-Za-z_:][A-Za-z0-9_.:/\-]{0,254}$", RegexOptions.Compiled);

        private const string Environment = "ResourceAttributes['deployment.environment.name']";
        private const string Instance = "ResourceAttributes['service.instance.id']";
        private const string Host = "ResourceAttributes['host.name']";
        private const string DbSystem = "lowerUTF8(if(SpanAttributes['db.system.name'] != '', SpanAttributes['db.system.name'], SpanAttributes['db.system']))";

        public static RouteGroupBuilder MapTelemetryEndpoints(this RouteGroupBuilder tenant)
        {
            var telemetry = tenant.MapGroup("/telemetry");
            telemetry.MapGet("/traces", SearchTracesAsync);
            telemetry.MapGet("/traces/{traceId}", GetTraceAsync);
            telemetry.MapGet("/logs", SearchLogsAsync);
            telemetry.MapGet("/operations", ListOperationsAsync);
            telemetry.MapGet("/metrics", ListMetricsAsync);
            telemetry.MapGet("/metrics/series", GetSeriesAsync);
            telemetry.MapGet("/service-stats", GetServiceStatsAsync);
            telemetry.MapGet("/capacity", GetCapacityAsync);
            telemetry.MapGet("/dependencies", ListDependencyStatsAsync);
            telemetry.MapGet("/dependencies/{dependencyId:guid}", GetDependencyEvidenceAsync);
            tenant.MapGet("/search", SearchAsync);
            return tenant;
        }

        // ─── filters ───

        /// <summary>Filters shared by the telemetry queries. Platform ids are resolved here; telemetry names never come from input.</summary>
        public sealed record Filter
        {
            public Guid? ServiceId { get; init; }
            public string? Environment { get; init; }
            public Guid? InstanceId { get; init; }
            public Guid? ResourceId { get; init; }
            public Guid? DependencyId { get; init; }
            public string? Operation { get; init; }
            public string? TraceId { get; init; }
            public string? SpanId { get; init; }
            public string[]? Attribute { get; init; }
            public DateTimeOffset? From { get; init; }
            public DateTimeOffset? To { get; init; }
        }

        /// <summary>A validated query scope: the window and the span-level and resource-level conditions.</summary>
        private sealed class Scope(TelemetryWindow window)
        {
            public TelemetryWindow Window { get; } = window;
            public Dictionary<string, object> Parameters { get; } = [];
            /// <summary>Conditions with <c>{time}</c> for the table's time column.</summary>
            public List<string> Conditions { get; } = [$"{Tenant}", $"{{time}} >= {From}", $"{{time}} < {To}"];
            /// <summary>Conditions only spans can meet (span attributes, names, ids); empty for logs and metrics.</summary>
            public List<string> SpanConditions { get; } = [];

            public string Where(string time, bool spans = false)
                => string.Join(" AND ", (spans ? Conditions.Concat(SpanConditions) : Conditions).Select(c => c.Replace("{time}", time)));

            public string Add(string name, object value)
            {
                var key = $"p{Parameters.Count}";
                Parameters[key] = value;
                return $"{{{key}:{name}}}";
            }
        }

        /// <summary>
        /// Where a newest-first page of traces or logs ended: a time in Unix nanoseconds and a tie-breaker, so the next page
        /// starts strictly after it and rows that share a timestamp are neither skipped nor repeated. Clients pass it back
        /// as <c>cursor</c> with the same window and filters.
        /// </summary>
        private readonly record struct Position(long Nanos, string Key)
        {
            public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Nanos}:{Key}"));

            public static bool TryParse(string? text, out Position? position)
            {
                position = null;
                if (text is not { Length: > 0 }) return true;
                try
                {
                    var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(text)).Split(':', 2);
                    if (parts.Length != 2 || !long.TryParse(parts[0], out var nanos) || parts[1] is not { Length: > 0 and <= 64 }) return false;
                    position = new Position(nanos, parts[1]);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }

        private static IResult BadCursor() => Bad("Invalid cursor", "cursor is the nextCursor of a previous page.");

        private sealed class FilterException(IResult problem) : Exception
        {
            public IResult Problem { get; } = problem;
        }

        private static IResult Bad(string title, string detail)
            => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

        private static FilterException Invalid(string title, string detail) => new(Bad(title, detail));

        private static FilterException Missing(string what) => new(Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found"));

        private static async Task<Scope> ScopeAsync(HttpContext http, PlatformDbContext db, TimeProvider clock, Filter filter, int limit,
            TimeSpan maxRange, CancellationToken ct)
        {
            var end = filter.To ?? clock.GetUtcNow();
            var start = filter.From ?? end - DefaultRange;
            if (start >= end || end - start > maxRange)
            {
                throw Invalid("Invalid time range", $"from must be before to, at most {maxRange.TotalHours:0} hours apart.");
            }
            var tenantKey = (string)http.Items[TenantKeyItem]!;
            var tenantId = await db.Tenants.AsNoTracking().Where(t => t.Key == tenantKey).Select(t => t.Id).SingleAsync(ct);
            var scope = new Scope(new TelemetryWindow(tenantKey, start, end, limit));

            if (filter.ServiceId is { } serviceId)
            {
                var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == serviceId, ct)
                    ?? throw Missing("Service");
                scope.Conditions.Add(ServiceCondition(scope, service));
            }
            if (filter.Environment is { Length: > 0 } environment)
            {
                var key = environment.Trim().ToLowerInvariant();
                if (key.Length > 128) throw Invalid("Invalid environment", "environment is at most 128 characters.");
                scope.Conditions.Add($"lowerUTF8(trimBoth({Environment})) = {scope.Add("String", key == Normalizer.UnspecifiedEnvironment ? "" : key)}");
            }
            if (filter.InstanceId is { } instanceId)
            {
                var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == instanceId, ct)
                    ?? throw Missing("Instance");
                scope.Conditions.Add(InstanceCondition(scope, instance));
            }
            if (filter.ResourceId is { } resourceId)
            {
                var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == resourceId, ct)
                    ?? throw Missing("Resource");
                var (condition, spanOnly) = ResourceCondition(scope, resource);
                (spanOnly ? scope.SpanConditions : scope.Conditions).Add(condition);
            }
            if (filter.DependencyId is { } dependencyId)
            {
                var dependency = await db.Dependencies.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == dependencyId, ct)
                    ?? throw Missing("Dependency");
                scope.SpanConditions.Add(await DependencyConditionAsync(scope, db, dependency, ct));
            }
            if (filter.Operation is { Length: > 0 } operation)
            {
                if (operation.Length > MaxText) throw Invalid("Invalid operation", $"operation is at most {MaxText} characters.");
                scope.SpanConditions.Add($"SpanName = {scope.Add("String", operation)}");
            }
            if (filter.TraceId is { Length: > 0 } traceId)
            {
                scope.Conditions.Add($"TraceId = {scope.Add("String", TraceIdOf(traceId))}");
            }
            if (filter.SpanId is { Length: > 0 } spanId)
            {
                if (!SpanIdPattern.IsMatch(spanId.ToLowerInvariant())) throw Invalid("Invalid span id", "A span id is 16 hexadecimal characters.");
                scope.SpanConditions.Add($"SpanId = {scope.Add("String", spanId.ToLowerInvariant())}");
            }
            foreach (var attribute in (filter.Attribute ?? []).Select((a, i) => (Text: a, Index: i)))
            {
                var separator = attribute.Text.IndexOf('=');
                if (attribute.Index >= MaxAttributeFilters || separator < 1
                    || !AttributeKeyPattern.IsMatch(attribute.Text[..separator]) || attribute.Text.Length - separator - 1 > MaxText)
                {
                    throw Invalid("Invalid attribute", $"attribute is key=value, at most {MaxAttributeFilters} of them.");
                }
                scope.SpanConditions.Add($"SpanAttributes[{scope.Add("String", attribute.Text[..separator])}] = {scope.Add("String", attribute.Text[(separator + 1)..])}");
            }
            return scope;
        }

        private static string TraceIdOf(string traceId)
            => PlainTraceId(traceId) ?? throw Invalid("Invalid trace id", "A trace id is 32 hexadecimal characters, or a W3C traceparent.");

        /// <summary>The 32-hex trace id of a trace id or a W3C traceparent; <c>null</c> for anything else.</summary>
        private static string? PlainTraceId(string text)
        {
            var lower = text.Trim().ToLowerInvariant();
            if (TraceIdPattern.IsMatch(lower)) return lower;
            var parent = TraceParentPattern.Match(lower);
            return parent.Success ? parent.Groups[1].Value : null;
        }

        private static string ServiceCondition(Scope scope, Service service)
            => $"ServiceName = {scope.Add("String", service.Name)} AND ResourceAttributes['service.namespace'] = {scope.Add("String", service.Namespace)}";

        /// <summary>The attributes discovery derived the instance key from (D1): the instance id, or host and process id.</summary>
        private static string InstanceCondition(Scope scope, ServiceInstance instance)
        {
            if (instance.KeySource == InstanceKeySource.Observed)
            {
                return $"{Instance} = {scope.Add("String", instance.InstanceKey)}";
            }
            var separator = instance.InstanceKey.LastIndexOf(':');
            return separator > 0
                ? $"{Instance} = '' AND lowerUTF8({Host}) = {scope.Add("String", instance.InstanceKey[..separator])} AND ResourceAttributes['process.pid'] = {scope.Add("String", instance.InstanceKey[(separator + 1)..])}"
                : $"{Instance} = '' AND lowerUTF8({Host}) = {scope.Add("String", instance.InstanceKey)}";
        }

        /// <summary>Hosts, containers and pods match resource attributes; databases and destinations match span attributes.</summary>
        private static (string Condition, bool SpanOnly) ResourceCondition(Scope scope, Resource resource)
        {
            string Attr(string key) => resource.Attributes.GetValueOrDefault(key, "");
            return resource.Type switch
            {
                ResourceType.Database => ($"{DbSystem} = {scope.Add("String", Attr("db.system.name"))} AND lowerUTF8(SpanAttributes['server.address']) = {scope.Add("String", Attr("server.address"))} AND SpanAttributes['db.namespace'] = {scope.Add("String", Attr("db.namespace"))}", true),
                ResourceType.MessagingDestination => ($"lowerUTF8(SpanAttributes['messaging.system']) = {scope.Add("String", Attr("messaging.system"))} AND SpanAttributes['messaging.destination.name'] = {scope.Add("String", Attr("messaging.destination.name"))}", true),
                ResourceType.Host => ($"(lowerUTF8(ResourceAttributes['host.id']) = {scope.Add("String", resource.Key)} OR (ResourceAttributes['host.id'] = '' AND lowerUTF8({Host}) = {scope.Add("String", resource.Key)}))", false),
                ResourceType.Container => ($"lowerUTF8(ResourceAttributes['container.id']) = {scope.Add("String", resource.Key)}", false),
                _ => (string.Join(" AND ", resource.Attributes.Where(kv => kv.Key.StartsWith("k8s.", StringComparison.Ordinal))
                    .Select(kv => $"ResourceAttributes[{scope.Add("String", kv.Key)}] = {scope.Add("String", kv.Value)}").DefaultIfEmpty("false")), false),
            };
        }

        /// <summary>
        /// The calling side of a dependency: the source service's client or producer spans toward the target. For a service
        /// target, spans whose child runs in the target service (how discovery observed it).
        /// </summary>
        private static async Task<string> DependencyConditionAsync(Scope scope, PlatformDbContext db, Dependency dependency, CancellationToken ct)
        {
            var source = await db.Services.AsNoTracking().SingleAsync(s => s.Id == dependency.SourceServiceId, ct);
            var target = dependency.TargetServiceId is { } targetId ? await db.Services.AsNoTracking().SingleAsync(s => s.Id == targetId, ct) : null;
            var resource = target is null ? await db.Resources.AsNoTracking().SingleAsync(r => r.Id == dependency.TargetResourceId, ct) : null;
            return DependencyCondition(scope, source, target, resource);
        }

        /// <summary><see cref="DependencyConditionAsync"/> with the edge's ends already loaded: its target service, or else its target resource.</summary>
        private static string DependencyCondition(Scope scope, Service source, Service? target, Resource? resource)
        {
            var condition = ServiceCondition(scope, source);
            if (target is not null)
            {
                return $"""
                    {condition} AND SpanKind IN ('Client', 'Producer') AND (TraceId, SpanId) IN (
                        SELECT TraceId, ParentSpanId FROM otel.otel_traces
                        WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} + INTERVAL 5 MINUTE
                          AND {ServiceCondition(scope, target)} AND SpanKind IN ('Server', 'Consumer'))
                    """;
            }
            return $"{condition} AND SpanKind IN ('Client', 'Producer', 'Consumer') AND {ResourceCondition(scope, resource!).Condition}";
        }

        private static IResult? Limit(int? limit, out int take)
        {
            take = limit ?? Cursor.DefaultLimit;
            return take is < 1 or > Cursor.MaxLimit ? Bad("Invalid limit", $"limit must be between 1 and {Cursor.MaxLimit}.") : null;
        }

        private static async Task<IResult> Run(Func<Task<IResult>> query)
        {
            try
            {
                return await query();
            }
            catch (FilterException e)
            {
                return e.Problem;
            }
        }

        private static Dictionary<string, string> Bounded(object value)
            => ((IDictionary<string, string>)value).OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxAttributes)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Length > 1024 ? kv.Value[..1024] : kv.Value);

        private static string? NonEmpty(string value) => value.Length == 0 ? null : value;

        private static double Ms(object nanos) => Convert.ToDouble(nanos) / 1_000_000d;

        // ─── traces ───

        /// <summary>
        /// Traces with at least one span meeting every filter in the window, newest first. <c>status=error</c> keeps traces
        /// with a failed span, <c>status=ok</c> traces without one; the duration bounds apply to the matching span. Traces are
        /// ordered by their latest matching span; to page back, pass <c>nextCursor</c> as <c>cursor</c> with the same window.
        /// </summary>
        private static Task<IResult> SearchTracesAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, [AsParameters] Filter filter, string? status, double? minDurationMs, double? maxDurationMs, int? limit,
            string? cursor, CancellationToken ct) => Run(async () =>
        {
            if (Limit(limit, out var take) is { } invalid) return invalid;
            if (!Position.TryParse(cursor, out var after) || after is { Key: var key } && !TraceIdPattern.IsMatch(key)) return BadCursor();
            if (status is not (null or "error" or "ok")) return Bad("Invalid status", "status is error or ok.");
            if (minDurationMs is < 0 || maxDurationMs is < 0) return Bad("Invalid duration", "Durations are zero or more.");
            var scope = await ScopeAsync(http, db, clock, filter, take + 1, MaxSearchRange, ct);
            if (status == "error") scope.SpanConditions.Add("StatusCode = 'Error'");
            if (minDurationMs is { } min) scope.SpanConditions.Add($"Duration >= {scope.Add("UInt64", (ulong)(min * 1_000_000))}");
            if (maxDurationMs is { } max) scope.SpanConditions.Add($"Duration <= {scope.Add("UInt64", (ulong)(max * 1_000_000))}");
            var afterCondition = after is { } a
                ? $"HAVING (toUnixTimestamp64Nano(last), TraceId) < ({scope.Add("Int64", a.Nanos)}, {scope.Add("String", a.Key)})"
                : "";
            // The matching traces in a stable order (latest matching span, then trace id), then every span of those traces
            // near the window (a trace may start before it). status=ok is applied after paging, so the cursor still moves on.
            const string Root = "ParentSpanId = ''";
            var sql = $"""
                WITH matched AS (
                    SELECT TraceId, max(Timestamp) AS last FROM otel.otel_traces WHERE {scope.Where("Timestamp", spans: true)}
                    GROUP BY TraceId {afterCondition}
                    ORDER BY last DESC, TraceId DESC LIMIT {ClickHouseTelemetrySource.Limit}
                )
                SELECT s.*, toUnixTimestamp64Nano(m.last) FROM (
                SELECT TraceId, min(Timestamp) AS started,
                       max(toUnixTimestamp64Nano(Timestamp) + Duration) - min(toUnixTimestamp64Nano(Timestamp)) AS duration,
                       if(countIf({Root}) > 0, argMinIf(ServiceName, Timestamp, {Root}), argMin(ServiceName, Timestamp)),
                       if(countIf({Root}) > 0, argMinIf(SpanName, Timestamp, {Root}), argMin(SpanName, Timestamp)),
                       if(countIf({Root}) > 0, argMinIf({Environment}, Timestamp, {Root}), argMin({Environment}, Timestamp)),
                       if(countIf({Root}) > 0, argMinIf({Instance}, Timestamp, {Root}), argMin({Instance}, Timestamp)),
                       if(countIf({Root}) > 0, argMinIf({Host}, Timestamp, {Root}), argMin({Host}, Timestamp)),
                       count() AS spans, countIf(StatusCode = 'Error') AS errors, groupUniqArray(20)(toString(ServiceName))
                FROM otel.otel_traces
                WHERE {Tenant} AND Timestamp >= {From} - INTERVAL 1 HOUR AND Timestamp < {To} + INTERVAL 1 HOUR
                  AND TraceId IN (SELECT TraceId FROM matched)
                GROUP BY TraceId
                ) AS s INNER JOIN matched AS m ON s.TraceId = m.TraceId
                ORDER BY m.last DESC, s.TraceId DESC
                """;
            var rows = await clickHouse.QueryAsync(sql, scope.Window, r => (Trace: new TraceSummaryDto(r.GetString(0), Utc(r.GetValue(1)),
                Ms(r.GetValue(2)), r.GetString(3), r.GetString(4), NonEmpty(r.GetString(5)), NonEmpty(r.GetString(6)), NonEmpty(r.GetString(7)),
                Convert.ToInt64(r.GetValue(8)), Convert.ToInt64(r.GetValue(9)), ((string[])r.GetValue(10)).Order(StringComparer.Ordinal).ToList()),
                Position: new Position(Convert.ToInt64(r.GetValue(11)), r.GetString(0))), ct, scope.Parameters);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? page[^1].Position.Encode() : null;
            return Results.Ok(new TelemetryPage<TraceSummaryDto>(
                page.Select(r => r.Trace).Where(t => status != "ok" || t.ErrorCount == 0).ToList(), next is not null, next));
        });

        /// <summary>
        /// Every span of one trace (at most <see cref="MaxSpans"/>), in start order, with the platform id of its service and,
        /// for a call, its target: the service running the child span, or the database or destination discovery recorded.
        /// </summary>
        private static Task<IResult> GetTraceAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            IOptions<DiscoveryOptions> discovery, string traceId, CancellationToken ct)
            => Run(async () =>
        {
            traceId = TraceIdOf(traceId);
            var tenantKey = (string)http.Items[TenantKeyItem]!;
            var parameters = new Dictionary<string, object> { ["trace"] = traceId };
            // The exporter's per-trace time index bounds the span query; it carries no tenant, so it only narrows time.
            var bounds = await clickHouse.QueryAsync(
                "SELECT min(Start), max(End) FROM otel.otel_traces_trace_id_ts WHERE TraceId = {trace:String} HAVING count() > 0",
                new TelemetryWindow(tenantKey, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1),
                r => (Start: Utc(r.GetValue(0)), End: Utc(r.GetValue(1))), ct, parameters);
            if (bounds.Count == 0) throw Missing("Trace");
            var window = new TelemetryWindow(tenantKey, bounds[0].Start.AddMinutes(-1), bounds[0].End.AddMinutes(1), MaxSpans + 1);
            var spans = await clickHouse.QueryAsync($"""
                SELECT SpanId, ParentSpanId, ServiceName, {Environment}, {Instance}, {Host}, SpanName, SpanKind, Timestamp, Duration,
                       StatusCode, StatusMessage, ScopeName, ScopeVersion, SpanAttributes, Events.Name, Events.Timestamp,
                       Events.Attributes, Links.TraceId, Links.SpanId, ResourceAttributes['service.namespace'],
                       SpanAttributes['messaging.system'], SpanAttributes['messaging.destination.name'],
                       if(SpanAttributes['db.system.name'] != '', SpanAttributes['db.system.name'], SpanAttributes['db.system']),
                       SpanAttributes['db.namespace'], SpanAttributes['server.address']
                FROM otel.otel_traces
                WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND TraceId = {"{trace:String}"}
                ORDER BY Timestamp
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, window, r => (Span: new SpanDto(r.GetString(0), NonEmpty(r.GetString(1)), null, r.GetString(2), NonEmpty(r.GetString(3)),
                    NonEmpty(r.GetString(4)), NonEmpty(r.GetString(5)), r.GetString(6), r.GetString(7), Utc(r.GetValue(8)), Ms(r.GetValue(9)),
                    r.GetString(10), NonEmpty(r.GetString(11)), NonEmpty(r.GetString(12)), NonEmpty(r.GetString(13)), Bounded(r.GetValue(14)),
                    ((string[])r.GetValue(15)).Select((name, i) => new SpanEventDto(name, Utc(((Array)r.GetValue(16)).GetValue(i)!),
                        Bounded(((Array)r.GetValue(17)).GetValue(i)!))).Take(MaxAttributes).ToList(),
                    ((string[])r.GetValue(18)).Zip((string[])r.GetValue(19)).Take(MaxAttributes).Select(l => new SpanLinkDto(l.First, l.Second)).ToList(),
                    null),
                    Use: new ResourceUseObservation
                    {
                        ServiceNamespace = r.GetString(20), ServiceName = r.GetString(2), SpanKind = r.GetString(7),
                        MessagingSystem = r.GetString(21), MessagingDestination = r.GetString(22), DbSystem = r.GetString(23),
                        DbNamespace = r.GetString(24), ServerAddress = r.GetString(25),
                    }),
                ct, parameters);
            // Another tenant's trace and an unknown one look the same.
            if (spans.Count == 0) throw Missing("Trace");

            // Platform ids, by the same normalization discovery uses.
            var normalizer = new Normalizer(discovery.Value.Aliases);
            var tenantId = await db.Tenants.AsNoTracking().Where(t => t.Key == tenantKey).Select(t => t.Id).SingleAsync(ct);
            var services = (await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => new { s.Id, s.Namespace, s.Name }).ToListAsync(ct))
                .ToDictionary(s => (s.Namespace, s.Name), s => s.Id);
            Guid? ServiceIdOf(ResourceUseObservation o)
                => normalizer.Service(o.ServiceNamespace, o.ServiceName) is { } key && services.TryGetValue(key, out var id) ? id : null;
            var resourceKeys = spans.Select(s => normalizer.UsedResource(s.Use)).OfType<ResourceKey>().Select(k => k.Key).Distinct().ToList();
            var resources = (await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && resourceKeys.Contains(r.Key))
                .Select(r => new { r.Id, r.Type, r.Key, r.DisplayName }).ToListAsync(ct)).ToDictionary(r => (r.Type, r.Key));
            // An instance whose key discovery derived (no service.instance.id) has no name on its spans to match.
            var instanceKeys = spans.Select(s => s.Span.Instance).OfType<string>().Distinct().ToList();
            var instances = (await (
                from i in db.Instances.AsNoTracking()
                join e in db.Environments.AsNoTracking() on i.EnvironmentId equals e.Id
                where i.TenantId == tenantId && i.KeySource == InstanceKeySource.Observed && instanceKeys.Contains(i.InstanceKey)
                select new { i.Id, i.ServiceId, Environment = e.Key, i.InstanceKey }).ToListAsync(ct))
                .ToDictionary(i => (i.ServiceId, i.Environment, i.InstanceKey), i => i.Id);
            Guid? InstanceIdOf((SpanDto Span, ResourceUseObservation Use) s)
            {
                var environment = s.Span.Environment?.Trim().ToLowerInvariant();
                return s.Span.Instance is { } key && ServiceIdOf(s.Use) is { } serviceId
                    && instances.TryGetValue((serviceId, string.IsNullOrEmpty(environment) ? Normalizer.UnspecifiedEnvironment : environment, key), out var id)
                    ? id : null;
            }
            var children = spans.Where(s => s.Span.ParentSpanId is not null).ToLookup(s => s.Span.ParentSpanId!, StringComparer.Ordinal);

            SpanTargetDto? TargetOf((SpanDto Span, ResourceUseObservation Use) s)
            {
                if (s.Span.Kind is not ("Client" or "Producer" or "Consumer")) return null;
                var callee = children[s.Span.SpanId].FirstOrDefault(c => c.Span.Service != s.Span.Service && c.Span.Kind is "Server" or "Consumer");
                if (callee.Span is not null && ServiceIdOf(callee.Use) is { } calleeId)
                    return new SpanTargetDto("service", calleeId, callee.Span.Service);
                return normalizer.UsedResource(s.Use) is { } key && resources.TryGetValue((key.Type, key.Key), out var resource)
                    ? new SpanTargetDto("resource", resource.Id, resource.DisplayName)
                    : null;
            }
            var result = spans.Take(MaxSpans).Select(s => s.Span with { ServiceId = ServiceIdOf(s.Use), Target = TargetOf(s), InstanceId = InstanceIdOf(s) }).ToList();
            return Results.Ok(new TraceDto(traceId, result, spans.Count > MaxSpans));
        });

        // ─── logs ───

        /// <summary>
        /// Log records newest first. <c>minSeverity</c> is an OpenTelemetry severity number (9 info, 13 warn, 17 error); <c>q</c>
        /// is a case-insensitive substring of the body. <c>traceId</c> and <c>spanId</c> give a trace's or a span's logs. To
        /// page back, pass <c>nextCursor</c> as <c>cursor</c> with the same window.
        /// </summary>
        private static Task<IResult> SearchLogsAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid? serviceId, string? environment, Guid? instanceId, Guid? resourceId, string? traceId, string? spanId,
            DateTimeOffset? from, DateTimeOffset? to, int? minSeverity, string? q, int? limit, string? cursor, CancellationToken ct) => Run(async () =>
        {
            if (Limit(limit, out var take) is { } invalid) return invalid;
            if (!Position.TryParse(cursor, out var after) || after is { Key: var key } && !ulong.TryParse(key, out _)) return BadCursor();
            if (minSeverity is < 0 or > 24) return Bad("Invalid minSeverity", "minSeverity is a severity number from 0 to 24.");
            if (q is { Length: > MaxText }) return Bad("Invalid q", $"q is at most {MaxText} characters.");
            var resource = resourceId is { } r ? await db.Resources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r, ct) : null;
            if (resource is { Type: ResourceType.Database or ResourceType.MessagingDestination })
            {
                return Bad("Invalid resourceId", "Logs are filtered by host, container or pod; a database or destination has no logs.");
            }
            var scope = await ScopeAsync(http, db, clock, new Filter
            {
                ServiceId = serviceId, Environment = environment, InstanceId = instanceId, ResourceId = resourceId, TraceId = traceId, From = from, To = to,
            }, take + 1, MaxSearchRange, ct);
            if (spanId is { Length: > 0 })
            {
                if (!SpanIdPattern.IsMatch(spanId.ToLowerInvariant())) return Bad("Invalid span id", "A span id is 16 hexadecimal characters.");
                scope.Conditions.Add($"SpanId = {scope.Add("String", spanId.ToLowerInvariant())}");
            }
            if (minSeverity is { } severity) scope.Conditions.Add($"SeverityNumber >= {scope.Add("UInt8", (byte)severity)}");
            if (q is { Length: > 0 }) scope.Conditions.Add($"positionCaseInsensitiveUTF8(Body, {scope.Add("String", q)}) > 0");
            // Records have no id: a hash of their content breaks ties between records with the same timestamp.
            const string Tie = "cityHash64(TraceId, SpanId, ServiceName, SeverityNumber, Body, toString(LogAttributes), toString(ResourceAttributes))";
            if (after is { } a)
            {
                var (nanos, tie) = (scope.Add("Int64", a.Nanos), scope.Add("UInt64", ulong.Parse(a.Key)));
                scope.Conditions.Add($"Timestamp <= fromUnixTimestamp64Nano({nanos}) AND (toUnixTimestamp64Nano(Timestamp), {Tie}) < ({nanos}, {tie})");
            }
            var rows = await clickHouse.QueryAsync($"""
                SELECT Timestamp, SeverityText, SeverityNumber, ServiceName, {Environment}, {Instance},
                       substringUTF8(Body, 1, {MaxBody}), lengthUTF8(Body) > {MaxBody}, TraceId, SpanId, LogAttributes,
                       toUnixTimestamp64Nano(Timestamp), {Tie} AS tie
                FROM otel.otel_logs
                WHERE {scope.Where("Timestamp")}
                ORDER BY Timestamp DESC, tie DESC
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, x => (Log: new LogDto(Utc(x.GetValue(0)), x.GetString(1), Convert.ToInt32(x.GetValue(2)), x.GetString(3),
                    NonEmpty(x.GetString(4)), NonEmpty(x.GetString(5)), x.GetString(6), Convert.ToBoolean(x.GetValue(7)),
                    NonEmpty(x.GetString(8)), NonEmpty(x.GetString(9)), Bounded(x.GetValue(10))),
                    Position: new Position(Convert.ToInt64(x.GetValue(11)), Convert.ToUInt64(x.GetValue(12)).ToString())),
                ct, scope.Parameters);
            var page = rows.Take(take).ToList();
            var next = rows.Count > take ? page[^1].Position.Encode() : null;
            return Results.Ok(new TelemetryPage<LogDto>(page.Select(r => r.Log).ToList(), next is not null, next));
        });

        // ─── operations ───

        /// <summary>
        /// A service's operations: its entry spans (server, consumer, or a trace root other than a client span, which is a
        /// background poll such as an outbox worker's SELECT) by name and kind, busiest first, with
        /// throughput, error rate and latency percentiles over the window.
        /// </summary>
        private static Task<IResult> ListOperationsAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid? serviceId, string? environment, Guid? instanceId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            CancellationToken ct) => Run(async () =>
        {
            if (serviceId is null) return Bad("Invalid serviceId", "serviceId is required.");
            if (Limit(limit, out var take) is { } invalid) return invalid;
            var scope = await ScopeAsync(http, db, clock, new Filter
            {
                ServiceId = serviceId, Environment = environment, InstanceId = instanceId, From = from, To = to,
            }, take + 1, MaxSeriesRange, ct);
            var seconds = Math.Max(1, (scope.Window.To - scope.Window.From).TotalSeconds);
            var rows = await clickHouse.QueryAsync($"""
                SELECT SpanName, toString(SpanKind), count() AS n, countIf(StatusCode = 'Error') / n,
                       quantiles(0.5, 0.95, 0.99)(Duration), max(Timestamp)
                FROM otel.otel_traces
                WHERE {scope.Where("Timestamp")} AND (SpanKind IN ('Server', 'Consumer') OR (ParentSpanId = '' AND SpanKind != 'Client'))
                GROUP BY SpanName, SpanKind
                ORDER BY n DESC
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r =>
                {
                    var q = ((Array)r.GetValue(4)).Cast<object>().Select(Ms).ToArray();
                    var count = Convert.ToInt64(r.GetValue(2));
                    return new OperationDto(r.GetString(0), r.GetString(1), count, count / seconds, Convert.ToDouble(r.GetValue(3)),
                        q[0], q[1], q[2], Utc(r.GetValue(5)));
                }, ct, scope.Parameters);
            return Results.Ok(new TelemetryPage<OperationDto>(rows.Take(take).ToList(), rows.Count > take));
        });

        // ─── dependencies ───

        /// <summary>Beyond this many edges a graph can't be read anyway, and each one adds to the query.</summary>
        private const int MaxDependencyStats = 200;
        /// <summary>Edges counted by one query, which keeps its parameters to a size every server accepts.</summary>
        private const int DependencyStatsBatch = 25;

        /// <summary>
        /// Calls and error rate of each edge in the window, for colouring the graph: the same spans as an edge's evidence.
        /// Edges with no calls in the window are returned with a count of 0.
        /// </summary>
        private static Task<IResult> ListDependencyStatsAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, string? environment, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Run(async () =>
        {
            var filter = new Filter { Environment = environment, From = from, To = to };
            // Validates the window, and serves the first batch's query.
            Scope? next = await ScopeAsync(http, db, clock, filter, 1, MaxSeriesRange, ct);
            var tenantId = await db.Tenants.AsNoTracking().Where(t => t.Key == next.Window.TenantKey).Select(t => t.Id).SingleAsync(ct);
            var dependencies = await db.Dependencies.AsNoTracking().Where(d => d.TenantId == tenantId)
                .OrderByDescending(d => d.LastSeen).ThenBy(d => d.Id).Take(MaxDependencyStats + 1).ToListAsync(ct);
            var counted = dependencies.Take(MaxDependencyStats).ToList();
            // Every edge's ends in two queries, not one or two per edge.
            var serviceIds = counted.Select(d => d.SourceServiceId).Concat(counted.Select(d => d.TargetServiceId).OfType<Guid>()).Distinct().ToList();
            var resourceIds = counted.Where(d => d.TargetServiceId is null).Select(d => d.TargetResourceId).OfType<Guid>().Distinct().ToList();
            var services = await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
            var resources = await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && resourceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            var items = new List<DependencyStatsDto>();
            foreach (var batch in counted.Chunk(DependencyStatsBatch))
            {
                // Each query has its own parameters.
                var scope = next ?? await ScopeAsync(http, db, clock, filter, 1, MaxSeriesRange, ct);
                next = null;
                var conditions = batch.Select(d => DependencyCondition(scope, services[d.SourceServiceId],
                    d.TargetServiceId is { } target ? services[target] : null,
                    d.TargetServiceId is null ? resources[d.TargetResourceId!.Value] : null)).ToList();
                var columns = string.Join(", ", conditions.Select(c => $"countIf({c}), countIf(StatusCode = 'Error' AND ({c}))"));
                var rows = await clickHouse.QueryAsync($"""
                    SELECT {columns} FROM otel.otel_traces WHERE {scope.Where("Timestamp", spans: true)}
                    """, scope.Window, r => batch.Select((dependency, i) =>
                    {
                        var (count, errors) = (Convert.ToInt64(r.GetValue(i * 2)), Convert.ToInt64(r.GetValue(i * 2 + 1)));
                        return new DependencyStatsDto(dependency.Id, count, count > 0 ? (double)errors / count : 0);
                    }).ToList(), ct, scope.Parameters);
                items.AddRange(rows[0]);
            }
            return Results.Ok(new DependencyStatsPageDto(items, dependencies.Count > MaxDependencyStats));
        });

        /// <summary>
        /// Evidence for one dependency edge from the caller's spans: call count, error rate, p95, last seen, and the slowest
        /// and most recent failed traces as representatives.
        /// </summary>
        private static Task<IResult> GetDependencyEvidenceAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid dependencyId, string? environment, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Run(async () =>
        {
            var scope = await ScopeAsync(http, db, clock, new Filter { DependencyId = dependencyId, Environment = environment, From = from, To = to },
                5, MaxSeriesRange, ct);
            var dependency = await db.Dependencies.AsNoTracking().SingleAsync(d => d.Id == dependencyId, ct);
            var rows = await clickHouse.QueryAsync($"""
                SELECT count() AS n, if(n > 0, countIf(StatusCode = 'Error') / n, 0), if(n > 0, quantile(0.95)(Duration), NULL),
                       if(n > 0, max(Timestamp), NULL),
                       arrayMap(x -> x.2, arraySlice(arrayReverseSort(x -> x.1, groupArray((Duration, TraceId))), 1, {ClickHouseTelemetrySource.Limit})),
                       arrayMap(x -> x.2, arraySlice(arrayReverseSort(x -> x.1, groupArrayIf((Timestamp, TraceId), StatusCode = 'Error')), 1, {ClickHouseTelemetrySource.Limit}))
                FROM otel.otel_traces
                WHERE {scope.Where("Timestamp", spans: true)}
                """, scope.Window, r => new DependencyEvidenceDto(dependencyId, dependency.Relation.ToString(), dependency.Confidence.ToString(),
                    Convert.ToInt64(r.GetValue(0)), Convert.ToDouble(r.GetValue(1)), r.IsDBNull(2) ? null : Ms(r.GetValue(2)),
                    r.IsDBNull(3) ? null : Utc(r.GetValue(3)), ((string[])r.GetValue(4)).Distinct().ToList(), ((string[])r.GetValue(5)).Distinct().ToList()),
                ct, scope.Parameters);
            return Results.Ok(rows[0]);
        });

        // ─── metrics ───

        private static readonly (string Type, string Table)[] MetricTables =
            [("gauge", "otel_metrics_gauge"), ("sum", "otel_metrics_sum"), ("histogram", "otel_metrics_histogram")];

        /// <summary>The metrics reported in the window, by name.</summary>
        private static Task<IResult> ListMetricsAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid? serviceId, string? environment, Guid? instanceId, Guid? resourceId, DateTimeOffset? from,
            DateTimeOffset? to, CancellationToken ct) => Run(async () =>
        {
            var scope = await MetricScopeAsync(http, db, clock, serviceId, environment, instanceId, resourceId, from, to, Cursor.MaxLimit + 1, ct);
            var where = scope.Where("TimeUnix");
            var rows = await clickHouse.QueryAsync($"""
                SELECT MetricName, any(type), any(MetricUnit), any(MetricDescription) FROM (
                {string.Join("\nUNION ALL\n", MetricTables.Select(t =>
                    $"SELECT MetricName, '{t.Type}' AS type, MetricUnit, MetricDescription FROM otel.{t.Table} WHERE {where} LIMIT 1 BY MetricName"))}
                )
                GROUP BY MetricName ORDER BY MetricName
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r => new MetricInfoDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, scope.Parameters);
            return Results.Ok(new TelemetryPage<MetricInfoDto>(rows.Take(Cursor.MaxLimit).ToList(), rows.Count > Cursor.MaxLimit));
        });

        private static async Task<Scope> MetricScopeAsync(HttpContext http, PlatformDbContext db, TimeProvider clock, Guid? serviceId,
            string? environment, Guid? instanceId, Guid? resourceId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
        {
            var scope = await ScopeAsync(http, db, clock, new Filter
            {
                ServiceId = serviceId, Environment = environment, InstanceId = instanceId, ResourceId = resourceId, From = from, To = to,
            }, limit, MaxSeriesRange, ct);
            return scope.SpanConditions.Count > 0
                ? throw Invalid("Invalid resourceId", "Metrics are filtered by host, container or pod; a database or destination has no metrics of its own.")
                : scope;
        }

        /// <summary>
        /// One metric as a series over the window, summed across its series: a gauge as the average of each bucket; a
        /// monotonic sum as a rate per second (a counter reset counts from zero); another sum as its average; a histogram
        /// as the average observed value (delta of Sum over delta of Count). A bucket without data has no point: no data
        /// is not zero. <c>level</c> reads a sum as its average even if it says it is monotonic: .NET reports its thread pool's
        /// queue length and thread count that way.
        /// </summary>
        private static Task<IResult> GetSeriesAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, string? name, Guid? serviceId, string? environment, Guid? instanceId, Guid? resourceId,
            DateTimeOffset? from, DateTimeOffset? to, int? stepSeconds, bool? level, CancellationToken ct) => Run(async () =>
        {
            if (name is null || !MetricNamePattern.IsMatch(name)) return Bad("Invalid name", "name is a metric name.");
            var scope = await MetricScopeAsync(http, db, clock, serviceId, environment, instanceId, resourceId, from, to, MaxPoints, ct);
            var step = Step(scope.Window, stepSeconds);
            var stepParameter = scope.Add("UInt32", (uint)step);
            var where = $"{scope.Where("TimeUnix")} AND MetricName = {scope.Add("String", name)}";

            var info = await clickHouse.QueryAsync($"""
                SELECT type, unit, monotonic FROM (
                    SELECT 'gauge' AS type, any(MetricUnit) AS unit, false AS monotonic, count() AS n FROM otel.otel_metrics_gauge WHERE {where}
                    UNION ALL SELECT 'sum', any(MetricUnit), toBool(max(IsMonotonic)), count() FROM otel.otel_metrics_sum WHERE {where}
                    UNION ALL SELECT 'histogram', any(MetricUnit), false, count() FROM otel.otel_metrics_histogram WHERE {where}
                ) WHERE n > 0 ORDER BY n DESC LIMIT 1
                """, scope.Window, r => (Type: r.GetString(0), Unit: r.GetString(1), Monotonic: Convert.ToBoolean(r.GetValue(2))), ct, scope.Parameters);
            if (info.Count == 0) return Results.Ok(new SeriesDto(name, "unknown", "", "none", step, []));
            var (type, unit, monotonic) = info[0];

            var bucket = $"toStartOfInterval(TimeUnix, toIntervalSecond({stepParameter}))";
            const string Series = "cityHash64(ResourceAttributes, Attributes)";
            const string Window = "OVER (PARTITION BY series ORDER BY b ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)";
            var limitRows = ClickHouseTelemetrySource.Limit;
            var (aggregation, sql) = type switch
            {
                "gauge" => ("avg", $"SELECT {bucket} AS b, avg(Value) FROM otel.otel_metrics_gauge WHERE {where} GROUP BY b ORDER BY b LIMIT {limitRows}"),
                "sum" when !monotonic || level == true => ("avg", $"SELECT {bucket} AS b, avg(Value) FROM otel.otel_metrics_sum WHERE {where} GROUP BY b ORDER BY b LIMIT {limitRows}"),
                // A series' first bucket has no previous value: no delta (NULL), never a rate of zero.
                "sum" => ("rate", $"""
                    SELECT b, if(count(d) = 0, NULL, sum(d) / {stepParameter}) FROM (
                        SELECT b, if(n = 1, NULL, if(v >= prev, v - prev, v)) AS d FROM (
                            SELECT series, b, v, lagInFrame(v, 1, v) {Window} AS prev, row_number() {Window.Replace(" ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING", "")} AS n FROM (
                                SELECT {Series} AS series, {bucket} AS b, max(Value) AS v FROM otel.otel_metrics_sum WHERE {where} GROUP BY series, b)))
                    GROUP BY b ORDER BY b LIMIT {limitRows}
                    """),
                _ => ("avg", $"""
                    SELECT b, if(sum(dc) > 0, sum(ds) / sum(dc), NULL) FROM (
                        SELECT b, if(c >= pc, c - pc, c) AS dc, if(c >= pc, s - ps, s) AS ds FROM (
                            SELECT series, b, c, s, lagInFrame(c, 1, c) {Window} AS pc, lagInFrame(s, 1, s) {Window} AS ps FROM (
                                SELECT {Series} AS series, {bucket} AS b, max(Count) AS c, max(Sum) AS s FROM otel.otel_metrics_histogram WHERE {where} GROUP BY series, b)))
                    GROUP BY b ORDER BY b LIMIT {limitRows}
                    """),
            };
            var points = await clickHouse.QueryAsync(sql, scope.Window,
                r => new PointDto(Utc(r.GetValue(0)), r.IsDBNull(1) ? null : Convert.ToDouble(r.GetValue(1))), ct, scope.Parameters);
            return Results.Ok(new SeriesDto(name, type, unit, aggregation, step, points));
        });

        /// <summary>
        /// Throughput, error rate and p95 latency of a service's entry spans, per bucket; empty buckets have no point. A rate is
        /// spans divided by the length of the period: here a bucket, in the operations list the whole window, so a burst shows
        /// higher here. Consumer spans are messages, the other entry spans requests: a request and the message it causes are
        /// not one rate.
        /// </summary>
        private static Task<IResult> GetServiceStatsAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid? serviceId, string? environment, Guid? instanceId, string? operation, DateTimeOffset? from,
            DateTimeOffset? to, int? stepSeconds, CancellationToken ct) => Run(async () =>
        {
            if (serviceId is null) return Bad("Invalid serviceId", "serviceId is required.");
            var scope = await ScopeAsync(http, db, clock, new Filter
            {
                ServiceId = serviceId, Environment = environment, InstanceId = instanceId, Operation = operation, From = from, To = to,
            }, MaxPoints, MaxSeriesRange, ct);
            var step = scope.Add("UInt32", (uint)Step(scope.Window, stepSeconds));
            var points = await clickHouse.QueryAsync($"""
                SELECT toStartOfInterval(Timestamp, toIntervalSecond({step})) AS b, countIf(SpanKind != 'Consumer') / {step}, countIf(SpanKind = 'Consumer') / {step},
                       countIf(StatusCode = 'Error') / count(), quantile(0.95)(Duration) / 1e6
                FROM otel.otel_traces
                WHERE {scope.Where("Timestamp", spans: true)} AND (SpanKind IN ('Server', 'Consumer') OR (ParentSpanId = '' AND SpanKind != 'Client'))
                GROUP BY b ORDER BY b
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r => new ServiceStatsPointDto(Utc(r.GetValue(0)), Convert.ToDouble(r.GetValue(1)), Convert.ToDouble(r.GetValue(2)),
                    Convert.ToDouble(r.GetValue(3)), r.IsDBNull(4) ? null : Convert.ToDouble(r.GetValue(4))), ct, scope.Parameters);
            return Results.Ok(new ServiceStatsDto(Step(scope.Window, stepSeconds), points));
        });

        // ─── capacity ───

        /// <summary>A week of minutes for five instances.</summary>
        private const int MaxCapacityRows = 50_400;
        /// <summary>One length for any window: longer buckets would average away the peaks that sizing is about.</summary>
        private const int CapacityStepSeconds = 60;
        /// <summary>How far back "the outbox is behind now" looks.</summary>
        private const int CapacityRecentSeconds = 300;
        private const string InstanceOfRow = $"concat({Instance}, '|', {Host}, '|', ResourceAttributes['process.pid'])";

        /// <summary>
        /// What a service was measured to need, per instance: each instance's work, CPU, memory and thread pool queue per
        /// bucket, read by <see cref="CapacityEstimate"/>. Only server and consumer spans are work: a root span may be a background
        /// poll, such as an outbox worker's SELECT many times a second, which would swamp the real load. Requests come from <c>http.server.request.duration</c> when the host
        /// exports it, because spans undercount when traces are sampled; otherwise from entry spans, and <c>requestSource</c>
        /// says which. Messages always come from spans. CPU, memory and the queue come from .NET's <c>System.Runtime</c> meter.
        /// Buckets the window cuts short are left out: they would pair part of the spans with a whole CPU delta.
        /// </summary>
        private static Task<IResult> GetCapacityAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, Guid? serviceId, string? environment, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) => Run(async () =>
        {
            if (serviceId is null) return Bad("Invalid serviceId", "serviceId is required.");
            var scope = await ScopeAsync(http, db, clock, new Filter { ServiceId = serviceId, Environment = environment, From = from, To = to },
                MaxCapacityRows, MaxSeriesRange, ct);
            const int step = CapacityStepSeconds;
            var stepParameter = scope.Add("UInt32", (uint)step);
            string Bucket(string time) => $"toStartOfInterval({time}, toIntervalSecond({stepParameter}))";
            var metrics = scope.Where("TimeUnix");

            var spans = await clickHouse.QueryAsync($"""
                SELECT {InstanceOfRow} AS instance, {Bucket("Timestamp")} AS b, countIf(SpanKind = 'Server'), countIf(SpanKind = 'Consumer'),
                       sumIf(Duration, SpanKind = 'Server') / 1e9, sumIf(Duration, SpanKind = 'Consumer') / 1e9
                FROM otel.otel_traces
                WHERE {scope.Where("Timestamp")} AND SpanKind IN ('Server', 'Consumer')
                GROUP BY instance, b ORDER BY b DESC
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r => (Key: (r.GetString(0), Utc(r.GetValue(1))), Requests: Convert.ToDouble(r.GetValue(2)), Messages: Convert.ToDouble(r.GetValue(3)),
                    RequestSeconds: Convert.ToDouble(r.GetValue(4)), MessageSeconds: Convert.ToDouble(r.GetValue(5))), ct, scope.Parameters);

            // How fast a cumulative value grew per second, per instance and bucket, summed across its series. A series' first
            // bucket has no earlier value and gives no row; a gap divides by its real length, so a late export isn't a spike.
            async Task<Dictionary<(string, DateTimeOffset), double>> GrowthAsync(string table, string column, string metric)
            {
                const string Frame = "OVER (PARTITION BY series ORDER BY b ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)";
                var rows = await clickHouse.QueryAsync($"""
                    SELECT instance, b, sum(d / dt) FROM (
                        SELECT instance, b, if(v >= prev, v - prev, v) AS d, dateDiff('second', before, b) AS dt FROM (
                            SELECT series, instance, b, v, lagInFrame(v, 1, v) {Frame} AS prev, lagInFrame(b, 1, b) {Frame} AS before FROM (
                                SELECT cityHash64(ResourceAttributes, Attributes) AS series, {InstanceOfRow} AS instance, {Bucket("TimeUnix")} AS b, max({column}) AS v
                                FROM otel.{table} WHERE {metrics} AND MetricName = {scope.Add("String", metric)} GROUP BY series, instance, b)))
                    WHERE dt > 0
                    GROUP BY instance, b ORDER BY b DESC
                    LIMIT {ClickHouseTelemetrySource.Limit}
                    """, scope.Window, r => (Key: (r.GetString(0), Utc(r.GetValue(1))), Rate: Convert.ToDouble(r.GetValue(2))), ct, scope.Parameters);
                return rows.ToDictionary(r => r.Key, r => r.Rate);
            }
            var cores = await GrowthAsync("otel_metrics_sum", "Value", "dotnet.process.cpu.time");
            var requestRate = await GrowthAsync("otel_metrics_histogram", "Count", "http.server.request.duration");
            var requestSeconds = await GrowthAsync("otel_metrics_histogram", "Sum", "http.server.request.duration");

            var (memoryName, queueName, cpuCountName) = (scope.Add("String", "dotnet.process.memory.working_set"),
                scope.Add("String", "dotnet.thread_pool.queue.length"), scope.Add("String", "dotnet.process.cpu.count"));
            // .NET reports the queue length as a monotonic counter, and these may be gauges or sums: each is read as a level.
            var levels = await clickHouse.QueryAsync($"""
                SELECT {InstanceOfRow} AS instance, {Bucket("TimeUnix")} AS b,
                       countIf(MetricName = {memoryName}), avgIf(Value, MetricName = {memoryName}),
                       countIf(MetricName = {queueName}), maxIf(Value, MetricName = {queueName}),
                       maxIf(Value, MetricName = {cpuCountName})
                FROM otel.otel_metrics_sum
                WHERE {metrics} AND MetricName IN ({memoryName}, {queueName}, {cpuCountName})
                GROUP BY instance, b ORDER BY b DESC
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r => (Key: (r.GetString(0), Utc(r.GetValue(1))),
                    Memory: Convert.ToInt64(r.GetValue(2)) > 0 ? Convert.ToDouble(r.GetValue(3)) : (double?)null,
                    Queue: Convert.ToInt64(r.GetValue(4)) > 0 ? Convert.ToDouble(r.GetValue(5)) : (double?)null,
                    CpuCount: Convert.ToDouble(r.GetValue(6))), ct, scope.Parameters);

            var spansOf = spans.ToDictionary(s => s.Key);
            var levelsOf = levels.ToDictionary(l => l.Key);
            // An instance's first bucket has no growth to read. If it exports the metric, sampled spans must not stand in for it.
            var countedByMetric = requestRate.Keys.Select(k => k.Item1).ToHashSet();
            var samples = spansOf.Keys.Concat(cores.Keys).Concat(requestRate.Keys).Concat(levelsOf.Keys).Distinct()
                .Where(k => k.Item2 >= scope.Window.From && k.Item2.AddSeconds(step) <= scope.Window.To)
                .Where(k => requestRate.ContainsKey(k) || !countedByMetric.Contains(k.Item1))
                .Select(k =>
                {
                    spansOf.TryGetValue(k, out var s);
                    levelsOf.TryGetValue(k, out var l);
                    var measured = requestRate.TryGetValue(k, out var rate);
                    var perSecond = (measured ? rate : s.Requests / step) + s.Messages / step;
                    var inFlight = (measured ? requestSeconds.GetValueOrDefault(k) : s.RequestSeconds / step) + s.MessageSeconds / step;
                    return new CapacitySample(k.Item1, k.Item2, perSecond, inFlight, cores.TryGetValue(k, out var c) ? c : null, l.Memory, l.Queue,
                        s.Messages / step, s.MessageSeconds / step);
                }).ToList();

            var truncated = new[] { spans.Count, cores.Count, requestRate.Count, levels.Count }.Any(n => n >= MaxCapacityRows);
            var hosts = samples.Select(s => s.Instance.Split('|')[1]).Where(h => h.Length > 0).Distinct().Order(StringComparer.Ordinal).Take(20).ToList();
            var cpuCount = levels.Select(l => l.CpuCount).DefaultIfEmpty(0).Max();

            // Juice's own spans: "create" when a message is written to the outbox, "deliver" with its outcome when it's sent. A
            // message counts once however many routes it has. A delivery's parent is the span that created its message.
            const string Created = "SpanKind = 'Producer' AND SpanName LIKE 'create %' AND ScopeName = 'Juice'";
            const string Delivered = "SpanName LIKE 'deliver %' AND ScopeName = 'Juice' AND SpanAttributes['juice.delivery.outcome'] = 'published'";
            var traces = scope.Where("Timestamp");
            var outboxPoints = await clickHouse.QueryAsync($"""
                SELECT {Bucket("Timestamp")} AS b, uniqExactIf(SpanAttributes['messaging.message.id'], {Created}), uniqExactIf(SpanAttributes['messaging.message.id'], {Delivered})
                FROM otel.otel_traces
                WHERE {traces} AND (({Created}) OR ({Delivered}))
                GROUP BY b ORDER BY b DESC
                LIMIT {ClickHouseTelemetrySource.Limit}
                """, scope.Window, r => new OutboxPoint(Utc(r.GetValue(0)), Convert.ToDouble(r.GetValue(1)), Convert.ToDouble(r.GetValue(2))), ct, scope.Parameters);
            var whole = outboxPoints.Where(p => p.At >= scope.Window.From && p.At.AddSeconds(step) <= scope.Window.To).ToList();
            // Only the newest minutes: this joins on the deliveries, and it answers "is it behind now?". A message created
            // before the window has no create span to join, so a delay longer than the window doesn't show here.
            var delays = await clickHouse.QueryAsync($"""
                SELECT quantile(0.95)(dateDiff('millisecond', c.created, d.delivered)) / 1000.0, count()
                FROM (SELECT TraceId, ParentSpanId, Timestamp AS delivered FROM otel.otel_traces
                      WHERE {traces} AND {Delivered} AND Timestamp >= {ClickHouseTelemetrySource.To} - INTERVAL {CapacityRecentSeconds} SECOND) AS d
                INNER JOIN (SELECT TraceId, SpanId, Timestamp AS created FROM otel.otel_traces WHERE {traces} AND {Created}) AS c
                    ON d.TraceId = c.TraceId AND d.ParentSpanId = c.SpanId
                """, scope.Window, r => (Seconds: Convert.ToDouble(r.GetValue(0)), Count: Convert.ToInt64(r.GetValue(1))), ct, scope.Parameters);
            var recentDelay = delays.Count > 0 && delays[0].Count > 0 ? delays[0].Seconds : (double?)null;

            return Results.Ok(CapacityEstimate.From(samples, step, truncated, requestRate.Count > 0 ? "metrics" : spans.Count > 0 ? "spans" : "none",
                hosts, cpuCount > 0 ? (int)cpuCount : null) with { Outbox = CapacityEstimate.Backlog(whole, step, recentDelay) });
        });

        /// <summary>The bucket size: given, or the window over about 60 points; never more than <see cref="MaxPoints"/> points.</summary>
        private static int Step(TelemetryWindow window, int? stepSeconds)
        {
            var range = (window.To - window.From).TotalSeconds;
            var step = stepSeconds ?? Math.Max(10, (int)Math.Ceiling(range / 60 / 10) * 10);
            return step < 10 || range / step > MaxPoints
                ? throw Invalid("Invalid stepSeconds", $"stepSeconds is at least 10 and gives at most {MaxPoints} points.")
                : step;
        }

        // ─── search ───

        /// <summary>
        /// Direct lookup (Console requirements §13): a trace id (or a W3C traceparent), a span id (last 24 hours), or services by
        /// name. Ids that are not found return no hit rather than an error.
        /// </summary>
        private static Task<IResult> SearchAsync(HttpContext http, PlatformDbContext db, ClickHouseTelemetrySource clickHouse,
            TimeProvider clock, string? q, CancellationToken ct) => Run(async () =>
        {
            q = q?.Trim();
            if (string.IsNullOrEmpty(q) || q.Length > MaxText) return Bad("Invalid q", $"q is 1 to {MaxText} characters.");
            var hits = new List<SearchHitDto>();
            var tenantKey = (string)http.Items[TenantKeyItem]!;
            var lower = q.ToLowerInvariant();
            if (PlainTraceId(lower) is { } traceId)
            {
                var found = await clickHouse.QueryAsync("SELECT count() FROM otel.otel_traces_trace_id_ts WHERE TraceId = {trace:String}",
                    new TelemetryWindow(tenantKey, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1), r => Convert.ToInt64(r.GetValue(0)), ct,
                    new Dictionary<string, object> { ["trace"] = traceId });
                if (found[0] > 0) hits.Add(new SearchHitDto("trace", traceId, $"Trace {traceId}", traceId));
            }
            else if (SpanIdPattern.IsMatch(lower))
            {
                var now = clock.GetUtcNow();
                var spans = await clickHouse.QueryAsync($"""
                    SELECT TraceId, ServiceName, SpanName FROM otel.otel_traces
                    WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND SpanId = {"{span:String}"}
                    LIMIT {ClickHouseTelemetrySource.Limit}
                    """, new TelemetryWindow(tenantKey, now - MaxSearchRange, now, 1), r => (r.GetString(0), r.GetString(1), r.GetString(2)), ct,
                    new Dictionary<string, object> { ["span"] = lower });
                hits.AddRange(spans.Select(s => new SearchHitDto("span", lower, $"{s.Item2}: {s.Item3}", s.Item1)));
            }
            var tenantId = await db.Tenants.AsNoTracking().Where(t => t.Key == tenantKey).Select(t => t.Id).SingleAsync(ct);
            var pattern = $"%{q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var services = await db.Services.AsNoTracking()
                .Where(s => s.TenantId == tenantId && (EF.Functions.ILike(s.Name, pattern, "\\") || EF.Functions.ILike(s.Namespace, pattern, "\\")))
                .OrderBy(s => s.Name).Take(20).ToListAsync(ct);
            hits.AddRange(services.Select(s => new SearchHitDto("service", s.Id.ToString(), Health.HealthRunner.Name(s), null)));
            return Results.Ok(new TelemetryPage<SearchHitDto>(hits, false));
        });
    }
}
