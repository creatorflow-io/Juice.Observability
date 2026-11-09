using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>A diagnostic bundle as listed: what it is and until when it's kept, never its bytes.</summary>
    public sealed record AgentBundleDto(Guid Id, Guid AgentId, Guid CommandId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long SizeBytes, string Sha256);

    /// <summary>
    /// The diagnostic bundles agents uploaded (roadmap Phase 8). Anyone who can see the agent sees the list; downloading one, whose log excerpts may
    /// say more than the rest of the platform shows, takes an operator or an admin, and is audited as <c>agent.bundle.download</c>.
    /// </summary>
    public static class BundleEndpoints
    {
        public static RouteGroupBuilder MapBundleEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/agents/{agentId:guid}/bundles", ListAsync);
            tenant.MapGet("/agents/{agentId:guid}/bundles/{bundleId:guid}", DownloadAsync);
            return tenant;
        }

        private static async Task<IResult> ListAsync(Guid tenantId, Guid agentId, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var items = await db.AgentBundles.AsNoTracking().Where(b => b.TenantId == tenantId && b.AgentId == agentId && b.ExpiresAt > now)
                .OrderByDescending(b => b.CreatedAt).Take(100)
                .Select(b => new AgentBundleDto(b.Id, b.AgentId, b.CommandId, b.CreatedAt, b.ExpiresAt, b.SizeBytes, b.Sha256)).ToListAsync(ct);
            return Results.Ok(new Page<AgentBundleDto>(items, null));
        }

        private static async Task<IResult> DownloadAsync(Guid tenantId, Guid agentId, Guid bundleId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "agent.bundle.download", "agent_bundle");
            if (await mutation.RequireAsync(bundleId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            var now = clock.GetUtcNow();
            var bundle = await db.AgentBundles.AsNoTracking().SingleOrDefaultAsync(b => b.TenantId == tenantId && b.AgentId == agentId && b.Id == bundleId && b.ExpiresAt > now, ct);
            if (bundle is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Bundle not found", "No such bundle, or it's past its retention and deleted.");
            var hostname = await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.Hostname).SingleAsync(ct);
            mutation.Audit(AuditOutcome.Succeeded, bundleId.ToString(), new() { ["agent"] = agentId.ToString(), ["sha256"] = bundle.Sha256 });
            await db.SaveChangesAsync(ct);
            var name = new string([.. hostname.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);
            return Results.File(bundle.Content, "application/zip", $"juice-bundle-{name}-{bundle.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}.zip");
        }
    }
}
