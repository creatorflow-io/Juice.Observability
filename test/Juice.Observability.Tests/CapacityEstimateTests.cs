using Juice.Observability.Api.Endpoints;

namespace Juice.Observability.Tests
{
    /// <summary>What the capacity calculator is told: a line only where the data supports one, otherwise a figure that says what it is.</summary>
    public class CapacityEstimateTests
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

        private static List<CapacitySample> Samples(int count, Func<int, double> perSecond, Func<int, double, double?> cores,
            Func<int, double, double?>? memory = null, double responseSeconds = 0.1, double? queue = null)
            => Enumerable.Range(0, count).Select(i =>
            {
                var rate = perSecond(i);
                var inFlight = rate * responseSeconds;
                return new CapacitySample("a", Start.AddMinutes(i), rate, inFlight, cores(i, rate), memory?.Invoke(i, inFlight), queue);
            }).ToList();

        private static CapacityDto Estimate(List<CapacitySample> samples) => CapacityEstimate.From(samples, 60, false, "spans", [], null);

        private static OutboxBacklogDto? Backlog(double created, double delivered, double? delay)
            => CapacityEstimate.Backlog([new OutboxPoint(Start, created, delivered), new OutboxPoint(Start.AddMinutes(1), 0, 0)], 60, delay);

        [Fact]
        public void Outbox_that_keeps_up_is_not_behind()
        {
            var result = Backlog(1200, 1195, 0.3)!;

            Assert.Equal(10, result.CreatedPerSecond, 6);
            Assert.Equal(1195d / 120, result.DeliveredPerSecond, 6);
            Assert.Equal(5, result.Undelivered);
            Assert.False(result.FallingBehind);
        }

        [Fact]
        public void Outbox_with_growing_backlog_and_rising_delay_is_behind()
            => Assert.True(Backlog(1200, 600, 30)!.FallingBehind);

        [Fact]
        public void Outbox_that_delivered_nothing_recently_is_behind()
            => Assert.True(Backlog(1200, 0, null)!.FallingBehind);

        [Fact]
        public void Rolled_back_messages_alone_are_not_a_backlog()
            // Messages are missing, but what is delivered goes straight through.
            => Assert.False(Backlog(1200, 600, 0.3)!.FallingBehind);

        [Fact]
        public void Messages_in_transit_at_the_window_edges_are_not_a_backlog()
        {
            Assert.False(Backlog(20, 15, 30)!.FallingBehind);
            Assert.False(Backlog(1000, 960, 30)!.FallingBehind);
        }

        [Fact]
        public void A_backlog_from_before_the_window_being_worked_off_is_not_negative()
            => Assert.Equal(0, Backlog(100, 500, 30)!.Undelivered);

        [Fact]
        public void A_service_without_outbox_messages_has_no_figures()
            => Assert.Null(CapacityEstimate.Backlog([], 60, null));

        [Fact]
        public void Separates_idle_cpu_from_cpu_per_request_when_load_varies()
        {
            var result = Estimate(Samples(12, i => i + 1, (_, rate) => 0.25 + 0.05 * rate));

            Assert.Equal("fitted", result.CpuModel);
            Assert.Equal(0.05, result.CpuSecondsPerUnit!.Value, 6);
            Assert.Equal(0.25, result.IdleCores!.Value, 6);
            Assert.Equal(0.85, result.PeakCores!.Value, 6);
            Assert.Equal(12, result.PeakPerSecond, 6);
        }

        [Fact]
        public void Steady_load_gives_an_average_that_includes_idle_cpu()
        {
            // Idle and per-request cost can't be told apart at one load: 0.25 + 0.05 x 10 = 0.75 cores for 10 requests/s.
            var result = Estimate(Samples(12, _ => 10, (_, rate) => 0.25 + 0.05 * rate));

            Assert.Equal("average", result.CpuModel);
            Assert.Equal(0.075, result.CpuSecondsPerUnit!.Value, 6);
            Assert.Null(result.IdleCores);
        }

        [Fact]
        public void Cpu_that_does_not_follow_load_is_not_fitted()
        {
            var result = Estimate(Samples(12, i => i + 1, (i, _) => i % 2 == 0 ? 0.9 : 0.1));

            Assert.Equal("average", result.CpuModel);
            Assert.Null(result.IdleCores);
        }

        [Fact]
        public void Too_few_samples_are_not_fitted()
        {
            var result = Estimate(Samples(CapacityEstimate.MinFitSamples - 1, i => i + 1, (_, rate) => 0.25 + 0.05 * rate));

            Assert.Equal("average", result.CpuModel);
        }

        [Fact]
        public void Cost_that_falls_with_load_is_not_fitted()
        {
            var result = Estimate(Samples(12, i => i + 1, (_, rate) => 2 - 0.05 * rate));

            Assert.Equal("average", result.CpuModel);
        }

        [Fact]
        public void Idle_samples_count_toward_the_line_but_not_the_response_time()
        {
            var result = Estimate(Samples(12, i => i < 6 ? 0 : i, (_, rate) => 0.25 + 0.05 * rate));

            Assert.Equal("fitted", result.CpuModel);
            Assert.Equal(0.25, result.IdleCores!.Value, 6);
            Assert.Equal(100, result.ResponseMs!.Value, 6);
        }

        [Fact]
        public void Memory_is_idle_plus_growth_per_unit_in_flight_or_only_the_peak()
        {
            var fitted = Estimate(Samples(12, i => i + 1, (_, _) => null, (_, inFlight) => 200e6 + 10e6 * inFlight));
            Assert.Equal("fitted", fitted.MemoryModel);
            Assert.Equal(200e6, fitted.IdleMemoryBytes!.Value, 0);
            Assert.Equal(10e6, fitted.MemoryBytesPerInFlight!.Value, 0);

            var flat = Estimate(Samples(12, i => i + 1, (_, _) => null, (_, _) => 300e6));
            Assert.Equal("peak", flat.MemoryModel);
            Assert.Null(flat.IdleMemoryBytes);
            Assert.Equal(300e6, flat.PeakMemoryBytes!.Value, 0);
        }

        [Fact]
        public void Reports_response_time_rising_with_load()
        {
            var samples = Enumerable.Range(0, 12).Select(i =>
            {
                double rate = i + 1;
                var seconds = i < 6 ? 0.1 : 0.4;
                return new CapacitySample("a", Start.AddMinutes(i), rate, rate * seconds, null, null, null);
            }).ToList();

            var result = Estimate(samples);

            Assert.Equal(100, result.ResponseLowLoadMs!.Value, 6);
            Assert.Equal(400, result.ResponseHighLoadMs!.Value, 6);
        }

        [Fact]
        public void Response_by_load_is_not_given_when_load_barely_varied()
        {
            var result = Estimate(Samples(12, i => 10 + 0.1 * i, (_, _) => null));

            Assert.Null(result.ResponseLowLoadMs);
            Assert.Null(result.ResponseHighLoadMs);
        }

        [Fact]
        public void Without_a_line_cpu_per_request_is_the_cost_when_busiest()
        {
            // Cost per request rising with load: 30 ms when quiet, 80 ms at the top. Sizing from a plain average would read low.
            var result = Estimate(Samples(12, i => i + 1, (i, rate) => rate * (i < 9 ? 0.03 : 0.08)));

            Assert.Equal("average", result.CpuModel);
            Assert.Equal(0.08, result.CpuSecondsPerUnit!.Value, 6);
        }

        [Fact]
        public void Reports_the_busiest_a_consumer_was()
        {
            var samples = new[] { (Rate: 6.8, Seconds: 0.093), (Rate: 5.5, Seconds: 0.181), (Rate: 2.7, Seconds: 0.365) }
                .Select((m, i) => new CapacitySample("a", Start.AddMinutes(i), m.Rate + 1, m.Rate * m.Seconds, null, null, null, m.Rate, m.Rate * m.Seconds)).ToList();

            var result = Estimate(samples);

            Assert.Equal(6.8, result.PeakMessagesPerSecond, 6);
            Assert.Equal(5.5 * 0.181, result.PeakMessagesInFlight, 6);
        }

        [Fact]
        public void No_samples_and_missing_metrics_say_so()
        {
            var empty = Estimate([]);
            Assert.Equal(("none", "none"), (empty.CpuModel, empty.MemoryModel));
            Assert.Null(empty.ResponseMs);
            Assert.Null(empty.CpuSecondsPerUnit);

            // Requests but no System.Runtime meter: no CPU figure, not a figure of zero.
            var noRuntime = Estimate(Samples(12, i => i + 1, (_, _) => null));
            Assert.Equal(("none", "none"), (noRuntime.CpuModel, noRuntime.MemoryModel));
            Assert.Null(noRuntime.CpuSecondsPerUnit);
            Assert.Equal(100, noRuntime.ResponseMs!.Value, 6);
        }
    }
}
