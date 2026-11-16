using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using static Juice.Observability.Api.PlatformSampler;

namespace Juice.Observability.Api
{
    /// <summary>Where the capacity view's calibration is (Phase 11 R9): the file <c>deploy/local/scale/calibration.mjs</c> writes.</summary>
    public sealed class CapacityOptions
    {
        public const string Section = "Capacity";

        /// <summary>The calibration file. Unset or unreadable: the view shows utilisation only, and says so.</summary>
        public string? CalibrationFile { get; set; }
    }

    /// <summary>The scale steps the capacity view is calibrated on, as <c>calibration.mjs</c> writes them.</summary>
    public sealed record CapacityCalibration(int Version, DateTimeOffset Created, string? Commit, CapacityCalibration.HostInfo Host, CapacityCalibration.MixInfo Mix,
        CapacityCalibration.ReferenceLoad Reference, CapacityCalibration.KneeInfo Knee, IReadOnlyList<CapacityCalibration.Step> Steps)
    {
        public sealed record HostInfo(double Cores, double MemoryBytes, double? HostCores);

        public sealed record MixInfo(long Rules, long Slos, long Services, double SearchesPerSecond);

        /// <summary>§9's reference load at 1×, per second.</summary>
        public sealed record ReferenceLoad(double Spans, double Points, double Logs);

        /// <summary>The last calibration step every line held, and the first that crossed one (held-out steps aside).</summary>
        public sealed record KneeInfo(double? HeldTo, double? CrossedAt, IReadOnlyList<string> Crossed);

        public sealed record Step(double Multiplier, bool HeldOut, bool Held, IReadOnlyList<string> Crossed, IReadOnlyDictionary<string, Measure> Resources);

        public sealed record Measure(double? Mean, double? Max, double? P95, double? P99, string? Unit);

        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

        public static CapacityCalibration Parse(string json)
        {
            var calibration = JsonSerializer.Deserialize<CapacityCalibration>(json, Json) ?? throw new InvalidDataException("The calibration file is empty.");
            // The deserialiser leaves a missing section null: a bad file, said so, not a crash where it is used.
            if (calibration.Host is null || calibration.Mix is null || calibration.Reference is null || calibration.Knee is null || calibration.Steps is null
                || calibration.Steps.Any(s => s?.Resources is null))
            {
                throw new InvalidDataException("The calibration file lacks host, mix, reference, knee or steps.");
            }
            return calibration;
        }

        /// <summary>
        /// Whether this calibration is for the machine ClickHouse runs on: the same cores, and memory within 10 %. Matched by what ClickHouse
        /// sees, since the steps ran on the scale stack's ClickHouse (with Docker Desktop: the VM's cores and memory, not the host's).
        /// </summary>
        public bool Matches(double? cores, double? memoryBytes)
            => cores is { } c && memoryBytes is { } m && Math.Abs(c - Host.Cores) < 0.5 && Math.Abs(m - Host.MemoryBytes) <= 0.1 * Host.MemoryBytes;
    }

    /// <summary>Reads the calibration file, again whenever it changes; a missing or bad file is no calibration (logged once a change).</summary>
    public sealed class CapacityCalibrationStore(IOptions<CapacityOptions> options, ILogger<CapacityCalibrationStore> logger)
    {
        private (string Path, DateTime Written, CapacityCalibration? Calibration)? _cached;
        private readonly Lock _lock = new();

        public CapacityCalibration? Get()
        {
            if (options.Value.CalibrationFile is not { Length: > 0 } path)
            {
                return null;
            }
            lock (_lock)
            {
                var written = DateTime.MinValue;
                try
                {
                    written = File.GetLastWriteTimeUtc(path);
                    if (_cached is { } c && c.Path == path && c.Written == written)
                    {
                        return c.Calibration;
                    }
                    CapacityCalibration? calibration = null;
                    if (File.Exists(path))
                    {
                        calibration = CapacityCalibration.Parse(File.ReadAllText(path));
                    }
                    else
                    {
                        logger.LogWarning("The capacity calibration {File} doesn't exist: the capacity view shows utilisation only", path);
                    }
                    _cached = (path, written, calibration);
                    return calibration;
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "The capacity calibration {File} can't be read: the capacity view shows utilisation only", path);
                    _cached = (path, written, null);
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// The Overview's "Platform" panel and the capacity view (Phase 11 R8, R9), from the heartbeats' status and this replica's last sample:
    /// never a query of their own. Each figure has a level, <c>ok</c>, <c>warn</c> or <c>bad</c> (green, amber, red), against the scale steps'
    /// lines: with a calibration for this machine, a resource is amber at what the last step that held measured and red at what the step
    /// that crossed measured; without one, at fixed shares of its bound.
    /// </summary>
    public static class PlatformOverview
    {
        public const string Ok = "ok", Warn = "warn", Bad = "bad", Unknown = "unknown";

        public sealed record Overview(DateTimeOffset At, string Status, IReadOnlyList<Worker> Workers, IReadOnlyList<Backlog> Backlogs,
            Ingestion? Ingestion, ClickHouse? ClickHouse, Disk? Disk, Capacity Capacity);

        /// <summary>A worker: its state, last pass, its pass time against its interval (utilisation), and its level.</summary>
        public sealed record Worker(string Name, string State, string Level, DateTimeOffset? LastPassEndedAt, double IntervalSeconds, double? PassSeconds,
            double? Utilisation);

        /// <summary>A backlog's oldest age against its line (discovery's lag past its configured delay).</summary>
        public sealed record Backlog(string Name, string State, string Level, double? AgeSeconds, double LineSeconds);

        public sealed record Ingestion(IReadOnlyList<Signal> Signals, IReadOnlyList<Queue> Queues);

        public sealed record Signal(string Name, double? AcceptedPerSecond, double? RefusedPerSecond, double? DroppedPerSecond, double Refused, double Dropped,
            string Level);

        public sealed record Queue(string Exporter, double Size, double Capacity, double Fill, string Level);

        public sealed record ClickHouse(double? Cores, double? CoresAvailable, string CpuLevel, long RunningQueries, double? InsertsInFlight,
            double? InsertP95Seconds, string InsertLevel, IReadOnlyList<Caller> Reads);

        public sealed record Caller(string Name, long Count, double P95Seconds, string Level);

        /// <summary>
        /// Disk: used and free, what the tables add a day, what they need at their retention in the steady state, and the days until full at
        /// that rate (null when nothing grows, or when the tables reach their retention before the disk is full). <see cref="Estimated"/>: some
        /// table has no whole day yet, so its day is scaled from today's.
        /// </summary>
        public sealed record Disk(double Used, double Free, double Total, double? BytesPerDay, double? NeedAtRetention, double? DaysUntilFull, bool Estimated,
            string Level, IReadOnlyList<DiskTable> Tables);

        public sealed record DiskTable(string Name, double Bytes, double? BytesPerDay, int? RetentionDays, double? NeedAtRetention);

        /// <summary>
        /// The capacity view: <see cref="Label"/> says what it is calibrated on, or that it isn't. The load is a multiple of §9's reference
        /// per signal; the knee is the calibration's bracket: held to <see cref="HeldTo"/>, crossed at <see cref="CrossedAt"/>.
        /// </summary>
        public sealed record Capacity(bool Calibrated, string Label, IReadOnlyList<Load> Load, Mix Mix, double? HeldTo, double? CrossedAt,
            IReadOnlyList<string> Crossed, double? Headroom, IReadOnlyList<Resource> Resources);

        public sealed record Load(string Signal, double? PerSecond, double Reference, double? Multiple);

        public sealed record Mix(long Rules, long Slos, long Services, long? CalibratedRules, long? CalibratedSlos, long? CalibratedServices,
            double? CalibratedSearchesPerSecond);

        /// <summary>
        /// A resource now, and what it measured at the last calibration step that held (<see cref="AtHeld"/>) and at the one that crossed
        /// (<see cref="AtCrossed"/>), in the same measure: its mean over the step (the export queue: its 95th percentile).
        /// </summary>
        public sealed record Resource(string Name, string Unit, double? Now, double? AtHeld, double? AtCrossed, string Level);

        /// <summary>An exporter's queue amber and red shares of its bound; ClickHouse's CPU's of its cores without a calibration for this machine.</summary>
        public const double QueueWarn = 0.5, QueueBad = 0.9, CpuWarn = 0.5, CpuBad = 0.8;

        /// <summary>
        /// The Collector's insert p95 lines, from the final rerun (docs/scale-tests.md): 109 ms at 5×, where every line held; 243 ms at 6×,
        /// where a burst dropped points; 347 ms at 10×, where the export was saturated.
        /// </summary>
        public const double InsertWarnSeconds = 0.2, InsertBadSeconds = 0.35;

        /// <summary>An API search's p95: amber past the §9 API line (500 ms), red past 5 s.</summary>
        public const double SearchWarnSeconds = 0.5, SearchBadSeconds = 5;

        /// <summary>Days until the disk is full: red under a week, amber under a month.</summary>
        public const double DiskBadDays = 7, DiskWarnDays = 30;

        public static Overview Build(PlatformStatus.Report status, Sample? sample, DateTimeOffset localNow, CapacityCalibration? calibration)
        {
            var fresh = sample is not null && localNow - sample.At <= Fresh ? sample : null;
            var clickHouse = fresh?.ClickHouse is { Up: true } ch ? ch : null;
            var collector = fresh?.Collector is { Up: true } co ? co : null;
            var postgres = fresh?.Postgres is { Up: true } pg ? pg : null;
            var calibrated = calibration is not null && clickHouse is not null && calibration.Matches(clickHouse.CoresAvailable, clickHouse.MemoryTotal)
                ? calibration : null;

            var workers = status.Workers.Select(w =>
            {
                var utilisation = w.PassSeconds is { } pass && w.IntervalSeconds > 0 ? pass / w.IntervalSeconds : (double?)null;
                var level = PlatformStatus.BadStates.Contains(w.State) || utilisation >= 1 ? Bad : utilisation >= 0.8 ? Warn : Ok;
                return new Worker(w.Name, w.State, level, w.LastPassEndedAt, w.IntervalSeconds, w.PassSeconds, utilisation);
            }).ToList();
            var backlogs = status.Backlogs.Select(b => new Backlog(b.Name, b.State, b.State switch { "behind" => Bad, "ok" or "off" => Ok, _ => Unknown },
                b.AgeSeconds, b.LineSeconds)).ToList();

            Ingestion? ingestion = collector?.Signals is { } signals
                ? new Ingestion(
                    [.. signals.Select(s => new Signal(s.Signal, s.AcceptedPerSecond, s.RefusedPerSecond, s.DroppedPerSecond, s.Refused, s.Dropped,
                        s.RefusedPerSecond > 0 || s.DroppedPerSecond > 0 ? Bad : s.AcceptedPerSecond is null ? Unknown : Ok))],
                    [.. (collector.Queues ?? []).Select(q =>
                    {
                        var fill = q.Capacity > 0 ? q.Size / q.Capacity : 0;
                        // One exporter's queue, against fixed shares of its own bound: the calibration's queue is the queues' sum.
                        return new Queue(q.Exporter, q.Size, q.Capacity, fill, q.Capacity > 0 ? Level(fill, (QueueWarn, QueueBad)) : Unknown);
                    })])
                : null;

            ClickHouse? clickHouseFigures = null;
            if (clickHouse is not null)
            {
                var insertP95 = clickHouse.Finished.Where(f => f.User == "otel_writer" && f.Kind == "Insert").Select(f => f.P95).Max();
                var cpuLines = Calibrated(calibrated, "clickhouse.cpu", m => m.Mean)
                    ?? (clickHouse.CoresAvailable is { } available and > 0 ? (CpuWarn * available, CpuBad * available) : null);
                var cpuLevel = Level(clickHouse.Cores, cpuLines);
                clickHouseFigures = new ClickHouse(clickHouse.Cores, clickHouse.CoresAvailable, cpuLevel, clickHouse.Running.Values.Sum(), clickHouse.InsertsInFlight,
                    insertP95, insertP95 is { } p ? Level(p, (InsertWarnSeconds, InsertBadSeconds)) : Unknown,
                    [.. (clickHouse.Callers ?? []).Select(c => new Caller(c.Caller, c.Count, c.P95,
                        c.Caller.StartsWith("juice:api:", StringComparison.Ordinal) ? Level(c.P95, (SearchWarnSeconds, SearchBadSeconds)) : Ok))]);
            }

            var disk = clickHouse is null ? null : DiskOf(clickHouse);
            return new Overview(status.At, status.Status, workers, backlogs, ingestion, clickHouseFigures, disk,
                CapacityOf(workers, clickHouse, collector, postgres, calibration, calibrated));
        }

        private static Disk? DiskOf(ClickHouseSample sample)
        {
            if (sample.Disks.FirstOrDefault(d => d.Name == "default") is not { Total: > 0 } disk)
            {
                return null;
            }
            var bytes = sample.Tables.ToDictionary(t => t.Name, t => t.Bytes, StringComparer.Ordinal);
            var tables = (sample.Growth ?? []).Select(g => new DiskTable(g.Name, bytes.GetValueOrDefault(g.Name), g.BytesPerDay, g.RetentionDays,
                g.BytesPerDay is { } day && g.RetentionDays is { } days ? day * days : null)).OrderByDescending(t => t.Bytes).ToList();
            var perDay = tables.Any(t => t.BytesPerDay is not null) ? tables.Sum(t => t.BytesPerDay ?? 0) : (double?)null;
            var need = tables.Any(t => t.NeedAtRetention is not null) ? tables.Sum(t => t.NeedAtRetention ?? 0) : (double?)null;
            // Once a table holds its retention, old partitions go as new ones come: the disk fills only if what the tables still have to grow
            // to reach their retention is more than is free. Without a retention known for some table, the plain rate.
            var toGrow = need is { } n && tables.All(t => t.BytesPerDay is null || t.NeedAtRetention is not null) ? n - tables.Sum(t => t.Bytes) : (double?)null;
            var daysUntilFull = perDay is > 0 && (toGrow is null || toGrow > disk.Free) ? disk.Free / perDay : null;
            var used = disk.Total - disk.Free;
            // Full before retention lets old data go: the steady state needs more than the disk holds.
            var level = daysUntilFull < DiskBadDays ? Bad : daysUntilFull < DiskWarnDays || need > disk.Total ? Warn : Ok;
            return new Disk(used, disk.Free, disk.Total, perDay, need, daysUntilFull, (sample.Growth ?? []).Any(g => g.Estimated), level, tables);
        }

        private static Capacity CapacityOf(List<Worker> workers, ClickHouseSample? clickHouse, CollectorSample? collector, PostgresSample? postgres,
            CapacityCalibration? calibration, CapacityCalibration? calibrated)
        {
            var reference = calibration?.Reference ?? new CapacityCalibration.ReferenceLoad(1000, 10000, 1000);
            double? Rate(string signal) => collector?.Signals?.FirstOrDefault(s => s.Signal == signal)?.AcceptedPerSecond;
            var load = new List<Load>
            {
                LoadOf("spans", Rate("spans"), reference.Spans), LoadOf("points", Rate("points"), reference.Points), LoadOf("logs", Rate("logs"), reference.Logs),
            };
            var mix = new Mix(postgres?.Rules ?? 0, postgres?.Slos ?? 0, postgres?.Services ?? 0, calibration?.Mix.Rules, calibration?.Mix.Slos,
                calibration?.Mix.Services, calibration?.Mix.SearchesPerSecond);

            var resources = new List<Resource>();
            // A worker's line is its interval, whatever the steps measured (a light worker's few milliseconds there would make any pass
            // red): it is judged at fixed shares, and the steps' figures are shown beside it. The others at what the steps measured.
            void Add(string name, string unit, double? now, Func<CapacityCalibration.Measure, double?> measure, (double Warn, double Bad)? fallback,
                bool fixedLines = false)
            {
                var atHeld = calibrated is null ? null : ValueAt(calibrated, calibrated.Knee.HeldTo, name, measure);
                var atCrossed = calibrated is null ? null : ValueAt(calibrated, calibrated.Knee.CrossedAt, name, measure);
                var level = !fixedLines && atHeld is { } w && atCrossed is { } b && b > w ? Level(now, (w, b))
                    : fallback is { } f ? Level(now, f) : now is null ? Unknown : Ok;
                resources.Add(new Resource(name, unit, now, atHeld, atCrossed, level));
            }
            Add("clickhouse.cpu", "cores", clickHouse?.Cores, m => m.Mean,
                clickHouse?.CoresAvailable is { } available and > 0 ? (CpuWarn * available, CpuBad * available) : null);
            Add("clickhouse.inserts_in_flight", "inserts", clickHouse?.InsertsInFlight, m => m.Mean, null);
            var queues = collector?.Queues;
            // The queues' sum over one queue's bound, as the steps measured it (the metrics queue is nearly all of it).
            Add("collector.queue", "of the bound", queues is { Count: > 0 } && queues.Max(q => q.Capacity) is var bound and > 0 ? queues.Sum(q => q.Size) / bound : null,
                m => m.P95, (QueueWarn, QueueBad));
            foreach (var w in workers.Where(w => w.Utilisation is not null))
            {
                Add($"worker.{w.Name}", "of its interval", w.Utilisation, m => m.Mean, (0.8, 1), fixedLines: true);
            }

            var now = load.Max(l => l.Multiple);
            var label = calibration is null
                ? "Not calibrated: no calibration file (Capacity:CalibrationFile). Utilisation only."
                : calibrated is null
                    ? $"Not calibrated for this machine: the calibration is for {calibration.Host.Cores:0} cores and {calibration.Host.MemoryBytes / (1L << 30):0.#} GiB"
                      + (clickHouse?.CoresAvailable is { } c ? $", ClickHouse here has {c:0} cores and {(clickHouse.MemoryTotal ?? 0) / (1L << 30):0.#} GiB" : "")
                      + ". Utilisation only."
                    : $"Calibrated on {calibration.Host.Cores:0} cores and {calibration.Host.MemoryBytes / (1L << 30):0.#} GiB, with {calibration.Mix.Rules} rules, "
                      + $"{calibration.Mix.Slos} SLOs, {calibration.Mix.Services} services and {calibration.Mix.SearchesPerSecond:0.#} searches a second"
                      + $" ({calibration.Created:yyyy-MM-dd}).";
            return new Capacity(calibrated is not null, label, load, mix, calibrated?.Knee.HeldTo, calibrated?.Knee.CrossedAt, calibrated?.Knee.Crossed ?? [],
                calibrated?.Knee.HeldTo is { } heldTo && now is > 0 ? heldTo / now : null, resources);
        }

        private static Load LoadOf(string signal, double? perSecond, double reference)
            => new(signal, perSecond, reference, perSecond is { } r && reference > 0 ? r / reference : null);

        /// <summary>A resource's measure at the calibration step of <paramref name="multiplier"/>.</summary>
        private static double? ValueAt(CapacityCalibration calibration, double? multiplier, string resource, Func<CapacityCalibration.Measure, double?> measure)
            => multiplier is { } m && calibration.Steps.FirstOrDefault(s => !s.HeldOut && s.Multiplier == m) is { } step
               && step.Resources.TryGetValue(resource, out var value) ? measure(value) : null;

        /// <summary>A resource's amber and red lines from the calibration: what it measured where every line held, and where one crossed.</summary>
        private static (double Warn, double Bad)? Calibrated(CapacityCalibration? calibration, string resource, Func<CapacityCalibration.Measure, double?> measure)
            => calibration is not null && ValueAt(calibration, calibration.Knee.HeldTo, resource, measure) is { } warn
               && ValueAt(calibration, calibration.Knee.CrossedAt, resource, measure) is { } bad && bad > warn ? (warn, bad) : null;

        private static string Level(double? value, (double Warn, double Bad)? lines)
            => value is not { } v || lines is not { } l ? Unknown : v >= l.Bad ? Bad : v >= l.Warn ? Warn : Ok;
    }

    /// <summary>The panel's data for <c>/api/v1/platform/overview</c>: the status (cached, <see cref="PlatformStatusService"/>) and this replica's last sample.</summary>
    public sealed class PlatformOverviewService(PlatformStatusService status, PlatformSampler sampler, CapacityCalibrationStore calibration, TimeProvider clock)
    {
        public async Task<PlatformOverview.Overview> GetAsync(CancellationToken cancellationToken)
            => PlatformOverview.Build(await status.GetAsync(cancellationToken), sampler.Latest, clock.GetUtcNow(), calibration.Get());
    }
}
