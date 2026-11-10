namespace Juice.Observability.Data
{
    // Alert correlation suggestions (roadmap Phase 9, R9): the platform suggests, people decide.

    public enum CorrelationDecisionKind { Accepted, Dismissed }

    /// <summary>One reason two alerts were suggested together, with the weight it added to the score.</summary>
    public sealed record CorrelationEvidence(string Code, string Text, double Weight);

    /// <summary>
    /// An operator's decision on a suggested pair of alerts: accepted (merged into <see cref="IncidentId"/>) or dismissed (with
    /// <see cref="Reason"/>). One per pair, so a dismissed pair isn't suggested again, and a second decision on the same pair is refused.
    /// The score, evidence and weights at the moment of deciding are kept, to tune the weights against later.
    /// <see cref="FirstAlertId"/> is the lower id of the two, so a pair has one spelling.
    /// </summary>
    public sealed class CorrelationDecision
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid FirstAlertId { get; init; }
        public Guid SecondAlertId { get; init; }
        public CorrelationDecisionKind Decision { get; init; }
        public double Score { get; init; }
        public List<CorrelationEvidence> Evidence { get; init; } = [];
        /// <summary>Which set of weights scored it, so decisions made under different weights aren't compared as one.</summary>
        public required string WeightsVersion { get; init; }
        public string? Reason { get; init; }
        public Guid? IncidentId { get; init; }
        public required string DecidedBy { get; init; }
        public DateTimeOffset DecidedAt { get; init; }

        /// <summary>The pair in its stored order.</summary>
        public static (Guid First, Guid Second) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }
}
