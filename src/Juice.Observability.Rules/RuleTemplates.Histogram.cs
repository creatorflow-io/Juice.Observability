using Juice.Observability.Discovery;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        // Stub: slice B replaces this body (histogram percentiles, Avg and Rate).
        private partial Task<IReadOnlyList<RuleObservation>> ObserveHistogramAsync(RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RuleObservation>>([]);
    }
}
