using Juice.Observability.Api.Endpoints;

namespace Juice.Observability.Tests
{
    /// <summary>A reading order anyone can check: every point has a reason, and it never claims a cause.</summary>
    public class CorrelationRankerTests
    {
        private static readonly DateTimeOffset Breach = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        private static CorrelationCandidate Change(TimeSpan before, CorrelationScope scope = CorrelationScope.Subject, string kind = "Deployment", bool? sameEnvironment = true, string title = "change")
            => new("change", Guid.NewGuid(), Breach - before, title, null, kind, scope, sameEnvironment);

        private static CorrelatedItemDto One(CorrelationCandidate candidate) => Assert.Single(CorrelationRanker.Rank(Breach, [candidate]));

        [Fact]
        public void Every_point_has_a_reason()
        {
            var item = One(Change(TimeSpan.FromMinutes(10)));

            Assert.Equal(item.Score, item.Reasons.Sum(r => r.Points));
            Assert.Equal(["before-breach", "same-subject", "deployment", "same-environment"], item.Reasons.Select(r => r.Code));
            Assert.All(item.Reasons, r => Assert.False(string.IsNullOrEmpty(r.Text)));
        }

        [Fact]
        public void Nearer_the_breach_ranks_higher()
        {
            var ranked = CorrelationRanker.Rank(Breach, [Change(TimeSpan.FromMinutes(90), title: "far"), Change(TimeSpan.FromMinutes(5), title: "near")]);

            Assert.Equal(["near", "far"], ranked.Select(i => i.Title));
        }

        [Fact]
        public void The_alerts_own_subject_ranks_above_a_dependency()
        {
            var ranked = CorrelationRanker.Rank(Breach, [Change(TimeSpan.FromMinutes(5), CorrelationScope.Dependency, title: "dependency"), Change(TimeSpan.FromMinutes(5), title: "subject")]);

            Assert.Equal(["subject", "dependency"], ranked.Select(i => i.Title));
        }

        [Fact]
        public void A_change_after_the_breach_says_it_did_not_start_it()
        {
            var item = One(Change(TimeSpan.FromMinutes(-3)));
            var after = Assert.Single(item.Reasons, r => r.Code == "after-breach");

            Assert.Equal(CorrelationRanker.AfterBreachPoints, after.Points);
            Assert.Contains("didn't start it", after.Text);
            Assert.True(item.Score < One(Change(TimeSpan.FromMinutes(5))).Score);
        }

        [Fact]
        public void The_edge_of_the_lookback_adds_nothing_for_time()
            => Assert.Equal(0, One(Change(CorrelationRanker.Lookback)).Reasons.Single(r => r.Code == "before-breach").Points);

        [Fact]
        public void An_unknown_environment_adds_nothing()
            => Assert.DoesNotContain(One(Change(TimeSpan.FromMinutes(5), sameEnvironment: null)).Reasons, r => r.Code == "same-environment");

        [Fact]
        public void The_list_is_bounded()
        {
            var many = Enumerable.Range(1, CorrelationRanker.MaxItems + 5).Select(i => Change(TimeSpan.FromMinutes(i))).ToList();

            Assert.Equal(CorrelationRanker.MaxItems, CorrelationRanker.Rank(Breach, many).Count);
        }

        [Fact]
        public void The_label_says_correlation_and_never_claims_a_cause()
        {
            Assert.Equal("correlation", CorrelationRanker.Relation);
            Assert.Contains("not a proven cause", CorrelationRanker.Note);
            var words = One(Change(TimeSpan.FromMinutes(5))).Reasons.Select(r => r.Text.ToLowerInvariant());
            Assert.DoesNotContain(words, w => w.Contains("caused") || w.Contains("root cause"));
        }
    }
}
