namespace Juice.Observability.Data
{
    // Silences, maintenance windows and the selection of an escalation policy (roadmap Phase 6; the policy itself is the notification pipeline's).

    /// <summary>Who owns a rule: the deployment's configuration file, or the API. Each leaves the other's alone.</summary>
    public static class ManagedBy
    {
        public const string Configuration = "config";
        public const string Api = "api";
        /// <summary>Made and kept by the platform itself (the SLO burn-rate rules): neither the file nor the API changes it.</summary>
        public const string Platform = "platform";
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

    /// <summary>
    /// Which escalation policy an alert escalates by: the severity, rules and environments that select it. The policy of that key in the
    /// notification pipeline says what it does (its steps), so the platform keeps no steps of its own.
    /// </summary>
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
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
