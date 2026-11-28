using Juice.Observability.Discovery;

namespace Juice.Observability.Rules
{
    public sealed partial class RuleTemplates
    {
        // Stub in the first commit; the gauge, counter and up-down counter queries follow.
        private Task<IReadOnlyList<RuleObservation>> ObserveMetricAsync(RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RuleObservation>>([]);
    }
}
