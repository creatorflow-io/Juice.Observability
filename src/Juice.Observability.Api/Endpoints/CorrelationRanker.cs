namespace Juice.Observability.Api.Endpoints
{
    /// <summary>How something relates to the subject of an alert.</summary>
    public enum CorrelationScope { Subject, Dependency, Rule }

    /// <summary>Something that happened near an alert, before ranking.</summary>
    public sealed record CorrelationCandidate(string Type, Guid Id, DateTimeOffset At, string Title, string? Detail, string? Kind,
        CorrelationScope Scope, bool? SameEnvironment);

    /// <summary>One reason an item ranks where it does, with what it added.</summary>
    public sealed record CorrelationReasonDto(string Code, string Text, int Points);

    public sealed record CorrelatedItemDto(string Type, Guid Id, DateTimeOffset At, string Title, string? Detail, string? Kind, string Scope,
        int Score, IReadOnlyList<CorrelationReasonDto> Reasons);

    /// <summary>
    /// Ranks what happened around an alert by points anyone can check, each with its reason. It is a reading order, not a diagnosis:
    /// happening close in time says nothing about cause (Phase 5), and <see cref="Note"/> says so wherever these are shown.
    /// </summary>
    public static class CorrelationRanker
    {
        public const string Relation = "correlation";
        public const string Note = "These happened close in time to this alert or on what it depends on. That is correlation, not a proven cause.";
        public static readonly TimeSpan Lookback = TimeSpan.FromHours(2);
        /// <summary>A change just after the breach began can't have started it, but a clock can be off, and it may have made it worse.</summary>
        public static readonly TimeSpan LookAhead = TimeSpan.FromMinutes(5);
        public const int MaxProximityPoints = 40;
        public const int AfterBreachPoints = 5;
        public const int MaxItems = 20;

        public static IReadOnlyList<CorrelatedItemDto> Rank(DateTimeOffset breachStarted, IEnumerable<CorrelationCandidate> candidates)
            => candidates.Select(c => Score(breachStarted, c))
                .OrderByDescending(i => i.Score).ThenByDescending(i => i.At).ThenBy(i => i.Id).Take(MaxItems).ToList();

        private static CorrelatedItemDto Score(DateTimeOffset breachStarted, CorrelationCandidate c)
        {
            var reasons = new List<CorrelationReasonDto> { Proximity(breachStarted, c.At) };
            reasons.Add(c.Scope switch
            {
                CorrelationScope.Subject => new("same-subject", "On the alert's own subject", 30),
                CorrelationScope.Rule => new("same-rule", "A change to the rule that raised this alert", 20),
                _ => new("direct-dependency", "On something the subject directly depends on", 15),
            });
            reasons.Add(c.Kind switch
            {
                "Deployment" => new("deployment", "A deployment", 15),
                "Version" => new("version", "A new version", 15),
                "Configuration" => new("configuration", "A configuration change", 10),
                "Other" => new("other-change", "A reported change", 5),
                _ => new("health-change", "A health change", 10),
            });
            if (c.SameEnvironment == true) reasons.Add(new("same-environment", "In the same environment", 10));
            return new CorrelatedItemDto(c.Type, c.Id, c.At, c.Title, c.Detail, c.Kind, c.Scope.ToString().ToLowerInvariant(), reasons.Sum(r => r.Points), reasons);
        }

        private static CorrelationReasonDto Proximity(DateTimeOffset breachStarted, DateTimeOffset at)
        {
            var before = breachStarted - at;
            if (before < TimeSpan.Zero)
            {
                return new("after-breach", $"{Describe(-before)} after the breach began, so it didn't start it", AfterBreachPoints);
            }
            var share = 1 - Math.Min(1, before / Lookback);
            return new("before-breach", $"{Describe(before)} before the breach began", (int)Math.Round(MaxProximityPoints * share));
        }

        private static string Describe(TimeSpan span)
            => span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";
    }
}
