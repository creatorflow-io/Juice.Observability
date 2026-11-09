namespace Juice.Observability.Data
{
    // Juice Agent enrollment and commands (roadmap Phase 7).

    public enum AgentStatus { Active, Revoked }

    /// <summary>
    /// An enrolled Juice Agent: one host's identity, from a certificate the platform's agent CA issued for the agent's own key. Revoking it stops
    /// every call it makes at once, since each call is checked against this row. Kept after revocation, with who did it and why.
    /// </summary>
    public sealed class EnrolledAgent
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Hostname { get; set; }
        public string OperatingSystem { get; set; } = "";
        public string Version { get; set; } = "";
        /// <summary>The host discovery found with the same name, when there is one.</summary>
        public Guid? ResourceId { get; set; }
        public AgentStatus Status { get; set; } = AgentStatus.Active;
        /// <summary>The serial of the certificate it uses now, hex. Only this one, or <see cref="PreviousCertificateSerial"/>, is accepted.</summary>
        public required string CertificateSerial { get; set; }
        public DateTimeOffset CertificateNotAfter { get; set; }
        /// <summary>The one it renewed from, accepted until the agent first calls with the new one, so a renewal whose answer was lost doesn't lock it out.</summary>
        public string? PreviousCertificateSerial { get; set; }
        public Guid EnrollmentTokenId { get; init; }
        public required string EnrolledBy { get; init; }
        public DateTimeOffset EnrolledAt { get; init; }
        public DateTimeOffset? LastSeenAt { get; set; }
        public DateTimeOffset? RevokedAt { get; set; }
        public string? RevokedBy { get; set; }
        public string? RevokeReason { get; set; }
        /// <summary>
        /// What it reported it can run and what its host permits (an <c>ActionsReport</c>, JSON), each time it starts; null for an agent from before
        /// reports. Information: the agent checks every command itself whatever this says.
        /// </summary>
        public string? Actions { get; set; }
        public DateTimeOffset? ActionsReportedAt { get; set; }
    }

    /// <summary>A one-time token an admin creates for enrolling one agent. Only its SHA-256 is stored; the token itself is shown once.</summary>
    public sealed class AgentEnrollmentToken
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        /// <summary>SHA-256 of the token, hex.</summary>
        public required string TokenHash { get; init; }
        /// <summary>What it's for, as the admin said: a host name, a ticket.</summary>
        public string? Purpose { get; init; }
        public required string CreatedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? UsedAt { get; set; }
        public Guid? AgentId { get; set; }
    }

    /// <summary>
    /// Failed enrollments from one address in the current window, shared by every replica, so the limit is one per deployment, not per replica.
    /// Enrollment comes before any tenant is known, so this isn't a tenant's. A row whose window has passed is stale and is pruned.
    /// </summary>
    public sealed class AgentEnrollmentFailure
    {
        public required string Address { get; init; }
        public DateTimeOffset WindowStart { get; set; }
        public int Failures { get; set; }
    }

    public enum AgentCommandState { Pending, Delivered, Succeeded, Failed, Rejected, Expired, Cancelled }

    /// <summary>
    /// A command issued to one agent, with the exact bytes the platform signed. Its state follows it: waiting, handed to the agent, and what the agent
    /// reported, its refusals included.
    /// </summary>
    public sealed class AgentCommand
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid AgentId { get; init; }
        public required string Action { get; init; }
        /// <summary>The typed parameters, JSON.</summary>
        public required string Parameters { get; init; }
        public required string IssuedBy { get; init; }
        public DateTimeOffset IssuedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public required string Nonce { get; init; }
        public required string IdempotencyKey { get; init; }
        /// <summary>The signed envelope, base64, sent as it is to the agent.</summary>
        public required string Payload { get; init; }
        public required string Signature { get; init; }
        public required string KeyId { get; init; }
        public AgentCommandState State { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
        public int DeliveryCount { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        /// <summary>Why it failed or was refused, as the agent reported it.</summary>
        public string? Reason { get; set; }
        /// <summary>The action's data, JSON, when it succeeded.</summary>
        public string? Result { get; set; }
        public int? DurationMs { get; set; }
    }
}
