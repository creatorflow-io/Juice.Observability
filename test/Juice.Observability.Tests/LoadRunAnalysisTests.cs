using Juice.Observability.Api.Endpoints;

namespace Juice.Observability.Tests
{
    /// <summary>Where a service started to struggle: only a condition that lasts, and only one the load brought on.</summary>
    public class LoadRunAnalysisTests
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

        private static LoadRunPointDto Point(int minute, double perSecond, double p95 = 20, double errors = 0, double? queue = 0, double busy = 0.1, double created = 0, double delivered = 0, double backlog = 0)
            => new(Start.AddMinutes(minute), perSecond, errors, p95, queue, busy, created, delivered, backlog);

        private static LoadRunServiceDto Analyze(params LoadRunPointDto[] points) => LoadRunAnalysis.Analyze(null, "orders", points);

        /// <summary>Load rising 10, 20, ... with the p95 given per step.</summary>
        private static LoadRunPointDto[] Rising(params double[] p95) => p95.Select((v, i) => Point(i, (i + 1) * 10, p95: v)).ToArray();

        [Fact]
        public void A_healthy_run_has_no_limit()
        {
            var result = Analyze(Rising(20, 21, 22, 23, 24, 25, 26, 27));

            Assert.Null(result.Limit);
            Assert.Equal(80, result.PeakPerSecond);
            Assert.Equal(27, result.P95AtPeakMs);
            Assert.Equal(20, result.BaselineP95Ms);
        }

        [Fact]
        public void Latency_that_stays_up_is_found_where_it_began()
        {
            var limit = Analyze(Rising(20, 20, 20, 21, 90, 95, 120, 150)).Limit!;

            Assert.Equal(("latency", 50d, 90d, 20d), (limit.Reason, limit.PerSecond, limit.Value, limit.Baseline!.Value));
            Assert.Equal(Start.AddMinutes(4), limit.At);
        }

        [Fact]
        public void One_slow_step_is_a_blip() => Assert.Null(Analyze(Rising(20, 20, 20, 400, 20, 20, 20, 20)).Limit);

        [Fact]
        public void A_slow_last_step_alone_is_not_enough() => Assert.Null(Analyze(Rising(20, 20, 20, 20, 20, 20, 20, 400)).Limit);

        [Fact]
        public void Doubling_a_few_milliseconds_is_not_a_limit() => Assert.Null(Analyze(Rising(2, 2, 2, 2, 9, 9, 9, 9)).Limit);

        [Fact]
        public void A_cold_start_at_low_load_does_not_count()
        {
            // Slow at first because nothing is warmed up, and the load is low then: those are the quiet steps.
            var result = Analyze(Rising(400, 300, 20, 20, 20, 20, 20, 20));

            Assert.Null(result.Limit);
        }

        [Fact]
        public void A_cold_start_does_not_hide_a_real_limit()
        {
            var limit = Analyze(Rising(400, 300, 20, 20, 20, 20, 150, 180)).Limit!;

            Assert.Equal(("latency", 70d, 150d, 20d), (limit.Reason, limit.PerSecond, limit.Value, limit.Baseline!.Value));
        }

        [Fact]
        public void A_consumer_busy_all_the_time_is_a_limit_though_latency_is_flat()
        {
            // A message's wait in the queue isn't in its span, so this limit never shows in latency.
            var limit = Analyze(Point(0, 10, busy: 0.2), Point(1, 20, busy: 0.4), Point(2, 40, busy: 0.8), Point(3, 45, busy: 0.95), Point(4, 45, busy: 0.99), Point(5, 45, busy: 0.99)).Limit!;

            Assert.Equal(("consumer-busy", 45d), (limit.Reason, limit.PerSecond));
            Assert.Equal(Start.AddMinutes(3), limit.At);
        }

        [Fact]
        public void A_limit_reports_the_load_reached_though_the_rate_has_already_fallen()
        {
            // As seen on a real consumer: 64/s, then the rate falls as soon as it's saturated.
            var limit = Analyze(Point(0, 8), Point(1, 32), Point(2, 64, busy: 0.8), Point(3, 36, busy: 1), Point(4, 28, busy: 1)).Limit!;

            Assert.Equal(("consumer-busy", 64d), (limit.Reason, limit.PerSecond));
        }

        [Fact]
        public void A_thread_pool_queue_that_stays_is_a_limit()
        {
            var limit = Analyze(Point(0, 10), Point(1, 20), Point(2, 30, queue: 3), Point(3, 40, queue: 12), Point(4, 50, queue: 30)).Limit!;

            Assert.Equal(("queue", 12d), (limit.Reason, limit.Value));
        }

        [Fact]
        public void Errors_the_load_brought_on_are_a_limit()
        {
            var limit = Analyze(Point(0, 10), Point(1, 20), Point(2, 30, errors: 0.01), Point(3, 40, errors: 0.2), Point(4, 50, errors: 0.3)).Limit!;

            Assert.Equal(("errors", 40d), (limit.Reason, limit.PerSecond));
        }

        [Fact]
        public void Errors_from_the_start_are_not_a_limit_of_the_load()
            => Assert.Null(Analyze(Point(0, 10, errors: 0.2), Point(1, 20, errors: 0.2), Point(2, 30, errors: 0.2), Point(3, 40, errors: 0.2), Point(4, 50, errors: 0.2)).Limit);

        [Fact]
        public void A_growing_outbox_backlog_is_a_limit()
        {
            var limit = Analyze(
                Point(0, 10, created: 600, delivered: 600), Point(1, 20, created: 1200, delivered: 1195, backlog: 5),
                Point(2, 40, created: 2400, delivered: 1500, backlog: 905), Point(3, 40, created: 2400, delivered: 1500, backlog: 1805),
                Point(4, 40, created: 2400, delivered: 1500, backlog: 2705)).Limit!;

            Assert.Equal(("backlog", 905d), (limit.Reason, limit.Value));
            Assert.Equal(Start.AddMinutes(2), limit.At);
        }

        [Fact]
        public void A_backlog_being_worked_off_is_not_a_limit()
            => Assert.Null(Analyze(Point(0, 10, created: 600, backlog: 600), Point(1, 20, delivered: 200, backlog: 400), Point(2, 20, delivered: 200, backlog: 200), Point(3, 20, delivered: 200, backlog: 0)).Limit);

        [Fact]
        public void Too_few_steps_give_no_baseline_but_still_a_peak()
        {
            var result = Analyze(Rising(20, 400, 400));

            Assert.Null(result.BaselineP95Ms);
            Assert.Null(result.Limit);
            Assert.Equal(30, result.PeakPerSecond);
        }

        [Fact]
        public void A_service_with_no_traffic_has_nothing_to_report()
        {
            var result = Analyze();

            Assert.Equal(0, result.PeakPerSecond);
            Assert.Null(result.P95AtPeakMs);
            Assert.Null(result.Limit);
        }
    }
}
