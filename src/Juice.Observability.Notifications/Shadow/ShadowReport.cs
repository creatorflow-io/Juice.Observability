using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications.Shadow
{
    /// <summary>The shadow run over a window: what was compared, how it differed by kind and rule, and the latest differences no rule explains.</summary>
    public sealed record ShadowReportView(DateTimeOffset From, DateTimeOffset To, int Compared, int Matched, int Differences, int Unexplained,
        DateTimeOffset? LastComparedAt, IReadOnlyList<ShadowRuleCount> ByRule, IReadOnlyList<ShadowDay> Days, IReadOnlyList<ShadowDifferenceView> Latest);

    /// <summary>One UTC day of the window, the soak's daily report (p5-d7): units compared, their differences, and those no rule explains.</summary>
    public sealed record ShadowDay(DateOnly Day, int Compared, int Differences, int Unexplained);

    /// <summary>Differences of one kind under one rule (<see cref="ShadowComparer.Unexplained"/>: no rule).</summary>
    public sealed record ShadowRuleCount(string Rule, string Kind, int Count);

    public sealed record ShadowDifferenceView(Guid Id, Guid TransitionId, int EscalationStep, Guid AlertId, string Event, string EndpointKey, string Kind,
        string? Rule, string Detail, DateTimeOffset DetectedAt);

    /// <summary>The comparison's state over a window: units compared, differences no rule explains, and when it last compared anything.</summary>
    public sealed record ShadowStatus(int Compared, int Unexplained, DateTimeOffset? LastComparedAt);

    /// <summary>
    /// The report (p5-d7's daily report reads it), and the dead-man's shadow cause (<see cref="Juice.Observability.Health.DeadMan.IShadowReport"/>: null, or why), from the
    /// comparer's tables.
    /// </summary>
    public sealed class ShadowReport(PlatformDbContext db, IOptions<ShadowOptions> options, TimeProvider clock) : Juice.Observability.Health.DeadMan.IShadowReport
    {
        private sealed record DayRow(DateOnly Day, int Compared, int Differences, int Unexplained);

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
            // By UTC day, in the database (one row a day however many units).
            var days = await db.Database.SqlQuery<DayRow>($"""
                SELECT (compared_at AT TIME ZONE 'UTC')::date AS day, count(*)::int AS compared, coalesce(sum(differences), 0)::int AS differences,
                       coalesce(sum(unexplained), 0)::int AS unexplained
                FROM shadow_comparisons WHERE tenant_id = {tenantId} AND compared_at >= {from} AND compared_at < {to}
                GROUP BY 1 ORDER BY 1
                """).ToListAsync(ct);
            var last = await db.ShadowComparisons.AsNoTracking().Where(c => c.TenantId == tenantId).MaxAsync(c => (DateTimeOffset?)c.ComparedAt, ct);
            return new ShadowReportView(from, to, compared, matched, byRule.Sum(r => r.Count), byRule.Where(r => r.Rule is null).Sum(r => r.Count), last,
                [.. byRule.Select(r => new ShadowRuleCount(r.Rule ?? ShadowComparer.Unexplained, r.Kind, r.Count)).OrderBy(r => r.Rule, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal)],
                [.. days.Select(d => new ShadowDay(d.Day, d.Compared, d.Differences, d.Unexplained))],
                [.. unexplained.Select(d => new ShadowDifferenceView(d.Id, d.TransitionId, d.EscalationStep, d.AlertId, d.Event, d.EndpointKey, d.Kind, d.Rule, d.Detail, d.DetectedAt))]);
        }

        /// <summary>
        /// Why the shadow run needs a look, or null: a difference no rule explains within <see cref="ShadowOptions.AlarmWindow"/>, or a transition
        /// that should have been compared by now (its grace and the longest wait past, and more) and wasn't: the comparer stopped or fell behind.
        /// The dead-man's shadow cause (M49) asks it on each pass.
        /// </summary>
        public async ValueTask<string?> FailingAsync(CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var now = clock.GetUtcNow();
            var since = now - settings.AlarmWindow;
            var unexplained = await db.ShadowDifferences.AsNoTracking().CountAsync(d => d.DetectedAt >= since && d.Rule == null, cancellationToken);
            if (unexplained > 0)
            {
                return $"{unexplained} shadow difference(s) no rule explains in the last {settings.AlarmWindow.TotalMinutes:0} min";
            }
            // Planned while the comparer ran (it has compared something), and past every wait.
            var started = await db.ShadowComparisons.AsNoTracking().MinAsync(c => (DateTimeOffset?)c.ComparedAt, cancellationToken);
            var overdue = now - settings.Grace - settings.MaxWait - settings.AlarmWindow;
            if (started is { } first && await db.AlertTransitions.AsNoTracking().AnyAsync(t => t.NotificationsPlannedAt >= first && t.NotificationsPlannedAt < overdue
                    && (t.ToState == AlertState.Firing || t.ToState == AlertState.Resolved)
                    && !db.ShadowComparisons.Any(c => c.TenantId == t.TenantId && c.TransitionId == t.Id && c.EscalationStep == 0), cancellationToken))
            {
                return "the shadow comparer is behind: a transition planned before " + overdue.ToString("O") + " isn't compared";
            }
            return null;
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
