using Juice.Observability.Discovery;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        // Stubs: slice C replaces these bodies (HostCpu, HostDisk).
        private partial Task<IReadOnlyList<RuleObservation>> ObserveHostCpuAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RuleObservation>>([]);

        private partial Task<IReadOnlyList<RuleObservation>> ObserveHostDiskAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RuleObservation>>([]);
    }
}
