using Juice.Observability.Extensibility;

namespace Juice.Observability.Data
{
    /// <summary>
    /// The core's tables that features moving out of the core into a module will own (SLOs, load runs, correlation, escalation and agents). A module may
    /// adopt one of these, and only these, from the core model (<see cref="ModuleDataOptions.Adopts"/>): every other table of the core is the core's,
    /// and a module adopting one is refused at composition. A module never drops or alters an adopted table; it takes it over as it is.
    /// </summary>
    public static class CoreMovedTables
    {
        /// <summary>The schema of every table of the core.</summary>
        public const string Schema = "public";

        public static IReadOnlyList<AdoptedTable> All { get; } =
        [
            .. new[]
            {
                "slos", "slo_versions", "slo_buckets", "slo_days", "slo_hours",
                "load_runs", "correlation_decisions", "escalation_policies",
                "agents", "agent_enrollment_failures", "agent_enrollment_tokens", "agent_commands", "agent_bundles", "action_requests",
            }.Select(name => new AdoptedTable(Schema, name)),
        ];
    }
}
