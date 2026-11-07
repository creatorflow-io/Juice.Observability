using Juice.Domain;
using Juice.MediatR;

namespace Juice.Observability.Data
{
    // Rules and alerts (requirements §8, decision D5).

    public enum RuleTemplate { ErrorRate, LatencyP95, ThroughputAbsent, DeliveryFailureRate, HostMemory, MediatorErrors, ConsumerFailures }

    public enum AlertSeverity { Info, Warning, Critical }

    /// <summary>
    /// A typed rule, from deployment configuration at startup or the API (<see cref="ManagedBy"/>). <see cref="Definition"/>
    /// holds the validated settings as JSON; <see cref="DefinitionHash"/> tells a changed definition from a reload, and is the rule's ETag.
    /// </summary>
    public sealed class Rule
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string Name { get; set; }
        public RuleTemplate Template { get; set; }
        public AlertSeverity Severity { get; set; }
        public required string Definition { get; set; }
        public required string DefinitionHash { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTimeOffset LoadedAt { get; set; }
        public string ManagedBy { get; set; } = Data.ManagedBy.Api;
    }

    public enum AlertState { Pending, Firing, Resolved }

    /// <summary>
    /// One occurrence of a rule's condition on one subject. At most one open (Pending or Firing) alert per
    /// <see cref="DedupKey"/>. An alert that resolves while Pending never fired (<see cref="FiredAt"/> is null).
    /// It raises <see cref="AlertTransitioned"/> when it fires, and when it resolves after firing: the mediator's TransactionBehavior
    /// dispatches it in the transaction that saves the alert.
    /// </summary>
    public sealed class Alert : IAggregateRoot<INotification>
    {
        /// <summary>Raised, not stored: TransactionBehavior dispatches and clears them.</summary>
        public IList<INotification> DomainEvents { get; } = [];

        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid RuleId { get; init; }
        /// <summary>Rule key and subject key: <c>error-rate|service:shop/orders@production</c>.</summary>
        public required string DedupKey { get; init; }
        public required string SubjectKey { get; init; }
        public HealthSubject SubjectType { get; init; }
        public Guid? SubjectId { get; init; }
        public Guid? EnvironmentId { get; init; }
        public AlertSeverity Severity { get; init; }
        public AlertState State { get; set; }
        public DateTimeOffset BreachStartedAt { get; init; }
        public DateTimeOffset? FiredAt { get; set; }
        public DateTimeOffset? RecoveringSince { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public DateTimeOffset LastEvaluatedAt { get; set; }
        public double? LastValue { get; set; }
        public long LastSamples { get; set; }
        /// <summary>The last evaluation had no usable data: the state was held, neither fired nor resolved.</summary>
        public bool NoData { get; set; }
        /// <summary>The evidence reference of the last evaluation: template, window, value, threshold.</summary>
        public Dictionary<string, string> Evidence { get; set; } = [];
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public string? AcknowledgedBy { get; set; }
        public string? AcknowledgeComment { get; set; }
    }

    /// <summary>The state history of an alert.</summary>
    public sealed class AlertTransition
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid AlertId { get; init; }
        public AlertState? FromState { get; init; }
        public AlertState ToState { get; init; }
        public DateTimeOffset At { get; init; }
        public double? Value { get; init; }
        public required string Reason { get; init; }
        /// <summary>
        /// When notifications planned this transition's deliveries and escalations. A concurrency token: the outbox may hand the transition over
        /// twice (once right after the commit, once from delivery), and a second plan must not send everything again.
        /// </summary>
        public DateTimeOffset? NotificationsPlannedAt { get; set; }
    }

    /// <summary>
    /// An alert fired, or resolved after firing: the transitions that notify. A domain event, dispatched in the transaction that saves it, so
    /// whatever its handlers add (an integration event in the outbox) commits with the alert.
    /// </summary>
    public sealed record AlertTransitioned(Guid PlatformTenantId, Guid AlertId, Guid TransitionId, AlertState ToState) : MessageBase, INotification;

    /// <summary>
    /// The stored outcome of a mutation sent with an <c>Idempotency-Key</c> (requirements §6.1): a retry with the same
    /// key and request gets the same response; the same key with a different request is refused.
    /// </summary>
    public sealed class IdempotencyRecord
    {
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string Operation { get; init; }
        public required string RequestHash { get; init; }
        public int StatusCode { get; init; }
        public required string ResponseBody { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
