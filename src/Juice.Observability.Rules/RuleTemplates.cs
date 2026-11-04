using Juice.Observability.Data;
using Juice.Observability.Discovery;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// What a template measured for one subject. <see cref="Value"/> null means the subject has no usable data; it is
    /// never read as zero. <see cref="Samples"/> is how many records the value rests on.
    /// </summary>
    public sealed record RuleObservation(string SubjectKey, HealthSubject SubjectType, string ServiceNamespace, string ServiceName,
        string Environment, string Host, double? Value, long Samples);

    /// <summary>
    /// The platform-owned ClickHouse query of each template (requirements §8). Every query filters on the Collector-stamped
    /// tenant, is bounded by the evidence window and row limit, and takes the rule's scope only as parameters.
    /// </summary>
    public sealed class RuleTemplates(ClickHouseTelemetrySource clickHouse)
    {
        private const string Svc = "{svc:String}", Ns = "{ns:String}", EnvParam = "{env:String}", HostParam = "{host:String}";

        // Service scope: the rule's service (namespace and name) and environment, empty for all.
        private const string Env = "if(ResourceAttributes['deployment.environment.name'] = '', 'unspecified', lower(trim(BOTH ' ' FROM ResourceAttributes['deployment.environment.name'])))";
        private const string ServiceScope = $"""
            ServiceName != ''
            AND ({Svc} = '' OR (ServiceName = {Svc} AND ResourceAttributes['service.namespace'] = {Ns}))
            AND ({EnvParam} = '' OR {Env} = {EnvParam})
            """;
        private const string ServiceKeys = $"ResourceAttributes['service.namespace'] AS ns, ServiceName AS svc, {Env} AS env";

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
                SELECT if(ResourceAttributes['host.id'] != '', lower(ResourceAttributes['host.id']), lower(ResourceAttributes['host.name'])) AS host,
                       Attributes['state'] AS state, argMax(Value, TimeUnix) AS bytes
                FROM otel.otel_metrics_sum
                WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = 'system.memory.usage'
                  AND ({HostParam} = '' OR lower(ResourceAttributes['host.name']) = {HostParam} OR lower(ResourceAttributes['host.id']) = {HostParam})
                GROUP BY host, state
            )
            WHERE host != ''
            GROUP BY host
            HAVING sum(bytes) > 0
            LIMIT {Limit}
            """;

        // Juice's counters are cumulative, and a counter's series appears with its first increment: a first error is a new
        // series whose only point may already be 1. So the increase is measured from the series' last value before the
        // window, or from zero when it has none. A series without a point in the window is left out, so a service that
        // stopped reporting has no data rather than a zero; one that reports without errors has a zero.
        private static string CounterIncrease(string failed, string total, string metrics) => $"""
            SELECT ns, svc, env, sumIf(increase, {failed}) AS failed, sumIf(increase, {total}) AS total
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

        private static readonly string DeliveryFailureRate = CounterIncrease(
            "MetricName = 'delivery_failure_total'", "MetricName = 'delivery_attempt_total'",
            "'delivery_failure_total', 'delivery_attempt_total'");

        private static readonly string MediatorErrors = CounterIncrease(
            "MetricName = 'juice.mediator.errors'", "MetricName = 'juice.mediator.requests'",
            "'juice.mediator.errors', 'juice.mediator.requests'");

        private static readonly string ConsumerFailures = CounterIncrease(
            "Attributes['juice.dispatch.result'] = 'failure'", "1", "'juice.messaging.consumed'");

        public async Task<IReadOnlyList<RuleObservation>> ObserveAsync(RuleDefinition rule, TelemetryWindow window, CancellationToken cancellationToken)
        {
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
                RuleTemplate.HostMemory => await clickHouse.QueryAsync(HostMemory, window, r => new RuleObservation($"host:{r.GetString(0)}",
                    HealthSubject.Host, "", "", "", r.GetString(0), Convert.ToDouble(r.GetValue(1)), 1), cancellationToken, scope),
                RuleTemplate.MediatorErrors => await ObserveCountAsync(MediatorErrors, window, scope, cancellationToken),
                RuleTemplate.ConsumerFailures => await ObserveCountAsync(ConsumerFailures, window, scope, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Template, "Unknown template"),
            };
        }

        private Task<IReadOnlyList<RuleObservation>> ObserveCountAsync(string query, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => clickHouse.QueryAsync(query, window, r => Service(r.GetString(0), r.GetString(1), r.GetString(2),
                Convert.ToDouble(r.GetValue(3)), Convert.ToInt64(Convert.ToDouble(r.GetValue(4)))), cancellationToken, scope);

        public static string ServiceSubjectKey(string ns, string name, string environment)
            => $"service:{(ns.Length == 0 ? name : $"{ns}/{name}")}@{environment}";

        private static RuleObservation Service(string ns, string name, string env, double? value, long samples)
            => new(ServiceSubjectKey(ns, name, env), HealthSubject.Service, ns, name, env, "", value, samples);

        private static double? Ratio(object numerator, object denominator)
        {
            var d = Convert.ToDouble(denominator);
            return d > 0 ? Convert.ToDouble(numerator) / d : null;
        }

        private static (string Namespace, string Name) SplitService(string service)
        {
            var slash = service.LastIndexOf('/');
            return slash < 0 ? ("", service) : (service[..slash], service[(slash + 1)..]);
        }
    }
}
