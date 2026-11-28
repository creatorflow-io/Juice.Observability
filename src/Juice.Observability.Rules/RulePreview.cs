using Juice.Observability.Data;

namespace Juice.Observability.Rules
{
    /// <summary>One sampled instant of a previewed subject: the value (null: no data) and how the rule classed it.</summary>
    public sealed record PreviewPoint(DateTimeOffset At, double? Value, Condition Condition);

    /// <summary>
    /// What a draft rule would have done to one subject over the sampled instants. <see cref="WouldOpen"/> is the alerts it would have opened (Pending),
    /// <see cref="WouldFire"/> those that would have fired.
    /// </summary>
    public sealed record PreviewSubject(string SubjectKey, HealthSubject SubjectType, int Breaches, int NoData, int WouldOpen, int WouldFire,
        DateTimeOffset? FirstFiredAt, double? Last, double? Min, double? Max, IReadOnlyDictionary<string, string>? LastDetail, IReadOnlyList<PreviewPoint> Points);

    /// <summary>
    /// The pure part of the rule preview (Phase 12 M4): which instants of the last 24 hours a draft is evaluated at, and what its alert lifecycle would
    /// have done with what each instant observed. Evaluated at a step, not continuously, so the answer is approximate and says so (the step).
    /// </summary>
    public static class RulePreview
    {
        public static readonly TimeSpan Span = TimeSpan.FromHours(24);
        public static readonly TimeSpan MinimumStep = TimeSpan.FromMinutes(15);
        public const int MaxPoints = 96;

        /// <summary>
        /// The step, the larger of the rule's schedule, 15 minutes and half its window (rounded up to whole seconds), and the instants oldest first, the
        /// last at <paramref name="end"/>: at most <see cref="MaxPoints"/>, covering 24 hours at most.
        /// </summary>
        public static (TimeSpan Step, IReadOnlyList<DateTimeOffset> At) Plan(RuleDefinition rule, DateTimeOffset end)
        {
            var step = new[] { rule.Schedule, MinimumStep, rule.Window / 2 }.Max();
            step = TimeSpan.FromSeconds(Math.Ceiling(step.TotalSeconds));
            var points = (int)Math.Clamp(Span.Ticks / step.Ticks, 1, MaxPoints);
            var at = new List<DateTimeOffset>(points);
            for (var i = points - 1; i >= 0; i--)
            {
                at.Add(end - step * i);
            }
            return (step, at);
        }

        private sealed class Tracker(string key, HealthSubject type)
        {
            public readonly List<PreviewPoint> Points = [];
            public AlertState? State;
            public DateTimeOffset BreachStartedAt;
            public DateTimeOffset? RecoveringSince;
            public bool Fired;
            public DateTimeOffset? CooldownFrom;
            public int Breaches, NoData, WouldOpen, WouldFire;
            public DateTimeOffset? FirstFiredAt;
            public double? Last, Min, Max;
            public IReadOnlyDictionary<string, string>? LastDetail;
            public string Key => key;
            public HealthSubject Type => type;
        }

        /// <summary>
        /// Replays the alert lifecycle over the points (oldest first), per subject, as the evaluator applies it: <see cref="AlertLifecycle.Classify"/>
        /// then <see cref="AlertLifecycle.Next"/>, a subject missing from a point is no data, and a resolved alert starts a cooldown if it had fired.
        /// At most <paramref name="maxSubjects"/> are returned, those that would fire most first; <paramref name="truncated"/> says some were left out.
        /// </summary>
        public static IReadOnlyList<PreviewSubject> Replay(RuleDefinition rule,
            IReadOnlyList<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)> points, int maxSubjects, out bool truncated)
        {
            var trackers = new Dictionary<string, Tracker>(StringComparer.Ordinal);
            foreach (var (_, observed) in points)
            {
                foreach (var o in observed)
                {
                    trackers.TryAdd(o.SubjectKey, new Tracker(o.SubjectKey, o.SubjectType));
                }
            }
            foreach (var (at, observed) in points)
            {
                var byKey = new Dictionary<string, RuleObservation>(StringComparer.Ordinal);
                foreach (var o in observed) byKey.TryAdd(o.SubjectKey, o);
                foreach (var t in trackers.Values)
                {
                    byKey.TryGetValue(t.Key, out var observation);
                    Step(rule, t, at, observation);
                }
            }
            var ordered = trackers.Values
                .OrderByDescending(t => t.WouldFire).ThenByDescending(t => t.Breaches).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
            truncated = ordered.Count > maxSubjects;
            return [.. ordered.Take(maxSubjects).Select(t => new PreviewSubject(t.Key, t.Type, t.Breaches, t.NoData, t.WouldOpen, t.WouldFire,
                t.FirstFiredAt, t.Last, t.Min, t.Max, t.LastDetail, t.Points))];
        }

        private static void Step(RuleDefinition rule, Tracker t, DateTimeOffset at, RuleObservation? observation)
        {
            var condition = observation is null ? Condition.NoData : AlertLifecycle.Classify(rule, observation.Value, observation.Samples);
            var value = condition == Condition.NoData ? null : observation!.Value;
            t.Points.Add(new PreviewPoint(at, value, condition));
            if (condition == Condition.Breach) t.Breaches++;
            if (condition == Condition.NoData) t.NoData++;
            if (value is { } v)
            {
                t.Last = v;
                t.Min = t.Min is { } min ? Math.Min(min, v) : v;
                t.Max = t.Max is { } max ? Math.Max(max, v) : v;
            }
            if (observation is not null) t.LastDetail = observation.Detail;

            var cooldownUntil = t.CooldownFrom is { } from ? from + rule.Cooldown : (DateTimeOffset?)null;
            var decision = AlertLifecycle.Next(rule, t.State, t.State is null ? at : t.BreachStartedAt, t.RecoveringSince, condition, at, cooldownUntil);
            if (t.State is null)
            {
                if (decision.To != AlertState.Pending) return;
                t.State = AlertState.Pending;
                t.BreachStartedAt = at;
                t.Fired = false;
                t.WouldOpen++;
                // A rule with no ForDuration fires on the first breach.
                decision = AlertLifecycle.Next(rule, AlertState.Pending, at, null, condition, at, cooldownUntil);
            }
            if (decision.To is { } to && to != t.State)
            {
                if (to == AlertState.Firing)
                {
                    t.Fired = true;
                    t.WouldFire++;
                    t.FirstFiredAt ??= at;
                }
                if (to == AlertState.Resolved)
                {
                    if (t.Fired) t.CooldownFrom = at;
                    t.State = null;
                    t.RecoveringSince = null;
                    t.Fired = false;
                    return;
                }
                t.State = to;
            }
            t.RecoveringSince = decision.RecoveringSince;
        }
    }
}
