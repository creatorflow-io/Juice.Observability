namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>What a finished run makes of an instance (decision 6).</summary>
    internal sealed record RunDecision(IntegrationState State, string? Reason, int Failures, CollectionResult Result, IntegrationFailure Failure, bool Success, TimeSpan? RetryAfter);

    internal static class IntegrationOutcomes
    {
        /// <summary>The longest wait a plugin can ask for with <c>RetryAfter</c>.</summary>
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

        /// <summary>
        /// Succeeded: Enabled. Partial: Degraded. Failed (timeout and throws too): one more failure in a row, Degraded, or Failed at <paramref name="failedAfter"/>.
        /// Authentication, permission and configuration failures are Failed at once: retrying with the same credentials or settings won't help.
        /// The reason is the core's own text, never the plugin's.
        /// </summary>
        public static RunDecision Decide(CollectionOutcome outcome, int failuresBefore, int failedAfter)
        {
            var retry = outcome.RetryAfter is { } r && r > TimeSpan.Zero ? (r > MaxRetryAfter ? MaxRetryAfter : r) : (TimeSpan?)null;
            switch (outcome.Result)
            {
                case CollectionResult.Succeeded:
                    return new(IntegrationState.Enabled, null, 0, CollectionResult.Succeeded, IntegrationFailure.None, true, retry);
                case CollectionResult.Partial:
                    return new(IntegrationState.Degraded, "Collection was partial", 0, CollectionResult.Partial, outcome.Failure, true, retry);
                default:
                    var kind = outcome.Failure == IntegrationFailure.None ? IntegrationFailure.Internal : outcome.Failure;
                    var failures = failuresBefore + 1;
                    var immediate = kind is IntegrationFailure.Authentication or IntegrationFailure.Permission or IntegrationFailure.Configuration;
                    var failed = immediate || failures >= Math.Max(1, failedAfter);
                    var reason = immediate ? $"{kind} failure" : $"{kind} failure, {failures} in a row";
                    return new(failed ? IntegrationState.Failed : IntegrationState.Degraded, reason, failures, CollectionResult.Failed, kind, false, retry);
            }
        }
    }

    internal static class IntegrationBackoff
    {
        public const double Jitter = 0.1;

        /// <summary>
        /// When the next run is due after the one that just ended. A success waits the interval. Failures wait min(interval·2^(n-1), <paramref name="max"/>)
        /// and a Failed instance waits <paramref name="max"/>; <paramref name="jitter"/> (−1 to 1) moves it by up to ±10 %. Never sooner than
        /// the decision's <c>RetryAfter</c>.
        /// </summary>
        public static TimeSpan Delay(RunDecision decision, TimeSpan interval, TimeSpan max, double jitter)
        {
            TimeSpan delay;
            if (decision.Success)
            {
                delay = interval;
            }
            else if (decision.State == IntegrationState.Failed)
            {
                delay = max;
            }
            else
            {
                var factor = Math.Pow(2, Math.Min(Math.Max(decision.Failures, 1) - 1, 30));
                delay = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, max.Ticks));
            }
            delay = TimeSpan.FromTicks((long)(delay.Ticks * (1 + Jitter * Math.Clamp(jitter, -1, 1))));
            return decision.RetryAfter is { } after && after > delay ? after : delay;
        }
    }
}
