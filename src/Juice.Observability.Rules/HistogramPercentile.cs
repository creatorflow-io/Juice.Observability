namespace Juice.Observability.Rules
{
    /// <summary>A percentile estimated from bucket counts: the interpolated <see cref="Value"/>, and the bucket's bounds it lies in.</summary>
    public readonly record struct Percentile(double Value, double Lower, double Upper);

    /// <summary>
    /// Percentiles of an explicit-bucket histogram. Bucket i holds the observations in (bounds[i-1], bounds[i]], and the last holds
    /// everything above the last bound. The estimate is interpolated linearly inside the bucket, so the true value always lies in
    /// [Lower, Upper]; the bounds are what the evidence reports.
    /// </summary>
    public static class HistogramPercentile
    {
        /// <summary>
        /// The q-quantile (0 to 1) of <paramref name="counts"/> (one more than bounds). Null when there are no observations or the shapes don't fit.
        /// The first bucket's lower bound is 0 when bounds[0] is positive; a quantile in the overflow bucket gives Value = Lower = the last bound, Upper = +Inf.
        /// </summary>
        public static Percentile? Compute(IReadOnlyList<double> bounds, IReadOnlyList<long> counts, double q)
        {
            if (counts.Count != bounds.Count + 1 || double.IsNaN(q)) return null;
            q = Math.Clamp(q, 0d, 1d);
            double total = 0;
            for (var i = 0; i < counts.Count; i++) total += Math.Max(counts[i], 0);
            if (total <= 0) return null;

            var rank = Math.Max(Math.Ceiling(q * total), 1d);
            double before = 0;
            for (var i = 0; i < counts.Count; i++)
            {
                var c = Math.Max(counts[i], 0);
                if (c == 0) continue;
                if (before + c < rank) { before += c; continue; }

                if (i == bounds.Count)
                {
                    var last = bounds.Count == 0 ? 0d : bounds[^1];
                    return new Percentile(last, last, double.PositiveInfinity);
                }
                var upper = bounds[i];
                double lower;
                if (i > 0) lower = bounds[i - 1];
                else if (upper > 0) lower = 0;
                else return new Percentile(upper, double.NegativeInfinity, upper); // nothing to interpolate from below the first bound
                return new Percentile(lower + (upper - lower) * ((rank - before) / c), lower, upper);
            }
            return null;
        }
    }
}
