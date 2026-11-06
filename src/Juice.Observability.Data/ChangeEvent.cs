namespace Juice.Observability.Data
{
    public enum ChangeKind { Deployment, Version, Configuration, Other }

    /// <summary>
    /// Something that changed: a deployment or configuration change a pipeline reported, a new <c>service.version</c> seen in
    /// telemetry, or the platform's own rules and webhooks. Stored as a safe summary only (Phase 5): text is scrubbed of secrets
    /// and attributes are allowlisted before they get here.
    /// </summary>
    public sealed class ChangeEvent
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public ChangeKind Kind { get; init; }
        /// <summary><see cref="TelemetrySource"/>, <see cref="PlatformSource"/>, or what a caller named, such as a pipeline.</summary>
        public required string Source { get; init; }
        /// <summary>The authenticated caller; null when telemetry showed the change, since nothing says who made it.</summary>
        public string? Actor { get; init; }
        public required string Summary { get; init; }
        public Guid? ServiceId { get; init; }
        public Guid? ResourceId { get; init; }
        /// <summary>The environment key; null when the change isn't tied to one.</summary>
        public string? Environment { get; init; }
        public string? FromVersion { get; init; }
        public string? ToVersion { get; init; }
        public Dictionary<string, string> Attributes { get; init; } = [];
        public DateTimeOffset OccurredAt { get; init; }
        public DateTimeOffset RecordedAt { get; init; }
        /// <summary>Unique per source, so reporting or detecting the same change twice stores one event.</summary>
        public string? ExternalKey { get; init; }

        public const string TelemetrySource = "telemetry";
        public const string PlatformSource = "platform";
        /// <summary>Set on a rule's own change, so an alert of that rule can relate to it.</summary>
        public const string RuleAttribute = "rule";
    }
}
