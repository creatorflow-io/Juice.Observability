using Juice.Observability.Webhooks;
using Juice.Observability.Rules.Notifications;
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
        /// after <c>AddPlatformNotifications</c> and <c>AddPlatformWebhooks</c>, whose provisioner the alias wraps.
        /// </summary>
        public static IServiceCollection AddPlatformNotificationsMigration(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<NotificationsMigrationOptions>()
                .Bind(configuration.GetSection(Section))
                .PostConfigure(o => o.SmtpFrom ??= configuration[$"{WebhookOptions.Section}:Smtp:From"] is { Length: > 0 } from ? from : null);
            services.AddScoped<MigrationPlanner>();
            services.AddScoped<WebhookAlias>();
            // The alias and the migration write as webhooks-alias, whose secrets notifications checks against its alias limits (M46-3, waves
            // §5.3), never the API's: they are the legacy API's own (Webhooks:ApiSecretEnvPrefix, ApiSecretDirectory), unless the deployment
            // sets them in Notifications:Delivery. The notifications API's ApiSecret* limits stay as they are, so its admin can't name these.
            var delivery = configuration.GetSection("Notifications:Delivery");
            services.PostConfigure<Juice.Notifications.Providers.DeliveryOptions>(o =>
            {
                var legacy = configuration.GetSection(WebhookOptions.Section).Get<WebhookOptions>() ?? new WebhookOptions();
                if (delivery[nameof(o.AliasSecretEnvPrefix)] is null)
                {
                    o.AliasSecretEnvPrefix = legacy.ApiSecretEnvPrefix;
                }
                if (delivery[nameof(o.AliasSecretDirectory)] is null)
                {
                    o.AliasSecretDirectory = legacy.ApiSecretDirectory;
                }
            });
            // Webhooks:Endpoints through the alias (I69): legacy's provisioner wrapped, in its place.
            AliasProvisioner.Wrap(services);
            return services;
        }
    }
}
