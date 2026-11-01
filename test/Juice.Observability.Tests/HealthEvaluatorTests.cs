using Juice.Observability.Data;
using Juice.Observability.Health;

namespace Juice.Observability.Tests
{
    /// <summary>The §7 rules with the D4 defaults (grace 5 min, 3 samples, error rate 1 % / 5 %, quorum 50 %).</summary>
    public class HealthEvaluatorTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly HealthEvaluator _evaluator = new(new HealthPolicy());

        private static Assessment Healthy => Assessment.Of(HealthState.Healthy, Now.AddSeconds(-20), "error_rate_ok");
        private static Assessment Unhealthy => Assessment.Of(HealthState.Unhealthy, Now.AddSeconds(-20), "error_rate_high");
        private static Assessment Offline => Assessment.Of(HealthState.Offline, Now.AddMinutes(-10), "no_telemetry_within_grace");
        private static Assessment Unknown => Assessment.Of(HealthState.Unknown, null, "never_seen");
        private static readonly Evidence NoTraffic = new(null, 0, 0);

        [Fact]
        public void Never_seen_instance_is_unknown_not_healthy_or_offline()
            => Expect(_evaluator.Instance(Now, null, null), HealthState.Unknown, "never_seen");

        [Fact]
        public void Previously_seen_instance_past_the_grace_is_offline()
            => Expect(_evaluator.Instance(Now, Now.AddMinutes(-6), null), HealthState.Offline, "no_telemetry_within_grace");

        [Fact]
        public void Fresh_instance_without_enough_requests_is_healthy_on_heartbeat()
            => Expect(_evaluator.Instance(Now, null, new Evidence(Now.AddSeconds(-30), 2, 2)), HealthState.Healthy, "heartbeat_only");

        [Theory]
        [InlineData(100, 0, HealthState.Healthy, "error_rate_ok")]
        [InlineData(100, 2, HealthState.Degraded, "error_rate_elevated")]
        [InlineData(100, 5, HealthState.Unhealthy, "error_rate_high")]
        public void Instance_error_rate_thresholds(long requests, long errors, HealthState state, string reason)
            => Expect(_evaluator.Instance(Now, null, new Evidence(Now.AddSeconds(-30), requests, errors)), state, reason);

        [Fact]
        public void Service_never_seen_is_unknown()
            => Expect(_evaluator.Service(Now, null, [], NoTraffic, []), HealthState.Unknown, "never_seen");

        [Fact]
        public void Service_whose_instances_all_went_silent_is_offline_after_the_grace()
            => Expect(_evaluator.Service(Now, null, [Offline, Offline], NoTraffic, []), HealthState.Offline, "no_active_instances");

        [Fact]
        public void Recently_seen_service_with_nothing_in_the_window_is_unknown()
            => Expect(_evaluator.Service(Now, Now.AddMinutes(-1), [Unknown], NoTraffic, []), HealthState.Unknown, "insufficient_evidence");

        [Fact]
        public void Service_without_instance_identity_is_judged_on_its_own_telemetry()
        {
            Expect(_evaluator.Service(Now, null, [], new Evidence(Now.AddSeconds(-20), 0, 0), []), HealthState.Healthy, "heartbeat_only");
            Expect(_evaluator.Service(Now, null, [], new Evidence(Now.AddSeconds(-20), 100, 10), []), HealthState.Unhealthy, "error_rate_high");
        }

        [Fact]
        public void Service_without_instances_silent_past_the_grace_is_offline()
            => Expect(_evaluator.Service(Now, Now.AddMinutes(-10), [], NoTraffic, []), HealthState.Offline, "no_telemetry_within_grace");

        [Fact]
        public void One_failing_instance_of_three_degrades_the_service()
            => Expect(_evaluator.Service(Now, null, [Healthy, Healthy, Unhealthy], NoTraffic, []), HealthState.Degraded, "partial_instance_failure");

        [Fact]
        public void Unhealthy_quorum_makes_the_service_unhealthy()
            => Expect(_evaluator.Service(Now, null, [Healthy, Unhealthy, Unhealthy], NoTraffic, []), HealthState.Unhealthy, "instance_quorum_unhealthy");

        [Fact]
        public void Silent_old_instances_do_not_degrade_a_service_after_a_restart()
            => Expect(_evaluator.Service(Now, null, [Healthy, Offline, Offline], NoTraffic, []), HealthState.Healthy, "low_traffic");

        [Fact]
        public void Failed_critical_dependency_makes_the_service_critical()
        {
            var result = _evaluator.Service(Now, null, [Healthy], NoTraffic, [("shop/billing", HealthState.Unhealthy)]);
            Expect(result, HealthState.Critical, "critical_dependency_failed:shop/billing");
        }

        [Fact]
        public void Unknown_critical_dependency_is_reported_but_does_not_fail_the_service()
        {
            var result = _evaluator.Service(Now, null, [Healthy], NoTraffic, [("shop/billing", HealthState.Unknown)]);
            Assert.Equal(HealthState.Healthy, result.State);
            Assert.Contains("critical_dependency_unknown:shop/billing", result.Reasons);
        }

        [Fact]
        public void Failed_called_service_is_named_without_changing_the_state()
        {
            var healthy = _evaluator.Service(Now, null, [Healthy], NoTraffic, [],
                [("shop/billing", HealthState.Unhealthy), ("shop/catalog", HealthState.Healthy), ("shop/stock", HealthState.Unknown)]);
            Assert.Equal(HealthState.Healthy, healthy.State);
            Assert.Equal(["dependency_failed:shop/billing"], healthy.Reasons.Where(r => r.StartsWith("dependency_", StringComparison.Ordinal)));

            var unhealthy = _evaluator.Service(Now, null, [Unhealthy], NoTraffic, [], [("shop/billing", HealthState.Offline)]);
            Assert.Equal(HealthState.Unhealthy, unhealthy.State);
            Assert.Equal("instance_quorum_unhealthy", unhealthy.Reasons[0]);
            Assert.Contains("dependency_failed:shop/billing", unhealthy.Reasons);
        }

        [Fact]
        public void Critical_dependency_is_not_named_twice()
        {
            var result = _evaluator.Service(Now, null, [Healthy], NoTraffic, [("shop/billing", HealthState.Unhealthy)],
                [("shop/billing", HealthState.Unhealthy)]);
            Assert.Equal(["critical_dependency_failed:shop/billing"], result.Reasons.Where(r => r.Contains("dependency", StringComparison.Ordinal)));
        }

        [Fact]
        public void Service_error_rate_counts_across_instances()
            => Expect(_evaluator.Service(Now, null, [Healthy, Healthy], new Evidence(Now, 200, 4), []), HealthState.Degraded, "error_rate_elevated");

        [Fact]
        public void Missing_optional_service_does_not_make_the_application_unhealthy()
        {
            var result = _evaluator.Application([Child("orders", Healthy), Child("billing", Healthy), Child("reports", Unknown)]);
            Assert.Equal(HealthState.Healthy, result.State);
        }

        [Fact]
        public void Application_below_minimum_coverage_is_unknown()
            => Expect(_evaluator.Application([Child("orders", Healthy), Child("billing", Unknown), Child("reports", Unknown)]),
                HealthState.Unknown, "insufficient_coverage");

        [Fact]
        public void Application_with_every_service_offline_is_offline()
            => Expect(_evaluator.Application([Child("orders", Offline), Child("billing", Offline)]), HealthState.Offline, "all_services_offline");

        [Fact]
        public void Critical_service_failure_decides_the_application()
            => Expect(_evaluator.Application([Child("orders", Unhealthy, critical: true), Child("a", Healthy), Child("b", Healthy), Child("c", Healthy)]),
                HealthState.Unhealthy, "critical_service_failed:orders");

        [Fact]
        public void Critical_service_without_evidence_makes_the_application_unknown()
            => Expect(_evaluator.Application([Child("orders", Unknown, critical: true), Child("a", Healthy)]),
                HealthState.Unknown, "critical_service_unknown:orders");

        [Fact]
        public void Child_weights_decide_between_degraded_and_unhealthy()
        {
            Expect(_evaluator.Application([Child("heavy", Unhealthy, weight: 3), Child("light", Healthy)]), HealthState.Unhealthy, "services_unhealthy");
            Expect(_evaluator.Application([Child("heavy", Healthy, weight: 3), Child("light", Unhealthy)]), HealthState.Degraded, "partial_service_failure");
        }

        [Fact]
        public void Host_rules()
        {
            Expect(_evaluator.Host(Now, null, null, null), HealthState.Unknown, "never_seen");
            Expect(_evaluator.Host(Now, Now.AddMinutes(-20), null, null), HealthState.Offline, "no_telemetry_within_grace");
            Expect(_evaluator.Host(Now, null, Now.AddSeconds(-30), 0.97), HealthState.Degraded, "memory_high");
            Expect(_evaluator.Host(Now, null, Now.AddSeconds(-30), 0.40), HealthState.Healthy, "resources_ok");
        }

        [Fact]
        public void Policy_version_follows_the_rules_not_the_schedule()
        {
            var policy = new HealthPolicy();
            var version = policy.Version;
            policy.Interval = TimeSpan.FromSeconds(10);
            Assert.Equal(version, policy.Version);
            policy.ErrorRateUnhealthy = 0.1;
            Assert.NotEqual(version, policy.Version);
            Assert.Equal(12, policy.Version.Length);
        }

        private static ChildAssessment Child(string name, Assessment assessment, double weight = 1, bool critical = false)
            => new(name, assessment, weight, critical);

        private static void Expect(Assessment assessment, HealthState state, string reason)
        {
            Assert.Equal(state, assessment.State);
            Assert.Equal(reason, assessment.Reasons[0]);
        }
    }
}
