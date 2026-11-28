using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Juice.Observability.Data
{
    /// <summary>
    /// The tenant key the Collector stamps on the platform's own telemetry (Phase 11 R1): never a deployment's tenant, so the platform's and the
    /// Collector's metrics stay out of every tenant's inventory, evaluations and Grafana. A deployment may not take it as its key.
    /// </summary>
    public static class ReservedTenant
    {
        public const string Key = "juice-platform";
    }

    /// <summary>
    /// Which part of the platform is doing the work in progress (a worker, or an API endpoint), for what the platform measures about itself:
    /// a ClickHouse query is timed and tagged under it (<c>juice.observability.clickhouse.*</c>, and <c>log_comment</c> in ClickHouse's
    /// <c>system.query_log</c>). Set once at the top of a worker or a request; it flows into every task started under it.
    /// The names are the platform's own, a fixed set: never a tenant's value.
    /// </summary>
    public static class PlatformCaller
    {
        private static readonly AsyncLocal<(string Caller, CancellationToken Stopping)?> CurrentCaller = new();

        /// <summary>The caller in progress, or <c>other</c>.</summary>
        public static string Current => CurrentCaller.Value?.Caller ?? "other";

        /// <summary>
        /// What ends the caller's work from outside: the host stopping a worker, or a request aborted. A query cancelled while it isn't cancelled
        /// was cut by the platform's own deadline (a timeout), not stopped. Null when the caller didn't say.
        /// </summary>
        public static CancellationToken? Stopping => CurrentCaller.Value?.Stopping;

        /// <summary>Names the caller from here on, in this flow and every task started from it, with what stops its work (<see cref="Stopping"/>).</summary>
        public static void Set(string caller, CancellationToken stopping = default) => CurrentCaller.Value = (caller, stopping);
    }

    /// <summary>
    /// What the platform's workers report about themselves (Phase 11 R4, R6), under the platform's meter <c>Juice.Observability</c>:
    /// <list type="bullet">
    /// <item><c>juice.observability.worker.passes</c> and <c>juice.observability.worker.pass.duration</c>: each pass of a worker that runs in
    /// passes (discovery, health, SLO counting, notifications, action requests, the platform sampler), with its
    /// outcome: <c>success</c>, <c>partial</c> (done, but part of it failed, such as one SLO of many), <c>failure</c>, or <c>canceled</c> (the
    /// host stopping);</item>
    /// <item><c>juice.observability.worker.pass.age</c>: how long the pass in progress has run (0 between passes), so a pass stuck in a query shows;</item>
    /// <item><c>juice.observability.worker.since_last_pass</c>: seconds since this replica's last pass ended: while it holds the lease, or always
    /// for a worker that runs in every replica (<see cref="Register"/> with <c>leased: false</c>);</item>
    /// <item><c>juice.observability.worker.lease</c>: 1 while this replica holds the worker's lease (<see cref="WorkerLease"/>), else 0.</item>
    /// </list>
    /// Tagged by <c>worker</c> only (a fixed set of names); the replica is the exporter's <c>service.instance.id</c>. The same state feeds the
    /// replica's heartbeats (<see cref="Snapshot"/>), as ages on this process's monotonic clock, so no replica's wall clock is trusted.
    /// </summary>
    public static class WorkerPulse
    {
        public const string MeterName = "Juice.Observability";

        /// <summary>
        /// Every worker the platform has, enabled or not: each replica reports each one, so a worker no live replica runs reads as gone, not as
        /// switched off, unless every live replica says it is off.
        /// </summary>
        public static readonly IReadOnlyList<string> KnownWorkers =
            ["action-requests", "discovery", "health", "notifications-holds", "notifications-release", "rules", "sampler", "slos"];

        /// <summary>The known workers that run in every replica, without a lease.</summary>
        public static readonly IReadOnlySet<string> UnleasedWorkers = new HashSet<string> { "sampler" };

        private static readonly Meter Meter = new(MeterName);
        private static readonly Counter<long> Passes = Meter.CreateCounter<long>("juice.observability.worker.passes", "{pass}",
            "Passes of a worker, by outcome (success, partial, failure, canceled)");
        private static readonly Histogram<double> PassDuration = Meter.CreateHistogram<double>("juice.observability.worker.pass.duration", "s",
            "How long a worker's pass took");
        private static readonly ConcurrentDictionary<string, State> Workers = new();
        private static readonly ConcurrentDictionary<string, byte> Declared = new();

        static WorkerPulse()
        {
            Meter.CreateObservableGauge("juice.observability.worker.pass.age", () => Observe(s => s.Running is { } started ? Seconds(started) : 0), "s",
                "How long the worker's pass in progress has run; 0 between passes");
            Meter.CreateObservableGauge("juice.observability.worker.since_last_pass", () => Observe(s => (s.Held || s.Unleased) && s.LastEnd is { } end ? Seconds(end) : null), "s",
                "Seconds since the worker's last pass ended, while this replica holds its lease (always, for a worker without one)");
            Meter.CreateObservableGauge("juice.observability.worker.lease", () => Observe(s => s.Unleased ? null : s.Held ? 1 : 0), "{lease}",
                "1 while this replica holds the worker's lease");
        }

        internal sealed class State
        {
            // Stopwatch timestamps: this process's monotonic clock.
            public long? Running;
            public long? LastEnd;
            public long? HeldSince;
            public long Registered;
            public bool Held;
            public bool Unleased;
            public string? LastOutcome;
            /// <summary>How long its recent passes took: a moving mean, each pass weighing <see cref="PassWeight"/> (Phase 11 R9).</summary>
            public double? PassSeconds;
            public TimeSpan Interval;
            /// <summary>How many running instances registered it (a test process can host several); enabled while above 0.</summary>
            public int Registrations;
        }

        /// <summary>
        /// One worker as this replica sees it, for its heartbeat row (<c>worker_heartbeats</c>): enabled here or not, under a lease and holding it,
        /// its interval, and ages on this process's clock (the writer turns them into the database's time): since it registered, since it took
        /// the lease, of the pass in progress, and since the last one ended.
        /// </summary>
        public sealed record WorkerState(string Worker, bool Enabled, bool Leased, bool Held, TimeSpan Interval, TimeSpan? RegisteredFor,
            TimeSpan? HeldFor, TimeSpan? RunningFor, TimeSpan? SinceLastEnd, string? LastOutcome, double? PassSeconds = null);

        /// <summary>The weight of each pass in <see cref="WorkerState.PassSeconds"/>: about the last ten passes count.</summary>
        public const double PassWeight = 0.2;

        /// <summary>Every <see cref="KnownWorkers"/> worker (and any other registered), now: an unregistered one as not enabled.</summary>
        public static IReadOnlyList<WorkerState> Snapshot()
        {
            var states = new List<WorkerState>();
            foreach (var worker in KnownWorkers.Union(Workers.Keys).Union(Declared.Keys).Order())
            {
                if (!Workers.TryGetValue(worker, out var state))
                {
                    states.Add(new WorkerState(worker, false, !UnleasedWorkers.Contains(worker), false, TimeSpan.Zero, null, null, null, null, null));
                    continue;
                }
                lock (state)
                {
                    var enabled = state.Registrations > 0;
                    if (!enabled && !KnownWorkers.Contains(worker) && !Declared.ContainsKey(worker))
                    {
                        continue;
                    }
                    states.Add(new WorkerState(worker, enabled, enabled ? !state.Unleased : !UnleasedWorkers.Contains(worker), enabled && state.Held, state.Interval,
                        enabled ? Age(state.Registered) : null, enabled && state.Held && state.HeldSince is { } h ? Age(h) : null,
                        enabled && state.Running is { } r ? Age(r) : null, enabled && state.LastEnd is { } e ? Age(e) : null,
                        enabled ? state.LastOutcome : null, enabled ? state.PassSeconds : null));
                }
            }
            return states;
        }

        /// <summary>
        /// Names a worker that isn't one of <see cref="KnownWorkers"/> (a module's leased worker) so every replica reports it, enabled or not: one no live
        /// replica runs reads as gone, not as switched off. It doesn't enable it: <see cref="Register"/> does.
        /// </summary>
        public static void Declare(string worker) => Declared.TryAdd(worker, 0);

        private static TimeSpan Age(long since) => Stopwatch.GetElapsedTime(since);

        /// <summary>
        /// Registers <paramref name="worker"/> as enabled in this process, running every <paramref name="interval"/>, until <paramref name="stopping"/>
        /// (the host stopping it): its heartbeat says so, and the platform's status expects its passes. A worker whose loop dies without the host
        /// stopping stays registered, and goes stale. <paramref name="leased"/> false: it runs in every replica, without a lease (it reports no
        /// lease, and its time since the last pass always).
        /// </summary>
        public static void Register(string worker, TimeSpan interval, bool leased = true, CancellationToken stopping = default)
        {
            var state = Of(worker);
            lock (state)
            {
                if (state.Registrations++ == 0)
                {
                    // A clean slate: nothing from an earlier registration in this process (a pass that ended after it stopped) carries over.
                    state.Registered = Stopwatch.GetTimestamp();
                    state.Running = null;
                    state.LastEnd = null;
                    state.LastOutcome = null;
                    state.PassSeconds = null;
                    state.Held = false;
                    state.HeldSince = null;
                }
                state.Interval = interval;
                state.Unleased = !leased;
            }
            stopping.Register(() => Unregister(worker));
        }

        /// <summary>Undoes one <see cref="Register"/>: the worker reads as not enabled here once nothing running registered it.</summary>
        public static void Unregister(string worker)
        {
            if (!Workers.TryGetValue(worker, out var state))
            {
                return;
            }
            lock (state)
            {
                if (state.Registrations > 0 && --state.Registrations == 0)
                {
                    state.Held = false;
                    state.Running = null;
                    state.LastEnd = null;
                    state.HeldSince = null;
                    state.LastOutcome = null;
                    state.PassSeconds = null;
                }
            }
        }

        /// <summary>A pass with no work worth timing (the rule scheduler's tick): it counts as one done, for the heartbeat only.</summary>
        public static void Tick(string worker)
        {
            var state = Of(worker);
            lock (state)
            {
                state.LastEnd = Stopwatch.GetTimestamp();
                state.LastOutcome = "success";
            }
        }

        private static State Of(string worker) => Workers.GetOrAdd(worker, _ => new State());

        private static double Seconds(long since) => Stopwatch.GetElapsedTime(since).TotalSeconds;

        private static IEnumerable<Measurement<double>> Observe(Func<State, double?> value)
        {
            foreach (var (worker, state) in Workers)
            {
                double? v;
                lock (state)
                {
                    v = value(state);
                }
                if (v is { } measured)
                {
                    yield return new Measurement<double>(measured, new KeyValuePair<string, object?>("worker", worker));
                }
            }
        }

        /// <summary>Whether this replica holds <paramref name="worker"/>'s lease now (called by <see cref="WorkerLease"/>).</summary>
        public static void Lease(string worker, bool held)
        {
            var state = Of(worker);
            lock (state)
            {
                // Taken (back): a pass from an earlier hold isn't this hold's last pass, so the time since it would read as a stopped worker; and
                // the hold starts now, which tells the latest holder from one that lost the lease and hasn't noticed yet.
                if (held && !state.Held)
                {
                    state.LastEnd = null;
                    state.PassSeconds = null;   // and how long an earlier hold's passes took isn't this hold's
                    state.HeldSince = Stopwatch.GetTimestamp();
                }
                state.Held = held;
            }
        }

        /// <summary>
        /// Starts timing a pass of <paramref name="worker"/>; complete it with <see cref="Pass.Succeeded"/> or <see cref="Pass.Partial"/>. Disposed without
        /// either, it is a failure, or <c>canceled</c> when <paramref name="stopping"/> (the host stopping) was cancelled.
        /// </summary>
        public static Pass Begin(string worker, CancellationToken stopping = default)
        {
            var state = Of(worker);
            var started = Stopwatch.GetTimestamp();
            lock (state)
            {
                state.Running = started;
            }
            return new Pass(worker, state, started, stopping);
        }

        /// <summary>One pass in progress: <see cref="Succeeded"/> or <see cref="Partial"/> when it finished; disposed without either, a failure (or canceled).</summary>
        public sealed class Pass : IDisposable
        {
            private readonly string _worker;
            private readonly State _state;
            private readonly long _started;
            private readonly CancellationToken _stopping;
            private bool _done;
            private bool _idle;
            private string? _outcome;

            internal Pass(string worker, State state, long started, CancellationToken stopping) => (_worker, _state, _started, _stopping) = (worker, state, started, stopping);

            public void Succeeded() => _outcome = "success";

            /// <summary>Finished, but part of the work failed (one SLO of many, say): logged by the worker.</summary>
            public void Partial() => _outcome = "partial";

            /// <summary>Nothing to do this time: a heartbeat only, as <see cref="Tick"/>; no pass is counted or timed.</summary>
            public void Idle() => _idle = true;

            public void Dispose()
            {
                if (_done)
                {
                    return;
                }
                _done = true;
                var ended = Stopwatch.GetTimestamp();
                if (_idle && _outcome is null)
                {
                    lock (_state)
                    {
                        _state.Running = null;
                        _state.LastEnd = ended;
                        _state.LastOutcome = "success";
                    }
                    return;
                }
                var outcome = _outcome ?? (_stopping.IsCancellationRequested ? "canceled" : "failure");
                var seconds = Stopwatch.GetElapsedTime(_started, ended).TotalSeconds;
                lock (_state)
                {
                    _state.Running = null;
                    _state.LastEnd = ended;
                    _state.LastOutcome = outcome;
                    _state.PassSeconds = _state.PassSeconds is { } mean ? mean + PassWeight * (seconds - mean) : seconds;
                }
                var tag = new KeyValuePair<string, object?>("worker", _worker);
                PassDuration.Record(Stopwatch.GetElapsedTime(_started, ended).TotalSeconds, tag);
                Passes.Add(1, tag, new KeyValuePair<string, object?>("outcome", outcome));
            }
        }
    }
}
