using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Slos
{
    /// <summary>
    /// Counts each SLO's minutes from ClickHouse into <see cref="SloBucket"/>s (roadmap Phase 9, pre-aggregation): one bounded, tenant-scoped query
    /// per SLO and version segment, grouped by minute, class and latency slot. Minutes are counted only up to the newest telemetry the tenant has
    /// sent, and only minutes it sent some telemetry in, so a stopped pipeline leaves minutes unknown instead of empty. The newest minutes are counted again until they're sealed; a failed
    /// read leaves them as they were and is tried again. One replica at a time.
    /// </summary>
    public sealed class SloCounter(IServiceScopeFactory scopes, ClickHouseTelemetrySource clickHouse, IOptions<SloOptions> options,
        IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<SloCounter> logger) : BackgroundService
    {
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Passes = Meter.CreateCounter<long>("juice.observability.slos.counted", "{pass}",
            "SLO counting passes, by outcome (counted, idle, failed)");

        /// <summary>Codes of the classes the query sorts requests into.</summary>
        internal const int ExcludedHealth = 1, ExcludedCancelled = 2, ExcludedClient = 3, BadRequest = 4, GoodRequest = 5;


        /// <summary>
        /// The request classes and latency slot of one SLO's spans, by minute. Eligibility (Phase 9 plan): health checks are excluded; 408 and 499 are
        /// excluded or bad; other 4xx are good, bad or excluded; 5xx, 429, an error status without an HTTP code, and Juice's failed dispatch are bad.
        /// </summary>
        internal static readonly string CountQuery = $"""
            SELECT minute, cls, slot, count() AS n
            FROM (
                SELECT toStartOfMinute(Timestamp) AS minute,
                       toUInt16OrZero(if(SpanAttributes['http.response.status_code'] != '', SpanAttributes['http.response.status_code'],
                                         SpanAttributes['http.status_code'])) AS code,
                       SpanAttributes['http.route'] AS route,
                       SpanAttributes['url.path'] AS path,
                       arrayExists(p -> route LIKE p OR path LIKE p OR SpanName LIKE p, {"{exclude:Array(String)}"}) AS health,
                       code IN (408, 499) AS cancelled,
                       code >= 400 AND code < 500 AND code NOT IN (408, 429, 499) AS client,
                       code >= 500 OR code = 429 OR (code = 0 AND StatusCode = 'Error') OR SpanAttributes['juice.dispatch.result'] = 'failure' AS failed,
                       multiIf(health, {ExcludedHealth},
                               cancelled AND {"{cancel:UInt8}"} = 0, {ExcludedCancelled},
                               client AND {"{client:UInt8}"} = 2, {ExcludedClient},
                               (failed AND NOT client AND NOT cancelled) OR (cancelled AND {"{cancel:UInt8}"} = 1) OR (client AND {"{client:UInt8}"} = 1), {BadRequest},
                               {GoodRequest}) AS cls,
                       if(cls = {GoodRequest}, arrayFirstIndex(b -> Duration / 1e6 <= b, [{string.Join(", ", SloHistogram.Boundaries.Select(b => $"{b}."))}]), 0) AS slot
                FROM otel.otel_traces
                WHERE {Tenant} AND Timestamp >= {From} AND Timestamp < {To} AND SpanKind IN ('Server', 'Consumer')
                  AND {ServiceMatch}
                  AND (empty({"{ops:Array(String)}"}) OR arrayExists(p -> route LIKE p OR SpanName LIKE p, {"{ops:Array(String)}"}))
            )
            GROUP BY minute, cls, slot
            LIMIT {Limit}
            """;

        /// <summary>
        /// The newest telemetry the SLO's service has sent lately, of any signal (<see cref="ResourceTables"/>): minutes after it can't be told from
        /// silence. Per service, so one service stopping shows even while the rest of the tenant carries on.
        /// </summary>
        internal static readonly string WatermarkQuery = $"""
            SELECT greatest({string.Join(", ", ResourceTables.Select(s => $"(SELECT max({s.Time}) FROM otel.{s.Table} WHERE {Tenant} AND {ServiceMatch} AND {s.Time} >= {From} AND {s.Time} < {To})"))})
            """;

        /// <summary>
        /// The minutes in which the SLO's service sent any telemetry, of any signal (a running service exports its metrics every minute, busy or not).
        /// A minute without any is a gap, the service or the pipeline down, not a quiet minute: it isn't counted, so it stays unknown.
        /// </summary>
        internal static readonly string PresenceQuery = $"""
            SELECT DISTINCT minute FROM (
                {string.Join("\n    UNION ALL\n    ", ResourceTables.Select(s => $"SELECT toStartOfMinute({s.Time}) AS minute FROM otel.{s.Table} WHERE {Tenant} AND {ServiceMatch} AND {s.Time} >= {From} AND {s.Time} < {To}"))})
            LIMIT {Limit}
            """;

        /// <summary>An SLO's service in its environment: what its newest telemetry and its minutes with telemetry are read for.</summary>
        internal sealed record ServiceKey(string Namespace, string Name, string Environment)
        {
            public static ServiceKey Of(SloDefinition definition)
            {
                var (ns, name) = definition.SplitService();
                return new(ns, name, definition.Environment);
            }

            public Dictionary<string, object> Parameters() => new() { ["svc"] = Name, ["ns"] = Namespace, ["env"] = Environment };
        }

        /// <summary>Minutes counted per SLO per pass, halved after a pass that ran out of time and grown back after one that didn't.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _chunks = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var lease = WorkerLease.For(scopes, "slos", logger);
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken)) await PassAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "SLO counting failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public static DateTimeOffset Floor(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

        /// <summary>Counts every active SLO once, and drops buckets past retention. Exposed for tests.</summary>
        public async Task PassAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(ct);
            var now = clock.GetUtcNow();
            var ids = await db.Slos.AsNoTracking().Where(s => s.TenantId == tenantId && !s.Retired).Select(s => s.Id).ToListAsync(ct);
            var versions = (await db.SloVersions.AsNoTracking().Where(v => ids.Contains(v.SloId)).OrderBy(v => v.Version).ToListAsync(ct)).ToLookup(v => v.SloId);
            // Minutes with telemetry, request counts and each service's newest telemetry don't depend on the SLO: read once per pass.
            var cache = new PassCache();
            var lookback = Lookback(options.Value);
            foreach (var id in ids)
            {
                // Each SLO on its own: one that can't be read (a stored definition this build can't parse) doesn't hold back the others.
                try
                {
                    // Watched through the service in effect now (not a later one still waiting for its date) and, for a while after a move, the
                    // one it moved from, so the minutes just before the move are still counted again as their late telemetry arrives.
                    var sloVersions = versions[id].ToList();
                    var services = new[] { now, now - lookback }.Select(t => ServiceKey.Of(SloDefinition.FromJson(VersionAt(sloVersions, t).Definition))).Distinct();
                    DateTimeOffset? newest = null;
                    foreach (var service in services)
                    {
                        if (await WatermarkAsync(service, now, cache, ct) is { } w && (newest is null || w > newest)) newest = w;
                    }
                    if (newest is not { } watermark)
                    {
                        // Nothing from the service lately (or it couldn't be read): its minutes stay unknown until it sends again.
                        Passes.Add(1, new KeyValuePair<string, object?>("outcome", "idle"));
                        continue;
                    }
                    await CountAsync(id, watermark, now, cache, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "SLO {Slo}: can't be counted; the other SLOs carry on", id);
                    Passes.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                }
            }
            // Buckets expire a minute at a time: deleting them once an hour is plenty.
            if (now - _lastExpiry >= TimeSpan.FromHours(1))
            {
                var expired = now - options.Value.Retention;
                await db.SloBuckets.Where(b => b.TenantId == tenantId && b.Minute < expired).ExecuteDeleteAsync(ct);
                _lastExpiry = now;
            }
        }

        private DateTimeOffset _lastExpiry = DateTimeOffset.MinValue;

        /// <summary>What one pass reads from ClickHouse that SLOs can share.</summary>
        private sealed class PassCache
        {
            /// <summary>A service's minutes with telemetry, by service and range.</summary>
            public Dictionary<(ServiceKey, DateTimeOffset, DateTimeOffset), HashSet<DateTimeOffset>> Presence { get; } = [];
            /// <summary>Request counts, by everything the count query is given (two SLOs on the same requests share one read) and range.</summary>
            public Dictionary<(string, DateTimeOffset, DateTimeOffset), Dictionary<DateTimeOffset, MinuteCounts>> Counts { get; } = [];
            /// <summary>Each service's newest telemetry; null when there's none lately or it couldn't be read.</summary>
            public Dictionary<ServiceKey, DateTimeOffset?> Watermarks { get; } = [];
        }

        /// <summary>
        /// How far back a service's newest telemetry is looked for, and how long an SLO still watches the service it moved from: at least the
        /// recount window and a margin, so minutes are recounted for all of <see cref="SloOptions.Lateness"/>.
        /// </summary>
        internal static TimeSpan Lookback(SloOptions options)
        {
            var needed = options.Lateness + TimeSpan.FromMinutes(5);
            return needed > TimeSpan.FromMinutes(15) ? needed : TimeSpan.FromMinutes(15);
        }

        private async Task<DateTimeOffset?> WatermarkAsync(ServiceKey service, DateTimeOffset now, PassCache cache, CancellationToken ct)
        {
            if (cache.Watermarks.TryGetValue(service, out var known)) return known;
            var lookback = Lookback(options.Value);
            DateTimeOffset? newest = null;
            try
            {
                var rows = await clickHouse.QueryAsync(WatermarkQuery, new TelemetryWindow(platform.Value.TenantKey, now - lookback, now + TimeSpan.FromMinutes(1), 1),
                    r => Utc(r.GetValue(0)), ct, service.Parameters());
                // max() over nothing is the epoch.
                newest = rows.Count == 1 && rows[0] > now - lookback ? rows[0] : null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Can't read {Service}'s newest telemetry; its SLO minutes stay unknown until it's back", service);
            }
            return cache.Watermarks[service] = newest;
        }

        /// <summary>
        /// The minutes to count now for an SLO: from where it got to (or the newest unsealed minute, whichever is earlier, but never before the
        /// 30-day window), up to the last minute with telemetry and no further than one pass may go. Empty when there's nothing to count. Minutes
        /// are sealed <see cref="SloOptions.Lateness"/> behind the newest telemetry, not behind the clock, so a lagging pipeline's minutes are
        /// still counted again as the rest of them arrives. <paramref name="maxMinutes"/> narrows a pass after one that ran out of time.
        /// </summary>
        public static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? countedThrough, DateTimeOffset watermark, DateTimeOffset now, SloOptions options,
            int? maxMinutes = null)
        {
            var sealedBefore = Floor(Min(now, watermark) - options.Lateness);
            var windowStart = Floor(now - SloOptions.Window);
            var from = countedThrough is { } through ? (through < sealedBefore ? through : sealedBefore) : windowStart;
            if (from < windowStart) from = windowStart;
            var limit = Floor(Min(now - TimeSpan.FromSeconds(10), watermark + TimeSpan.FromMinutes(1)));
            var to = Min(limit, from + TimeSpan.FromMinutes(maxMinutes ?? options.MaxMinutesPerPass));
            return (from, to > from ? to : from);
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

        /// <summary>
        /// A version withdrawn before it took effect, because the file changed again while it waited for its date: it never applies.
        /// </summary>
        public static bool Withdrawn(SloVersion version) => version.WithdrawnAt is not null;

        /// <summary>
        /// The version a minute is counted under: of the versions not withdrawn (ordered by number, which orders their dates too), the newest
        /// effective then, or the first for history before it. <paramref name="versions"/> is ordered by number.
        /// </summary>
        public static SloVersion VersionAt(IReadOnlyList<SloVersion> versions, DateTimeOffset minute)
        {
            var live = versions.Where(v => !Withdrawn(v)).ToList();
            return live.LastOrDefault(v => v.EffectiveFrom <= minute) ?? live[0];
        }

        /// <summary>
        /// The smallest a pass may narrow to after running out of time: a pass starts <see cref="SloOptions.Lateness"/> back to count those minutes
        /// again, so it must reach past them to the newest ones, or they'd never be counted.
        /// </summary>
        internal static int MinimumChunk(SloOptions options) => Math.Max(10, (int)Math.Ceiling(options.Lateness.TotalMinutes) + 5);

        private async Task CountAsync(Guid sloId, DateTimeOffset watermark, DateTimeOffset now, PassCache cache, CancellationToken stoppingToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(options.Value.QueryTimeout);
            var ct = timeout.Token;
            var chunk = _chunks.GetValueOrDefault(sloId, options.Value.MaxMinutesPerPass);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var slo = await db.Slos.SingleAsync(s => s.Id == sloId, ct);
                var versions = await db.SloVersions.AsNoTracking().Where(v => v.SloId == sloId).OrderBy(v => v.Version).ToListAsync(ct);
                var (from, to) = Range(slo.CountedThrough, watermark, now, options.Value, chunk);
                if (to <= from)
                {
                    Passes.Add(1, new KeyValuePair<string, object?>("outcome", "idle"));
                    return;
                }
                var buckets = new List<SloBucket>();
                // One stretch of minutes per version: each is counted, and checked for gaps, against that version's own service.
                var start = from;
                while (start < to)
                {
                    var version = VersionAt(versions, start);
                    var next = versions.Where(v => !Withdrawn(v) && v.EffectiveFrom > start && v.EffectiveFrom < to).Select(v => Floor(v.EffectiveFrom) == v.EffectiveFrom ? v.EffectiveFrom : Floor(v.EffectiveFrom) + TimeSpan.FromMinutes(1))
                        .Where(t => t > start).DefaultIfEmpty(to).Min();
                    var definition = SloDefinition.FromJson(version.Definition);
                    var service = ServiceKey.Of(definition);
                    if (!cache.Presence.TryGetValue((service, start, next), out var present))
                        cache.Presence[(service, start, next)] = present = await PresenceAsync(service, start, next, ct);
                    if (present.Count == 0)
                    {
                        start = next;
                        continue;
                    }
                    var counts = await ReadAsync(definition, start, next, cache, ct);
                    var kind = SliKinds.Find(definition.Sli)!;
                    for (var minute = start; minute < next; minute += TimeSpan.FromMinutes(1))
                    {
                        // No telemetry at all that minute: a gap, left unknown rather than counted as quiet.
                        if (!present.Contains(minute)) continue;
                        var c = counts.GetValueOrDefault(minute) ?? MinuteCounts.Empty;
                        var (total, good) = kind.Score(c, definition);
                        buckets.Add(new SloBucket
                        {
                            TenantId = slo.TenantId, SloId = slo.Id, Minute = minute, Version = version.Version, Total = total, Good = good,
                            Eligible = c.Eligible, Bad = c.Bad, ExcludedHealth = c.ExcludedHealth, ExcludedCancelled = c.ExcludedCancelled,
                            ExcludedClientErrors = c.ExcludedClientErrors, Histogram = c.Histogram, CountedAt = now,
                        });
                    }
                    start = next;
                }
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SloBuckets.Where(b => b.SloId == slo.Id && b.Minute >= from && b.Minute < to).ExecuteDeleteAsync(ct);
                db.SloBuckets.AddRange(buckets);
                if (slo.CountedThrough is null || to > slo.CountedThrough) slo.CountedThrough = to;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                Passes.Add(1, new KeyValuePair<string, object?>("outcome", "counted"));
                // Grow back towards the full stretch once a pass fits.
                _chunks[sloId] = Math.Min(options.Value.MaxMinutesPerPass, chunk * 2);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The minutes stay as they were: unknown if never counted. Tried again next pass, over half the stretch if this one ran out of
                // time, so a stretch too big to count in time doesn't hold the SLO back for good.
                var timedOut = timeout.IsCancellationRequested;
                if (timedOut) _chunks[sloId] = NextChunk(chunk, options.Value);
                logger.LogWarning(ex, "SLO {Slo}: counting failed{Narrowed}; its newest minutes stay unknown until a later pass", sloId,
                    timedOut ? $", out of time: next pass counts {NextChunk(chunk, options.Value)} minutes" : "");
                Passes.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            }
        }

        /// <summary>The stretch to try after one that ran out of time: half of it, but not below <see cref="MinimumChunk"/>.</summary>
        internal static int NextChunk(int chunk, SloOptions options) => Math.Max(MinimumChunk(options), chunk / 2);

        private async Task<HashSet<DateTimeOffset>> PresenceAsync(ServiceKey service, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var minutes = (int)(to - from).TotalMinutes;
            var rows = await clickHouse.QueryAsync(PresenceQuery, new TelemetryWindow(platform.Value.TenantKey, from, to, minutes + 10), r => Utc(r.GetValue(0)), ct,
                service.Parameters());
            return [.. rows];
        }

        /// <summary>A stretch of minutes counted for one definition, by minute.</summary>
        private async Task<Dictionary<DateTimeOffset, MinuteCounts>> ReadAsync(SloDefinition definition, DateTimeOffset from, DateTimeOffset to, PassCache cache,
            CancellationToken ct)
        {
            var (ns, svc) = definition.SplitService();
            var parameters = new Dictionary<string, object>
            {
                ["svc"] = svc, ["ns"] = ns, ["env"] = definition.Environment,
                ["exclude"] = definition.Exclusions.Select(SloDefinition.ToLike).ToArray(),
                ["ops"] = definition.Operations.Select(SloDefinition.ToLike).ToArray(),
                ["client"] = (byte)definition.ClientErrors, ["cancel"] = (byte)definition.Cancellations,
            };
            // The query's inputs decide its rows: availability and latency of the same requests read them once.
            var key = (System.Text.Json.JsonSerializer.Serialize(parameters), from, to);
            if (cache.Counts.TryGetValue(key, out var cached)) return cached;
            var minutes = (int)((to - from).TotalMinutes);
            var rows = await clickHouse.QueryAsync(CountQuery, new TelemetryWindow(platform.Value.TenantKey, from, to, minutes * (SloHistogram.Slots + 4) + 10),
                r => (Minute: Utc(r.GetValue(0)), Class: Convert.ToInt32(r.GetValue(1)), Slot: Convert.ToInt32(r.GetValue(2)), Count: Convert.ToInt64(r.GetValue(3))), ct, parameters);
            return cache.Counts[key] = Fold(rows);
        }

        /// <summary>The query's rows as each minute's counts. A latency slot of 0 is slower than the last boundary.</summary>
        internal static Dictionary<DateTimeOffset, MinuteCounts> Fold(IEnumerable<(DateTimeOffset Minute, int Class, int Slot, long Count)> rows)
        {
            var result = new Dictionary<DateTimeOffset, MinuteCounts>();
            foreach (var group in rows.GroupBy(r => r.Minute))
            {
                long eligible = 0, bad = 0, health = 0, cancelled = 0, client = 0;
                var histogram = new long[SloHistogram.Slots];
                foreach (var (_, cls, slot, count) in group)
                {
                    switch (cls)
                    {
                        case ExcludedHealth: health += count; break;
                        case ExcludedCancelled: cancelled += count; break;
                        case ExcludedClient: client += count; break;
                        case BadRequest: eligible += count; bad += count; break;
                        case GoodRequest:
                            eligible += count;
                            histogram[slot >= 1 && slot <= SloHistogram.Boundaries.Count ? slot - 1 : SloHistogram.Slots - 1] += count;
                            break;
                    }
                }
                result[group.Key] = new MinuteCounts(eligible, bad, health, cancelled, client, histogram.Any(h => h > 0) ? histogram : []);
            }
            return result;
        }
    }
}
