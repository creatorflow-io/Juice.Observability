using Juice.Observability.Extensibility;

namespace Juice.Observability.Api.Modules
{
    /// <summary>One composed module: what it declared, in the order it was composed.</summary>
    internal sealed class ModuleEntry(IObservabilityModule module, bool isInternal, ConsoleManifest? manifest, IReadOnlySet<string> policies, IReadOnlyList<Type> migrations)
    {
        public IObservabilityModule Module { get; } = module;

        public ModuleInfo Info => Module.Info;

        /// <summary>Compiled into the platform (core, slos, agents): may map at the tenant root.</summary>
        public bool IsInternal { get; } = isInternal;

        public ConsoleManifest? Manifest { get; } = manifest;

        public IReadOnlySet<string> Policies { get; } = policies;

        /// <summary>The DbContext types to migrate, in declaration order.</summary>
        public IReadOnlyList<Type> Migrations { get; } = migrations;
    }

    /// <summary>
    /// The modules a host runs, in composition order (core, slos, agents, then external). Created by <c>AddJuiceObservability</c>, so its absence
    /// means a module was added too early. Read after the host is built; never changed once it runs.
    /// </summary>
    internal sealed class ModuleRegistry
    {
        private readonly List<ModuleEntry> _entries = [];

        public IReadOnlyList<ModuleEntry> Entries => _entries;

        /// <summary>Set once the migration runner is registered (with the first external module).</summary>
        public bool RunnerRegistered { get; set; }

        public bool Has(string name) => _entries.Any(e => e.Info.Name == name);

        public IEnumerable<string> PolicyNames => _entries.SelectMany(e => e.Policies);

        public void Add(ModuleEntry entry) => _entries.Add(entry);
    }

    /// <summary>The tenant-root segments of the core's own routes under <c>/api/v1/tenants/{tenantId}</c>: an external module can't take one.</summary>
    internal static class CoreRouteSegments
    {
        // Pinned to the real routes by CoreRouteSegments_matches_real_tenant_root_segments.
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            "action-requests", "agents", "alerts", "applications", "changes", "environments", "escalation-policies", "health", "incidents",
            "load-runs", "resources", "rules", "search", "silences", "slos", "telemetry", "timeline", "topology",
        };
    }
}
