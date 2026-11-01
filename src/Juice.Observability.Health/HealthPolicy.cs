using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Juice.Observability.Health
{
    /// <summary>
    /// Configuration section <c>Health</c>: when and how subjects are evaluated (decision D4). Every value that changes
    /// a state feeds <see cref="Version"/>, so a policy change is visible on every snapshot and transition.
    /// </summary>
    public sealed class HealthPolicy
    {
        public const string Section = "Health";

        /// <summary>Whether the background worker runs. Tests run <see cref="HealthRunner"/> directly.</summary>
        public bool Enabled { get; set; } = true;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>The evidence window, ending <see cref="Lag"/> before now.</summary>
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Time for telemetry to reach ClickHouse (measured p95 under reference load: 1.4 s).</summary>
        public TimeSpan Lag { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>A previously seen subject without telemetry for longer than this is Offline.</summary>
        public TimeSpan FreshnessGrace { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Requests needed in the window before an error rate counts.</summary>
        public int MinimumSamples { get; set; } = 3;

        public double ErrorRateDegraded { get; set; } = 0.01;

        public double ErrorRateUnhealthy { get; set; } = 0.05;

        /// <summary>Share of fresh instances that are Unhealthy or Critical that makes the service Unhealthy.</summary>
        public double UnhealthyInstanceQuorum { get; set; } = 0.5;

        /// <summary>Share of an application's child weight that must have a known state, else the application is Unknown.</summary>
        public double MinimumCoverage { get; set; } = 0.5;

        public double HostMemoryDegraded { get; set; } = 0.95;

        /// <summary>A failing target makes the source service Critical (requirements §7).</summary>
        public List<CriticalDependency> CriticalDependencies { get; set; } = [];

        /// <summary>Per-service weight and criticality inside its application. Unlisted services weigh 1 and are optional.</summary>
        public List<ServiceWeight> ServiceWeights { get; set; } = [];

        /// <summary>A short hash of every setting that changes a state.</summary>
        public string Version
        {
            get
            {
                var json = JsonSerializer.Serialize(new
                {
                    FreshnessGrace, MinimumSamples, ErrorRateDegraded, ErrorRateUnhealthy, UnhealthyInstanceQuorum,
                    MinimumCoverage, HostMemoryDegraded, Window,
                    CriticalDependencies = CriticalDependencies.Select(d => $"{d.Service}>{d.DependsOn}").Order(StringComparer.Ordinal),
                    ServiceWeights = ServiceWeights.Select(w => $"{w.Service}:{w.Weight}:{w.Critical}").Order(StringComparer.Ordinal),
                });
                return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
            }
        }
    }

    /// <summary>Services are named <c>namespace/name</c>, or <c>name</c> when the namespace is empty.</summary>
    public sealed class CriticalDependency
    {
        public required string Service { get; set; }
        public required string DependsOn { get; set; }
    }

    public sealed class ServiceWeight
    {
        public required string Service { get; set; }
        public double Weight { get; set; } = 1;
        /// <summary>A critical child's failure or missing evidence decides the application's state.</summary>
        public bool Critical { get; set; }
    }
}
