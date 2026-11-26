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
        /// platform's does (<c>Database:Migrate</c>). <c>Notifications:Pipeline</c>: <c>legacy</c> (the default) runs none of its stages;
        /// <c>shadow</c> runs them in dry run beside legacy; <c>new</c> is the only path.
        /// </summary>
        public static IServiceCollection AddPlatformNotifications(this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection(PlatformNotificationsOptions.Section);
            var mode = section.GetValue(nameof(PlatformNotificationsOptions.Pipeline), NotificationsPipeline.Legacy);
            // Shadow must never really send beside legacy (M45 review focus 5): without the pipeline's dry run it would.
            if (mode == NotificationsPipeline.Shadow && !DryRunAvailable)
            {
                throw new InvalidOperationException("Notifications:Pipeline 'shadow' needs the notification pipeline's dry run (Juice.Notifications M44-2), "
                    + "which this version doesn't have: it would send for real beside legacy. Use 'legacy' or 'new'.");
            }
            services.Configure<PlatformNotificationsOptions>(section);
            services.AddSingleton<PlatformCommitActions>();
            services.AddScoped<PlatformNotificationTransaction>();
            services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<PlatformNotificationTransaction>());
            var before = services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
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
            // Its changes to the setup, as the platform's audit records, in the change's transaction (I62).
            services.AddScoped<INotificationsAuditSink, PlatformAuditSink>();
            // The schema migrated by the platform, and the pipeline's start failing readiness, not the host.
            var guarded = PipelineStart.Guard(services, MigratesSchema(section, configuration));
            // The producer adapter (I64): the platform's domain events and action request notices, in their own transactions.
            services.AddScoped<ProducerAdapter>();
            services.AddScoped<IActionRequestNotifications, ActionRequestNotifications>();
            services.AddScoped<SilenceHolds>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertNotificationsPlanned>, OnNotificationsPlanned>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertAcknowledged>, OnAlertAcknowledged>();
            if (mode != NotificationsPipeline.Legacy)
            {
                // After the migrations and the provisioning (hosted services start in order).
                services.AddHostedService<NotificationsReleasePass>();
                services.AddHostedService<SilenceHoldPass>();
            }
            // After a failed start, none of the pipeline's other services starts (they would poll tables that aren't there); in legacy, none runs.
            PipelineStart.GateTheRest(services, before, guarded, runs: mode != NotificationsPipeline.Legacy);
            // The platform's transaction leaves the pipeline's commands to the pipeline's own (by its list, not by name).
            services.AddSingleton<IEmbeddedCommands, PipelineCommands>();
            Coexistence.Isolate(services);
            return services;
        }

        /// <summary>
        /// Whether the pipeline's dry run works: its published capability (<c>Juice.Notifications.NotificationsCapabilities.DryRun</c>, M44-2, a
        /// property so it is read at run time rather than inlined). Read by reflection, so an alpha without it counts as without a dry run.
        /// </summary>
        internal static bool DryRunAvailable { get; } = DryRunIn(typeof(INotificationClient).Assembly.GetType("Juice.Notifications.NotificationsCapabilities"));

        /// <summary>The <c>DryRun</c> capability of <paramref name="capabilities"/>: false when the type, or its static bool property, is absent.</summary>
        internal static bool DryRunIn(Type? capabilities)
            => capabilities?.GetProperty("DryRun", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) is true;

        /// <summary>
        /// Whether the platform migrates the pipeline's schema at start: when it migrates its own (<c>Database:Migrate</c>), unless the section
        /// says otherwise (<c>Notifications:Database:Migrate</c>).
        /// </summary>
        internal static bool MigratesSchema(IConfigurationSection section, IConfiguration configuration)
            => (section.GetValue<bool?>("Database:Migrate") ?? configuration.GetValue<bool?>("Database:Migrate")) == true;

        /// <summary>
        /// The pipeline's settings: the host's <c>Notifications</c> section, with the switch's decisions on top. <c>legacy</c> runs no stage
        /// (<c>Stages:Run=false</c>); <c>shadow</c> runs them in dry run (<c>Delivery:DryRun=true</c>), which records what would be sent and sends
        /// nothing; <c>new</c> runs them as the section says. The pipeline never migrates at start itself (its migrator throws on a failure):
        /// <see cref="PipelineStart"/> does.
        /// </summary>
        internal static IConfiguration PipelineSettings(IConfigurationSection section, IConfiguration configuration, NotificationsPipeline mode)
        {
            var overrides = new Dictionary<string, string?> { ["Database:Migrate"] = "false" };
            if (mode == NotificationsPipeline.Legacy)
            {
                overrides["Stages:Run"] = "false";
            }
            if (mode == NotificationsPipeline.Shadow)
            {
                overrides["Delivery:DryRun"] = "true";
            }
            return new ConfigurationBuilder().AddConfiguration(section).AddInMemoryCollection(overrides).Build();
        }
    }
}
