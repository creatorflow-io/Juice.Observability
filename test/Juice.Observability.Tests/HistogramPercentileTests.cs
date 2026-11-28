using Juice.Observability.Rules;

namespace Juice.Observability.Tests
{
    /// <summary>Percentiles from bucket counts: always inside the reported bounds, null without observations.</summary>
    public class HistogramPercentileTests
    {
        private static readonly double[] Bounds = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
        private static readonly double[] Qs = [0.5, 0.9, 0.95, 0.99, 0.999];

        private static long[] Bucket(IReadOnlyList<double> data, IReadOnlyList<double> bounds)
        {
            var counts = new long[bounds.Count + 1];
            foreach (var v in data)
            {
                var i = 0;
                while (i < bounds.Count && v > bounds[i]) i++;
                counts[i]++;
            }
            return counts;
        }

        private static double Exact(List<double> data, double q)
        {
            var s = data.OrderBy(x => x).ToList();
            return s[(int)Math.Max(Math.Ceiling(q * s.Count), 1) - 1];
        }

        public static IEnumerable<object[]> Distributions()
        {
            var rnd = new Random(42);
            yield return ["uniform", Enumerable.Range(0, 5000).Select(_ => rnd.NextDouble() * 10).ToList()];
            yield return ["skewed", Enumerable.Range(0, 5000).Select(_ => -Math.Log(1 - rnd.NextDouble()) * 0.1).ToList()];
            yield return ["bimodal", Enumerable.Range(0, 5000).Select(i => i % 2 == 0 ? 0.02 + rnd.NextDouble() * 0.01 : 3 + rnd.NextDouble()).ToList()];
            yield return ["tiny", new List<double> { 0.3, 0.7, 4 }];
            yield return ["overflow", new List<double> { 1, 2, 20, 30, 40 }];
        }

        [Theory]
        [MemberData(nameof(Distributions))]
        public void Compute_matches_exact_within_reported_bounds(string name, List<double> data)
        {
            var counts = Bucket(data, Bounds);
            foreach (var q in Qs)
            {
                var p = HistogramPercentile.Compute(Bounds, counts, q);
                Assert.NotNull(p);
                var exact = Exact(data, q);
                var r = p!.Value;
                Assert.True(exact >= r.Lower && exact <= r.Upper, $"{name} q={q}: exact {exact} outside [{r.Lower}, {r.Upper}]");
                Assert.True(r.Value >= r.Lower && (double.IsPositiveInfinity(r.Upper) || r.Value <= r.Upper));
            }
        }

        [Fact]
        public void Compute_empty_null()
        {
            Assert.Null(HistogramPercentile.Compute(Bounds, new long[Bounds.Length + 1], 0.95));
            Assert.Null(HistogramPercentile.Compute([], [0], 0.5));
            Assert.Null(HistogramPercentile.Compute(Bounds, [1, 2], 0.5)); // shape doesn't fit
        }

        [Fact]
        public void Overflow_bucket_reports_infinite_upper()
        {
            var p = HistogramPercentile.Compute([1, 2], [0, 1, 9], 0.9)!.Value;
            Assert.Equal(2, p.Value);
            Assert.Equal(2, p.Lower);
            Assert.True(double.IsPositiveInfinity(p.Upper));
        }

        [Fact]
        public void Single_bucket()
        {
            // No bounds (a histogram that kept only count and sum): there is no percentile to give, never 0.
            Assert.Null(HistogramPercentile.Compute([], [5], 0.5));

            // One bound, everything at or below it: interpolated from 0.
            var p = HistogramPercentile.Compute([10], [4, 0], 0.5)!.Value;
            Assert.Equal(0, p.Lower);
            Assert.Equal(10, p.Upper);
            Assert.Equal(5, p.Value, 9);
        }

        [Fact]
        public void Zero_counts_skipped()
        {
            // Empty buckets around the one that holds everything never become the answer.
            var p = HistogramPercentile.Compute([1, 2, 3, 4], [0, 0, 10, 0, 0], 0.5)!.Value;
            Assert.Equal(2, p.Lower);
            Assert.Equal(3, p.Upper);
            Assert.Equal(2.5, p.Value, 9);
            var zero = HistogramPercentile.Compute([1, 2, 3, 4], [0, 0, 10, 0, 0], 0)!.Value;
            Assert.Equal(2, zero.Lower);
        }

        [Fact]
        public void Non_positive_first_bound_has_no_lower()
        {
            var p = HistogramPercentile.Compute([-1, 0, 1], [3, 0, 0, 0], 0.5)!.Value;
            Assert.Equal(-1, p.Value);
            Assert.True(double.IsNegativeInfinity(p.Lower));
        }
    }
}
