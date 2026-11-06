namespace Juice.Observability.Api.Endpoints
{
    /// <summary>What one service did in one time step of a load run. <c>Backlog</c> is outbox messages created minus delivered so far.</summary>
    public sealed record LoadRunPointDto(DateTimeOffset At, double PerSecond, double ErrorRate, double? P95Ms, double? Queue,
        double ConsumerBusy, double Created, double Delivered, double Backlog);

    /// <summary>
    /// Where a service started to struggle. <c>Reason</c> is <c>latency</c> (p95 against <c>Baseline</c>), <c>consumer-busy</c> (share of
    /// time an instance spent handling messages), <c>queue</c> (thread pool), <c>errors</c> (error rate) or <c>backlog</c> (outbox messages).
    /// <c>PerSecond</c> is the highest load reached by then: a struggling service's own rate can already have fallen in that step.
    /// </summary>
    public sealed record LoadRunLimitDto(string Reason, DateTimeOffset At, double PerSecond, double Value, double? Baseline);

    public sealed record LoadRunServiceDto(Guid? ServiceId, string Name, IReadOnlyList<LoadRunPointDto> Points, double PeakPerSecond,
        double? P95AtPeakMs, double ErrorRateAtPeak, double? BaselineP95Ms, LoadRunLimitDto? Limit);

    public sealed record LoadRunResultsDto(LoadRunDto Run, int StepSeconds, IReadOnlyList<LoadRunServiceDto> Services, bool Truncated);

    /// <summary>
    /// Finds the first time step from which a service was struggling. One step can be a blip, so a condition has to hold in two steps
    /// running. The quietest quarter of the run never counts as a limit, and errors are judged against it. The latency baseline is the
    /// fastest step of the quieter half: a cold start is slow and quiet too, and must not raise the bar for a real limit.
    /// </summary>
    public static class LoadRunAnalysis
    {
        /// <summary>Fewer active steps than this can't separate a quiet part from a busy one.</summary>
        public const int MinActivePoints = 4;
        public const double LatencyFactor = 2;
        /// <summary>Doubling a few milliseconds isn't a problem anyone would feel.</summary>
        public const double MinLatencyRiseMs = 50;
        public const double BusyConsumer = 0.9;
        public const double NotableQueue = 10;
        public const double NotableErrorRate = 0.05;
        public const double MinErrorRise = 0.04;
        public const double MinBacklog = 10;
        public const double MinBacklogShare = 0.05;

        public static LoadRunServiceDto Analyze(Guid? serviceId, string name, IReadOnlyList<LoadRunPointDto> points)
        {
            var active = points.Where(p => p.PerSecond > 0).ToList();
            var peak = active.Count == 0 ? null : active.MaxBy(p => p.PerSecond);
            var quiet = active.Count < MinActivePoints ? [] : active.OrderBy(p => p.PerSecond).Take(Math.Max(1, active.Count / 4)).ToList();
            var quietLatency = active.Count < MinActivePoints ? [] : active.OrderBy(p => p.PerSecond).Take(active.Count / 2)
                .Where(p => p.P95Ms is not null).Select(p => p.P95Ms!.Value).ToList();
            double? baseline = quietLatency.Count == 0 ? null : quietLatency.Min();
            var quietErrors = quiet.Count == 0 ? 0 : quiet.Average(p => p.ErrorRate);
            var isQuiet = quiet.Select(p => p.At).ToHashSet();

            return new LoadRunServiceDto(serviceId, name, points, peak?.PerSecond ?? 0, peak?.P95Ms, peak?.ErrorRate ?? 0, baseline,
                FindLimit(points, baseline, quietErrors, isQuiet));
        }

        private static LoadRunLimitDto? FindLimit(IReadOnlyList<LoadRunPointDto> points, double? baseline, double quietErrors, HashSet<DateTimeOffset> quiet)
        {
            var created = 0d;
            var backlogBefore = 0d;
            var reached = 0d;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                var (now, next) = (points[i], points[i + 1]);
                created += now.Created;
                reached = Math.Max(reached, now.PerSecond);
                var everCreated = created + next.Created;

                if (now.ConsumerBusy >= BusyConsumer && next.ConsumerBusy >= BusyConsumer)
                {
                    return new LoadRunLimitDto("consumer-busy", now.At, reached, now.ConsumerBusy, null);
                }
                if (baseline is { } normal && !quiet.Contains(now.At) && Slow(now, normal) && Slow(next, normal))
                {
                    return new LoadRunLimitDto("latency", now.At, reached, now.P95Ms!.Value, normal);
                }
                if (now.Queue >= NotableQueue && next.Queue >= NotableQueue)
                {
                    return new LoadRunLimitDto("queue", now.At, reached, now.Queue.Value, null);
                }
                if (!quiet.Contains(now.At) && Failing(now, quietErrors) && Failing(next, quietErrors))
                {
                    return new LoadRunLimitDto("errors", now.At, reached, now.ErrorRate, quietErrors);
                }
                // Growing, and more than the messages in transit at any moment.
                if (now.Backlog > backlogBefore && next.Backlog > now.Backlog
                    && now.Backlog >= Math.Max(MinBacklog, MinBacklogShare * everCreated))
                {
                    return new LoadRunLimitDto("backlog", now.At, reached, now.Backlog, null);
                }
                backlogBefore = now.Backlog;
            }
            return null;
        }

        private static bool Slow(LoadRunPointDto point, double baseline)
            => point.P95Ms is { } p95 && p95 >= LatencyFactor * baseline && p95 - baseline >= MinLatencyRiseMs;

        private static bool Failing(LoadRunPointDto point, double quietErrors)
            => point.ErrorRate >= NotableErrorRate && point.ErrorRate - quietErrors >= MinErrorRise;
    }
}
