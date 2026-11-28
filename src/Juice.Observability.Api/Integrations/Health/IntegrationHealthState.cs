using System.Diagnostics.Metrics;
using Juice.Observability.Data;

namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>
    /// The integration metrics (decision 9) and what the worker keeps in memory between passes. The instance label is the id (N format), never the name.
    /// Freshness is read from memory, which each pass replaces (so a deleted instance's series ends); cardinality is bounded by the instances per tenant.
    /// </summary>
    internal sealed class IntegrationHealthState : IDisposable
    {
        private readonly Meter _meter = new(WorkerPulse.MeterName);
        private readonly Counter<long> _runs;
        private readonly Counter<long> _failures;
        private readonly TimeProvider _clock;
        private volatile Dictionary<Guid, (string Plugin, DateTimeOffset LastSuccess)> _freshness = [];
        private int _conflictLogged;

        public IntegrationHealthState(TimeProvider clock)
        {
            _clock = clock;
            _runs = _meter.CreateCounter<long>("juice.integration.runs", "{run}", "Integration runs by plugin, instance and outcome");
            _failures = _meter.CreateCounter<long>("juice.integration.failures", "{failure}", "Failed integration runs by plugin, instance and reason");
            _meter.CreateObservableGauge("juice.integration.freshness", Observe, "s", "Seconds since an instance last collected");
        }

        /// <summary>True once the integration rule is known to exist as the platform's (cleared when the lease is retaken).</summary>
        public volatile bool RuleReady;

        /// <summary>True the first time it is asked, once per process: a non-platform rule holding the key is logged once.</summary>
        public bool FirstConflict() => Interlocked.Exchange(ref _conflictLogged, 1) == 0;

        public void Run(string plugin, Guid instance, string outcome, IntegrationFailure failure)
        {
            var id = instance.ToString("N");
            _runs.Add(1, new("plugin", plugin), new("instance", id), new("outcome", outcome));
            if (failure != IntegrationFailure.None)
            {
                _failures.Add(1, new("plugin", plugin), new("instance", id), new("reason", failure.ToString().ToLowerInvariant()));
            }
        }

        /// <summary>The instances' last successes, replacing every earlier one: what is no longer there stops being reported.</summary>
        public void Replace(IEnumerable<(Guid Id, string Plugin, DateTimeOffset? LastSuccess)> instances)
            => _freshness = instances.Where(i => i.LastSuccess is not null).ToDictionary(i => i.Id, i => (i.Plugin, i.LastSuccess!.Value));

        public void Succeeded(Guid id, string plugin, DateTimeOffset at)
        {
            var copy = new Dictionary<Guid, (string, DateTimeOffset)>(_freshness) { [id] = (plugin, at) };
            _freshness = copy;
        }

        private IEnumerable<Measurement<double>> Observe()
        {
            var now = _clock.GetUtcNow();
            foreach (var (id, entry) in _freshness)
            {
                yield return new Measurement<double>(Math.Max(0, (now - entry.LastSuccess).TotalSeconds), new("plugin", entry.Plugin), new("instance", id.ToString("N")));
            }
        }

        public void Dispose() => _meter.Dispose();
    }
}
