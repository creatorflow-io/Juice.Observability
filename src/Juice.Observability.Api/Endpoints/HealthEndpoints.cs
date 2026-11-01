using Juice.Observability.Data;
using Juice.Observability.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// A subject's current health. <see cref="Stale"/> is true when the last evaluation is older than three intervals:
    /// the state is then out of date, which is different from a current failure (requirements §7).
    /// </summary>
    public sealed record HealthDto(string SubjectType, Guid SubjectId, string SubjectName, string? Environment, string State,
        IReadOnlyList<string> Reasons, string PolicyVersion, DateTimeOffset EvaluatedAt, DateTimeOffset EvidenceFrom,
        DateTimeOffset EvidenceTo, DateTimeOffset? LastEvidenceAt, DateTimeOffset Since, bool Stale);

    public static class HealthEndpoints
    {
        public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/health", ListHealthAsync);
            return tenant;
        }

        /// <summary>Current health, filtered by <c>subjectType</c>, <c>environment</c> and <c>state</c>, paged.</summary>
        private static async Task<IResult> ListHealthAsync(Guid tenantId, string? subjectType, string? environment, string? state,
            int? limit, string? cursor, PlatformDbContext db, IOptions<HealthPolicy> policy, TimeProvider clock, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.HealthSnapshots.AsNoTracking().Where(h => h.TenantId == tenantId);
            if (subjectType is not null)
            {
                if (!Enum.TryParse<HealthSubject>(subjectType, ignoreCase: true, out var type))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid subjectType",
                        detail: $"subjectType must be one of: {string.Join(", ", Enum.GetNames<HealthSubject>().Select(n => n.ToLowerInvariant()))}.");
                }
                query = query.Where(h => h.SubjectType == type);
            }
            if (state is not null)
            {
                if (!Enum.TryParse<HealthState>(state, ignoreCase: true, out var parsed))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid state",
                        detail: $"state must be one of: {string.Join(", ", Enum.GetNames<HealthState>().Select(n => n.ToLowerInvariant()))}.");
                }
                query = query.Where(h => h.State == parsed);
            }
            if (environment is not null)
            {
                var key = environment.ToLowerInvariant();
                query = query.Where(h => db.Environments.Any(e => e.Id == h.EnvironmentId && e.Key == key));
            }
            if (after is not null)
            {
                query = query.Where(h => h.Id > after.After);
            }
            var rows = await query.OrderBy(h => h.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var items = await ToDtosAsync(db, tenantId, page, policy.Value, clock, ct);
            return Results.Ok(new Page<HealthDto>(items, rows.Count > take ? new Cursor("h", page[^1].Id).Encode() : null));
        }

        internal static async Task<List<HealthDto>> ToDtosAsync(PlatformDbContext db, Guid tenantId, IReadOnlyList<HealthSnapshot> rows,
            HealthPolicy policy, TimeProvider clock, CancellationToken ct)
        {
            var ids = rows.Select(r => r.SubjectId).Distinct().ToList();
            var names = new Dictionary<Guid, string>();
            foreach (var s in await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && ids.Contains(s.Id)).ToListAsync(ct))
                names[s.Id] = HealthRunner.Name(s);
            foreach (var a in await db.Applications.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).ToListAsync(ct))
                names[a.Id] = a.DisplayName;
            foreach (var r in await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.Id)).ToListAsync(ct))
                names[r.Id] = r.DisplayName;
            foreach (var i in await db.Instances.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.Id)).ToListAsync(ct))
                names[i.Id] = i.InstanceKey;
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.Key, ct);
            var staleBefore = clock.GetUtcNow() - 3 * policy.Interval;
            return rows.Select(r => new HealthDto(r.SubjectType.ToString().ToLowerInvariant(), r.SubjectId,
                names.GetValueOrDefault(r.SubjectId, ""), r.EnvironmentId is { } e ? environments.GetValueOrDefault(e) : null,
                r.State.ToString(), r.ReasonCodes, r.PolicyVersion, r.EvaluatedAt, r.EvidenceFrom, r.EvidenceTo, r.LastEvidenceAt,
                r.Since, r.EvaluatedAt < staleBefore)).ToList();
        }
    }
}
