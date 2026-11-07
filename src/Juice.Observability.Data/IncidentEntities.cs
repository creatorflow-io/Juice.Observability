namespace Juice.Observability.Data
{
    // Incidents (roadmap Phase 6): operational work grouped across alerts.

    public enum IncidentState { Open, Investigating, Mitigated, Resolved }

    /// <summary>
    /// A person's statement that something is wrong and being handled. It groups alerts but doesn't drive them: resolving an incident
    /// changes no alert, and an alert resolving changes no incident.
    /// </summary>
    public sealed class Incident
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Title { get; set; }
        public AlertSeverity Severity { get; set; }
        public IncidentState State { get; set; }
        public required string CreatedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
    }

    /// <summary>An alert in an incident. An alert is in one unresolved incident at most.</summary>
    public sealed class IncidentAlert
    {
        public Guid TenantId { get; init; }
        public Guid IncidentId { get; init; }
        public Guid AlertId { get; init; }
        public required string AddedBy { get; init; }
        public DateTimeOffset AddedAt { get; init; }
    }

    public static class IncidentEventTypes
    {
        public const string Created = "created";
        public const string State = "state";
        public const string Note = "note";
        public const string AlertAdded = "alert_added";
        public const string AlertRemoved = "alert_removed";
    }

    /// <summary>The incident's own record of what happened and who did it. Append only.</summary>
    public sealed class IncidentEvent
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid IncidentId { get; init; }
        public DateTimeOffset At { get; init; }
        public required string Actor { get; init; }
        public required string Type { get; init; }
        public IncidentState? FromState { get; init; }
        public IncidentState? ToState { get; init; }
        public string? Text { get; init; }
        public Guid? AlertId { get; init; }
    }
}
