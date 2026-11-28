using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Evidence
{
    /// <summary>
    /// The evidence sections of an alert and of a service, under the tenant group (the core's tenant filter and the read policy). The subject is looked up in
    /// the route tenant first: a subject outside it is a 404 and no contributor runs. Another tenant's id is the tenant filter's 403, the same as an unknown one.
    /// </summary>
    public static class EvidenceEndpoints
    {
        public static readonly TimeSpan AlertLookback = TimeSpan.FromHours(2);
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);
        public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

        public static RouteGroupBuilder MapEvidenceEndpoints(this RouteGroupBuilder tenant)
        {
            // Before the host starts: a contributor with a bad key or an unknown policy refuses it here.
            var services = ((IEndpointRouteBuilder)tenant).ServiceProvider;
            services.GetRequiredService<EvidenceCatalog>().Verify(services);
            tenant.MapGet("/alerts/{alertId:guid}/evidence-sections", AlertAsync);
            tenant.MapGet("/resources/{resourceId:guid}/evidence-sections", ResourceAsync);
            return tenant;
        }

        private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);

        private static async Task<IResult> AlertAsync(Guid tenantId, Guid alertId, HttpContext http, PlatformDbContext db, EvidenceRunner runner, TimeProvider clock, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null)
            {
                return Problem(StatusCodes.Status404NotFound, "Alert not found", "No such alert.");
            }
            var from = (alert.FiredAt ?? alert.BreachStartedAt) - AlertLookback;
            var context = new EvidenceContext(tenantId, EvidenceTarget.Alert, alert.Id, alert.SubjectType == HealthSubject.Service ? alert.SubjectId : null, alert.EnvironmentId,
                from, alert.ResolvedAt ?? clock.GetUtcNow());
            return Results.Ok(await runner.RunAsync(context, http.User, ct));
        }

        /// <summary>A service's sections. Any other resource has none (<c>items: []</c>); an id that is neither is a 404.</summary>
        private static async Task<IResult> ResourceAsync(Guid tenantId, Guid resourceId, Guid? environmentId, DateTimeOffset? from, DateTimeOffset? to,
            HttpContext http, PlatformDbContext db, EvidenceRunner runner, TimeProvider clock, CancellationToken ct)
        {
            var end = to ?? clock.GetUtcNow();
            var start = from ?? end - DefaultWindow;
            if (start >= end || end - start > MaxWindow)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid time range", "from must be before to, at most 7 days apart.");
            }
            if (!await db.Services.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && s.Id == resourceId, ct))
            {
                return await db.Resources.AsNoTracking().AnyAsync(r => r.TenantId == tenantId && r.Id == resourceId, ct)
                    ? Results.Ok(new EvidenceSectionsDto([]))
                    : Problem(StatusCodes.Status404NotFound, "Resource not found", "No such resource.");
            }
            if (environmentId is { } environment && !await db.Environments.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.Id == environment, ct))
            {
                return Problem(StatusCodes.Status404NotFound, "Environment not found", "No such environment.");
            }
            var context = new EvidenceContext(tenantId, EvidenceTarget.Service, resourceId, resourceId, environmentId, start, end);
            return Results.Ok(await runner.RunAsync(context, http.User, ct));
        }
    }
}
