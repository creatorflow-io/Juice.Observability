namespace Juice.Observability.Data
{
    // Signed webhook notification (requirements §8, roadmap Phase 4).

    /// <summary>
    /// A receiver of alert notifications, loaded from deployment configuration. <see cref="SecretRef"/> names where the
    /// signing secret is (<c>env:NAME</c> or <c>file:/path</c>); the secret itself is never stored.
    /// </summary>
    public sealed class WebhookEndpoint
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string Url { get; set; }
        public required string SecretRef { get; set; }
        /// <summary>Event types sent: <c>alert.firing</c>, <c>alert.resolved</c>.</summary>
        public string[] Events { get; set; } = [];
        /// <summary>Only these rules; empty for every rule.</summary>
        public string[] RuleKeys { get; set; } = [];
        public AlertSeverity MinimumSeverity { get; set; }
        public bool Enabled { get; set; } = true;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
    }

    /// <summary>A private host or CIDR (with ports) that webhooks may reach, loaded from configuration and audited.</summary>
    public sealed class WebhookAllowlistEntry
    {
        public Guid TenantId { get; init; }
        /// <summary>A CIDR (<c>10.20.0.0/16</c>) or an exact host name.</summary>
        public required string Destination { get; init; }
        /// <summary>Allowed ports; empty for 443 only.</summary>
        public int[] Ports { get; set; } = [];
        public DateTimeOffset LoadedAt { get; set; }
    }

    public enum DeliveryStatus { Pending, Retrying, Delivered, Failed }

    /// <summary>
    /// One notification to one endpoint, written with the alert transition (an outbox) and sent at least once by the
    /// dispatcher. <see cref="Id"/> is the stable <c>webhook-id</c> receivers deduplicate on.
    /// </summary>
    public sealed class WebhookDelivery
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid EndpointId { get; init; }
        public Guid AlertId { get; init; }
        public Guid TransitionId { get; init; }
        public required string Event { get; init; }
        /// <summary>The exact body that is signed and sent on every attempt.</summary>
        public required string Payload { get; init; }
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public int? LastStatusCode { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FirstAttemptAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }

    /// <summary>One send of a delivery: the delivery history.</summary>
    public sealed class WebhookAttempt
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid DeliveryId { get; init; }
        public int Number { get; init; }
        public DateTimeOffset At { get; init; }
        public int? StatusCode { get; init; }
        public string? Error { get; init; }
        public int DurationMs { get; init; }
    }
}
