using System.Globalization;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Signals;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        private const string HistogramHost = "if(JuiceHostId != '', lower(JuiceHostId), lower(JuiceHostName))";

        // Per series (resource and data-point attributes, restart, bucket layout, temporality) the increase of every bucket in the window:
        //   cumulative: the last point in the window less the last point before it (a series without one counts from 0, so a new start is a restart),
        //               never below 0; delta: the points in the window summed bucket by bucket.
        // A series without a point in the window is left out. The outer query adds the series up per subject and bucket layout.
        // The metric, the scope and the filters are parameters ({m}, {svc}, {fk0}, {fv0}...); only their count shapes the text.
        private static string HistogramQuery(string innerKeys, string outerKeys, string scope, string filters) => $"""
            SELECT {outerKeys}, bounds, sumForEach(inc) AS counts, sum(observed) AS observed, sum(total) AS total
            FROM (
                SELECT {innerKeys}, ExplicitBounds AS bounds,
                       if(AggregationTemporality = 1,
                          arrayMap(x -> toInt64(x), arrayReduce('sumForEach', groupArrayIf(BucketCounts, TimeUnix >= {From}))),
                          arrayMap((a, b) -> greatest(toInt64(a) - toInt64(b), 0),
                                   argMaxIf(BucketCounts, TimeUnix, TimeUnix >= {From}),
                                   arrayResize(argMaxIf(BucketCounts, TimeUnix, TimeUnix < {From}), length(ExplicitBounds) + 1))) AS inc,
                       arraySum(inc) AS observed,
                       if(AggregationTemporality = 1,
                          sumIf(Sum, TimeUnix >= {From}),
                          greatest(argMaxIf(Sum, TimeUnix, TimeUnix >= {From}) - argMaxIf(Sum, TimeUnix, TimeUnix < {From}), 0)) AS total
                FROM otel.otel_metrics_histogram
                WHERE {Tenant} AND TimeUnix >= {From} - INTERVAL 1 HOUR AND TimeUnix < {To} AND MetricName = {"{m:String}"}
                  AND length(BucketCounts) = length(ExplicitBounds) + 1 AND {scope}{filters}
                GROUP BY {outerKeys}, cityHash64(ResourceAttributes, Attributes), StartTimeUnix, ExplicitBounds, AggregationTemporality
                HAVING countIf(TimeUnix >= {From}) > 0
            )
            GROUP BY {outerKeys}, bounds
            LIMIT {Limit}
            """;

        private static string HistogramByService(string filters) => HistogramQuery(ServiceKeys, "ns, svc, env", ServiceScope, filters);

        private static string HistogramByHost(string filters) => HistogramQuery($"{HistogramHost} AS host", "host",
            $"({HostParam} = '' OR lower(JuiceHostName) = {HostParam} OR lower(JuiceHostId) = {HostParam}) AND {HistogramHost} != ''", filters);

        // One predicate per filter: the attribute key and the values are parameters, matched literally.
        private static string HistogramFilters(IReadOnlyList<MetricFilter> filters)
            => string.Concat(filters.Select((f, i) => f.Op == FilterOp.In
                ? $" AND Attributes[{{fk{i}:String}}] IN {{fv{i}:Array(String)}}"
                : $" AND Attributes[{{fk{i}:String}}] = {{fv{i}:String}}"));

        private sealed record HistogramRow(string SubjectKey, string Ns, string Svc, string Env, string Host, double[] Bounds, long[] Counts, long Observed, double Total);

        private async partial Task<IReadOnlyList<RuleObservation>> ObserveHistogramAsync(RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
        {
            var spec = rule.Metric ?? throw new ArgumentException("A MetricThreshold rule has a Metric", nameof(rule));
            var aggregation = spec.Aggregation;
            if (aggregation is not (MetricAggregation.P50 or MetricAggregation.P90 or MetricAggregation.P95 or MetricAggregation.P99
                or MetricAggregation.Avg or MetricAggregation.Rate))
            {
                throw new ArgumentOutOfRangeException(nameof(rule), aggregation, "A histogram has percentiles, Avg and Rate");
            }

            var parameters = new Dictionary<string, object>(scope) { ["m"] = spec.Name };
            for (var i = 0; i < spec.Filters.Count; i++)
            {
                parameters[$"fk{i}"] = spec.Filters[i].Key;
                parameters[$"fv{i}"] = spec.Filters[i].Op == FilterOp.In ? spec.Filters[i].Values.ToArray() : (object)(spec.Filters[i].Values.FirstOrDefault() ?? "");
            }

            var host = spec.Subject == MetricSubject.Host;
            var filters = HistogramFilters(spec.Filters);
            var sql = host ? HistogramByHost(filters) : HistogramByService(filters);
            var rows = await clickHouse.QueryAsync(sql, window, r => host
                ? new HistogramRow($"host:{r.GetString(0)}", "", "", "", r.GetString(0), Doubles(r.GetValue(1)), Longs(r.GetValue(2)), Convert.ToInt64(r.GetValue(3)), Convert.ToDouble(r.GetValue(4)))
                : new HistogramRow(ServiceSubjectKey(r.GetString(0), r.GetString(1), r.GetString(2)), r.GetString(0), r.GetString(1), r.GetString(2), "",
                    Doubles(r.GetValue(3)), Longs(r.GetValue(4)), Convert.ToInt64(r.GetValue(5)), Convert.ToDouble(r.GetValue(6))),
                cancellationToken, parameters);

            var seconds = Math.Max((window.To - window.From).TotalSeconds, 1);
            return rows.GroupBy(r => r.SubjectKey).Select(g =>
            {
                var layouts = g.ToList();
                var first = layouts[0];
                var observed = layouts.Sum(l => l.Observed);
                double? value;
                IReadOnlyDictionary<string, string>? detail = null;
                var samples = observed;
                switch (aggregation)
                {
                    case MetricAggregation.Rate:
                        value = observed / seconds;
                        break;
                    case MetricAggregation.Avg:
                        value = observed > 0 ? layouts.Sum(l => l.Total) / observed : null;
                        break;
                    default:
                        // A subject whose series disagree on the bucket layout is read in the layout holding most observations.
                        var use = layouts.OrderByDescending(l => l.Observed).First();
                        samples = use.Observed;
                        var q = aggregation switch { MetricAggregation.P50 => 0.5, MetricAggregation.P90 => 0.9, MetricAggregation.P95 => 0.95, _ => 0.99 };
                        var p = HistogramPercentile.Compute(use.Bounds, use.Counts, q);
                        value = p?.Value;
                        if (p is { } percentile)
                        {
                            var d = new Dictionary<string, string>
                            {
                                ["bucketLower"] = Bound(percentile.Lower),
                                ["bucketUpper"] = Bound(percentile.Upper),
                            };
                            if (layouts.Count > 1) d["mixedBounds"] = "true";
                            detail = d;
                        }
                        break;
                }
                return new RuleObservation(first.SubjectKey, host ? HealthSubject.Host : HealthSubject.Service, first.Ns, first.Svc, first.Env, first.Host, value, samples)
                { Detail = detail };
            }).ToList();
        }

        private static string Bound(double v)
            => double.IsPositiveInfinity(v) ? "+Inf" : double.IsNegativeInfinity(v) ? "-Inf" : v.ToString("R", CultureInfo.InvariantCulture);

        private static double[] Doubles(object value) => ((Array)value).Cast<object>().Select(Convert.ToDouble).ToArray();

        private static long[] Longs(object value) => ((Array)value).Cast<object>().Select(Convert.ToInt64).ToArray();
    }
}
