using System.Security.Claims;
using Juice.Notifications.Hosting;
using Juice.Notifications.Inbox;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Who is a person of the platform's inbox (M54): someone signed in with a token of this deployment (its tenant claim, judged as the admin
    /// access and <c>/api/v1/platform/status</c> judge it) who holds a Console role. A client's own token is no person. The roles the inbox may
    /// record are the Console's three, read from the token as the platform reads them (<see cref="ClaimsPrincipal.IsInRole(string)"/>): the host
    /// records only the ones it allowlists, and any other claim (<c>offline_access</c>, <c>Operator</c>) never reaches it.
    /// </summary>
    internal sealed class ObservabilityInboxAccess(NotificationsHosting hosting, IOptions<AuthOptions> auth, IOptions<PlatformOptions> platform,
        IServiceProvider? services = null)
        : SingleTenantInboxAccess(hosting.EmbeddedTenantId ?? throw new InvalidOperationException("The notification pipeline isn't embedded: it has no tenant."))
    {
        protected override async ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, CancellationToken ct)
        {
            var result = Person(user, out var forgetSubject);
            if (forgetSubject is not null)
            {
                await ForgetRolesAsync(forgetSubject, ct);
            }
            return result;
        }

        /// <summary>
        /// A person of this deployment whose token has no Console role any more: the roles recorded for them stop counting now (not after the
        /// 30 days), and nobody is made a subscriber for it. Never fails the refusal it goes with.
        /// </summary>
        private async Task ForgetRolesAsync(string subject, CancellationToken ct)
        {
            if (services?.GetService<Juice.Notifications.Data.NotificationsDbContext>() is not { } db)
            {
                return;
            }
            try
            {
                var tenant = TenantId;
                await db.SubscriberRoles.Where(r => r.TenantId == tenant && db.Subscribers.Any(s => s.TenantId == tenant && s.Id == r.SubscriberId
                    && s.Source == InboxPeople.OidcSource && s.ExternalId == subject)).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The refusal stands whether or not the rows could be removed (the schema not migrated yet, the database away).
            }
        }

        private InboxAccessResult Person(ClaimsPrincipal user, out string? forgetSubject)
        {
            forgetSubject = null;
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
                forgetSubject = subject;
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

        // Apart, so a host with the inbox off loads no SignalR nor its connection stack (the JIT resolves what a method calls when it compiles it).
        // The inbox package itself does load (this class names its types); InboxOffProcessTests probes a host process for it.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Add(IServiceCollection services)
        {
            services.AddJuiceNotificationsInbox();
            services.AddScoped<INotificationsInboxAccess, ObservabilityInboxAccess>();
            // The hub's token is in the query string, and the host's request log writes the query: it would put every session's token in the log.
            // A provider's own rule (Logging:Console:LogLevel:…) outranks a global one, so the rule is added for every provider the configuration names too.
            services.PostConfigure<LoggerFilterOptions>(options =>
            {
                var providers = options.Rules.Select(r => r.ProviderName).Where(p => p is not null).Distinct().ToList();
                // A wildcard rule (<c>Microsoft.AspNetCore.Hosting*</c>) that covers the category can outrank an exact one, whatever order they are in: its
                // own filter keeps this category at Warning, and it is the same rule for everything else.
                for (var i = 0; i < options.Rules.Count; i++)
                {
                    var rule = options.Rules[i];
                    if (rule.CategoryName is { } name && name.EndsWith('*') && RequestLogCategory.StartsWith(name[..^1], StringComparison.Ordinal))
                    {
                        var inner = rule.Filter;
                        options.Rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                            (provider, category, level) => category == RequestLogCategory ? level >= LogLevel.Warning : inner?.Invoke(provider, category, level) ?? true);
                    }
                }
                options.Rules.Add(new LoggerFilterRule(null, RequestLogCategory, LogLevel.Warning, null));
                foreach (var provider in providers)
                {
                    options.Rules.Add(new LoggerFilterRule(provider, RequestLogCategory, LogLevel.Warning, null));
                }
            });
        }

        /// <summary>The category of the host's "Request starting" and "Request finished" lines, which write the query string.</summary>
        internal const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

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
