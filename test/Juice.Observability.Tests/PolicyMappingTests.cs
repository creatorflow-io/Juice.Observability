using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Tests
{
    /// <summary>A legacy escalation policy's steps as a notifications policy, and back (phase-5-plan I66; waves §5.3, Q1, Q3).</summary>
    public class PolicyMappingTests
    {
        private static readonly IReadOnlySet<string> Endpoints = new HashSet<string> { "ops", "sre", "lead" };

        private static LegacyPolicy Policy(bool stop = true, params (int After, string[] Keys)[] steps)
            => new("paging", stop, steps.Select(s => new LegacyStep(s.After, s.Keys)).ToList());

        [Theory]
        [InlineData(new[] { 5 }, new[] { 5 })]
        [InlineData(new[] { 5, 15, 45 }, new[] { 5, 10, 30 })]
        [InlineData(new[] { 0, 0, 10 }, new[] { 0, 0, 10 })]
        [InlineData(new[] { 10, 10 }, new[] { 10, 0 })]
        public void Times_after_firing_become_times_after_the_step_before(int[] absolute, int[] relative)
        {
            Assert.Equal(relative, PolicyMapping.ToRelative(absolute));
            Assert.Equal(absolute, PolicyMapping.ToAbsolute(relative));
        }

        [Fact]
        public void Steps_name_the_endpoints_routes_and_map_back()
        {
            var legacy = Policy(false, (5, ["ops"]), (15, ["sre", "lead"]), (15, ["lead"]));
            var mapped = PolicyMapping.Map(legacy, Endpoints);

            Assert.True(mapped.Succeeded, string.Join("; ", mapped.Problems.Select(p => p.Message)));
            Assert.Equal("""[{"afterMinutes":5,"recipients":[{"key":"ops","kind":"route"}]},{"afterMinutes":10,"recipients":[{"key":"sre","kind":"route"},{"key":"lead","kind":"route"}]},{"afterMinutes":0,"recipients":[{"key":"lead","kind":"route"}]}]""",
                mapped.Value!.Steps);
            Assert.False(mapped.Value.StopOnAcknowledge);
            Assert.Equal(legacy, PolicyMapping.Unmap(mapped.Value).Value);
        }

        [Fact]
        public void A_step_before_the_one_ahead_of_it_is_refused()
        {
            var mapped = PolicyMapping.Map(Policy(true, (30, ["ops"]), (10, ["sre"])), Endpoints);
            Assert.Contains(mapped.Problems, p => p.Code == ProblemCodes.NonIncreasingSteps);
        }

        [Fact]
        public void The_schedule_is_legacys_on_time_and_after_a_late_step()
        {
            // Legacy: EscalationDecision.NextAt. New: each step its relative wait after the step before went out.
            var steps = new List<EscalationStep> { new() { AfterMinutes = 5 }, new() { AfterMinutes = 20 }, new() { AfterMinutes = 20 }, new() { AfterMinutes = 60 } };
            var relative = PolicyMapping.ToRelative(steps.Select(s => s.AfterMinutes).ToList());
            var fired = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
            foreach (var lateBy in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(90) })
            {
                var sent = fired;
                for (var next = 0; next < steps.Count; next++)
                {
                    var legacyDue = EscalationDecision.NextAt(steps, next, fired, sent);
                    var newDue = sent + TimeSpan.FromMinutes(relative[next]);
                    // A step that went out late (held) moves the rest by as much on both sides.
                    Assert.Equal(legacyDue, next == 0 ? fired + TimeSpan.FromMinutes(relative[0]) : Max(newDue, fired + TimeSpan.FromMinutes(steps[next].AfterMinutes)));
                    Assert.Equal(legacyDue, newDue);
                    sent = legacyDue + (next == 1 ? lateBy : TimeSpan.Zero);
                }
            }
        }

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

        public static TheoryData<LegacyPolicy, string> Refused => new()
        {
            { Policy(true), ProblemCodes.Steps },
            { Policy(true, (5, [])), ProblemCodes.Steps },
            { Policy(true, (5, ["ops", "ops"])), ProblemCodes.Steps },
            { Policy(true, (-1, ["ops"])), ProblemCodes.Steps },
            { Policy(true, (5, ["gone"])), ProblemCodes.UnknownEndpoint },
            { Policy(true, (0, ["ops"]), (8 * 24 * 60, ["sre"])), ProblemCodes.Steps },
            { new LegacyPolicy("9-paging", true, [new LegacyStep(5, ["ops"])]), ProblemCodes.InvalidKey },
            { new LegacyPolicy("p", true, [.. Enumerable.Range(0, 11).Select(i => new LegacyStep(i, ["ops"]))]), ProblemCodes.Steps },
        };

        [Theory]
        [MemberData(nameof(Refused))]
        public void What_a_notifications_policy_cant_hold_is_refused(LegacyPolicy policy, string code)
            => Assert.Contains(PolicyMapping.Map(policy, Endpoints).Problems, p => p.Code == code);

        private static Data.EscalationPolicy Legacy(string key, string[]? rules = null, string[]? environments = null, bool enabled = true,
            AlertSeverity minimum = AlertSeverity.Info, bool steps = true)
            => new() { Key = key, Name = key, RuleKeys = rules ?? [], Environments = environments ?? [], Enabled = enabled, MinimumSeverity = minimum,
                Steps = steps ? [new() { AfterMinutes = 5, EndpointKeys = ["ops"] }] : [] };

        [Theory]
        [InlineData(new string[0], new string[0], new string[0], new string[0], true)]
        [InlineData(new[] { "a" }, new string[0], new[] { "b" }, new string[0], false)]
        [InlineData(new[] { "a", "b" }, new string[0], new[] { "b" }, new string[0], true)]
        [InlineData(new[] { "a" }, new string[0], new string[0], new string[0], true)]
        [InlineData(new string[0], new[] { "production" }, new string[0], new[] { "staging" }, false)]
        [InlineData(new string[0], new[] { "production" }, new string[0], new string[0], true)]
        public void Policies_overlap_when_one_alert_can_match_both(string[] rulesA, string[] envA, string[] rulesB, string[] envB, bool overlap)
        {
            Assert.Equal(overlap, PolicyMapping.Overlap(Legacy("a", rulesA, envA), Legacy("b", rulesB, envB)));
            Assert.Equal(overlap, PolicyMapping.Overlap(Legacy("b", rulesB, envB), Legacy("a", rulesA, envA)));
        }

        [Fact]
        public void Severities_always_overlap_and_policies_that_never_run_dont()
        {
            Assert.True(PolicyMapping.Overlap(Legacy("a", minimum: AlertSeverity.Critical), Legacy("b", minimum: AlertSeverity.Info)));
            Assert.False(PolicyMapping.Overlap(Legacy("a"), Legacy("b", enabled: false)));
            Assert.False(PolicyMapping.Overlap(Legacy("a"), Legacy("b", steps: false)));
            Assert.False(PolicyMapping.Overlap(Legacy("a"), Legacy("a")));
        }

        [Fact]
        public void Steps_not_in_the_alias_form_arent_read_back()
        {
            Assert.Contains(PolicyMapping.Unmap(new PolicyDefinition("p", """[{"afterMinutes":5,"recipients":[{"kind":"group","id":"sre","resolver":"staff"}]}]""", true)).Problems,
                p => p.Code == ProblemCodes.NotAliasShaped);
        }
    }
}
