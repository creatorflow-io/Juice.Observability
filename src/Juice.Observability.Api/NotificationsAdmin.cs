using System.Security.Claims;
using Juice.Notifications.Admin;
using Juice.Notifications.Hosting;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Who administers the platform's notifications (phase-5-plan p5-d6): the platform's own roles, for its one tenant (the pipeline's
    /// embedded tenant). <c>admin</c> changes the setup (integrations, routes, policies); <c>operator</c> reads history and retries
    /// deliveries; <c>viewer</c> sees nothing of notifications. Only for a caller of this deployment: one IdP may serve several, and roles alone
    /// don't say which, so a caller whose tenant claim isn't this deployment's tenant key (or who has none) has no right, whatever its roles
    /// (as <c>/api/v1/platform/status</c> judges it).
    /// </summary>
    internal sealed class ObservabilityNotificationsAccess(NotificationsHosting hosting, IOptions<AuthOptions> auth, IOptions<PlatformOptions> platform)
        : SingleTenantAdminAccess(hosting.EmbeddedTenantId ?? throw new InvalidOperationException("The notification pipeline isn't embedded: it has no tenant."))
    {
        protected override ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, CancellationToken ct)
        {
            if (user.FindFirst(auth.Value.TenantClaim)?.Value is not { } claim || claim != platform.Value.TenantKey)
            {
                return ValueTask.FromResult(AdminRights.None);
            }
            return ValueTask.FromResult(user.IsInRole(Roles.Admin) ? AdminRights.All
                : user.IsInRole(Roles.Operator) ? AdminRights.Read | AdminRights.Operate
                : AdminRights.None);
        }
    }

    /// <summary>
    /// The notifications admin API in the platform's host (phase-5 M48, m44-architecture §3.4): Juice.Notifications.Admin under
    /// <c>/notifications</c> (the prefix form, M44 Q5), behind the platform's own authentication and roles. Juice Console mounts its pages with
    /// <c>apiBase: '/notifications'</c>.
    /// </summary>
    public static class NotificationsAdminExtensions
    {
        /// <summary>The prefix the admin API is mapped under: <c>/notifications/api/v1/…</c>.</summary>
        public const string Prefix = "/notifications";

        public static IServiceCollection AddPlatformNotificationsAdmin(this IServiceCollection services)
        {
            services.AddJuiceNotificationsAdmin();
            services.AddScoped<INotificationsAdminAccess, ObservabilityNotificationsAccess>();
            return services;
        }

        /// <summary>The admin endpoints under <see cref="Prefix"/>; every one needs a signed-in caller, then the right its page needs.</summary>
        public static IEndpointRouteBuilder MapPlatformNotificationsAdmin(this IEndpointRouteBuilder app)
        {
            app.MapJuiceNotificationsAdmin(Prefix);
            return app;
        }
    }
}
