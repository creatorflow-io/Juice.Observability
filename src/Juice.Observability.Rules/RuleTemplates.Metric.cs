using Juice.Observability.Data;
using Juice.Observability.Discovery;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        /// <summary>
        /// MetricThreshold over a gauge, a sum or an up-down counter (histograms are in RuleTemplates.Histogram.cs). The query is built only from platform
        /// constants and the count and operator of the rule's filters; the metric name, attribute keys and values are typed parameters, never text.
        /// </summary>
        private async Task<IReadOnlyList<RuleObservation>> ObserveMetricAsync(RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
        {
            // A rule without a kind (rules.json must give it) observes nothing, so a rule can never read the wrong table.
            if (rule.Metric is not { Kind: { } kind } metric || kind == MetricKind.Histogram) return [];
            var parameters = MetricParameters(metric, scope);
            var seconds = Math.Max((window.To - window.From).TotalSeconds, 1);
            var rate = metric.Aggregation == MetricAggregation.Rate;
            var host = metric.Subject == MetricSubject.Host;
            var query = MetricQuery(kind, metric.Aggregation, metric.Subject, metric.Filters);
            return await clickHouse.QueryAsync(query, window, r =>
            {
                var at = host ? 1 : 3;
                var value = Convert.ToDouble(r.GetValue(at)) / (rate ? seconds : 1);
                var samples = Convert.ToInt64(Convert.ToDouble(r.GetValue(at + 1)));
                var detail = new Dictionary<string, string> { ["unit"] = r.GetString(at + 2) };
                return (host
                    ? new RuleObservation($"host:{r.GetString(0)}", HealthSubject.Host, "", "", "", r.GetString(0), value, samples)
                    : Service(r.GetString(0), r.GetString(1), r.GetString(2), value, samples)) with { Detail = detail };
            }, cancellationToken, parameters);
        }

        /// <summary>The scope plus the metric name and each filter's key and values, all as parameters (<c>metric</c>, <c>f0k</c>, <c>f0v</c>, ...).</summary>
        internal static Dictionary<string, object> MetricParameters(MetricSpec metric, Dictionary<string, object> scope)
        {
            var parameters = new Dictionary<string, object>(scope) { ["metric"] = metric.Name };
            MetricFilters.AddParameters(metric.Filters, parameters);
            return parameters;
        }

        private const string SeriesKey = "cityHash64(ResourceAttributes, Attributes)";
        private const string HostKey = "if(JuiceHostId != '', lower(JuiceHostId), lower(JuiceHostName)) AS host";
        private const string HostScope = $"(JuiceHostId != '' OR JuiceHostName != '') AND ({HostParam} = '' OR lower(JuiceHostName) = {HostParam} OR lower(JuiceHostId) = {HostParam})";

        /// <summary>
        /// Columns: the subject (ns, svc, env; or host), the value, the points it rests on, the unit. Every part is a constant; the filters add only
        /// <c>f{index}k</c> and <c>f{index}v</c> parameter references.
        /// </summary>
        internal static string MetricQuery(MetricKind kind, MetricAggregation aggregation, MetricSubject subject, IReadOnlyList<MetricFilter> filters)
        {
            var host = subject == MetricSubject.Host;
            var keys = host ? HostKey : ServiceKeys;
            var group = host ? "host" : "ns, svc, env";
            var scope = host ? HostScope : ServiceScope;
            var attributes = MetricFilters.Sql(filters);
            var match = $"MetricName = {{metric:String}} AND {scope}{attributes}";
            var inWindow = $"{Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND {match}";

            switch (kind)
            {
                case MetricKind.Gauge when aggregation is MetricAggregation.Avg or MetricAggregation.Max or MetricAggregation.Min:
                    var function = aggregation switch { MetricAggregation.Avg => "avg", MetricAggregation.Max => "max", _ => "min" };
                    return $"""
                        SELECT {keys}, {function}(Value) AS v, count() AS n, any(MetricUnit) AS unit
                        FROM otel.otel_metrics_gauge
                        WHERE {inWindow}
                        GROUP BY {group}
                        LIMIT {Limit}
                        """;
                case MetricKind.Gauge or MetricKind.UpDownCounter:
                    // The last value of each series; series of one subject are averaged (a gauge) or summed (an up-down counter).
                    var table = kind == MetricKind.Gauge ? "otel.otel_metrics_gauge" : "otel.otel_metrics_sum";
                    var mono = kind == MetricKind.UpDownCounter ? " AND IsMonotonic = false" : "";
                    return $"""
                        SELECT {group}, {(kind == MetricKind.Gauge ? "avg" : "sum")}(last) AS v, sum(n) AS n, any(unit) AS unit
                        FROM (
                            SELECT {keys}, {SeriesKey} AS series, argMax(Value, TimeUnix) AS last, count() AS n, any(MetricUnit) AS unit
                            FROM {table}
                            WHERE {inWindow}{mono}
                            GROUP BY {group}, series
                        )
                        GROUP BY {group}
                        LIMIT {Limit}
                        """;
                default:
                    // A counter's series is (resource, attributes, start time): a new start time is a restart. Cumulative series count from their last value
                    // before the window, or from zero; delta series are summed. A series without a point in the window is left out.
                    return $"""
                        SELECT {group}, sum(increase) AS v, sum(n) AS n, any(unit) AS unit
                        FROM (
                            SELECT {keys}, {SeriesKey} AS series, StartTimeUnix,
                                   if(any(AggregationTemporality) = 1, sumIf(Value, TimeUnix >= {From}),
                                      greatest(maxIf(Value, TimeUnix >= {From}) - maxIf(Value, TimeUnix < {From}), 0)) AS increase,
                                   countIf(TimeUnix >= {From}) AS n, any(MetricUnit) AS unit
                            FROM otel.otel_metrics_sum
                            WHERE {Tenant} AND TimeUnix >= {From} - INTERVAL 1 HOUR AND TimeUnix < {To} AND {match} AND IsMonotonic = true
                            GROUP BY {group}, series, StartTimeUnix
                            HAVING n > 0
                        )
                        GROUP BY {group}
                        LIMIT {Limit}
                        """;
            }
        }
    }
}
