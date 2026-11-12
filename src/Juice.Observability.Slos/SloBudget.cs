using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Slos
{
    /// <summary>States of an SLO, or of one of its windows.</summary>
    public static class SloStates
    {
        /// <summary>Within budget, with less than <see cref="SloBudget.AtRiskShare"/> of it used.</summary>
        public const string Met = "Met";
        public const string AtRisk = "AtRisk";
        public const string Exhausted = "Exhausted";
        /// <summary>Too few events to say anything.</summary>
        public const string InsufficientData = "InsufficientData";
        /// <summary>Too many of the window's minutes weren't read.</summary>
        public const string Unknown = "Unknown";
        /// <summary>A burn window with enough known data to have a rate.</summary>
        public const string Known = "Known";
    }

    /// <summary>One version's sums over a window: its SLI events, and how many of the window's minutes were counted.</summary>
    public sealed record VersionSums(int Version, double Target, long Total, long Good, long Minutes);

    /// <param name="Name">How the window is called: <c>5m</c>, <c>30m</c>, <c>1h</c>, <c>6h</c>, <c>3d</c>, <c>30d</c>.</param>
    /// <param name="Length">How long it is.</param>
    /// <param name="Total">SLI events in it.</param>
    /// <param name="Good">Good ones.</param>
    /// <param name="Bad">The rest.</param>
    /// <param name="Sli">Good ÷ total, when there's enough data.</param>
    /// <param name="BurnRate">How fast the budget burns: 1 spends it exactly over 30 days. Null when the window is unknown or has too little data.</param>
    /// <param name="Coverage">The share of its minutes that were counted.</param>
    /// <param name="State"><see cref="SloStates.Known"/>, <see cref="SloStates.InsufficientData"/> or <see cref="SloStates.Unknown"/>.</param>
    /// <param name="Allowed">Bad events the budget allows of these: each version's events at its own target.</param>
    public sealed record WindowStatus(string Name, TimeSpan Length, long Total, long Good, long Bad, double? Sli, double? BurnRate, double Coverage, string State,
        double Allowed);

    /// <param name="State">One of <see cref="SloStates"/> but Known.</param>
    /// <param name="BudgetUsed">The share of the 30-day budget the bad events used; above 1 when it's overspent. Null when the state says nothing.</param>
    /// <param name="Budget">The 30-day window.</param>
    /// <param name="Windows">The burn windows, shortest first.</param>
    /// <param name="CountedSince">The SLO's first counted minute: windows expect minutes from then on. Null when nothing is counted yet.</param>
    public sealed record SloStatus(string State, double? BudgetUsed, WindowStatus Budget, IReadOnlyList<WindowStatus> Windows, DateTimeOffset? CountedSince = null);

    /// <summary>
    /// Error budgets and burn rates (roadmap Phase 9): the budget is (1 − target) of the 30 days' events, each version's events at its own target, so a
    /// changed target doesn't rewrite the past. Missing data is never good: too many unknown minutes is <c>Unknown</c>, too few events
    /// <c>InsufficientData</c>, and neither has a budget or a rate.
    /// </summary>
    public static class SloBudget
    {
        public const double AtRiskShare = 0.75;

        public static readonly IReadOnlyList<(string Name, TimeSpan Length)> BurnWindows =
        [
            ("5m", TimeSpan.FromMinutes(5)), ("30m", TimeSpan.FromMinutes(30)), ("1h", TimeSpan.FromHours(1)),
            ("6h", TimeSpan.FromHours(6)), ("3d", TimeSpan.FromDays(3)),
        ];

        /// <summary>One window's status from its versions' sums.</summary>
        /// <param name="name">How the window is called.</param>
        /// <param name="length">How long it is.</param>
        /// <param name="sums">Each version's sums over it.</param>
        /// <param name="minimumEvents">Fewer events than this is <see cref="SloStates.InsufficientData"/>.</param>
        /// <param name="unknownTolerance">More of the expected minutes missing than this share is <see cref="SloStates.Unknown"/>.</param>
        /// <param name="expectedMinutes">
        /// The minutes the window should have counted: its length, or fewer when the SLO's telemetry started inside it. Any of them missing is a gap.
        /// </param>
        public static WindowStatus Window(string name, TimeSpan length, IReadOnlyList<VersionSums> sums, long minimumEvents, double unknownTolerance,
            long? expectedMinutes = null)
        {
            var total = sums.Sum(s => s.Total);
            var good = sums.Sum(s => s.Good);
            var expected = expectedMinutes ?? (long)length.TotalMinutes;
            var coverage = expected == 0 ? 0 : Math.Min(1, (double)sums.Sum(s => s.Minutes) / expected);
            // What the budget allows of these events: each version's at its own target.
            var allowed = sums.Sum(s => s.Total * (1 - s.Target / 100));
            var state = 1 - coverage > unknownTolerance + 1e-9 ? SloStates.Unknown : total < minimumEvents ? SloStates.InsufficientData : SloStates.Known;
            var known = state == SloStates.Known;
            return new WindowStatus(name, length, total, good, total - good, known ? (double)good / total : null,
                known && allowed > 0 ? (total - good) / allowed : null, coverage, state, allowed);
        }

        public static SloStatus Status(WindowStatus budget, IReadOnlyList<WindowStatus> burns)
        {
            if (budget.State != SloStates.Known) return new SloStatus(budget.State, null, budget, burns);
            // Over the whole window, the burn rate is the share of the budget used.
            var used = budget.BurnRate ?? 0;
            var state = used >= 1 ? SloStates.Exhausted : used >= AtRiskShare ? SloStates.AtRisk : SloStates.Met;
            return new SloStatus(state, used, budget, burns);
        }
    }

    /// <summary>Hourly and daily totals of the minute buckets (<see cref="SloHour"/>, <see cref="SloDay"/>).</summary>
    public static class SloDays
    {
        /// <summary>Midnight UTC of the day <paramref name="t"/> is in.</summary>
        public static DateTimeOffset Day(DateTimeOffset t) => new(t.UtcDateTime.Date, TimeSpan.Zero);

        /// <summary>The first midnight at or after <paramref name="t"/>, in UTC.</summary>
        public static DateTimeOffset NextDay(DateTimeOffset t) => Day(t) == t ? Day(t) : Day(t) + TimeSpan.FromDays(1);

        /// <summary>The start of the UTC hour <paramref name="t"/> is in.</summary>
        public static DateTimeOffset Hour(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);

        /// <summary>The first whole hour at or after <paramref name="t"/>, in UTC.</summary>
        public static DateTimeOffset NextHour(DateTimeOffset t) => Hour(t) == t ? Hour(t) : Hour(t) + TimeSpan.FromHours(1);

        private const string Columns = "total, good, minutes, eligible, bad, excluded_health, excluded_cancelled, excluded_client_errors";
        private const string Updates = "total = excluded.total, good = excluded.good, minutes = excluded.minutes, eligible = excluded.eligible, bad = excluded.bad, "
            + "excluded_health = excluded.excluded_health, excluded_cancelled = excluded.excluded_cancelled, excluded_client_errors = excluded.excluded_client_errors";

        /// <summary>
        /// Hours from their minutes, then days from their hours, for one SLO, as one batch. The <c>SloRollups</c> migration's backfill sums the same way.
        /// Upserts: two at once over the same hours (the counter, and something seeding history) can't collide, the later sum replaces the earlier.
        /// </summary>
        private const string Resum = $"""
            DELETE FROM slo_hours WHERE slo_id = {"{0}"} AND hour >= {"{1}"} AND hour < {"{2}"};
            INSERT INTO slo_hours (tenant_id, slo_id, hour, version, {Columns})
            SELECT tenant_id, slo_id, date_trunc('hour', minute, 'UTC'), version, sum(total), sum(good), count(*), sum(eligible), sum(bad),
                   sum(excluded_health), sum(excluded_cancelled), sum(excluded_client_errors)
            FROM slo_buckets WHERE slo_id = {"{0}"} AND minute >= {"{1}"} AND minute < {"{2}"}
            GROUP BY tenant_id, slo_id, date_trunc('hour', minute, 'UTC'), version
            ON CONFLICT (slo_id, hour, version) DO UPDATE SET {Updates};
            DELETE FROM slo_days WHERE slo_id = {"{0}"} AND day >= {"{3}"} AND day < {"{4}"};
            INSERT INTO slo_days (tenant_id, slo_id, day, version, {Columns})
            SELECT tenant_id, slo_id, date_trunc('day', hour, 'UTC'), version, sum(total), sum(good), sum(minutes), sum(eligible), sum(bad),
                   sum(excluded_health), sum(excluded_cancelled), sum(excluded_client_errors)
            FROM slo_hours WHERE slo_id = {"{0}"} AND hour >= {"{3}"} AND hour < {"{4}"}
            GROUP BY tenant_id, slo_id, date_trunc('day', hour, 'UTC'), version
            ON CONFLICT (slo_id, day, version) DO UPDATE SET {Updates};
            """;

        /// <summary>
        /// Sums again every hour and day <c>[from, to)</c> touches, for one SLO: its hours from its buckets, its days from its hours (24 rows, not
        /// 1,440). In the caller's transaction, after its buckets are saved.
        /// </summary>
        public static async Task RecomputeAsync(PlatformDbContext db, Guid sloId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            if (to <= from) return;
            await db.Database.ExecuteSqlRawAsync(Resum, [sloId, Hour(from), NextHour(to), Day(from), NextDay(to)], ct);
        }

        /// <summary>
        /// Brings an SLO's totals level with its buckets for minutes counted without them: by a build from before the totals, still holding the
        /// counter's lease during a rolling upgrade, or before this replica took the lease back. From <paramref name="lookback"/> before the last
        /// summed hour, since such a build also counts its newest minutes again (as far back as the counter's lookback). Cheap when there's
        /// nothing to catch up: an hour or so again.
        /// </summary>
        public static async Task CatchUpAsync(PlatformDbContext db, Guid sloId, DateTimeOffset? countedThrough, TimeSpan lookback, CancellationToken ct)
        {
            if (countedThrough is not { } to) return;
            var lastHour = await db.SloHours.Where(h => h.SloId == sloId).MaxAsync(h => (DateTimeOffset?)h.Hour, ct);
            var from = lastHour is { } last ? last - lookback : await db.SloBuckets.Where(b => b.SloId == sloId).MinAsync(b => (DateTimeOffset?)b.Minute, ct);
            if (from is { } start && start < to)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await RecomputeAsync(db, sloId, start, to, ct);
                await transaction.CommitAsync(ct);
            }
        }
    }

    /// <summary>Reads an SLO's status and history from its buckets.</summary>
    public static class SloReader
    {
        /// <summary>
        /// Windows end two minutes before now: the newest minute is still being counted, so ending at now would always look partly unknown. A gap
        /// older than that is real.
        /// </summary>
        public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

        public static async Task<SloStatus> StatusAsync(PlatformDbContext db, Slo slo, IReadOnlyList<SloVersion> versions, SloOptions options, DateTimeOffset now,
            CancellationToken ct)
            => (await StatusesAsync(db, [slo.Id], _ => versions, options, now, ct))[slo.Id];

        /// <summary>
        /// Several SLOs' statuses in four queries, whatever their number. <paramref name="burnOnly"/> reads only the burn windows' 3 days: the 30-day
        /// budget it returns then isn't the SLO's, and isn't to be read. <paramref name="countedVersions"/>, when it gives a set for an SLO, counts
        /// only minutes counted under those versions: the others look unread, so a window mostly from before a change reads Unknown.
        /// </summary>
        public static async Task<Dictionary<Guid, SloStatus>> StatusesAsync(PlatformDbContext db, IReadOnlyList<Guid> ids, Func<Guid, IReadOnlyList<SloVersion>> versionsOf,
            SloOptions options, DateTimeOffset now, CancellationToken ct, bool burnOnly = false, Func<Guid, IReadOnlySet<int>?>? countedVersions = null)
            => Compute(ids, versionsOf, options, await ReadAsync(db, ids, now, burnOnly, ct), countedVersions);

        /// <summary>
        /// What the API shows from one read: the 30-day budget over every version's minutes, and the burn windows counting only
        /// <paramref name="burnVersions"/>' minutes, as the burn alerts judge them.
        /// </summary>
        public static async Task<Dictionary<Guid, SloStatus>> StatusesWithBurnsAsync(PlatformDbContext db, IReadOnlyList<Guid> ids,
            Func<Guid, IReadOnlyList<SloVersion>> versionsOf, SloOptions options, DateTimeOffset now, Func<Guid, IReadOnlySet<int>?> burnVersions, CancellationToken ct)
            => WithBurns(ids, versionsOf, options, await ReadAsync(db, ids, now, burnOnly: false, ct), burnVersions);

        /// <summary><see cref="StatusesWithBurnsAsync"/> from a read already made (<see cref="SloStatusCache"/>).</summary>
        internal static Dictionary<Guid, SloStatus> WithBurns(IReadOnlyList<Guid> ids, Func<Guid, IReadOnlyList<SloVersion>> versionsOf, SloOptions options, Read read,
            Func<Guid, IReadOnlySet<int>?> burnVersions)
        {
            var full = Compute(ids, versionsOf, options, read, null);
            var burn = Compute(ids, versionsOf, options, read, burnVersions);
            return full.ToDictionary(p => p.Key, p => p.Value with { Windows = burn[p.Key].Windows });
        }

        /// <summary>Where the windows read at <paramref name="now"/> end: the last whole minute, less <see cref="Settle"/>.</summary>
        public static DateTimeOffset End(DateTimeOffset now) => SloCounter.Floor(now) - Settle;

        /// <summary>The five burn windows, then the 30 days.</summary>
        private static readonly IReadOnlyList<(string Name, TimeSpan Length)> Windows = [.. SloBudget.BurnWindows, ("30d", SloOptions.Window)];

        /// <summary>What a status is computed from: where the windows end and start, each SLO's first counted minute, and the sums.</summary>
        internal sealed record Read(DateTimeOffset End, DateTimeOffset[] Starts, Dictionary<Guid, DateTimeOffset> Firsts, ILookup<Guid, Sums> Sums);

        /// <summary>One SLO version's SLI total, good and minutes counted, per window (indexed as <see cref="Windows"/>).</summary>
        internal sealed record Sums(int Version, long[] Total, long[] Good, long[] Minutes);

        internal static async Task<Read> ReadAsync(PlatformDbContext db, IReadOnlyList<Guid> ids, DateTimeOffset now, bool burnOnly, CancellationToken ct)
        {
            var end = End(now);
            var starts = Windows.Select(w => end - w.Length).ToArray();
            var idList = ids.Distinct().ToArray();
            // A window starts when the SLO's telemetry did: before its first counted minute there was nothing to miss. After it, a minute not
            // counted is a gap and makes the window unknown. No minute counted yet: every window is unknown.
            // The first counted minute of each, through the bucket key (one index probe per SLO), not a scan of every bucket.
            var firsts = (await db.Database.SqlQuery<FirstRow>($"""
                    SELECT s.id AS slo_id, (SELECT min(b.minute) FROM slo_buckets b WHERE b.slo_id = s.id) AS first
                    FROM unnest({idList}) AS s(id)
                    """).ToListAsync(ct))
                .Where(f => f.First is not null).ToDictionary(f => f.SloId, f => f.First!.Value);
            var from = burnOnly ? end - SloBudget.BurnWindows.Max(w => w.Length) : starts.Min();
            var rows = await SumsAsync(db, idList, starts.Select(s => s > from ? s : from).ToArray(), end, ct);
            var sums = rows.GroupBy(r => (r.SloId, r.Version)).Select(g =>
            {
                var (total, good, minutes) = (new long[Windows.Count], new long[Windows.Count], new long[Windows.Count]);
                foreach (var r in g)
                {
                    total[r.WindowIndex] += r.Total;
                    good[r.WindowIndex] += r.Good;
                    minutes[r.WindowIndex] += r.Minutes;
                }
                return (g.Key.SloId, Sums: new Sums(g.Key.Version, total, good, minutes));
            }).ToLookup(x => x.SloId, x => x.Sums);
            return new Read(end, starts, firsts, sums);
        }

        private static Dictionary<Guid, SloStatus> Compute(IReadOnlyList<Guid> ids, Func<Guid, IReadOnlyList<SloVersion>> versionsOf, SloOptions options, Read read,
            Func<Guid, IReadOnlySet<int>?>? countedVersions)
        {
            var result = new Dictionary<Guid, SloStatus>();
            foreach (var id in ids)
            {
                var versions = versionsOf(id);
                var counted = countedVersions?.Invoke(id);
                var rows = read.Sums[id].Where(r => counted is null || counted.Contains(r.Version)).ToList();
                DateTimeOffset? first = read.Firsts.TryGetValue(id, out var f) ? f : null;
                long Expected(DateTimeOffset windowStart)
                    => first is { } from ? Math.Max(0, (long)(read.End - (from > windowStart ? from : windowStart)).TotalMinutes) : (long)(read.End - windowStart).TotalMinutes;
                double TargetOf(int version) => versions.FirstOrDefault(v => v.Version == version)?.Target ?? versions[^1].Target;
                List<VersionSums> Of(int i) => [.. rows.Select(r => new VersionSums(r.Version, TargetOf(r.Version), r.Total[i], r.Good[i], r.Minutes[i]))];
                var burns = SloBudget.BurnWindows.Select((w, i) => SloBudget.Window(w.Name, w.Length, Of(i), options.MinimumBurnEvents, options.UnknownTolerance,
                    Expected(read.Starts[i]))).ToList();
                var last = Windows.Count - 1;
                var budget = SloBudget.Window("30d", SloOptions.Window, Of(last), options.MinimumEvents, options.UnknownTolerance, Expected(read.Starts[last]));
                result[id] = SloBudget.Status(budget, burns) with { CountedSince = first };
            }
            return result;
        }

        internal sealed record FirstRow(Guid SloId, DateTimeOffset? First);

        /// <summary>One window's sums from one source, for one SLO version.</summary>
        internal sealed record PartRow(Guid SloId, int Version, int WindowIndex, long Total, long Good, long Minutes);

        /// <summary>A stretch of a window read from one source: minutes, hours or days.</summary>
        private sealed record Part(int Window, DateTimeOffset From, DateTimeOffset To);

        /// <summary>
        /// Each window's sums (Phase 9, milestone 4), from three sources: the whole UTC days inside it from <see cref="SloDay"/>, the whole hours of its
        /// partial days from <see cref="SloHour"/>, and the minutes of its partial first and last hours from the buckets. So a read touches a few
        /// hundred rows per SLO, not the 43,000 minutes of 30 days.
        /// </summary>
        private static async Task<List<PartRow>> SumsAsync(PlatformDbContext db, Guid[] ids, DateTimeOffset[] starts, DateTimeOffset end, CancellationToken ct)
        {
            var (lastHour, today) = (SloDays.Hour(end), SloDays.Day(end));
            List<Part> minutes = [], hours = [], days = [];
            for (var i = 0; i < starts.Length; i++)
            {
                var s = starts[i];
                // s ≤ h ≤ d ≤ today ≤ lastHour ≤ end, each part empty when its bounds meet: a window inside the current hour is all minutes.
                var tail = lastHour > s ? lastHour : s;
                var h = SloDays.NextHour(s) < tail ? SloDays.NextHour(s) : tail;
                var d = s < today ? SloDays.NextDay(s) : h;
                minutes.Add(new(i, s, h));
                minutes.Add(new(i, tail, end));
                hours.Add(new(i, h, d));
                hours.Add(new(i, today > h ? today : h, lastHour));
                days.Add(new(i, d, today));
            }
            return [.. await PartsAsync(db, "slo_buckets", "minute", "1", ids, minutes, ct),
                .. await PartsAsync(db, "slo_hours", "hour", "x.minutes", ids, hours, ct),
                .. await PartsAsync(db, "slo_days", "day", "x.minutes", ids, days, ct)];
        }

        /// <summary>
        /// One source's sums per SLO, version and window. Each distinct stretch is its own SELECT, an index range on the source's key (one WHERE with
        /// the stretches ORed together read every minute of every SLO instead: 465 ms at 50 SLOs, against a few ms). UNION, not UNION ALL: stretches
        /// of different windows can overlap, and a row is read once. The join then gives each row to every window it falls in.
        /// </summary>
        private static async Task<List<PartRow>> PartsAsync(PlatformDbContext db, string table, string column, string minutes, Guid[] ids, IEnumerable<Part> parts,
            CancellationToken ct)
        {
            var nonEmpty = parts.Where(p => p.To > p.From).ToList();
            if (nonEmpty.Count == 0) return [];
            var args = new List<object> { ids };
            string Arg(object value)
            {
                args.Add(value);
                return $"{{{args.Count - 1}}}";
            }
            var ranges = nonEmpty.Select(p => (p.From, p.To)).Distinct()
                .Select(r => $"SELECT x.slo_id, x.version, x.total, x.good, {minutes} AS counted, x.{column} AS at FROM {table} x WHERE x.slo_id = ANY({{0}}) AND x.{column} >= {Arg(r.From)} AND x.{column} < {Arg(r.To)}")
                .ToList();
            var windows = nonEmpty.Select(p => $"SELECT {Arg(p.Window)}::int AS w, {Arg(p.From)}::timestamptz AS lo, {Arg(p.To)}::timestamptz AS hi").ToList();
            var sql = $"""
                SELECT r.slo_id, r.version, p.w AS window_index, sum(r.total)::bigint AS total, sum(r.good)::bigint AS good, sum(r.counted)::bigint AS minutes
                FROM ({string.Join(" UNION ", ranges)}) r JOIN ({string.Join(" UNION ALL ", windows)}) p ON r.at >= p.lo AND r.at < p.hi
                GROUP BY r.slo_id, r.version, p.w
                """;
            return await db.Database.SqlQueryRaw<PartRow>(sql, [.. args]).ToListAsync(ct);
        }

        /// <summary>
        /// The minutes a day of the 30 days should have counted: the part of it inside the window, from the SLO's first counted minute, up to
        /// now. The window's first day and today are partial; a day before the SLO's telemetry started expects none.
        /// </summary>
        public static long ExpectedMinutes(DateTimeOffset day, DateTimeOffset? countedSince, DateTimeOffset now)
        {
            var windowStart = SloCounter.Floor(now) - SloOptions.Window;
            var from = countedSince is { } since && since > windowStart ? since : windowStart;
            if (day > from) from = day;
            var end = SloCounter.Floor(now);
            var to = day + TimeSpan.FromDays(1) < end ? day + TimeSpan.FromDays(1) : end;
            return Math.Max(0, (long)(to - from).TotalMinutes);
        }

        /// <summary>One day of an SLO.</summary>
        public sealed record DayRow(DateTime Day, long Total, long Good, long Eligible, long Bad, long ExcludedHealth, long ExcludedCancelled, long ExcludedClientErrors, long Minutes);

        /// <summary>The window's days, oldest first: SLI events, what was excluded and why, and how many minutes were counted.</summary>
        // Columns in snake_case: the context's naming convention maps this unmapped type too.
        public static async Task<List<DayRow>> DaysAsync(PlatformDbContext db, Guid sloId, DateTimeOffset now, CancellationToken ct)
        {
            // Whole days from their totals; the window's first, partial day from its minutes.
            var from = SloCounter.Floor(now) - SloOptions.Window;
            var whole = SloDays.NextDay(from);
            return await db.Database.SqlQuery<DayRow>($"""
                SELECT day AT TIME ZONE 'UTC' AS day, sum(total)::bigint AS total, sum(good)::bigint AS good,
                       sum(eligible)::bigint AS eligible, sum(bad)::bigint AS bad, sum(excluded_health)::bigint AS excluded_health,
                       sum(excluded_cancelled)::bigint AS excluded_cancelled, sum(excluded_client_errors)::bigint AS excluded_client_errors,
                       sum(minutes)::bigint AS minutes
                FROM slo_days WHERE slo_id = {sloId} AND day >= {whole}
                GROUP BY 1
                UNION ALL
                SELECT date_trunc('day', minute AT TIME ZONE 'UTC'), sum(total)::bigint, sum(good)::bigint, sum(eligible)::bigint, sum(bad)::bigint,
                       sum(excluded_health)::bigint, sum(excluded_cancelled)::bigint, sum(excluded_client_errors)::bigint, count(*)::bigint
                FROM slo_buckets WHERE slo_id = {sloId} AND minute >= {from} AND minute < {whole}
                GROUP BY 1
                ORDER BY 1
                """).ToListAsync(ct);
        }
    }
}
