namespace Juice.Observability.Api.Integrations
{
    /// <summary>Configuration <c>Modules:integrations:*</c> (the <c>Integrations:Grafana</c> section is unrelated).</summary>
    internal sealed class IntegrationsOptions
    {
        public const string Section = "Modules:integrations";

        /// <summary><c>env:</c> secret references must start with this.</summary>
        public string SecretEnvPrefix { get; set; } = "JUICE_INTEGRATION_";

        /// <summary><c>file:</c> secret references must be inside this folder; unset means file references are refused.</summary>
        public string? SecretDirectory { get; set; }

        public TimeSpan ValidateTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Failed collections in a row before an instance is Failed (it is Degraded before).</summary>
        public int FailedAfter { get; set; } = 3;

        public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(5);

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

        public int MaxConcurrentRuns { get; set; } = 4;

        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Instances per tenant.</summary>
        public int MaxInstances { get; set; } = 100;
    }
}
