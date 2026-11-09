namespace Juice.Observability.Data
{
    // Signed webhook notification (requirements §8, roadmap Phase 4).

    public enum ChannelKind { Webhook, Slack, Teams, Email }

    /// <summary>
    /// A receiver of alert notifications, from deployment configuration or the API (<see cref="ManagedBy"/>). <see cref="SecretRef"/> names where
    /// the secret is (<c>env:NAME</c> or <c>file:/path</c>); the secret itself is never stored. For a signed webhook it's the signing key. Slack
    /// and Teams URLs are themselves credentials, so for those it holds the URL and <see cref="Url"/> is empty. Email uses neither.
    /// </summary>
    public sealed class WebhookEndpoint
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public ChannelKind Kind { get; set; }
        public required string Url { get; set; }
        public required string SecretRef { get; set; }
        /// <summary>Event types sent: <c>alert.firing</c>, <c>alert.resolved</c>.</summary>
        public string[] Events { get; set; } = [];
        /// <summary>Only these rules; empty for every rule.</summary>
        public string[] RuleKeys { get; set; } = [];
        /// <summary>Only alerts in these environments; empty for all. A host alert has no environment, so a list leaves it out.</summary>
        public string[] Environments { get; set; } = [];
        /// <summary>Email addresses, for <see cref="ChannelKind.Email"/>.</summary>
        public string[] Recipients { get; set; } = [];
        /// <summary>Waits this long and sends what gathered as one notification; 0 sends each at once.</summary>
        public int GroupWindowSeconds { get; set; }
        public AlertSeverity MinimumSeverity { get; set; }
        public bool Enabled { get; set; } = true;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset LoadedAt { get; set; }
        public string ManagedBy { get; set; } = Data.ManagedBy.Api;
    }

    /// <summary>A private host or CIDR (with ports) that webhooks may reach, from configuration or the API, always audited.</summary>
    public sealed class WebhookAllowlistEntry
    {
        public Guid TenantId { get; init; }
        /// <summary>A CIDR (<c>10.20.0.0/16</c>) or an exact host name.</summary>
        public required string Destination { get; init; }
        /// <summary>Allowed ports; empty for 443 only.</summary>
        public int[] Ports { get; set; } = [];
        public DateTimeOffset LoadedAt { get; set; }
        public string ManagedBy { get; set; } = Data.ManagedBy.Api;
    }

    public enum DeliveryStatus
    {
        Pending, Retrying, Delivered, Failed,
        /// <summary>Held by a silence; released or dropped when it ends.</summary>
        Suppressed,
        /// <summary>Held by a silence that ended when there was nothing left worth saying.</summary>
        Dropped,
        /// <summary>Sent as part of another delivery (<see cref="WebhookDelivery.GroupedInto"/>).</summary>
        Grouped,
    }

    /// <summary>
    /// One notification to one endpoint, written with the alert transition or action request it tells of (an outbox) and sent at least once by the
    /// dispatcher. <see cref="Id"/> is the stable <c>webhook-id</c> receivers deduplicate on. Either <see cref="AlertId"/> or <see cref="ActionRequestId"/> is set.
    /// </summary>
    public sealed class WebhookDelivery
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid EndpointId { get; init; }
        public Guid? AlertId { get; init; }
        public Guid? TransitionId { get; init; }
        /// <summary>The action request this tells of (<see cref="ActionRequestEvents"/>); such a delivery is never silenced or grouped.</summary>
        public Guid? ActionRequestId { get; init; }
        public required string Event { get; init; }
        /// <summary>Which step of an escalation; 0 for a plain notification.</summary>
        public int EscalationStep { get; init; }
        /// <summary>The exact body that is signed and sent on every attempt. Set once more if a first attempt gathers a group.</summary>
        public required string Payload { get; set; }
        public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public int? LastStatusCode { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FirstAttemptAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
        public Guid? SuppressedBy { get; set; }
        public Guid? GroupedInto { get; set; }
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
