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

        private sealed class Entry
        {
            public volatile Task? Plugin;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Entry> _running = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _started = new();

        /// <summary>
        /// Takes the instance for a run on this replica, or says it is still taken: its previous run, or the plugin call of it, hasn't finished. A plugin that
        /// ignores its cancellation token keeps its instance taken after the run timed out, so it is never called twice at once.
        /// </summary>
        public bool TryBegin(Guid id) => _running.TryAdd(id, new Entry());

        /// <summary>The plugin call of a run that began: the instance stays taken until it ends, whatever the run did meanwhile.</summary>
        public void Attach(Guid id, Task plugin)
        {
            if (_running.TryGetValue(id, out var entry))
            {
                entry.Plugin = plugin;
            }
        }

        /// <summary>The run ended: the instance is free once its plugin call has ended too.</summary>
        public void End(Guid id)
        {
            if (!_running.TryGetValue(id, out var entry))
            {
                return;
            }
            if (entry.Plugin is { IsCompleted: false } plugin)
            {
                _ = plugin.ContinueWith(t =>
                {
                    _ = t.Exception;
                    _running.TryRemove(id, out _);
                }, TaskScheduler.Default);
            }
            else
            {
                _running.TryRemove(id, out _);
            }
        }

        /// <summary>Remembers a started run until it ends (what <see cref="WhenRunsDoneAsync"/> waits for).</summary>
        public void Track(Task run)
        {
            _started.TryAdd(run, 0);
            _ = run.ContinueWith(t => _started.TryRemove(t, out _), TaskScheduler.Default);
        }

        private SemaphoreSlim? _slots;

        /// <summary>The runs at once across passes (<c>MaxConcurrentRuns</c>), shared by every pass of this replica.</summary>
        public SemaphoreSlim Slots(int max) => _slots ?? Interlocked.CompareExchange(ref _slots, new SemaphoreSlim(Math.Max(1, max)), null) ?? _slots!;

        /// <summary>The runs started so far have ended. For tests and a clean stop; a pass never waits for its runs.</summary>
        public Task WhenRunsDoneAsync() => Task.WhenAll(_started.Keys.ToArray());

        /// <summary>True once the integration rule is known to exist as the platform's (cleared when the lease is retaken).</summary>
        public volatile bool RuleReady;

        /// <summary>True the first time it is asked, once per process: a non-platform rule holding the key is logged once.</summary>
        public bool FirstConflict() => Interlocked.Exchange(ref _conflictLogged, 1) == 0;

        public void Run(string plugin, Guid instance, string outcome, IntegrationFailure failure)
        {
            var id = instance.ToString("N");
            _runs.Add(1, new("plugin", plugin), new("instance", id), new("outcome", outcome));
            // The failures counter counts failed runs only: a partial run names what limited it, but isn't a failure.
            if (failure != IntegrationFailure.None && outcome == "failed")
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
