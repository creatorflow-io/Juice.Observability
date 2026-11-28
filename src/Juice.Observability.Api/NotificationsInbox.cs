using System.Security.Claims;
using Juice.Notifications.Hosting;
using Juice.Notifications.Inbox;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Who is a person of the platform's inbox (M54): someone signed in with a token of this deployment (its tenant claim, judged as the admin
    /// access and <c>/api/v1/platform/status</c> judge it) who holds a Console role. A client's own token is no person. The roles the inbox may
    /// record are the Console's three, read from the token as the platform reads them (<see cref="ClaimsPrincipal.IsInRole(string)"/>): the host
    /// records only the ones it allowlists, and any other claim (<c>offline_access</c>, <c>Operator</c>) never reaches it.
    /// </summary>
    internal sealed class ObservabilityInboxAccess(NotificationsHosting hosting, IOptions<AuthOptions> auth, IOptions<PlatformOptions> platform)
        : SingleTenantInboxAccess(hosting.EmbeddedTenantId ?? throw new InvalidOperationException("The notification pipeline isn't embedded: it has no tenant."))
    {
        protected override ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, CancellationToken ct)
            => ValueTask.FromResult(Person(user));

        private InboxAccessResult Person(ClaimsPrincipal user)
        {
            // One IdP may serve several deployments, and roles alone don't say which.
            if (user.FindFirst(auth.Value.TenantClaim)?.Value is not { } claim || !string.Equals(claim, platform.Value.TenantKey, StringComparison.Ordinal))
            {
                return InboxAccessResult.NotAMember;
            }
            // A client's own token (a service account: its client_id is its azp) is a program, not a person: it never becomes a subscriber.
            if (user.FindFirst("client_id")?.Value is { Length: > 0 } client && user.FindFirst("azp")?.Value is { } azp && string.Equals(client, azp, StringComparison.Ordinal))
            {
                return InboxAccessResult.NotAPerson;
            }
            if (user.FindFirst("sub")?.Value is not { Length: > 0 } subject)
            {
                return InboxAccessResult.NotAMember;
            }
            var roles = InboxNotificationsOptions.KnownRoles.Where(user.IsInRole).ToList();
            if (roles.Count == 0)
            {
                return InboxAccessResult.NotAMember;
            }
            return InboxAccessResult.Of(new InboxPerson(subject, user.FindFirst("preferred_username")?.Value, roles));
        }
    }

    /// <summary>
    /// The embedded inbox in the platform's host (M54), opt-in (<c>Notifications:Inbox:Enabled</c>): the notifications' inbox API and hub under
    /// <c>/notifications</c> (as the admin API), for the people of the platform's own sign-in. Juice Console's bell reads them with
    /// <c>apiUrl: apiBase + '/notifications'</c>. Interactions aren't mapped: nothing in this host hears an inbox item's action, so a button there would do nothing.
    /// </summary>
    public static class NotificationsInboxExtensions
    {
        /// <summary>The hub: <c>/notifications/hubs/inbox</c>. Its token travels in the query string (a browser can't set a header on a WebSocket), here only.</summary>
        public const string HubPath = NotificationsAdminExtensions.Prefix + "/hubs/inbox";

        /// <summary>The query value that carries the hub's token.</summary>
        internal const string TokenQuery = "access_token";

        /// <summary>Whether the inbox is on: <c>Notifications:Inbox:Enabled</c>.</summary>
        internal static bool IsEnabled(IConfiguration configuration)
            => configuration.GetValue<bool>($"{PlatformNotificationsOptions.Section}:Inbox:{nameof(InboxNotificationsOptions.Enabled)}");

        /// <summary>The inbox's services, when it is on; otherwise nothing is added.</summary>
        public static IServiceCollection AddPlatformNotificationsInbox(this IServiceCollection services, IConfiguration configuration)
        {
            if (IsEnabled(configuration))
            {
                Add(services);
            }
            return services;
        }

        // Apart, so a host with the inbox off never loads the inbox package nor SignalR (the JIT resolves what a method calls when it compiles it).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Add(IServiceCollection services)
        {
            services.AddJuiceNotificationsInbox();
            services.AddScoped<INotificationsInboxAccess, ObservabilityInboxAccess>();
            // The hub's token is in the query string, and the host's request log writes the query: it would put every session's token in the log.
            services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
        }

        /// <summary>The inbox's endpoints and hub under <see cref="NotificationsAdminExtensions.Prefix"/>, when the inbox is on; otherwise nothing is mapped.</summary>
        public static IEndpointRouteBuilder MapPlatformNotificationsInbox(this IEndpointRouteBuilder app)
        {
            if (app.ServiceProvider.GetRequiredService<IOptions<PlatformNotificationsOptions>>().Value.Inbox.Enabled)
            {
                Map(app);
            }
            return app;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Map(IEndpointRouteBuilder app) => app.MapJuiceNotificationsInbox(NotificationsAdminExtensions.Prefix, interactions: false);

        /// <summary>
        /// The bearer token the hub's connection carries (<c>?access_token=</c>): only on the hub's path, and only when the query holds exactly one
        /// value. Any other path, or two values, leaves the request to its header: a token in a URL elsewhere is ignored, never honoured.
        /// </summary>
        internal static Task ReadHubToken(MessageReceivedContext context)
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments(HubPath, StringComparison.OrdinalIgnoreCase)
                && request.Query.TryGetValue(TokenQuery, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]))
            {
                context.Token = values[0];
            }
            return Task.CompletedTask;
        }
    }
}
