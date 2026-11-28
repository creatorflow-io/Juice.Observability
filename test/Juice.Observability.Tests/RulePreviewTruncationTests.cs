using Juice.Observability.Data;
using Juice.Observability.Rules;

namespace Juice.Observability.Tests
{
    /// <summary>A preview's cut subjects are not <c>rules.truncated</c>: only the scheduler's evaluations count (pure).</summary>
    [Trait("Milestone", "P12-M4")]
    public sealed class RulePreviewTruncationTests
    {
        private static IReadOnlyList<RuleObservation> Rows(int count)
            => [.. Enumerable.Range(0, count).Select(i => new RuleObservation($"host:{i}", HealthSubject.Host, "", "", "", $"{i}", 1, 1))];

        [Fact]
        public async Task A_cut_is_counted_unless_the_evaluation_is_a_preview_Async()
        {
            var template = "probe-" + Guid.NewGuid().ToString("N");
            using var probe = new TruncationProbe();

            Assert.Equal(2, RuleTemplates.CutLast(Rows(3), 2, template).Count);
            Assert.Equal(1, probe.Count(template));

            await RuleTemplates.AsPreviewAsync(() =>
            {
                Assert.Equal(2, RuleTemplates.CutLast(Rows(3), 2, template).Count);
                return Task.CompletedTask;
            });
            Assert.Equal(1, probe.Count(template));

            // The scope ended with the preview.
            RuleTemplates.CutLast(Rows(3), 2, template);
            Assert.Equal(2, probe.Count(template));
        }
    }
}
