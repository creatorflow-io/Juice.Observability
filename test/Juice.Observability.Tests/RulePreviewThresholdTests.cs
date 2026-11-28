using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Juice.Observability.Tests.Templates;

namespace Juice.Observability.Tests
{
    /// <summary>The preview judges each instant by the rule's threshold source, as the scheduler does (pure).</summary>
    [Trait("Milestone", "P12-M4")]
    public sealed class RulePreviewThresholdTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly DateTimeOffset End = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        private static RuleDefinition Draft(bool source = true) => new()
        {
            Key = "r-rule", Name = "r", Template = RuleTemplate.HostMemory, Threshold = 0.8, RecoveryThreshold = 0.7, ForDuration = TimeSpan.Zero,
            ThresholdSource = source ? new RuleThresholdSource { Provider = "demo.p95" } : null,
        };

        private static List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)> Points(double value)
            => [.. Enumerable.Range(0, 3).Select(i => (End.AddMinutes(15 * (i - 2)),
                (IReadOnlyList<RuleObservation>)[new RuleObservation("host:a", HealthSubject.Host, "", "", "", "a", value, 1)]))];

        [Fact]
        public async Task Preview_breaches_on_the_providers_numbers_not_the_drafts_Async()
        {
            // 0.5 is under the draft's 0.8, over the provider's 0.4.
            var provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            var rule = Draft();

            var points = await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.5), Ct, Ct);
            var subject = Assert.Single(RulePreview.Replay(rule, points, 50, out _));

            Assert.Equal(3, subject.Breaches);
            Assert.True(subject.WouldFire > 0);
            Assert.Equal(3, provider.Calls);
        }

        [Fact]
        public async Task A_subject_the_provider_leaves_out_is_no_data_Async()
        {
            var provider = new ThresholdSeamTests.Provider(_ => new Dictionary<string, RuleThreshold>());
            var rule = Draft();

            var subject = Assert.Single(RulePreview.Replay(rule,
                await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.99), Ct, Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(3, subject.NoData);
        }

        [Fact]
        public async Task A_provider_that_is_not_installed_is_no_data_Async()
        {
            var rule = Draft();

            var subject = Assert.Single(RulePreview.Replay(rule, await RulePreview.WithThresholdsAsync(RuleThresholds.Default, rule, Points(0.99), Ct, Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(3, subject.NoData);
        }

        /// <summary>Answers the first instant, then waits until it is cancelled (or throws, when told to).</summary>
        private sealed class Awkward(bool throws) : IRuleThresholdProvider
        {
            private int _calls;

            public string Key => "demo.p95";

            public async ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, System.Text.Json.JsonElement? settings,
                IReadOnlyCollection<string> subjectKeys, DateTimeOffset at, CancellationToken ct)
            {
                if (throws) throw new InvalidOperationException("the provider failed");
                if (Interlocked.Increment(ref _calls) > 1) await Task.Delay(Timeout.Infinite, ct);
                return subjectKeys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3));
            }
        }

        private sealed class FailsLate : IRuleThresholdProvider
        {
            public string Key => "demo.p95";

            public async ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, System.Text.Json.JsonElement? settings,
                IReadOnlyCollection<string> subjectKeys, DateTimeOffset at, CancellationToken ct)
            {
                // Runs the budget out, then fails with something that is not a cancellation.
                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
                throw new InvalidOperationException("failed after the budget");
            }
        }

        [Fact]
        public async Task A_provider_that_fails_after_the_budget_ran_out_ends_the_preview_as_partial_Async()
        {
            var rule = Draft();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            budget.CancelAfter(TimeSpan.FromMilliseconds(200));

            var points = await RulePreview.WithThresholdsAsync(ThresholdsOf(new FailsLate()), rule, Points(0.5), budget.Token, Ct);

            Assert.Equal(3, points.Count);
            Assert.Equal(3, Assert.Single(RulePreview.Replay(rule, points, 50, out _)).NoData);
        }

        private static RuleThresholds ThresholdsOf(IRuleThresholdProvider provider)
            => new(new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [provider], [new ModuleRuleContribution("demo", provider.GetType())]));

        [Fact]
        public async Task A_slow_provider_ends_the_preview_as_a_partial_result_within_the_budget_Async()
        {
            var rule = Draft();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            budget.CancelAfter(TimeSpan.FromMilliseconds(300));
            var started = System.Diagnostics.Stopwatch.StartNew();

            var points = await RulePreview.WithThresholdsAsync(ThresholdsOf(new Awkward(false)), rule, Points(0.5), budget.Token, Ct);

            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
            // The instant answered before the budget ran out is judged; the others are kept, with no threshold: no data.
            Assert.Equal(3, points.Count);
            var subject = Assert.Single(RulePreview.Replay(rule, points, 50, out _));
            Assert.Equal(1, subject.Breaches);
            Assert.Equal(2, subject.NoData);
        }

        [Fact]
        public async Task Loss_is_counted_only_for_calls_skipped_cut_off_or_failed_Async()
        {
            var rule = Draft();
            var answers = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));

            // The provider answers every instant, and the budget fires after: nothing was lost.
            using var late = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var answered = new RulePreview.ThresholdLoss();
            await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(answers), rule, Points(0.5), late.Token, Ct, answered);
            await late.CancelAsync();
            Assert.Equal((0, 0), (answered.BudgetLost, answered.Failed));

            // A spent budget: every call is skipped.
            using var spent = new CancellationTokenSource();
            await spent.CancelAsync();
            var skipped = new RulePreview.ThresholdLoss();
            await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(answers), rule, Points(0.5), spent.Token, Ct, skipped);
            Assert.Equal((3, 0), (skipped.BudgetLost, skipped.Failed));

            // A provider that throws within the budget: failed, not lost to the budget.
            var failed = new RulePreview.ThresholdLoss();
            await RulePreview.WithThresholdsAsync(ThresholdsOf(new Awkward(true)), rule, Points(0.5), Ct, Ct, failed);
            Assert.Equal((0, 3), (failed.BudgetLost, failed.Failed));

            // No subjects at any instant: nothing was asked, nothing lost, even on a spent budget.
            var quiet = new RulePreview.ThresholdLoss();
            await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(answers), rule, [(End, [])], spent.Token, Ct, quiet);
            Assert.Equal((0, 0), (quiet.BudgetLost, quiet.Failed));
        }

        [Fact]
        public async Task A_budget_already_spent_keeps_the_observed_points_with_no_threshold_Async()
        {
            var rule = Draft();
            using var budget = new CancellationTokenSource();
            await budget.CancelAsync();
            var provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));

            var points = await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.5), budget.Token, Ct);

            Assert.Equal(3, points.Count);
            Assert.Equal(0, provider.Calls);   // not asked on a spent budget
            var subject = Assert.Single(RulePreview.Replay(rule, points, 50, out _));
            Assert.Equal(3, subject.NoData);
        }

        [Fact]
        public async Task A_provider_that_throws_gives_no_data_for_the_instant_not_an_error_Async()
        {
            var rule = Draft();

            var points = await RulePreview.WithThresholdsAsync(ThresholdsOf(new Awkward(true)), rule, Points(0.99), Ct, Ct);
            var subject = Assert.Single(RulePreview.Replay(rule, points, 50, out _));

            Assert.Equal(3, points.Count);
            Assert.Equal(0, subject.Breaches);
            Assert.Equal(3, subject.NoData);
        }

        [Fact]
        public async Task A_rule_without_a_source_uses_its_own_numbers_and_asks_no_one_Async()
        {
            var provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            var rule = Draft(source: false);

            var subject = Assert.Single(RulePreview.Replay(rule,
                await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.5), Ct, Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(0, provider.Calls);
        }
    }
}
