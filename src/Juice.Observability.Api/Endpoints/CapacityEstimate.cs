namespace Juice.Observability.Api.Endpoints
{
    /// <summary>What one instance did during one time bucket. Metrics a host doesn't report are <c>null</c>.</summary>
    public sealed record CapacitySample(string Instance, DateTimeOffset At, double PerSecond, double InFlight, double? Cores,
        double? MemoryBytes, double? Queue, double MessagesPerSecond = 0, double MessagesInFlight = 0);

    /// <summary>
    /// What a service was measured to need, per instance, for the Console's capacity calculator. A unit of work is a request or
    /// a message. <c>CpuModel</c> says how to read the CPU figures: <c>fitted</c> separates idle CPU from CPU per unit, <c>average</c>
    /// is CPU divided by work in the busiest samples (it includes idle CPU, so it can read high), <c>none</c> has no CPU data. <c>MemoryModel</c> is
    /// <c>fitted</c> (idle plus growth per unit in flight), <c>peak</c> (only the highest seen) or <c>none</c>.
    /// <c>PeakMessagesInFlight</c> near 1 means a consumer was busy nearly all the time: if it handles one message at a time, it was
    /// at its limit whatever CPU says, and <c>PeakMessagesPerSecond</c> is about what one instance can do.
    /// </summary>
    public sealed record CapacityDto(int StepSeconds, int Samples, int Instances, bool Truncated, string RequestSource,
        IReadOnlyList<string> Hosts, int? CoresPerInstance, double PeakPerSecond, double? ResponseMs, double? ResponseLowLoadMs,
        double? ResponseHighLoadMs, double? PeakQueue, string CpuModel, double? CpuSecondsPerUnit, double? IdleCores, double? PeakCores,
        string MemoryModel, double? IdleMemoryBytes, double? MemoryBytesPerInFlight, double? PeakMemoryBytes,
        double PeakMessagesPerSecond, double PeakMessagesInFlight, OutboxBacklogDto? Outbox = null);

    /// <summary>The messages a service wrote to its outbox and delivered in one time bucket.</summary>
    public sealed record OutboxPoint(DateTimeOffset At, double Created, double Delivered);

    /// <summary>
    /// Whether a service's outbox keeps up: CPU and requests can look healthy while messages wait. The rates are over the minutes
    /// with outbox activity, so they don't fall as the window grows. <c>Undelivered</c> is created
    /// minus delivered in the window, so it includes messages of rolled-back transactions, which are never delivered.
    /// <c>RecentDelaySeconds</c> is the p95 from creating a message to delivering it, in the newest minutes, or <c>null</c> when none
    /// was delivered then. Rollbacks don't raise the delay, which is why <c>FallingBehind</c> needs both.
    /// </summary>
    public sealed record OutboxBacklogDto(double CreatedPerSecond, double DeliveredPerSecond, double Undelivered,
        double? RecentDelaySeconds, bool FallingBehind);

    /// <summary>Turns samples into the measurements of <see cref="CapacityDto"/>. A straight line is only trusted when the data supports one.</summary>
    public static class CapacityEstimate
    {
        /// <summary>Fewer samples than this can't support a line.</summary>
        public const int MinFitSamples = 10;
        /// <summary>A line explaining less than this of the variation is noise, such as CPU that doesn't follow load.</summary>
        public const double MinFitQuality = 0.5;
        /// <summary>Load must vary at least this much: without it, idle and per-unit cost can't be told apart.</summary>
        public const double MinLoadSpread = 2;
        private const int MinQuartileSamples = 8;

        public static CapacityDto From(IReadOnlyList<CapacitySample> samples, int stepSeconds, bool truncated, string requestSource,
            IReadOnlyList<string> hosts, int? coresPerInstance)
        {
            var active = samples.Where(s => s.PerSecond > 0).ToList();
            var totalWork = active.Sum(s => s.PerSecond);
            var (low, high) = ResponseByLoad(active);

            var withCores = samples.Where(s => s.Cores is not null).ToList();
            var cpu = Fit(withCores.Select(s => (s.PerSecond, s.Cores!.Value)).ToList());
            // Without a line, the cost when busiest: cost per unit can rise with load, and sizing is about high load.
            var coresWhileActive = Busiest(active.Where(s => s.Cores is not null).ToList());
            var workWithCores = coresWhileActive.Sum(s => s.PerSecond);

            var withMemory = samples.Where(s => s.MemoryBytes is not null).ToList();
            var memory = Fit(withMemory.Select(s => (s.InFlight, s.MemoryBytes!.Value)).ToList());
            var queues = samples.Where(s => s.Queue is not null).ToList();

            return new CapacityDto(stepSeconds, samples.Count, samples.Select(s => s.Instance).Distinct().Count(), truncated, requestSource,
                hosts, coresPerInstance,
                samples.Count == 0 ? 0 : samples.Max(s => s.PerSecond),
                totalWork > 0 ? active.Sum(s => s.InFlight) / totalWork * 1000 : null,
                low, high,
                queues.Count == 0 ? null : queues.Max(s => s.Queue!.Value),
                cpu is not null ? "fitted" : workWithCores > 0 ? "average" : "none",
                cpu?.Slope ?? (workWithCores > 0 ? coresWhileActive.Sum(s => s.Cores!.Value) / workWithCores : null),
                cpu?.Intercept,
                withCores.Count == 0 ? null : withCores.Max(s => s.Cores!.Value),
                memory is not null ? "fitted" : withMemory.Count > 0 ? "peak" : "none",
                memory?.Intercept,
                memory?.Slope,
                withMemory.Count == 0 ? null : withMemory.Max(s => s.MemoryBytes!.Value),
                samples.Count == 0 ? 0 : samples.Max(s => s.MessagesPerSecond),
                samples.Count == 0 ? 0 : samples.Max(s => s.MessagesInFlight));
        }

        /// <summary>Fewer undelivered messages than this are ones in transit at the window's edges.</summary>
        public const double MinUndelivered = 10;
        /// <summary>A smaller share of the messages created is the same edge effect, at a higher rate.</summary>
        public const double MinUndeliveredShare = 0.05;
        /// <summary>A delivery taking longer than this is waiting in a backlog, not for the worker's next poll.</summary>
        public const double BehindDelaySeconds = 5;

        /// <summary>The outbox figures of a service, or <c>null</c> when it created and delivered nothing in the window.</summary>
        public static OutboxBacklogDto? Backlog(IReadOnlyList<OutboxPoint> points, int stepSeconds, double? recentDelaySeconds)
        {
            var (created, delivered) = (points.Sum(p => p.Created), points.Sum(p => p.Delivered));
            if (created + delivered <= 0) return null;
            var seconds = points.Count * (double)stepSeconds;
            // More delivered than created is a backlog from before the window, or a message delivered to several routes.
            var undelivered = Math.Max(0, created - delivered);
            var growing = undelivered >= Math.Max(MinUndelivered, MinUndeliveredShare * created);
            return new OutboxBacklogDto(created / seconds, delivered / seconds, undelivered, recentDelaySeconds,
                growing && (recentDelaySeconds is null || recentDelaySeconds >= BehindDelaySeconds));
        }

        /// <summary>The busiest quarter of the samples, or all of them when there are too few for a quarter to mean anything.</summary>
        private static List<CapacitySample> Busiest(List<CapacitySample> samples)
            => samples.Count < MinQuartileSamples ? samples : samples.OrderBy(s => s.PerSecond).TakeLast(samples.Count / 4).ToList();

        /// <summary>Least squares, or <c>null</c> when the line wouldn't mean anything: too few samples, too little spread, or a poor or impossible fit.</summary>
        internal static (double Slope, double Intercept)? Fit(IReadOnlyList<(double X, double Y)> points)
        {
            if (points.Count < MinFitSamples) return null;
            var (minX, maxX) = (points.Min(p => p.X), points.Max(p => p.X));
            if (maxX <= 0 || maxX < MinLoadSpread * minX) return null;

            var (meanX, meanY) = (points.Average(p => p.X), points.Average(p => p.Y));
            var xx = points.Sum(p => (p.X - meanX) * (p.X - meanX));
            var xy = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
            var yy = points.Sum(p => (p.Y - meanY) * (p.Y - meanY));
            if (xx <= 0 || yy <= 0) return null;

            var slope = xy / xx;
            var intercept = meanY - slope * meanX;
            var quality = xy * xy / (xx * yy);
            // Cost that falls with load, or a negative idle cost, is not a description of a service.
            return slope <= 0 || intercept < 0 || quality < MinFitQuality ? null : (slope, intercept);
        }

        /// <summary>Average response time in the quietest and busiest quarters of the samples, when load varied enough to compare them.</summary>
        private static (double? Low, double? High) ResponseByLoad(List<CapacitySample> active)
        {
            if (active.Count < MinQuartileSamples) return (null, null);
            var sorted = active.OrderBy(s => s.PerSecond).ToList();
            var quarter = sorted.Count / 4;
            var (quiet, busy) = (sorted.Take(quarter).ToList(), sorted.TakeLast(quarter).ToList());
            if (busy.Average(s => s.PerSecond) < MinLoadSpread * quiet.Average(s => s.PerSecond)) return (null, null);
            static double Ms(List<CapacitySample> part) => part.Sum(s => s.InFlight) / part.Sum(s => s.PerSecond) * 1000;
            return (Ms(quiet), Ms(busy));
        }
    }
}
