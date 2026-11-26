using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Notifications.Shadow
{
    /// <summary>The shadow run over a window: what was compared, how it differed by kind and rule, and the latest differences no rule explains.</summary>
    public sealed record ShadowReportView(DateTimeOffset From, DateTimeOffset To, int Compared, int Matched, int Differences, int Unexplained,
        DateTimeOffset? LastComparedAt, IReadOnlyList<ShadowRuleCount> ByRule, IReadOnlyList<ShadowDifferenceView> Latest);

    /// <summary>Differences of one kind under one rule (<see cref="ShadowComparer.Unexplained"/>: no rule).</summary>
    public sealed record ShadowRuleCount(string Rule, string Kind, int Count);

    public sealed record ShadowDifferenceView(Guid Id, Guid TransitionId, int EscalationStep, Guid AlertId, string Event, string EndpointKey, string Kind,
        string? Rule, string Detail, DateTimeOffset DetectedAt);

    /// <summary>
    /// The shadow comparison's state for another part of the platform (the dead-man's shadow cause, M49): unexplained differences over a
    /// window, and when the comparer last compared anything (a comparer that stopped is a cause too).
    /// </summary>
    public interface IShadowReport
    {
        Task<ShadowStatus> StatusAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken);
    }

    public sealed record ShadowStatus(int Compared, int Unexplained, DateTimeOffset? LastComparedAt);

    /// <summary>The report (p5-d7's daily report reads it) and <see cref="IShadowReport"/>, from the comparer's tables.</summary>
    public sealed class ShadowReport(PlatformDbContext db, TimeProvider clock) : IShadowReport
    {
        public const int MaxLatest = 100;

        public async Task<ShadowReportView> GetAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, int latest, CancellationToken ct)
        {
            var comparisons = db.ShadowComparisons.AsNoTracking().Where(c => c.TenantId == tenantId && c.ComparedAt >= from && c.ComparedAt < to);
            var differences = db.ShadowDifferences.AsNoTracking().Where(d => d.TenantId == tenantId && d.DetectedAt >= from && d.DetectedAt < to);
            var compared = await comparisons.CountAsync(ct);
            var matched = await comparisons.CountAsync(c => c.Differences == 0, ct);
            var byRule = await differences.GroupBy(d => new { d.Rule, d.Kind }).Select(g => new { g.Key.Rule, g.Key.Kind, Count = g.Count() }).ToListAsync(ct);
            var unexplained = await differences.Where(d => d.Rule == null).OrderByDescending(d => d.DetectedAt).ThenBy(d => d.Id)
                .Take(Math.Clamp(latest, 0, MaxLatest)).ToListAsync(ct);
            var last = await db.ShadowComparisons.AsNoTracking().Where(c => c.TenantId == tenantId).MaxAsync(c => (DateTimeOffset?)c.ComparedAt, ct);
            return new ShadowReportView(from, to, compared, matched, byRule.Sum(r => r.Count), byRule.Where(r => r.Rule is null).Sum(r => r.Count), last,
                [.. byRule.Select(r => new ShadowRuleCount(r.Rule ?? ShadowComparer.Unexplained, r.Kind, r.Count)).OrderBy(r => r.Rule, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal)],
                [.. unexplained.Select(d => new ShadowDifferenceView(d.Id, d.TransitionId, d.EscalationStep, d.AlertId, d.Event, d.EndpointKey, d.Kind, d.Rule, d.Detail, d.DetectedAt))]);
        }

        public async Task<ShadowStatus> StatusAsync(Guid tenantId, TimeSpan window, CancellationToken cancellationToken)
        {
            var from = clock.GetUtcNow() - window;
            var compared = await db.ShadowComparisons.AsNoTracking().CountAsync(c => c.TenantId == tenantId && c.ComparedAt >= from, cancellationToken);
            var unexplained = await db.ShadowDifferences.AsNoTracking().CountAsync(d => d.TenantId == tenantId && d.DetectedAt >= from && d.Rule == null, cancellationToken);
            var last = await db.ShadowComparisons.AsNoTracking().Where(c => c.TenantId == tenantId).MaxAsync(c => (DateTimeOffset?)c.ComparedAt, cancellationToken);
            return new ShadowStatus(compared, unexplained, last);
        }
    }
}
