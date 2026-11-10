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
        /// Several SLOs' statuses in two queries, whatever their number. <paramref name="burnOnly"/> reads only the burn windows' 3 days: the 30-day
        /// budget it returns then isn't the SLO's, and isn't to be read. <paramref name="countedVersions"/>, when it gives a set for an SLO, counts
        /// only minutes counted under those versions: the others look unread, so a window mostly from before a change reads Unknown.
        /// </summary>
        public static async Task<Dictionary<Guid, SloStatus>> StatusesAsync(PlatformDbContext db, IReadOnlyList<Guid> ids, Func<Guid, IReadOnlyList<SloVersion>> versionsOf,
            SloOptions options, DateTimeOffset now, CancellationToken ct, bool burnOnly = false, Func<Guid, IReadOnlySet<int>?>? countedVersions = null)
        {
            var end = SloCounter.Floor(now) - Settle;
            var windows = SloBudget.BurnWindows.Append((Name: "30d", Length: SloOptions.Window)).ToList();
            var starts = windows.Select(w => end - w.Length).ToArray();
            var start = burnOnly ? end - SloBudget.BurnWindows.Max(w => w.Length) : starts.Min();
            // A window starts when the SLO's telemetry did: before its first counted minute there was nothing to miss. After it, a minute not
            // counted is a gap and makes the window unknown. No minute counted yet: every window is unknown.
            var idList = ids.ToArray();
            var firsts = await db.SloBuckets.Where(b => idList.Contains(b.SloId)).GroupBy(b => b.SloId)
                .Select(g => new { SloId = g.Key, First = g.Min(b => b.Minute) }).ToDictionaryAsync(x => x.SloId, x => x.First, ct);
            var all = await db.SloBuckets.AsNoTracking().Where(b => idList.Contains(b.SloId) && b.Minute >= start && b.Minute < end)
                .GroupBy(b => new { b.SloId, b.Version })
                .Select(g => new
                {
                    g.Key.SloId,
                    g.Key.Version,
                    T0 = g.Sum(b => b.Minute >= starts[0] ? b.Total : 0), G0 = g.Sum(b => b.Minute >= starts[0] ? b.Good : 0), M0 = g.Sum(b => b.Minute >= starts[0] ? 1 : 0),
                    T1 = g.Sum(b => b.Minute >= starts[1] ? b.Total : 0), G1 = g.Sum(b => b.Minute >= starts[1] ? b.Good : 0), M1 = g.Sum(b => b.Minute >= starts[1] ? 1 : 0),
                    T2 = g.Sum(b => b.Minute >= starts[2] ? b.Total : 0), G2 = g.Sum(b => b.Minute >= starts[2] ? b.Good : 0), M2 = g.Sum(b => b.Minute >= starts[2] ? 1 : 0),
                    T3 = g.Sum(b => b.Minute >= starts[3] ? b.Total : 0), G3 = g.Sum(b => b.Minute >= starts[3] ? b.Good : 0), M3 = g.Sum(b => b.Minute >= starts[3] ? 1 : 0),
                    T4 = g.Sum(b => b.Minute >= starts[4] ? b.Total : 0), G4 = g.Sum(b => b.Minute >= starts[4] ? b.Good : 0), M4 = g.Sum(b => b.Minute >= starts[4] ? 1 : 0),
                    T5 = g.Sum(b => b.Total), G5 = g.Sum(b => b.Good), M5 = g.Count(),
                })
                .ToListAsync(ct);
            var bySlo = all.ToLookup(r => r.SloId);
            var result = new Dictionary<Guid, SloStatus>();
            foreach (var id in ids)
            {
                var versions = versionsOf(id);
                var counted = countedVersions?.Invoke(id);
                var rows = bySlo[id].Where(r => counted is null || counted.Contains(r.Version)).ToList();
                DateTimeOffset? first = firsts.TryGetValue(id, out var f) ? f : null;
                long Expected(DateTimeOffset windowStart)
                    => first is { } from ? Math.Max(0, (long)(end - (from > windowStart ? from : windowStart)).TotalMinutes) : (long)(end - windowStart).TotalMinutes;
                double TargetOf(int version) => versions.FirstOrDefault(v => v.Version == version)?.Target ?? versions[^1].Target;
                List<VersionSums> Sums(int i) => [.. rows.Select(r => i switch
                {
                    0 => new VersionSums(r.Version, TargetOf(r.Version), r.T0, r.G0, r.M0),
                    1 => new VersionSums(r.Version, TargetOf(r.Version), r.T1, r.G1, r.M1),
                    2 => new VersionSums(r.Version, TargetOf(r.Version), r.T2, r.G2, r.M2),
                    3 => new VersionSums(r.Version, TargetOf(r.Version), r.T3, r.G3, r.M3),
                    4 => new VersionSums(r.Version, TargetOf(r.Version), r.T4, r.G4, r.M4),
                    _ => new VersionSums(r.Version, TargetOf(r.Version), r.T5, r.G5, r.M5),
                })];
                var burns = SloBudget.BurnWindows.Select((w, i) => SloBudget.Window(w.Name, w.Length, Sums(i), options.MinimumBurnEvents, options.UnknownTolerance,
                    Expected(starts[i]))).ToList();
                var budget = SloBudget.Window("30d", SloOptions.Window, Sums(5), options.MinimumEvents, options.UnknownTolerance, Expected(starts[5]));
                result[id] = SloBudget.Status(budget, burns) with { CountedSince = first };
            }
            return result;
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
            var from = SloCounter.Floor(now) - SloOptions.Window;
            return await db.Database.SqlQuery<DayRow>($"""
                SELECT date_trunc('day', minute AT TIME ZONE 'UTC') AS day, sum(total)::bigint AS total, sum(good)::bigint AS good,
                       sum(eligible)::bigint AS eligible, sum(bad)::bigint AS bad, sum(excluded_health)::bigint AS excluded_health,
                       sum(excluded_cancelled)::bigint AS excluded_cancelled, sum(excluded_client_errors)::bigint AS excluded_client_errors,
                       count(*)::bigint AS minutes
                FROM slo_buckets WHERE slo_id = {sloId} AND minute >= {from}
                GROUP BY 1 ORDER BY 1
                """).ToListAsync(ct);
        }
    }
}
