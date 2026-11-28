using Juice.Observability.Api.Integrations;
using Juice.Observability.Api.Integrations.Health;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>What a finished run makes of an instance, and when it runs next (decisions 6 and 7). No database.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class IntegrationOutcomeTests
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5), Max = TimeSpan.FromHours(1);

        private static RunDecision Fail(int before, IntegrationFailure kind = IntegrationFailure.Remote, TimeSpan? retry = null, int failedAfter = 3)
            => IntegrationOutcomes.Decide(new CollectionOutcome(CollectionResult.Failed, kind, "plugin text", RetryAfter: retry), before, failedAfter);

        [Fact]
        public void Backoff_doubles_caps_jitter_and_honours_retry_after()
        {
            // The interval doubles with each failure in a row, up to the cap.
            Assert.Equal(Interval, IntegrationBackoff.Delay(Fail(0), Interval, Max, 0));
            Assert.Equal(Interval * 2, IntegrationBackoff.Delay(Fail(1), Interval, Max, 0));
            Assert.Equal(Interval * 4, IntegrationBackoff.Delay(Fail(2, failedAfter: 9), Interval, Max, 0));
            Assert.Equal(Max, IntegrationBackoff.Delay(Fail(20, failedAfter: 99), Interval, Max, 0));
            Assert.Equal(Max, IntegrationBackoff.Delay(Fail(1, IntegrationFailure.Authentication), Interval, Max, 0));
            // A success waits the interval.
            Assert.Equal(Interval, IntegrationBackoff.Delay(IntegrationOutcomes.Decide(new(CollectionResult.Succeeded), 4, 3), Interval, Max, 0));
            // Jitter moves it by at most 10 %, either way.
            Assert.Equal(Interval * 1.1, IntegrationBackoff.Delay(Fail(0), Interval, Max, 1));
            Assert.Equal(Interval * 0.9, IntegrationBackoff.Delay(Fail(0), Interval, Max, -1));
            Assert.Equal(Interval * 1.1, IntegrationBackoff.Delay(Fail(0), Interval, Max, 50));
            // Never sooner than the plugin asked, and a plugin's ask is capped at 24 h.
            Assert.Equal(TimeSpan.FromMinutes(20), IntegrationBackoff.Delay(Fail(0, retry: TimeSpan.FromMinutes(20)), Interval, Max, 0));
            Assert.Equal(Interval, IntegrationBackoff.Delay(Fail(0, retry: TimeSpan.FromSeconds(10)), Interval, Max, 0));
            Assert.Equal(TimeSpan.FromHours(24), IntegrationBackoff.Delay(Fail(0, retry: TimeSpan.FromDays(30)), Interval, Max, 0));
        }

        [Fact]
        public void Outcome_to_state_table()
        {
            var ok = IntegrationOutcomes.Decide(new(CollectionResult.Succeeded), 2, 3);
            Assert.Equal((IntegrationState.Enabled, 0, true), (ok.State, ok.Failures, ok.Success));

            var partial = IntegrationOutcomes.Decide(new(CollectionResult.Partial, IntegrationFailure.RateLimited), 2, 3);
            Assert.Equal((IntegrationState.Degraded, 0, true), (partial.State, partial.Failures, partial.Success));

            // Other failures: Degraded until FailedAfter in a row.
            Assert.Equal(IntegrationState.Degraded, Fail(0).State);
            Assert.Equal(IntegrationState.Degraded, Fail(1).State);
            Assert.Equal((IntegrationState.Failed, 3), (Fail(2).State, Fail(2).Failures));
            Assert.Equal(IntegrationState.Failed, Fail(0, failedAfter: 1).State);
            foreach (var kind in new[] { IntegrationFailure.Connectivity, IntegrationFailure.RateLimited, IntegrationFailure.Remote, IntegrationFailure.Timeout, IntegrationFailure.Internal })
            {
                Assert.Equal(IntegrationState.Degraded, Fail(0, kind).State);
            }
            // Authentication, permission and configuration: Failed at once.
            foreach (var kind in new[] { IntegrationFailure.Authentication, IntegrationFailure.Permission, IntegrationFailure.Configuration })
            {
                Assert.Equal(IntegrationState.Failed, Fail(0, kind).State);
            }
            // No kind given: internal.
            Assert.Equal(IntegrationFailure.Internal, Fail(0, IntegrationFailure.None).Failure);
            // The reason is the core's own text, never the plugin's.
            Assert.DoesNotContain("plugin text", Fail(0).Reason);
        }

        [Fact]
        public void Reconcile_fires_for_failed_observes_degraded_and_resolves_the_rest()
        {
            var now = DateTimeOffset.UtcNow;
            IntegrationAlerts.Row Make(IntegrationState state) => new(Guid.NewGuid(), "n", "sample.jobs", state, "Remote", 3);
            var failed = Make(IntegrationState.Failed);
            var degraded = Make(IntegrationState.Degraded);
            var recovered = Make(IntegrationState.Enabled);
            var disabled = Make(IntegrationState.Disabled);
            var gone = Guid.NewGuid();
            SourceAlert Open(Guid id) => new(Guid.NewGuid(), IntegrationHealthRule.Key, IntegrationHealthRule.DedupKey(id), IntegrationHealthRule.SubjectKey(id), "Firing", now, 3);

            var signals = IntegrationAlerts.Reconcile([failed, degraded, recovered, disabled], [Open(degraded.Id), Open(recovered.Id), Open(disabled.Id), Open(gone)], now, canFire: true);

            Assert.Equal(AlertSignalKind.Fire, signals.Single(s => s.DedupKey == IntegrationHealthRule.DedupKey(failed.Id)).Kind);
            Assert.Equal(AlertSignalKind.Observe, signals.Single(s => s.DedupKey == IntegrationHealthRule.DedupKey(degraded.Id)).Kind);
            Assert.Equal("recovered", signals.Single(s => s.DedupKey == IntegrationHealthRule.DedupKey(recovered.Id)).Reason);
            Assert.Equal("disabled", signals.Single(s => s.DedupKey == IntegrationHealthRule.DedupKey(disabled.Id)).Reason);
            Assert.Equal("deleted", signals.Single(s => s.DedupKey == IntegrationHealthRule.DedupKey(gone)).Reason);
            var fire = signals.Single(s => s.Kind == AlertSignalKind.Fire);
            Assert.Equal(new AlertSubject(AlertSubjectKind.Integration, $"integration:{failed.Id:N}"), fire.Subject);
            Assert.Equal(["consecutiveFailures", "failure", "instance", "plugin"], fire.Evidence!.Keys.Order());
            // Idempotent: a failed instance with its alert open is observed, never fired again; and without our rule nothing fires.
            Assert.DoesNotContain(IntegrationAlerts.Reconcile([failed], [Open(failed.Id)], now, true), s => s.Kind == AlertSignalKind.Fire);
            Assert.Empty(IntegrationAlerts.Reconcile([failed], [], now, canFire: false));
        }
    }
}
