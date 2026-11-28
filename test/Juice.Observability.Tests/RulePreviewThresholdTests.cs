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

            var points = await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.5), Ct);
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
                await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.99), Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(3, subject.NoData);
        }

        [Fact]
        public async Task A_provider_that_is_not_installed_is_no_data_Async()
        {
            var rule = Draft();

            var subject = Assert.Single(RulePreview.Replay(rule, await RulePreview.WithThresholdsAsync(RuleThresholds.Default, rule, Points(0.99), Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(3, subject.NoData);
        }

        [Fact]
        public async Task A_rule_without_a_source_uses_its_own_numbers_and_asks_no_one_Async()
        {
            var provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            var rule = Draft(source: false);

            var subject = Assert.Single(RulePreview.Replay(rule,
                await RulePreview.WithThresholdsAsync(ThresholdSeamTests.Thresholds(provider), rule, Points(0.5), Ct), 50, out _));

            Assert.Equal(0, subject.Breaches);
            Assert.Equal(0, provider.Calls);
        }
    }
}
