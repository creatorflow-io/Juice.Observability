namespace Juice.Observability.Api.Integrations
{
    /// <summary>Configured → Validated → Enabled → Degraded → Disabled/Failed. Stored as its name (20 characters).</summary>
    internal enum IntegrationState { Configured, Validated, Enabled, Degraded, Disabled, Failed }

    /// <summary>One configured use of a plugin in a tenant. Holds secret references, never secrets.</summary>
    internal sealed class IntegrationInstance
    {
        public Guid Id { get; set; } = Guid.CreateVersion7();
        public Guid TenantId { get; set; }
        public string PluginKey { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>Generated: <c>lower(name)</c>, so the name is unique per tenant without regard to case.</summary>
        public string NameKey { get; private set; } = "";

        /// <summary>JSON text (jsonb), validated against the plugin's schema and canonical (property order fixed), so its hash is stable.</summary>
        public string Config { get; set; } = "{}";
        public int ConfigVersion { get; set; }

        /// <summary>JSON object: declared secret name to <c>env:NAME</c> or <c>file:/path</c>.</summary>
        public string SecretRefs { get; set; } = "{}";
        public string Scope { get; set; } = "{}";
        public int IntervalSeconds { get; set; }
        public IntegrationState State { get; set; } = IntegrationState.Configured;
        public string? StateReason { get; set; }
        public string? ValidatedHash { get; set; }
        public string? LastValidation { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public DateTimeOffset? NextRunAt { get; set; }
        public Guid? RunToken { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? LastOutcome { get; set; }
        public string? LastFailure { get; set; }
        public string? LastFailureDetail { get; set; }

        /// <summary>Concurrency token, bumped by admin changes only (the worker's outcome writes do not).</summary>
        public int Version { get; set; } = 1;
        public string CreatedBy { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }

        public List<IntegrationCheckpointRow> Checkpoints { get; set; } = [];
    }

    /// <summary>Where a plugin's stream of an instance had got to; committed with a run's accepted outcome.</summary>
    internal sealed class IntegrationCheckpointRow
    {
        public Guid InstanceId { get; set; }
        public string Stream { get; set; } = "";
        public DateTimeOffset Watermark { get; set; }
        public string? Cursor { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
