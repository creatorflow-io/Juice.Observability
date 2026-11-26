using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications
{
    public static class NotificationsRegistration
    {
        /// <summary>The platform's application in the pipeline: its types are <c>observability.*</c>.</summary>
        public const string ApplicationKey = "observability";

        public const string ApplicationName = "Juice Observability";

        /// <summary>
        /// The notification pipeline in the platform's host (phase-5-plan I57), on the platform's PostgreSQL (its <c>notifications</c> schema) and
        /// tenant (the platform tenant's Id, never one from input), writing in the platform's transaction (<see cref="PlatformNotificationTransaction"/>).
        /// Register after the platform's messaging and the tenant provisioner: the pipeline's schema migrates after the platform's (I60), when the
        /// platform's does (<c>Database:Migrate</c>). <c>legacy</c>, the default and the only mode until M45, runs none of its stages.
        /// </summary>
        public static IServiceCollection AddPlatformNotifications(this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection(PlatformNotificationsOptions.Section);
            var mode = section.GetValue(nameof(PlatformNotificationsOptions.Pipeline), NotificationsPipeline.Legacy);
            if (mode != NotificationsPipeline.Legacy)
            {
                throw new InvalidOperationException($"Notifications:Pipeline '{mode}' isn't available yet: only 'legacy' is, until M45 adds 'shadow' and 'new'.");
            }
            services.Configure<PlatformNotificationsOptions>(section);
            services.AddSingleton<PlatformCommitActions>();
            services.AddScoped<PlatformNotificationTransaction>();
            services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<PlatformNotificationTransaction>());
            services.AddJuiceNotificationsEmbedded(PipelineSettings(section, configuration, mode), embedded =>
            {
                embedded.ConnectionString = configuration.GetConnectionString("Platform") ?? throw new InvalidOperationException("ConnectionStrings:Platform is required");
                // The platform tenant's Id, refused as the tenant provisioner refuses it.
                embedded.TenantId = PlatformOptions.TenantId(configuration[$"{PlatformOptions.Section}:{nameof(PlatformOptions.TenantKey)}"]);
                embedded.ApplicationKey = ApplicationKey;
                embedded.ApplicationName = ApplicationName;
                embedded.Types.AddRange(ObservabilityTypes.All());
            });
            services.AddChatProviders();
            // The schema migrated by the platform, and the pipeline's start failing readiness, not the host.
            PipelineStart.Guard(services, MigratesSchema(section, configuration));
            // The producer adapter (I64): the platform's domain events and action request notices, in their own transactions.
            services.AddScoped<ProducerAdapter>();
            services.AddScoped<IActionRequestNotifications, ActionRequestNotifications>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertNotificationsPlanned>, OnNotificationsPlanned>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertAcknowledged>, OnAlertAcknowledged>();
            Coexistence.Isolate(services);
            return services;
        }

        /// <summary>
        /// Whether the platform migrates the pipeline's schema at start: when it migrates its own (<c>Database:Migrate</c>), unless the section
        /// says otherwise (<c>Notifications:Database:Migrate</c>).
        /// </summary>
        internal static bool MigratesSchema(IConfigurationSection section, IConfiguration configuration)
            => (section.GetValue<bool?>("Database:Migrate") ?? configuration.GetValue<bool?>("Database:Migrate")) == true;

        /// <summary>
        /// The pipeline's settings: the host's <c>Notifications</c> section, with the switch's decisions on top. <c>legacy</c> runs no stage
        /// (<c>Stages:Run=false</c>). The pipeline never migrates at start itself (its migrator throws on a failure): <see cref="PipelineStart"/> does.
        /// </summary>
        internal static IConfiguration PipelineSettings(IConfigurationSection section, IConfiguration configuration, NotificationsPipeline mode)
        {
            var overrides = new Dictionary<string, string?> { ["Database:Migrate"] = "false" };
            if (mode == NotificationsPipeline.Legacy)
            {
                overrides["Stages:Run"] = "false";
            }
            return new ConfigurationBuilder().AddConfiguration(section).AddInMemoryCollection(overrides).Build();
        }
    }
}
