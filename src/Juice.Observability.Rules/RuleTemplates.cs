using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Signals;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// What a template measured for one subject. <see cref="Value"/> null means the subject has no usable data; it is
    /// never read as zero. <see cref="Samples"/> is how many records the value rests on.
    /// </summary>
    public sealed record RuleObservation(string SubjectKey, HealthSubject SubjectType, string ServiceNamespace, string ServiceName,
        string Environment, string Host, double? Value, long Samples)
    {
        /// <summary>Extra evidence (bucket bounds, source, mount), merged into the alert's evidence; it can't override the base keys.</summary>
        public IReadOnlyDictionary<string, string>? Detail { get; init; }
    }

    /// <summary>
    /// The platform-owned ClickHouse query of each template (requirements §8). Every query filters on the Collector-stamped
    /// tenant, is bounded by the evidence window and row limit, and takes the rule's scope only as parameters.
    /// </summary>
    public sealed partial class RuleTemplates(ClickHouseTelemetrySource clickHouse, SignalPacks? packs = null)
    {
        private const string Svc = "{svc:String}", Ns = "{ns:String}", EnvParam = "{env:String}", HostParam = "{host:String}";

        // Service scope: the rule's service (namespace and name) and environment, empty for all.
        private const string Env = EnvironmentKey;
        private const string ServiceScope = $"""
            ServiceName != ''
            AND ({Svc} = '' OR (ServiceName = {Svc} AND JuiceServiceNamespace = {Ns}))
            AND ({EnvParam} = '' OR {Env} = {EnvParam})
            """;
        private const string ServiceKeys = $"JuiceServiceNamespace AS ns, ServiceName AS svc, {Env} AS env";

        private static readonly string ErrorRate = $"""
            SELECT {ServiceKeys}, countIf(StatusCode = 'Error') AS errors, count() AS requests
            FROM otel.otel_traces
            WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND SpanKind IN ('Server', 'Consumer') AND {ServiceScope}
            GROUP BY ns, svc, env
            LIMIT {Limit}
            """;

        private static readonly string LatencyP95 = $"""
            SELECT {ServiceKeys}, quantile(0.95)(Duration) / 1e6 AS p95_ms, count() AS requests
            FROM otel.otel_traces
            WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND SpanKind IN ('Server', 'Consumer') AND {ServiceScope}
            GROUP BY ns, svc, env
            LIMIT {Limit}
            """;

        // Requests in the window, and in the hour before it: a service with no baseline traffic has no data.
        private static readonly string ThroughputAbsent = $"""
            SELECT {ServiceKeys}, countIf(Timestamp >= {From}) AS current, countIf(Timestamp < {From}) AS baseline
            FROM otel.otel_traces
            WHERE {Tenant} AND Timestamp >= {From} - INTERVAL 1 HOUR AND Timestamp < {To} AND SpanKind IN ('Server', 'Consumer') AND {ServiceScope}
            GROUP BY ns, svc, env
            HAVING baseline > 0
            LIMIT {Limit}
            """;

        private static readonly string HostMemory = $"""
            SELECT host, sumIf(bytes, state = 'used') / sum(bytes) AS used
            FROM (
                SELECT if(JuiceHostId != '', lower(JuiceHostId), lower(JuiceHostName)) AS host,
                       Attributes['state'] AS state, argMax(Value, TimeUnix) AS bytes
                FROM otel.otel_metrics_sum
                WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = 'system.memory.usage'
                  AND ({HostParam} = '' OR lower(JuiceHostName) = {HostParam} OR lower(JuiceHostId) = {HostParam})
                GROUP BY host, state
            )
            WHERE host != ''
            GROUP BY host
            HAVING sum(bytes) > 0
            ORDER BY host
            LIMIT {Limit}
            """;

        // These counters are cumulative, and a counter's series appears with its first increment: a first error is a new
        // series whose only point may already be 1. So the increase is measured from the series' last value before the
        // window, or from zero when it has none. A series without a point in the window is left out, so a service that
        // stopped reporting has no data rather than a zero; one that reports without errors has a zero.
        // failed and total are aggregates over the series' increase (sumIf(increase, ...)).
        private static string CounterIncrease(string failed, string total, string metrics) => $"""
            SELECT ns, svc, env, {failed} AS failed, {total} AS total
            FROM (
                SELECT {ServiceKeys}, MetricName, Attributes, StartTimeUnix,
                       greatest(maxIf(Value, TimeUnix >= {From}) - maxIf(Value, TimeUnix < {From}), 0) AS increase
                FROM otel.otel_metrics_sum
                WHERE {Tenant} AND TimeUnix >= {From} - INTERVAL 1 HOUR AND TimeUnix < {To}
                  AND MetricName IN ({metrics}) AND {ServiceScope}
                GROUP BY ns, svc, env, MetricName, Attributes, StartTimeUnix
                HAVING countIf(TimeUnix >= {From}) > 0
            )
            GROUP BY ns, svc, env
            LIMIT {Limit}
            """;

        // The Juice signal pack's templates (JuiceSignals).
        // A service on Juice 10.14.0 or later sends juice.delivery.*, and the old names too when it collects delivery.metrics: the old names
        // are read only from a service (in an environment) that sends none of the new ones.
        private static string Delivery(string metric, string legacy)
            => $"if(countIf(MetricName IN ('{JuiceSignals.DeliveryAttempts}', '{JuiceSignals.DeliveryFailures}')) > 0, sumIf(increase, MetricName = '{metric}'), sumIf(increase, MetricName = '{legacy}'))";

        private static readonly string DeliveryFailureRate = CounterIncrease(
            Delivery(JuiceSignals.DeliveryFailures, JuiceSignals.LegacyDeliveryFailures), Delivery(JuiceSignals.DeliveryAttempts, JuiceSignals.LegacyDeliveryAttempts),
            $"'{JuiceSignals.DeliveryFailures}', '{JuiceSignals.DeliveryAttempts}', '{JuiceSignals.LegacyDeliveryFailures}', '{JuiceSignals.LegacyDeliveryAttempts}'");

        private static readonly string MediatorErrors = CounterIncrease(
            $"sumIf(increase, MetricName = '{JuiceSignals.MediatorErrors}')", $"sumIf(increase, MetricName = '{JuiceSignals.MediatorRequests}')",
            $"'{JuiceSignals.MediatorErrors}', '{JuiceSignals.MediatorRequests}'");

        private static readonly string ConsumerFailures = CounterIncrease(
            $"sumIf(increase, Attributes['{JuiceSignals.DispatchResult}'] = '{JuiceSignals.DispatchFailure}')", "sum(increase)", $"'{JuiceSignals.MessagesConsumed}'");

        /// <summary>
        /// What the rule's template measures now. A template whose signal pack is off observes nothing, so a rule made before the pack was
        /// switched off holds its open alerts as no data.
        /// </summary>
        public async Task<IReadOnlyList<RuleObservation>> ObserveAsync(RuleDefinition rule, TelemetryWindow window, CancellationToken cancellationToken)
        {
            if (!(packs ?? SignalPacks.All).Provides(rule.Template)) return [];
            var (ns, svc) = SplitService(rule.Service);
            var scope = new Dictionary<string, object> { ["svc"] = svc, ["ns"] = ns, ["env"] = rule.Environment, ["host"] = rule.Host.ToLowerInvariant() };
            return rule.Template switch
            {
                RuleTemplate.ErrorRate => await clickHouse.QueryAsync(ErrorRate, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                    Ratio(r.GetValue(3), r.GetValue(4)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope),
                RuleTemplate.LatencyP95 => await clickHouse.QueryAsync(LatencyP95, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                    Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope),
                RuleTemplate.ThroughputAbsent => await clickHouse.QueryAsync(ThroughputAbsent, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                    Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope),
                RuleTemplate.DeliveryFailureRate => await clickHouse.QueryAsync(DeliveryFailureRate, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                    Ratio(r.GetValue(3), r.GetValue(4)), Convert.ToInt64(Convert.ToDouble(r.GetValue(4)))), cancellationToken, scope),
                RuleTemplate.HostMemory => await QueryBoundedAsync(RuleTemplate.HostMemory, HostMemory, window, r => new RuleObservation($"host:{r.GetString(0)}",
                    HealthSubject.Host, "", "", "", r.GetString(0), Convert.ToDouble(r.GetValue(1)), 1), cancellationToken, scope),
                RuleTemplate.MediatorErrors => await ObserveCountAsync(MediatorErrors, window, scope, cancellationToken),
                RuleTemplate.ConsumerFailures => await ObserveCountAsync(ConsumerFailures, window, scope, cancellationToken),
                RuleTemplate.MetricThreshold => rule.Metric is { Kind: MetricKind.Histogram }
                    ? await ObserveHistogramAsync(rule, window, scope, cancellationToken)
                    : await ObserveMetricAsync(rule, window, scope, cancellationToken),
                RuleTemplate.HostCpu => await ObserveHostCpuAsync(window, scope, cancellationToken),
                RuleTemplate.HostDisk => await ObserveHostDiskAsync(window, scope, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Template, "Unknown template"),
            };
        }

        private Task<IReadOnlyList<RuleObservation>> ObserveCountAsync(string query, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(query, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(Convert.ToDouble(r.GetValue(4)))), cancellationToken, scope);

        // Bodies in RuleTemplates.Histogram.cs (histograms) and RuleTemplates.Host.cs (HostCpu, HostDisk).
        private partial Task<IReadOnlyList<RuleObservation>> ObserveHistogramAsync(RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken);

        private partial Task<IReadOnlyList<RuleObservation>> ObserveHostCpuAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken);

        private partial Task<IReadOnlyList<RuleObservation>> ObserveHostDiskAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken);

        /// <summary>
        /// A query ordered by subject, read one row past the limit: a result that exceeds it was cut, so its last subject (possibly partial) is left
        /// out and <c>rules.truncated</c> counted. The same for every template, as the histogram's.
        /// </summary>
        private async Task<IReadOnlyList<RuleObservation>> QueryBoundedAsync(RuleTemplate template, string sql, TelemetryWindow window,
            Func<System.Data.Common.DbDataReader, RuleObservation> map, CancellationToken cancellationToken, Dictionary<string, object> parameters, bool cut = true)
        {
            var rows = await clickHouse.QueryAsync(sql, window with { Limit = window.Limit + 1 }, map, cancellationToken, parameters);
            return cut ? CutLast(rows, window.Limit, template) : rows;   // cut: false when the caller merges sources and cuts once
        }

        private static IReadOnlyList<RuleObservation> CutLast(IReadOnlyList<RuleObservation> rows, int limit, RuleTemplate template)
        {
            if (rows.Count <= limit) return rows;
            var last = rows[^1].SubjectKey;
            RulesTruncated.Add(1, new KeyValuePair<string, object?>("template", template.ToString()));
            return [.. rows.Where(r => r.SubjectKey != last)];
        }

        public static string ServiceSubjectKey(string ns, string name, string environment)
            => $"service:{(ns.Length == 0 ? name : $"{ns}/{name}")}@{environment}";

        private static RuleObservation Service(string ns, string name, string env, double? value, long samples)
            => new(ServiceSubjectKey(ns, name, env), HealthSubject.Service, ns, name, env, "", value, samples);

        private static double? Ratio(object numerator, object denominator)
        {
            var d = Convert.ToDouble(denominator);
            return d > 0 ? Convert.ToDouble(numerator) / d : null;
        }

    }
}
