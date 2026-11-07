namespace Juice.Observability.Data
{
    // Silences, maintenance windows and escalation (roadmap Phase 6).

    /// <summary>Who owns a rule, endpoint or allowlist entry: the deployment's configuration file, or the API. Each leaves the other's alone.</summary>
    public static class ManagedBy
    {
        public const string Configuration = "config";
        public const string Api = "api";
    }

    public enum SilenceKind
    {
        /// <summary>Ad hoc, usually from now: someone is already on it.</summary>
        Silence,
        /// <summary>Planned, usually ahead of time, and may repeat weekly.</summary>
        Maintenance,
    }

    /// <summary>
    /// Holds notifications for matching alerts while active. Alerts keep firing and resolving: a silence only affects what is sent, and what
    /// it held is released when it ends, if it still matters. Cancelled silences are kept as history.
    /// </summary>
    public sealed class Silence
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public SilenceKind Kind { get; init; }
        public required string Comment { get; init; }
        /// <summary>Only these rules; empty for any rule.</summary>
        public string[] RuleKeys { get; init; } = [];
        /// <summary><c>namespace/name</c> or <c>name</c>; empty for any.</summary>
        public string? Service { get; init; }
        public string? Environment { get; init; }
        public string? Host { get; init; }
        /// <summary>Only alerts of this severity or lower, so a silence on warnings still lets a critical alert through.</summary>
        public AlertSeverity? MaximumSeverity { get; init; }
        /// <summary>Set explicitly: a silence with no scope is otherwise refused, since it would hide everything.</summary>
        public bool AllAlerts { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset EndsAt { get; init; }
        /// <summary>Repeats every 7 days from <see cref="StartsAt"/>, for the same length, until cancelled.</summary>
        public bool RepeatWeekly { get; init; }
        public required string CreatedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CancelledAt { get; set; }
        public string? CancelledBy { get; set; }
    }

    /// <summary>One level of a policy: <see cref="AfterMinutes"/> after the alert fired, these endpoints are notified if it's still open.</summary>
    public sealed class EscalationStep
    {
        public int AfterMinutes { get; set; }
        public List<string> EndpointKeys { get; set; } = [];
    }

    /// <summary>Who to notify as an alert stays open. Matches alerts as an endpoint does; its endpoints are named per step.</summary>
    public sealed class EscalationPolicy
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string Name { get; set; }
        public bool Enabled { get; set; } = true;
        public string[] RuleKeys { get; set; } = [];
        public string[] Environments { get; set; } = [];
        public AlertSeverity MinimumSeverity { get; set; }
        /// <summary>Stop when someone acknowledges. A resolved alert always stops.</summary>
        public bool StopOnAcknowledge { get; set; } = true;
        public List<EscalationStep> Steps { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public enum EscalationState { Active, Stopped, Completed }

    /// <summary>A policy running on one alert, started when the alert fires.</summary>
    public sealed class AlertEscalation
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid AlertId { get; init; }
        public Guid PolicyId { get; init; }
        public EscalationState State { get; set; }
        public int NextStep { get; set; }
        public DateTimeOffset NextAt { get; set; }
        /// <summary><c>resolved</c>, <c>acknowledged</c> or <c>policy_disabled</c> when stopped.</summary>
        public string? StopReason { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
    }
}
