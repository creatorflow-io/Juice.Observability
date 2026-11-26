using Juice.Observability.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications.Migration
{
    public static class MigrationRegistration
    {
        /// <summary>The configuration section of the migration's own settings: <c>Notifications:Migration</c>.</summary>
        public const string Section = "Notifications:Migration";

        /// <summary>
        /// The migration tool (phase-5-plan I70, M46b): <see cref="MigrationPlanner"/>, scoped with the pipeline's <c>NotificationsDbContext</c>.
        /// Its email endpoints send through the deployment's server named <c>Notifications:Migration:SmtpServer</c> in notifications'
        /// <c>Delivery:SmtpServers</c>, from <c>Notifications:Migration:SmtpFrom</c>, by default legacy's <c>Webhooks:Smtp:From</c>. Register
        /// after <c>AddPlatformNotifications</c>.
        /// </summary>
        public static IServiceCollection AddPlatformNotificationsMigration(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<NotificationsMigrationOptions>()
                .Bind(configuration.GetSection(Section))
                .PostConfigure(o => o.SmtpFrom ??= configuration[$"{WebhookOptions.Section}:Smtp:From"] is { Length: > 0 } from ? from : null);
            services.AddScoped<MigrationPlanner>();
            return services;
        }
    }
}
