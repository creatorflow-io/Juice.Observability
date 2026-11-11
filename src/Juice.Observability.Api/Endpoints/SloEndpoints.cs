using Juice.Observability.Data;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>A window of an SLO: its events, attainment and burn rate, or why it has none.</summary>
    public sealed record SloWindowDto(string Name, long Total, long Good, long Bad, double? Attainment, double? BurnRate, double Coverage, string State);

    /// <summary>
    /// An SLO as it stands: the version in effect now (a newer one may be waiting for its date), its 30-day budget and its burn windows. <c>State</c>
    /// is <c>Met</c>, <c>AtRisk</c>, <c>Exhausted</c>, <c>InsufficientData</c> or <c>Unknown</c>; the last two have no budget figures.
    /// <c>BudgetAllowed</c> is the bad requests the 30 days' budget allows, each version's requests at its own target.
    /// </summary>
    public sealed record SloSummaryDto(string Key, string Name, string Category, string Sli, string Service, string Environment, double Target, int? ThresholdMs,
        int Version, int? PendingVersion, bool Retired, string State, double? Attainment, double? BudgetUsed, double? BudgetRemaining, long Total, long Good, long Bad,
        double BudgetAllowed, double Coverage, IReadOnlyList<SloWindowDto> Burn);

    public sealed record SloVersionDto(int Version, double Target, DateTimeOffset EffectiveFrom, DateTimeOffset CreatedAt, string CreatedBy, string Hash, string Status,
        SloDefinitionDto Definition);

    /// <summary>A version's definition: what counts and how (the Phase 9 plan's eligibility).</summary>
    public sealed record SloDefinitionDto(string Name, string Sli, string Service, string Environment, double Target, int? ThresholdMs, IReadOnlyList<string> Operations,
        IReadOnlyList<string> ExcludeOperations, string ClientErrors, string Cancellations);

    /// <summary>
    /// A UTC day of an SLO. <c>Minutes</c> were counted of the <c>ExpectedMinutes</c> it should have: fewer than 1440 for the window's first
    /// day, today, and the day the SLO's telemetry started.
    /// </summary>
    public sealed record SloDayDto(DateTime Day, long Total, long Good, long Bad, double? Attainment, long ExcludedHealth, long ExcludedCancelled,
        long ExcludedClientErrors, long Minutes, long ExpectedMinutes);

    public sealed record SloDetailDto(SloSummaryDto Slo, SloDefinitionDto Definition, IReadOnlyList<SloVersionDto> Versions, IReadOnlyList<SloDayDto> Days,
        DateTimeOffset? CountedThrough, IReadOnlyList<SloAlertDto> Alerts);

    /// <summary>
    /// One of the SLO's burn alerts (<c>Rule</c> is <c>slo-fast-burn</c>, <c>slo-slow-burn</c> or <c>slo-slow-burn-3d</c>): open ones, then the latest
    /// resolved. <c>NoData</c> says its last evaluation had a window without a rate, so it was held, neither fired nor resolved.
    /// </summary>
    public sealed record SloAlertDto(Guid Id, string Rule, string State, string Severity, DateTimeOffset? FiredAt, DateTimeOffset? ResolvedAt, double? BurnRate,
        bool NoData);

    /// <summary>
    /// SLOs, read-only (roadmap Phase 9): they're defined in the deployment's file. Technical and business SLOs are listed apart by <c>category</c>.
    /// </summary>
    public static class SloEndpoints
    {
        public static RouteGroupBuilder MapSloEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/slos", ListAsync);
            tenant.MapGet("/slos/{key}", GetAsync);
            return tenant;
        }

        private static SloDefinitionDto ToDto(SloDefinition d) => new(d.Name, d.Sli, d.Service, d.Environment, d.Target, d.ThresholdMs, d.Operations,
            d.Exclusions, d.ClientErrors.ToString(), d.Cancellations.ToString());

        /// <summary>
        /// <c>Effective</c> (the one counted now, even when it's the only one and dated later: history before it is counted under it),
        /// <c>Withdrawn</c> (replaced before it took effect), <c>Pending</c> or <c>Superseded</c>.
        /// </summary>
        private static string Status(List<SloVersion> versions, SloVersion v, SloVersion effective, DateTimeOffset now)
            => v.Version == effective.Version ? "Effective" : SloCounter.Withdrawn(v) ? "Withdrawn" : v.EffectiveFrom > now ? "Pending" : "Superseded";

        private static SloWindowDto ToDto(WindowStatus w) => new(w.Name, w.Total, w.Good, w.Bad, w.Sli, w.BurnRate, w.Coverage, w.State);

        private static SloSummaryDto Summary(Slo slo, List<SloVersion> versions, SloStatus status, DateTimeOffset now)
        {
            var effective = SloCounter.VersionAt(versions, now);
            var definition = SloDefinition.FromJson(effective.Definition);
            var pending = versions.LastOrDefault(v => v.EffectiveFrom > now && v.Version != effective.Version && !SloCounter.Withdrawn(v));
            return new SloSummaryDto(slo.Key, definition.Name, slo.Category.ToString(), slo.Sli, definition.Service, definition.Environment, effective.Target,
                definition.ThresholdMs, effective.Version, pending?.Version, slo.Retired, status.State, status.Budget.Sli, status.BudgetUsed,
                status.BudgetUsed is { } used ? 1 - used : null, status.Budget.Total, status.Budget.Good, status.Budget.Bad, status.Budget.Allowed,
                status.Budget.Coverage, [.. status.Windows.Select(ToDto)]);
        }

        /// <summary>Every SLO, technical and business apart when <c>category</c> says so; retired ones only with <c>retired=true</c>.</summary>
        /// <remarks>
        /// Pages of <see cref="SloOptions.MaxSlos"/> by key, <c>next</c> naming the key to continue <c>after</c>. Active SLOs fit on one page (the file
        /// holds at most that many); retired ones can go past it.
        /// </remarks>
        private static async Task<IResult> ListAsync(Guid tenantId, string? category, bool? retired, string? after, PlatformDbContext db, IOptions<SloOptions> options,
            TimeProvider clock, CancellationToken ct)
        {
            var query = db.Slos.AsNoTracking().Where(s => s.TenantId == tenantId);
            if (category is { Length: > 0 })
            {
                if (!Enum.TryParse<SloCategory>(category, ignoreCase: true, out var wanted)) return Mutation.Invalid(["category is Technical or Business."]);
                query = query.Where(s => s.Category == wanted);
            }
            if (retired != true) query = query.Where(s => !s.Retired);
            if (after is { Length: > 0 }) query = query.Where(s => string.Compare(s.Key, after) > 0);
            var slos = await query.OrderBy(s => s.Key).Take(SloOptions.MaxSlos + 1).ToListAsync(ct);
            var next = slos.Count > SloOptions.MaxSlos ? slos[SloOptions.MaxSlos - 1].Key : null;
            if (next is not null) slos.RemoveAt(SloOptions.MaxSlos);
            var ids = slos.Select(s => s.Id).ToList();
            var versions = (await db.SloVersions.AsNoTracking().Where(v => ids.Contains(v.SloId)).OrderBy(v => v.Version).ToListAsync(ct)).ToLookup(v => v.SloId);
            var now = clock.GetUtcNow();
            var statuses = await StatusesAsync(db, ids, id => [.. versions[id]], options.Value, now, ct);
            return Results.Ok(new Page<SloSummaryDto>([.. slos.Select(slo => Summary(slo, [.. versions[slo.Id]], statuses[slo.Id], now))], next));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, string key, PlatformDbContext db, IOptions<SloOptions> options, TimeProvider clock, CancellationToken ct)
        {
            var slo = await db.Slos.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key, ct);
            if (slo is null) return Mutation.Problem(StatusCodes.Status404NotFound, "SLO not found", "No SLO has this key.");
            var versions = await db.SloVersions.AsNoTracking().Where(v => v.SloId == slo.Id).OrderBy(v => v.Version).ToListAsync(ct);
            var now = clock.GetUtcNow();
            var effective = SloCounter.VersionAt(versions, now);
            var days = await SloReader.DaysAsync(db, slo.Id, now, ct);
            var status = (await StatusesAsync(db, [slo.Id], _ => versions, options.Value, now, ct))[slo.Id];
            return Results.Ok(new SloDetailDto(
                Summary(slo, versions, status, now),
                ToDto(SloDefinition.FromJson(effective.Definition)),
                [.. versions.OrderByDescending(v => v.Version).Select(v => new SloVersionDto(v.Version, v.Target, v.EffectiveFrom, v.CreatedAt, v.CreatedBy, v.Hash,
                    Status(versions, v, effective, now), ToDto(SloDefinition.FromJson(v.Definition))))],
                Days(days, status.CountedSince, now),
                slo.CountedThrough,
                await AlertsAsync(db, tenantId, slo.Key, ct)));
        }

        /// <summary>
        /// The SLOs' statuses: the 30-day budget over every version's minutes, and the burn windows as the burn alerts judge them, over the current
        /// service's minutes only, so the page and the alerts agree after a move.
        /// </summary>
        private static async Task<Dictionary<Guid, SloStatus>> StatusesAsync(PlatformDbContext db, IReadOnlyList<Guid> ids, Func<Guid, IReadOnlyList<SloVersion>> versionsOf,
            SloOptions options, DateTimeOffset now, CancellationToken ct)
        {
            IReadOnlySet<int>? SameService(Guid id)
            {
                try
                {
                    var versions = versionsOf(id);
                    return SloBurnAlerts.SameServiceVersions(versions, SloDefinition.FromJson(SloCounter.VersionAt(versions, now).Definition));
                }
                catch (Exception)
                {
                    // The version in effect can't be read: the burn alerts hold for it, so whatever the windows show here, nothing fires on them.
                    return null;
                }
            }
            // One read for both: the same sums, the burn windows counting only the current service's versions.
            return await SloReader.StatusesWithBurnsAsync(db, ids, versionsOf, options, now, SameService, ct);
        }

        /// <summary>The SLO's burn alerts: every open one, then the ten latest resolved.</summary>
        private static async Task<List<SloAlertDto>> AlertsAsync(PlatformDbContext db, Guid tenantId, string sloKey, CancellationToken ct)
        {
            var keys = SloBurnRules.Keys.Select(rule => SloBurnRules.DedupKey(rule, sloKey)).ToList();
            var alerts = db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId && keys.Contains(a.DedupKey));
            var list = await alerts.Where(a => a.State != AlertState.Resolved).OrderByDescending(a => a.Id).ToListAsync(ct);
            list.AddRange(await alerts.Where(a => a.State == AlertState.Resolved).OrderByDescending(a => a.Id).Take(10).ToListAsync(ct));
            // The dedup key starts with the rule's key (SloBurnRules.DedupKey).
            return [.. list.Select(a => new SloAlertDto(a.Id, a.DedupKey[..a.DedupKey.IndexOf('|')], a.State.ToString(), a.Severity.ToString(), a.FiredAt, a.ResolvedAt,
                a.LastValue, a.NoData))];
        }

        /// <summary>
        /// Every day of the 30 that should have minutes, oldest first, including one with none counted: a whole day missing shows as 0 of its
        /// minutes, not as a row left out.
        /// </summary>
        private static List<SloDayDto> Days(List<SloReader.DayRow> rows, DateTimeOffset? countedSince, DateTimeOffset now)
        {
            // The day comes back without a zone; it's a UTC day, and says so, or the Console reads it as local time.
            var result = new List<SloDayDto>();
            // Nothing counted yet: no days to show (the page says so), not 30 days of gaps.
            if (countedSince is null) return result;
            var counted = rows.ToDictionary(d => DateTime.SpecifyKind(d.Day, DateTimeKind.Utc));
            var first = (SloCounter.Floor(now) - SloOptions.Window).UtcDateTime.Date;
            for (var day = DateTime.SpecifyKind(first, DateTimeKind.Utc); day <= now.UtcDateTime.Date; day = day.AddDays(1))
            {
                var expected = SloReader.ExpectedMinutes(new DateTimeOffset(day, TimeSpan.Zero), countedSince, now);
                var d = counted.GetValueOrDefault(day);
                if (d is null && expected == 0) continue;
                result.Add(d is null
                    ? new SloDayDto(day, 0, 0, 0, null, 0, 0, 0, 0, expected)
                    : new SloDayDto(day, d.Total, d.Good, d.Total - d.Good, d.Total > 0 ? (double)d.Good / d.Total : null, d.ExcludedHealth, d.ExcludedCancelled,
                        d.ExcludedClientErrors, d.Minutes, expected));
            }
            return result;
        }
    }
}
