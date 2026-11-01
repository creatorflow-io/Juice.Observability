using Juice.Observability.Data;
using Juice.Observability.Rules;

namespace Juice.Observability.Tests
{
    /// <summary>The alert state machine (requirements §8) and rule validation, without telemetry.</summary>
    public class AlertLifecycleTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        private static RuleDefinition Rule(Action<RuleDefinition>? configure = null)
        {
            var rule = new RuleDefinition
            {
                Key = "orders-errors", Name = "Orders error rate", Template = RuleTemplate.ErrorRate,
                Threshold = 0.05, RecoveryThreshold = 0.02, MinimumSamples = 10,
                ForDuration = TimeSpan.FromMinutes(2), RecoveryDuration = TimeSpan.FromMinutes(2), Cooldown = TimeSpan.FromMinutes(10),
            };
            configure?.Invoke(rule);
            return rule;
        }

        [Theory]
        [InlineData(0.10, 100, Condition.Breach)]
        [InlineData(0.03, 100, Condition.Band)]
        [InlineData(0.02, 100, Condition.Recovered)]
        [InlineData(0.50, 5, Condition.NoData)]   // too few samples: no value, not zero
        public void Classify_with_hysteresis_and_minimum_samples(double value, long samples, Condition expected)
            => Assert.Equal(expected, AlertLifecycle.Classify(Rule(), value, samples));

        [Fact]
        public void Missing_value_is_no_data_never_zero()
        {
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(Rule(), null, 1000));
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(Rule(), double.NaN, 1000));
            // Even for a "below" rule, where zero would breach.
            var absent = Rule(r => { r.Template = RuleTemplate.ThroughputAbsent; r.Threshold = 0; r.RecoveryThreshold = 0; });
            Assert.Equal(Condition.NoData, AlertLifecycle.Classify(absent, null, 0));
            Assert.Equal(Condition.Breach, AlertLifecycle.Classify(absent, 0, 50));
        }

        /// <summary>Replays a series of per-minute values and returns the state after each.</summary>
        private static List<AlertState?> Simulate(RuleDefinition rule, params double?[] values)
        {
            AlertState? state = null;
            DateTimeOffset breachStarted = default;
            DateTimeOffset? recovering = null;
            var states = new List<AlertState?>();
            for (var i = 0; i < values.Length; i++)
            {
                var at = T0.AddMinutes(i);
                var condition = AlertLifecycle.Classify(rule, values[i], 100);
                var decision = AlertLifecycle.Next(rule, state, breachStarted, recovering, condition, at, null);
                if (state is null && decision.To == AlertState.Pending)
                {
                    breachStarted = at;
                    state = AlertState.Pending;
                    decision = AlertLifecycle.Next(rule, state, breachStarted, null, condition, at, null);
                }
                if (decision.To is { } to) state = to == AlertState.Resolved ? null : to;
                recovering = decision.RecoveringSince;
                states.Add(state);
            }
            return states;
        }

        [Fact]
        public void Sustained_breach_fires_once_after_for_duration()
        {
            var states = Simulate(Rule(), 0.1, 0.1, 0.1, 0.1, 0.1);
            Assert.Equal([AlertState.Pending, AlertState.Pending, AlertState.Firing, AlertState.Firing, AlertState.Firing], states);
        }

        [Fact]
        public void Transient_breach_never_fires()
        {
            var states = Simulate(Rule(), 0.1, 0.0, 0.1, 0.0, 0.1, 0.0);
            Assert.DoesNotContain(AlertState.Firing, states);
        }

        [Fact]
        public void Values_oscillating_in_the_hysteresis_band_do_not_flap()
        {
            // Fires, then hovers between recovery (2 %) and threshold (5 %): stays firing, never resolves and re-fires.
            var states = Simulate(Rule(), 0.1, 0.1, 0.1, 0.04, 0.06, 0.04, 0.03, 0.04);
            Assert.Equal(AlertState.Firing, states[^1]);
            Assert.Equal(1, states.Zip(states.Skip(1)).Count(p => p.First != AlertState.Firing && p.Second == AlertState.Firing));
        }

        [Fact]
        public void Recovery_needs_the_recovery_duration_without_interruption()
        {
            var states = Simulate(Rule(), 0.1, 0.1, 0.1, 0.01, 0.01, 0.04, 0.01, 0.01, 0.01);
            // Recovery at minute 3, interrupted at 5 by the band, restarted at 6: resolved at 8 (6 + 2 minutes).
            Assert.Equal(AlertState.Firing, states[7]);
            Assert.Null(states[8]);
        }

        [Fact]
        public void No_data_holds_pending_and_firing_alerts()
        {
            var rule = Rule();
            Assert.Null(AlertLifecycle.Next(rule, AlertState.Pending, T0, null, Condition.NoData, T0.AddMinutes(5), null).To);
            var firing = AlertLifecycle.Next(rule, AlertState.Firing, T0, T0.AddMinutes(1), Condition.NoData, T0.AddMinutes(10), null);
            Assert.Null(firing.To);
            Assert.Equal(T0.AddMinutes(1), firing.RecoveringSince);   // recovery neither progresses to resolution nor resets
            Assert.Equal("no_data", firing.Reason);
        }

        [Fact]
        public void Cooldown_holds_a_new_alert_in_pending()
        {
            var rule = Rule();
            var cooldownUntil = T0.AddMinutes(10);
            Assert.Equal("cooldown", AlertLifecycle.Next(rule, AlertState.Pending, T0, null, Condition.Breach, T0.AddMinutes(5), cooldownUntil).Reason);
            Assert.Equal(AlertState.Firing, AlertLifecycle.Next(rule, AlertState.Pending, T0, null, Condition.Breach, T0.AddMinutes(10), cooldownUntil).To);
        }

        [Fact]
        public void Valid_rule_has_no_errors()
            => Assert.Empty(Rule().Validate());

        [Theory]
        [InlineData("Bad Key")]
        [InlineData("threshold-out-of-range")]
        [InlineData("recovery-above-threshold")]
        [InlineData("host-on-service-rule")]
        [InlineData("window-shorter-than-schedule")]
        public void Invalid_rules_are_rejected(string problem)
        {
            var rule = Rule(r =>
            {
                switch (problem)
                {
                    case "Bad Key": r.Key = "Bad Key"; break;
                    case "threshold-out-of-range": r.Threshold = 5; break;
                    case "recovery-above-threshold": r.RecoveryThreshold = 0.5; break;
                    case "host-on-service-rule": r.Host = "web-1"; break;
                    case "window-shorter-than-schedule": r.Window = TimeSpan.FromSeconds(30); break;
                }
            });
            Assert.NotEmpty(rule.Validate());
        }

        [Fact]
        public void Definition_round_trips_and_hash_tracks_changes()
        {
            var rule = Rule();
            var copy = RuleDefinition.FromJson(rule.ToJson());
            Assert.Equal(rule.Hash(), copy.Hash());
            Assert.Equal(RuleTemplate.ErrorRate, copy.Template);
            copy.Threshold = 0.06;
            Assert.NotEqual(rule.Hash(), copy.Hash());
        }
    }
}
