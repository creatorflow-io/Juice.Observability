using Juice.Observability.Data;
using Juice.Observability.Health;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// Forgetting a retired service (<c>DELETE …/resources/{id}</c>, admin): the service and what discovery and health keep about it go.
    /// Discovery retires a service after <c>Discovery:ServiceRetireAfter</c> without telemetry; only then can it be forgotten, so a service
    /// in use is never deleted by mistake. Alerts, incidents, audit records and change events stay as history: they name the service
    /// by its key, and a change event's link to it is cleared. Telemetry seen afterwards makes it a new service.
    /// </summary>
    internal static class ServiceRetirement
    {
        public static async Task<IResult> ForgetAsync(Guid tenantId, Guid resourceId, HttpContext http, PlatformDbContext db, TimeProvider clock,
            CancellationToken ct)
        {
            var target = resourceId.ToString();
            var mutation = new Mutation(http, db, tenantId, "service.forget", "service");
            if (await mutation.RequireAsync(target, ct, Roles.Admin) is { } denied) return denied;

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Discovery restoring it (new telemetry) and this wait for each other on the row: whichever is second sees what the first did.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM services WHERE id = {resourceId} AND tenant_id = {tenantId} FOR UPDATE", ct);
            var service = await db.Services.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == resourceId, ct);
            if (service is null)
            {
                if (await db.Resources.AsNoTracking().AnyAsync(r => r.TenantId == tenantId && r.Id == resourceId, ct))
                {
                    return await RefuseAsync(db, transaction, mutation, target, "not_a_service", StatusCodes.Status409Conflict, "Not a service",
                        "Only a retired service can be forgotten; infrastructure resources are kept.", ct);
                }
                return Mutation.Problem(StatusCodes.Status404NotFound, "Resource not found", "No such service.");
            }
            var name = HealthRunner.Name(service);
            if (service.RetiredAt is null)
            {
                return await RefuseAsync(db, transaction, mutation, target, "in_use", StatusCodes.Status409Conflict, "Service in use",
                    $"{name} is not retired. Discovery retires a service after Discovery:ServiceRetireAfter without telemetry; it can be forgotten then.", ct);
            }
            var open = await db.Alerts.AsNoTracking().CountAsync(a => a.TenantId == tenantId && a.SubjectId == service.Id && a.State != AlertState.Resolved, ct);
            if (open > 0)
            {
                return await RefuseAsync(db, transaction, mutation, target, "open_alerts", StatusCodes.Status409Conflict, "Open alerts",
                    $"{name} has {open} open alert(s). Resolve them, or disable or rescope their rules, first.", ct);
            }

            // Health keeps its states and transitions by subject id, without a foreign key: the service's and its instances' go here.
            var subjects = await db.Instances.AsNoTracking().Where(i => i.TenantId == tenantId && i.ServiceId == service.Id).Select(i => i.Id).ToListAsync(ct);
            subjects.Add(service.Id);
            await db.HealthSnapshots.Where(h => h.TenantId == tenantId && subjects.Contains(h.SubjectId)).ExecuteDeleteAsync(ct);
            await db.HealthTransitions.Where(h => h.TenantId == tenantId && subjects.Contains(h.SubjectId)).ExecuteDeleteAsync(ct);
            // Instances, service environments and dependencies go with the row (cascade); change events keep theirs without the link.
            await db.Services.Where(s => s.Id == service.Id).ExecuteDeleteAsync(ct);

            // An application left with no service goes too, with its health.
            var application = false;
            if (service.ApplicationId is { } applicationId && !await db.Services.AnyAsync(s => s.TenantId == tenantId && s.ApplicationId == applicationId, ct))
            {
                await db.HealthSnapshots.Where(h => h.TenantId == tenantId && h.SubjectId == applicationId).ExecuteDeleteAsync(ct);
                await db.HealthTransitions.Where(h => h.TenantId == tenantId && h.SubjectId == applicationId).ExecuteDeleteAsync(ct);
                application = await db.Applications.Where(a => a.TenantId == tenantId && a.Id == applicationId).ExecuteDeleteAsync(ct) > 0;
            }

            var now = clock.GetUtcNow();
            mutation.Audit(AuditOutcome.Succeeded, target, new()
            {
                ["service"] = name, ["retiredAt"] = service.RetiredAt.Value.ToString("O"), ["lastSeen"] = service.LastSeen.ToString("O"),
                ["applicationRemoved"] = application ? "true" : "false",
            });
            mutation.RecordChange($"Service {name}: forgotten", now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }

        /// <summary>The refusal, audited and committed (the audit record outlives the refused change).</summary>
        private static async Task<IResult> RefuseAsync(PlatformDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
            Mutation mutation, string target, string reason, int status, string title, string detail,
            CancellationToken ct)
        {
            mutation.Audit(AuditOutcome.Failed, target, new() { ["reason"] = reason });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Mutation.Problem(status, title, detail);
        }
    }
}
