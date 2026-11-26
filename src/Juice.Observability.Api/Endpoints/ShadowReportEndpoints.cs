using Juice.Observability.Notifications.Shadow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// <c>GET /api/v1/tenants/{id}/notifications/shadow-report</c> (M47, waves §6.1): the shadow run's comparison over a window, for the soak's
    /// daily report (p5-d7). Operators and admins; nothing in it is secret (the bodies compared are what was sent, and only their first
    /// difference is shown).
    /// </summary>
    public static class ShadowReportEndpoints
    {
        /// <summary>The window a report covers when none is given, and the longest one asked.</summary>
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(1);
        public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(31);

        public static RouteGroupBuilder MapShadowReport(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/notifications/shadow-report", GetAsync);
            return tenant;
        }

        private static async Task<IResult> GetAsync(Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, int? latest, HttpContext http, ShadowReport report,
            TimeProvider clock, CancellationToken ct)
        {
            if (!http.User.IsInRole(Roles.Operator) && !http.User.IsInRole(Roles.Admin))
            {
                return Mutation.Problem(StatusCodes.Status403Forbidden, "Forbidden", "The shadow report needs the operator or admin role.");
            }
            var end = to ?? clock.GetUtcNow();
            var start = from ?? end - DefaultWindow;
            if (start >= end || end - start > MaxWindow)
            {
                return Mutation.Problem(StatusCodes.Status400BadRequest, "Invalid window", $"from is before to, at most {MaxWindow.TotalDays} days apart.");
            }
            return Results.Ok(await report.GetAsync(tenantId, start, end, Math.Clamp(latest ?? 20, 1, ShadowReport.MaxLatest), ct));
        }
    }
}
