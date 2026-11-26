namespace Juice.Observability.Data
{
    // The shadow run's comparison (phase 5, M47; phase-5-plan I67, p5-d7): what legacy sent against what the embedded pipeline would have sent.

    /// <summary>
    /// One compared unit: a transition's own notifications (<see cref="EscalationStep"/> 0), or one escalation step of its alert. Written once,
    /// when both pipelines have settled on it (or the wait for them ran out), with the differences it found.
    /// </summary>
    public sealed class ShadowComparison
    {
        public Guid TenantId { get; init; }
        public Guid TransitionId { get; init; }
        public int EscalationStep { get; init; }
        public Guid AlertId { get; init; }
        /// <summary><c>alert.firing</c>, <c>alert.resolved</c> or <c>alert.escalated</c>.</summary>
        public required string Event { get; init; }
        /// <summary>Endpoints legacy sent to, and that the new pipeline would have sent to.</summary>
        public int Legacy { get; init; }
        public int New { get; init; }
        public int Differences { get; init; }
        /// <summary>Differences no rule explains: what the soak must show none of.</summary>
        public int Unexplained { get; init; }
        public DateTimeOffset ComparedAt { get; init; }
    }

    /// <summary>One way the two pipelines differed on a compared unit, for one endpoint (an integration of the same key).</summary>
    public sealed class ShadowDifference
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid TransitionId { get; init; }
        public int EscalationStep { get; init; }
        public Guid AlertId { get; init; }
        public required string Event { get; init; }
        public required string EndpointKey { get; init; }
        /// <summary>A <see cref="ShadowDifferenceKinds"/> value.</summary>
        public required string Kind { get; init; }
        /// <summary>The <see cref="ShadowRules"/> rule that explains it; null when none does.</summary>
        public string? Rule { get; init; }
        /// <summary>What differed, for a person: the path and both values, or the two times. Never a secret: bodies are what was sent.</summary>
        public required string Detail { get; init; }
        public DateTimeOffset DetectedAt { get; init; }
    }

    public static class ShadowDifferenceKinds
    {
        /// <summary>Legacy sent to the endpoint; the new pipeline wouldn't have.</summary>
        public const string MissingInNew = "missing_in_new";
        /// <summary>The new pipeline would have sent to the endpoint; legacy didn't.</summary>
        public const string MissingInLegacy = "missing_in_legacy";
        /// <summary>Both sent, with different bodies (modulo ids and times).</summary>
        public const string Body = "body";
        /// <summary>Both sent, further apart in time than the tolerance.</summary>
        public const string Timing = "timing";
        /// <summary>Both sent, as a different number of messages, or a group of other alerts.</summary>
        public const string Shape = "shape";
        /// <summary>One side hadn't settled when the wait ran out.</summary>
        public const string Unsettled = "unsettled";
    }

    /// <summary>
    /// The rules a difference may be explained by (p5-d7: explained only if the report names a rule; waves §6.1). Each is a decided difference
    /// between the pipelines, not a defect.
    /// </summary>
    public static class ShadowRules
    {
        /// <summary>A route digest's window isn't legacy's group window: what it gathers and when it closes differ at the edges (M46-1).</summary>
        public const string DigestBoundary = "digest_boundary";
        /// <summary>A resolve cancels a firing still queued (in a window); legacy sends both (Q2).</summary>
        public const string ResolveCancelsFiring = "resolve_cancels_firing";
        /// <summary>Legacy's destination policy refused it; a dry run stops before the provider's own check (M44 Q7).</summary>
        public const string BlockedByPolicyInLegacy = "blocked_by_policy_in_legacy";
        /// <summary>After a silence, "told" is per alert in the new pipeline, per endpoint in legacy (Q4).</summary>
        public const string ToldPerAlert = "told_per_alert";
        /// <summary>An email endpoint with several recipients: one message per recipient in the new pipeline, one to all in legacy (Q13).</summary>
        public const string EmailFanOut = "email_fan_out";
        /// <summary>
        /// The new pipeline rendered the delivery with the type's default template (template version 0): no template for the channel is ported
        /// yet (M46c), so its body differs from legacy's by design. Explained, so the soak and the dead-man's shadow cause don't fire on every
        /// unit until the templates are in.
        /// </summary>
        public const string TemplatesNotPorted = "templates_not_ported";

        public static readonly IReadOnlyList<string> All = [DigestBoundary, ResolveCancelsFiring, BlockedByPolicyInLegacy, ToldPerAlert, EmailFanOut, TemplatesNotPorted];
    }
}
