using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
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
    public sealed partial class RuleTemplates(ClickHouseTelemetrySource clickHouse, SignalPacks? packs = null, IEnumerable<IRuleTemplate>? moduleTemplates = null,
        IEnumerable<IRuleThresholdProvider>? thresholdProviders = null, IEnumerable<ModuleRuleContribution>? contributions = null)
    {
        private RuleTemplateRegistry? _registry;

        /// <summary>
        /// The templates and threshold providers this host knows: the core's, whose queries are in this class, and the modules' (core 2.0 M2).
        /// </summary>
        public RuleTemplateRegistry Registry => LazyInitializer.EnsureInitialized(ref _registry, () =>
            new RuleTemplateRegistry(BuiltInTemplates.Create(this), moduleTemplates, thresholdProviders, contributions))!;

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
        public async Task<IReadOnlyList<RuleObservation>> ObserveAsync(RuleDefinition rule, TelemetryWindow window, CancellationToken cancellationToken, bool preview = false)
        {
            if (preview)
            {
                IReadOnlyList<RuleObservation> result = [];
                await AsPreviewAsync(async () => result = await ObserveAsync(rule, window, cancellationToken));
                return result;
            }
            if (!(packs ?? SignalPacks.All).Provides(rule.Template)) return [];
            if (!Registry.TryGet(rule.Template.ToString(), out var template)) throw new ArgumentOutOfRangeException(nameof(rule), rule.Template, "Unknown template");
            var telemetry = new TemplateTelemetry(clickHouse, window);
            var observed = await template.ObserveAsync(rule.ToSpec(), telemetry, new TemplateWindow(window.From, window.To, window.Limit), cancellationToken);
            // A template, a module's too, can't return more subjects than the window's row limit.
            return [.. observed.Take(window.Limit).Select(o => new RuleObservation(o.SubjectKey, o.SubjectKind == AlertSubjectKind.Host ? HealthSubject.Host : HealthSubject.Service,
                o.ServiceNamespace, o.ServiceName, o.Environment, o.Host, o.Value, o.Samples) { Detail = o.Detail })];
        }

        /// <summary>The rule's scope as the parameters of every built-in query: values only, never text.</summary>
        internal static Dictionary<string, object> Scope(RuleDefinition rule)
        {
            var (ns, svc) = SplitService(rule.Service);
            return new Dictionary<string, object> { ["svc"] = svc, ["ns"] = ns, ["env"] = rule.Environment, ["host"] = rule.Host.ToLowerInvariant() };
        }

        // The built-in templates' queries, each called by its adapter (BuiltInTemplates).
        internal Task<IReadOnlyList<RuleObservation>> ObserveErrorRateAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(ErrorRate, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Ratio(r.GetValue(3), r.GetValue(4)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope);

        internal Task<IReadOnlyList<RuleObservation>> ObserveLatencyP95Async(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(LatencyP95, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope);

        internal Task<IReadOnlyList<RuleObservation>> ObserveThroughputAbsentAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(ThroughputAbsent, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(r.GetValue(4))), cancellationToken, scope);

        internal Task<IReadOnlyList<RuleObservation>> ObserveDeliveryFailureRateAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(DeliveryFailureRate, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Ratio(r.GetValue(3), r.GetValue(4)), Convert.ToInt64(Convert.ToDouble(r.GetValue(4)))), cancellationToken, scope);

        internal Task<IReadOnlyList<RuleObservation>> ObserveHostMemoryAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => QueryBoundedAsync(RuleTemplate.HostMemory, HostMemory, window, r => new RuleObservation($"host:{r.GetString(0)}",
                HealthSubject.Host, "", "", "", r.GetString(0), Convert.ToDouble(r.GetValue(1)), 1), cancellationToken, scope);

        internal Task<IReadOnlyList<RuleObservation>> ObserveMediatorErrorsAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => ObserveCountAsync(MediatorErrors, window, scope, cancellationToken);

        internal Task<IReadOnlyList<RuleObservation>> ObserveConsumerFailuresAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => ObserveCountAsync(ConsumerFailures, window, scope, cancellationToken);

        // The host templates' bodies are private to RuleTemplates.Host.cs.
        internal Task<IReadOnlyList<RuleObservation>> ObserveCpuAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => ObserveHostCpuAsync(window, scope, cancellationToken);

        internal Task<IReadOnlyList<RuleObservation>> ObserveDiskAsync(TelemetryWindow window, Dictionary<string, object> scope, CancellationToken cancellationToken)
            => ObserveHostDiskAsync(window, scope, cancellationToken);

        internal Task<IReadOnlyList<RuleObservation>> ObserveMetricThresholdAsync(RuleDefinition rule, TelemetryWindow window, Dictionary<string, object> scope,
            CancellationToken cancellationToken)
            => rule.Metric is { Kind: MetricKind.Histogram }
                ? ObserveHistogramAsync(rule, window, scope, cancellationToken)
                : ObserveMetricAsync(rule, window, scope, cancellationToken);

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
            return cut ? CutLast(rows, window.Limit, template.ToString()) : rows;   // cut: false when the caller merges sources and cuts once
        }

        internal static IReadOnlyList<RuleObservation> CutLast(IReadOnlyList<RuleObservation> rows, int limit, string template)
        {
            if (rows.Count <= limit) return rows;
            var last = rows[^1].SubjectKey;
            CountCut(template);
            return [.. rows.Where(r => r.SubjectKey != last)];
        }

        private static readonly AsyncLocal<bool> Previewing = new();

        /// <summary>Counts a cut (<c>rules.truncated</c>), except in a preview: only the scheduler's evaluations are counted.</summary>
        private static void CountCut(string template)
        {
            if (!Previewing.Value) RulesTruncated.Add(1, new KeyValuePair<string, object?>("template", template));
        }

        /// <summary>Runs <paramref name="observe"/> as a preview: what it cuts is not counted. The flag ends with the call.</summary>
        internal static async Task AsPreviewAsync(Func<Task> observe)
        {
            var before = Previewing.Value;
            Previewing.Value = true;
            try
            {
                await observe();
            }
            finally
            {
                Previewing.Value = before;
            }
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
