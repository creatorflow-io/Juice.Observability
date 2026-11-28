using Juice.Observability.Data;
using Juice.Observability.Rules;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 12 M4: the preview's plan and its in-memory replay of the alert lifecycle (pure).</summary>
    public class RulePreviewReplayTests
    {
        private static readonly DateTimeOffset End = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        private static RuleDefinition Rule(Action<RuleDefinition>? change = null)
        {
            var rule = new RuleDefinition
            {
                Key = "r", Name = "r", Template = RuleTemplate.HostMemory, Threshold = 0.8, RecoveryThreshold = 0.7,
                Schedule = TimeSpan.FromMinutes(1), Window = TimeSpan.FromMinutes(5),
                ForDuration = TimeSpan.FromMinutes(30), RecoveryDuration = TimeSpan.FromMinutes(30), Cooldown = TimeSpan.Zero,
            };
            change?.Invoke(rule);
            return rule;
        }

        private static RuleObservation Host(string host, double? value, long samples = 1)
            => new($"host:{host}", HealthSubject.Host, "", "", "", host, value, samples);

        /// <summary>One point per 15 minutes; each entry is the value of subject "a" (null: observed with no value, <c>NaN</c> there: not observed).</summary>
        private static List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)> Series(params double?[] values)
            => [.. values.Select((v, i) => (End.AddMinutes(15 * (i - values.Length + 1)),
                (IReadOnlyList<RuleObservation>)(v is { } d && double.IsNaN(d) ? [] : [Host("a", v)])))];

        [Fact]
        public void Subjects_seen_at_different_instants_are_capped_at_the_row_limit_together()
        {
            // Each instant sees 2 subjects (under a row limit of 3), but the instants see different ones: the union is 6.
            var points = new List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)>();
            for (var i = 0; i < 3; i++)
            {
                points.Add((End.AddMinutes(15 * (i - 2)), [Host($"s{2 * i}", 0.9), Host($"s{2 * i + 1}", 0.9)]));
            }
            var cap = Api.Endpoints.RulePreviewEndpoints.SubjectCap(rowLimit: 3);

            var subjects = RulePreview.Replay(Rule(), points, cap, out var truncated);

            Assert.Equal(3, cap);
            Assert.Equal(3, subjects.Count);
            Assert.True(truncated);
            Assert.Equal(50, Api.Endpoints.RulePreviewEndpoints.SubjectCap(rowLimit: 10_000));
            Assert.Equal(1, Api.Endpoints.RulePreviewEndpoints.SubjectCap(rowLimit: 0));
        }

        [Theory]
        [InlineData(1, 5, 15)]      // schedule and window below the floor: 15 minutes
        [InlineData(20, 5, 20)]     // a longer schedule
        [InlineData(1, 60, 30)]     // half a 1 h window
        [InlineData(1, 360, 180)]   // half the longest window
        public void Plan_step_is_max_of_schedule_15min_half_window(int scheduleMinutes, int windowMinutes, int expectedMinutes)
        {
            var rule = Rule(r => { r.Schedule = TimeSpan.FromMinutes(scheduleMinutes); r.Window = TimeSpan.FromMinutes(windowMinutes); });
            var (step, at) = RulePreview.Plan(rule, End);
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), step);
            Assert.Equal(24 * 60 / expectedMinutes, at.Count);
            Assert.All(at.Zip(at.Skip(1)), pair => Assert.Equal(step, pair.Second - pair.First));
        }

        [Theory]
        [InlineData(5)]
        [InlineData(15)]
        [InlineData(60)]
        public void Plan_at_most_96_points_ending_at_lag(int scheduleMinutes)
        {
            var (step, at) = RulePreview.Plan(Rule(r => { r.Schedule = TimeSpan.FromMinutes(scheduleMinutes); r.Window = TimeSpan.FromHours(1); }), End);
            Assert.InRange(at.Count, 1, 96);
            Assert.Equal(End, at[^1]);
            Assert.True(at[^1] - at[0] < TimeSpan.FromHours(24));
            Assert.Equal(at.OrderBy(a => a), at);
            // Even a 5 s schedule can't ask for more than 96 points.
            Assert.Equal(96, RulePreview.Plan(Rule(r => { r.Schedule = TimeSpan.FromSeconds(5); r.Window = TimeSpan.FromSeconds(5); }), End).At.Count);
            Assert.True(step >= TimeSpan.FromMinutes(15));
        }

        [Fact]
        public void Replay_sustained_breach_fires_once()
        {
            // ForDuration 30 min at a 15 min step: the third breaching point fires; it keeps breaching and doesn't fire again.
            var subjects = RulePreview.Replay(Rule(), Series(0.9, 0.9, 0.9, 0.9, 0.9), 50, out var truncated);

            var s = Assert.Single(subjects);
            Assert.False(truncated);
            Assert.Equal(5, s.Breaches);
            Assert.Equal(1, s.WouldOpen);
            Assert.Equal(1, s.WouldFire);
            Assert.Equal(End.AddMinutes(-30), s.FirstFiredAt);
            Assert.Equal(0.9, s.Last);
            Assert.Equal(5, s.Points.Count);
            Assert.All(s.Points, p => Assert.Equal(Condition.Breach, p.Condition));
        }

        [Fact]
        public void Replay_transient_breach_never_fires()
        {
            var s = Assert.Single(RulePreview.Replay(Rule(), Series(0.5, 0.9, 0.5, 0.9, 0.5), 50, out _));
            Assert.Equal(2, s.Breaches);
            Assert.Equal(2, s.WouldOpen);
            Assert.Equal(0, s.WouldFire);
            Assert.Null(s.FirstFiredAt);
            Assert.Equal(0.5, s.Min);
            Assert.Equal(0.9, s.Max);
        }

        [Fact]
        public void Replay_nodata_holds_state_never_zero()
        {
            // Breach, two points of no data, breach: the pending alert is held through them (not cleared, as a zero would), and fires 45 min after it opened.
            var s = Assert.Single(RulePreview.Replay(Rule(r => r.ForDuration = TimeSpan.FromMinutes(45)), Series(0.9, double.NaN, null, 0.9, 0.9, 0.9), 50, out _));
            Assert.Equal(2, s.NoData);
            Assert.Equal(1, s.WouldOpen);
            Assert.Equal(1, s.WouldFire);
            Assert.Equal(End.AddMinutes(-30), s.FirstFiredAt);
            Assert.Equal(Condition.NoData, s.Points[1].Condition);
            Assert.Null(s.Points[1].Value);
            Assert.Equal(Condition.NoData, s.Points[2].Condition);
            Assert.Equal(0.9, s.Min);   // a missing value was never a zero
        }

        [Fact]
        public void Replay_cooldown_blocks_refire()
        {
            // Zero ForDuration fires on the first breach, and resolves at the next recovered point (15 min later); the cooldown runs from then.
            var rule = Rule(r => { r.ForDuration = TimeSpan.Zero; r.RecoveryDuration = TimeSpan.Zero; r.Cooldown = TimeSpan.FromMinutes(100); });
            var s = Assert.Single(RulePreview.Replay(rule, Series(0.9, 0.5, 0.9, 0.9, 0.9, 0.9), 50, out _));
            Assert.Equal(1, s.WouldFire);
            Assert.Equal(2, s.WouldOpen);
            Assert.Equal(End.AddMinutes(-75), s.FirstFiredAt);
            var free = Assert.Single(RulePreview.Replay(Rule(r => { r.ForDuration = TimeSpan.Zero; r.RecoveryDuration = TimeSpan.Zero; r.Cooldown = TimeSpan.Zero; }),
                Series(0.9, 0.5, 0.9, 0.9, 0.9, 0.9), 50, out _));
            Assert.Equal(2, free.WouldFire);
        }

        [Fact]
        public void Replay_cooldown_holds_a_pending_alert_back()
        {
            var rule = Rule(r => { r.ForDuration = TimeSpan.FromMinutes(15); r.RecoveryDuration = TimeSpan.Zero; r.Cooldown = TimeSpan.FromHours(6); });
            // Fires at the second breach, resolves, breaches again for a long time: held by the cooldown, so it fires once.
            var s = Assert.Single(RulePreview.Replay(rule, Series(0.9, 0.9, 0.5, 0.9, 0.9, 0.9, 0.9, 0.9), 50, out _));
            Assert.Equal(1, s.WouldFire);
            Assert.Equal(2, s.WouldOpen);
            // The same without a cooldown fires twice.
            var free = Assert.Single(RulePreview.Replay(Rule(r => { r.ForDuration = TimeSpan.FromMinutes(15); r.RecoveryDuration = TimeSpan.Zero; r.Cooldown = TimeSpan.Zero; }),
                Series(0.9, 0.9, 0.5, 0.9, 0.9, 0.9, 0.9, 0.9), 50, out _));
            Assert.Equal(2, free.WouldFire);
        }

        [Fact]
        public void Replay_below_comparison()
        {
            var rule = new RuleDefinition
            {
                Key = "r", Name = "r", Template = RuleTemplate.MetricThreshold, Threshold = 10, RecoveryThreshold = 20, Window = TimeSpan.FromMinutes(5),
                ForDuration = TimeSpan.Zero, Metric = new MetricSpec { Name = "m", Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Last, Comparison = Comparison.Below },
            };
            var s = Assert.Single(RulePreview.Replay(rule, Series(50, 5, 15, 25), 50, out _));
            Assert.Equal([Condition.Recovered, Condition.Breach, Condition.Band, Condition.Recovered], s.Points.Select(p => p.Condition));
            Assert.Equal(1, s.Breaches);
            Assert.Equal(1, s.WouldFire);
        }

        [Fact]
        public void Replay_subjects_capped_and_ordered()
        {
            var rule = Rule(r => { r.ForDuration = TimeSpan.Zero; r.RecoveryDuration = TimeSpan.Zero; });
            // Ordered by WouldFire, then Breaches, then key.
            var points = new List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)>();
            for (var i = 0; i < 4; i++)
            {
                var at = End.AddMinutes(15 * (i - 3));
                points.Add((at,
                [
                    Host("zeta", 0.9),                      // breaches every point, fires once
                    Host("alpha", i < 2 ? 0.9 : 0.5),       // breaches twice, fires once
                    Host("beta", i < 2 ? 0.9 : 0.5),        // the same: after alpha by key
                    Host("quiet", 0.1),                     // never
                    Host("flap", i % 2 == 0 ? 0.9 : 0.5),   // fires twice (opens and fires on each breach)
                ]));
            }
            var all = RulePreview.Replay(rule, points, 50, out var truncated);
            Assert.False(truncated);
            Assert.Equal(["host:flap", "host:zeta", "host:alpha", "host:beta", "host:quiet"], all.Select(s => s.SubjectKey));

            var capped = RulePreview.Replay(rule, points, 2, out truncated);
            Assert.True(truncated);
            Assert.Equal(["host:flap", "host:zeta"], capped.Select(s => s.SubjectKey));
        }

        [Fact]
        public void Replay_last_detail_is_the_last_observations()
        {
            var detail = new Dictionary<string, string> { ["bounds"] = "1,2" };
            var points = new List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)>
            {
                (End.AddMinutes(-15), [Host("a", 0.5) with { Detail = new Dictionary<string, string> { ["bounds"] = "old" } }]),
                (End, [Host("a", 0.5) with { Detail = detail }]),
            };
            Assert.Equal(detail, Assert.Single(RulePreview.Replay(Rule(), points, 50, out _)).LastDetail);
        }
    }
}
