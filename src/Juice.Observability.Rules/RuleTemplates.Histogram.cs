using System.Diagnostics.Metrics;
using System.Globalization;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Signals;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        /// <summary>The bucket layouts one subject is read in at most, so a subject with many can't fill the row limit. The most observed layouts win.</summary>
        public const int MaxHistogramLayoutsPerSubject = 8;

        private static readonly Meter HistogramMeter = new("Juice.Observability");
        private static readonly Counter<long> HistogramTruncated = HistogramMeter.CreateCounter<long>("juice.observability.rules.truncated", "{subject}",
            "Subjects a rule's query left out because it reached the row limit, by template");

        private const string HistogramHost = "if(JuiceHostId != '', lower(JuiceHostId), lower(JuiceHostName))";

        // Per series (resource and data-point attributes, restart, bucket layout, temporality) the increase of every bucket and of the sum in the window:
        //   cumulative: the last point in the window less the last point before it (a series without one counts from 0, so a new start is a restart);
        //               a count that fell inside the window (a restart under the same start time) counts from 0 too, with the sum from 0 as well;
        //   delta: the points in the window summed bucket by bucket.
        // A series without a point in the window is left out. A sum of 0 behind observations in a bucket that can't hold 0 is a missing sum:
        // that series stays out of Avg (sum_ok). The outer query adds the series up per subject and bucket layout, keeps the most observed layouts
        // of each subject, and fetches one row more than the limit so a cut is seen.
        // The metric, the scope and the filters are parameters ({m}, {svc}, {fk0}, {fv0}...); only their count shapes the text.
        private static string HistogramQuery(string innerKeys, string outerKeys, string scope, string filters) => $"""
            SELECT {outerKeys}, bounds, sumForEach(inc) AS counts, sum(observed) AS obs, sumIf(observed, sum_ok) AS sum_obs, sumIf(series_sum, sum_ok) AS sum_total
            FROM (
                SELECT {innerKeys}, ExplicitBounds AS bounds,
                       argMaxIf(arraySum(BucketCounts), TimeUnix, TimeUnix >= {From}) AS last_n,
                       argMaxIf(arraySum(BucketCounts), TimeUnix, TimeUnix < {From}) AS before_n,
                       maxIf(arraySum(BucketCounts), TimeUnix >= {From}) AS peak_n,
                       AggregationTemporality = 1 AS is_delta,
                       last_n < greatest(before_n, peak_n) AS was_reset,
                       if(is_delta,
                          arrayMap(x -> toInt64(x), arrayReduce('sumForEach', groupArrayIf(BucketCounts, TimeUnix >= {From}))),
                          arrayMap((a, b) -> greatest(toInt64(a) - toInt64(b), 0),
                                   argMaxIf(BucketCounts, TimeUnix, TimeUnix >= {From}),
                                   arrayResize(if(was_reset, emptyArrayUInt64(), argMaxIf(BucketCounts, TimeUnix, TimeUnix < {From})), length(ExplicitBounds) + 1))) AS inc,
                       arraySum(inc) AS observed,
                       if(is_delta,
                          sumIf(Sum, TimeUnix >= {From}),
                          argMaxIf(Sum, TimeUnix, TimeUnix >= {From}) - if(was_reset, 0, argMaxIf(Sum, TimeUnix, TimeUnix < {From}))) AS series_sum,
                       arrayExists((c, i) -> c > 0 AND NOT ((i = 1 OR ExplicitBounds[i - 1] < 0) AND (i > length(ExplicitBounds) OR ExplicitBounds[i] >= 0)),
                                   inc, arrayEnumerate(inc)) AS beyond_zero,
                       NOT (series_sum = 0 AND beyond_zero) AS sum_ok
                FROM otel.otel_metrics_histogram
                WHERE {Tenant} AND TimeUnix >= {From} - INTERVAL 1 HOUR AND TimeUnix < {To} AND MetricName = {"{m:String}"}
                  AND length(BucketCounts) = length(ExplicitBounds) + 1 AND {scope}{filters}
                GROUP BY {outerKeys}, cityHash64(ResourceAttributes, Attributes), StartTimeUnix, ExplicitBounds, AggregationTemporality
                HAVING countIf(TimeUnix >= {From}) > 0
            )
            GROUP BY {outerKeys}, bounds
            ORDER BY {outerKeys}, obs DESC, bounds
            LIMIT {MaxHistogramLayoutsPerSubject} BY {outerKeys}
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

        private sealed record HistogramRow(string SubjectKey, string Ns, string Svc, string Env, string Host, double[] Bounds, long[] Counts,
            long Observed, long SumObserved, double SumTotal);

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
            // One row more than the limit: only a result that exceeds it was cut.
            var rows = await clickHouse.QueryAsync(sql, window with { Limit = window.Limit + 1 }, r => host
                ? new HistogramRow($"host:{r.GetString(0)}", "", "", "", r.GetString(0), Doubles(r.GetValue(1)), Longs(r.GetValue(2)),
                    Convert.ToInt64(r.GetValue(3)), Convert.ToInt64(r.GetValue(4)), Convert.ToDouble(r.GetValue(5)))
                : new HistogramRow(ServiceSubjectKey(r.GetString(0), r.GetString(1), r.GetString(2)), r.GetString(0), r.GetString(1), r.GetString(2), "",
                    Doubles(r.GetValue(3)), Longs(r.GetValue(4)), Convert.ToInt64(r.GetValue(5)), Convert.ToInt64(r.GetValue(6)), Convert.ToDouble(r.GetValue(7))),
                cancellationToken, parameters);

            // The rows come ordered by subject: the last subject may have lost some of its layouts, so it's left out and counted.
            // Which subjects a cut drops is the same every time, as with the other templates at the limit (10k subjects).
            if (rows.Count > window.Limit)
            {
                var last = rows[^1].SubjectKey;
                rows = rows.Where(r => r.SubjectKey != last).ToList();
                HistogramTruncated.Add(1, new KeyValuePair<string, object?>("template", nameof(RuleTemplate.MetricThreshold)));
            }

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
                        // Only series that report a sum count, in the sum and in the observations it's divided by.
                        var counted = layouts.Sum(l => l.SumObserved);
                        value = counted > 0 ? layouts.Sum(l => l.SumTotal) / counted : null;
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
