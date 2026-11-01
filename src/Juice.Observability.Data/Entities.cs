namespace Juice.Observability.Data
{
    // Domain records (requirements §6, decisions D1 and D3). Every Id is a UUIDv7 created by the platform; natural keys
    // come from telemetry and are unique per tenant. Times are UTC.

    public enum TenantStatus { Active, Suspended }

    /// <summary>The one provisioned tenant of a single-tenant deployment. <see cref="Key"/> is the slug the Collector stamps.</summary>
    public sealed class PlatformTenant
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        public required string DisplayName { get; set; }
        public TenantStatus Status { get; set; } = TenantStatus.Active;
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>A group of services. By default one per <c>service.namespace</c> (D1).</summary>
    public sealed class Application
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string DisplayName { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public enum EnvironmentType { Production, Staging, Development, Test, Other, Unspecified }

    /// <summary>From <c>deployment.environment.name</c>; telemetry without it lands in <c>unspecified</c>.</summary>
    public sealed class DeploymentEnvironment
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public EnvironmentType Type { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    /// <summary>A logical service, the same across environments (D1). Key: namespace + name.</summary>
    public sealed class Service
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Namespace { get; init; }
        public required string Name { get; init; }
        public Guid? ApplicationId { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public enum InstanceKeySource
    {
        /// <summary><c>service.instance.id</c> was reported.</summary>
        Observed,
        /// <summary>Derived from host name and process id; lower confidence (D1).</summary>
        Derived
    }

    public enum InstanceState { Active, Inactive }

    /// <summary>An ephemeral process of a service in one environment.</summary>
    public sealed class ServiceInstance
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid ServiceId { get; init; }
        public Guid EnvironmentId { get; init; }
        public required string InstanceKey { get; init; }
        public InstanceKeySource KeySource { get; init; }
        public string? ServiceVersion { get; set; }
        public Guid? HostResourceId { get; set; }
        public InstanceState State { get; set; } = InstanceState.Active;
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public enum ResourceType { Host, Container, KubernetesPod, MessagingDestination, Database }

    /// <summary>Infrastructure a service runs on or uses, keyed per type (D3).</summary>
    public sealed class Resource
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public ResourceType Type { get; init; }
        public required string Key { get; init; }
        public required string DisplayName { get; set; }
        /// <summary>Normalized, bounded attributes (at most 64, values at most 256 characters).</summary>
        public Dictionary<string, string> Attributes { get; set; } = [];
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public enum DependencyRelation { Calls, PublishesTo, ConsumesFrom, UsesDatabase }

    public enum DependencyConfidence
    {
        /// <summary>Both sides reported a span of the interaction.</summary>
        Observed,
        /// <summary>Only the calling side reported it.</summary>
        Inferred
    }

    /// <summary>
    /// A relationship inferred from traces, with its evidence. The target is a service or a resource, never both.
    /// Never stated as certain (requirements §6).
    /// </summary>
    public sealed class Dependency
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid EnvironmentId { get; init; }
        public Guid SourceServiceId { get; init; }
        public DependencyRelation Relation { get; init; }
        public Guid? TargetServiceId { get; init; }
        public Guid? TargetResourceId { get; init; }
        public DependencyConfidence Confidence { get; set; }
        public string EvidenceSource { get; init; } = "traces";
        public long SpanCount { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    public enum AuditOutcome { Succeeded, Denied, Failed }

    /// <summary>Who did what, written in the same transaction as the change (requirements §6.1).</summary>
    public sealed class AuditRecord
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Actor { get; init; }
        public required string Operation { get; init; }
        public required string TargetType { get; init; }
        public string? TargetId { get; init; }
        public AuditOutcome Outcome { get; init; }
        public string? CorrelationId { get; init; }
        public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
        public Dictionary<string, string> Metadata { get; init; } = [];
    }

    /// <summary>How far a discovery source has read telemetry (requirements §6).</summary>
    public sealed class ReconciliationCheckpoint
    {
        public Guid TenantId { get; init; }
        public required string Source { get; init; }
        public DateTimeOffset Watermark { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
