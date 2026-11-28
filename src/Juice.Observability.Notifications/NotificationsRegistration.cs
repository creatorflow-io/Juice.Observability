using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health.DeadMan;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        /// platform's does (<c>Database:Migrate</c>). It is the only pipeline since 2.0: a configuration that still names the removed ones
        /// (<see cref="LegacyConfiguration"/>) refuses the host's start.
        /// </summary>
        public static IServiceCollection AddPlatformNotifications(this IServiceCollection services, IConfiguration configuration)
        {
            LegacyConfiguration.Check(configuration);
            var section = configuration.GetSection(PlatformNotificationsOptions.Section);
            AddPlatformNotificationsOptions(services, configuration);
            if (LegacyConfiguration.PipelineKeyPresent(configuration))
            {
                services.AddHostedService<ObsoleteKeyNotice>();
            }
            services.AddSingleton<PlatformCommitActions>();
            services.AddScoped<PlatformNotificationTransaction>();
            services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<PlatformNotificationTransaction>());
            // The alert transitions the outbox hands over, planned as requests (the platform's rules, in a command of its own).
            services.AddMessaging().AddLocalConsumer(consumer => consumer.Subscribe<Juice.Observability.Rules.AlertTransitionedIntegrationEvent, AlertTransitionedConsumer>());
            var before = services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
            var settings = PipelineSettings(section);
            var tenantId = PlatformOptions.TenantId(configuration[$"{PlatformOptions.Section}:{nameof(PlatformOptions.TenantKey)}"]);
            services.AddJuiceNotificationsEmbedded(settings, embedded =>
            {
                embedded.ConnectionString = configuration.GetConnectionString("Platform") ?? throw new InvalidOperationException("ConnectionStrings:Platform is required");
                // The platform tenant's Id, refused as the tenant provisioner refuses it.
                embedded.TenantId = tenantId;
                embedded.ApplicationKey = ApplicationKey;
                embedded.ApplicationName = ApplicationName;
                embedded.Types.AddRange(ObservabilityTypes.All());
                // The inbox, when opted in (M54): the three Console roles are recorded from people's sign-ins, and the alerts name the configured ones.
                // Off, nothing is recorded and the host is what it was.
                if (section.GetValue<bool>($"Inbox:{nameof(InboxNotificationsOptions.Enabled)}"))
                {
                    embedded.Inbox = new EmbeddedInboxOptions();
                    embedded.Inbox.Roles.AddRange(InboxNotificationsOptions.KnownRoles);
                }
            });
            services.AddChatProviders();
            // Its changes to the setup, as the platform's audit records, in the change's transaction (I62).
            services.AddScoped<INotificationsAuditSink, PlatformAuditSink>();
            // The schema migrated by the platform, and the pipeline's start failing readiness, not the host.
            var guarded = PipelineStart.Guard(services, MigratesSchema(section, configuration), settings["Provisioning:Path"], tenantId);
            // The producer adapter (I64): the platform's domain events and action request notices, in their own transactions.
            services.AddScoped<PipelinePolicies>();
            services.AddScoped<ProducerAdapter>();
            services.AddScoped<IActionRequestNotifications, ActionRequestNotifications>();
            services.AddScoped<SilenceHolds>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertNotificationsPlanned>, OnNotificationsPlanned>();
            services.AddTransient<Juice.MediatR.INotificationHandler<AlertAcknowledged>, OnAlertAcknowledged>();
            // After the migrations and the provisioning (hosted services start in order).
            services.AddHostedService<NotificationsReleasePass>();
            services.AddHostedService<SilenceHoldPass>();
            // After a failed start, none of the pipeline's other services starts (they would poll tables that aren't there).
            PipelineStart.GateTheRest(services, before, guarded);
            // The platform's transaction leaves the pipeline's commands to the pipeline's own (by its list, not by name).
            services.AddSingleton<IEmbeddedCommands, PipelineCommands>();
            // After the gating: the dead-man must run when the pipeline doesn't (a failed start is when it matters most).
            AddDeadMan(services, configuration, section);
            Coexistence.Isolate(services);
            return services;
        }

        /// <summary>The platform's <c>Notifications</c> options, checked at start. <c>"Roles": []</c> binds to nothing, as an absent key does: a key that is there is an empty list.</summary>
        internal static void AddPlatformNotificationsOptions(IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection(PlatformNotificationsOptions.Section);
            services.AddOptions<PlatformNotificationsOptions>().Bind(section)
                .PostConfigure(options =>
                {
                    if (options.Inbox.Roles is null && section.GetSection("Inbox:Roles").Exists())
                    {
                        options.Inbox.Roles = [];
                    }
                })
                .ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PlatformNotificationsOptions>, PlatformNotificationsOptionsValidation>());
        }

        /// <summary>
        /// The dead-man alarm (M49), when <c>Notifications:DeadMan:Url</c> is set: checked at start (refused below its documented minimums, which
        /// depend on the stages' poll interval), one replica at a time, reported in readiness (<c>notifications-deadman</c>).
        /// </summary>
        internal static void AddDeadMan(IServiceCollection services, IConfiguration configuration, IConfigurationSection section)
        {
            var deadMan = configuration.GetSection(DeadManOptions.Section);
            if (string.IsNullOrWhiteSpace(deadMan[nameof(DeadManOptions.Url)]))
            {
                return;
            }
            var options = deadMan.Get<DeadManOptions>() ?? new DeadManOptions();
            if (options.Problems(LongestPoll(section)) is { Count: > 0 } problems)
            {
                throw new InvalidOperationException("The notification pipeline's dead-man: " + string.Join(" ", problems));
            }
            services.Configure<DeadManOptions>(deadMan);
            // Whether the pipeline started, and what was kept for it (not-started, kept): its own health reads quiet when it didn't start.
            services.AddScoped<IPipelineKeptReport, PipelineKeptReport>();
            services.AddSingleton<DeadManSender>();
            services.AddHostedService(sp => sp.GetRequiredService<DeadManSender>());
            services.AddHealthChecks().AddCheck<DeadManHealthCheck>(DeadManSender.Worker, tags: ["ready"]);
        }

        /// <summary>The longest poll interval of the pipeline's stages (<c>Notifications:Stages:{stage}:Poll</c>; 1 s by default).</summary>
        internal static TimeSpan LongestPoll(IConfigurationSection section)
            => section.GetSection("Stages").GetChildren().Select(s => s.GetValue<TimeSpan?>("Poll")).Where(p => p is not null).Select(p => p!.Value)
                .DefaultIfEmpty(TimeSpan.FromSeconds(1)).Max();


        /// <summary>
        /// Whether the platform migrates the pipeline's schema at start: when it migrates its own (<c>Database:Migrate</c>), unless the section
        /// says otherwise (<c>Notifications:Database:Migrate</c>).
        /// </summary>
        internal static bool MigratesSchema(IConfigurationSection section, IConfiguration configuration)
            => (section.GetValue<bool?>("Database:Migrate") ?? configuration.GetValue<bool?>("Database:Migrate")) == true;

        /// <summary>
        /// The pipeline's settings: the host's <c>Notifications</c> section. The pipeline never migrates at start itself (its migrator throws on a
        /// failure): <see cref="PipelineStart"/> does.
        /// </summary>
        internal static IConfiguration PipelineSettings(IConfigurationSection section)
            => new ConfigurationBuilder().AddConfiguration(section).AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Migrate"] = "false" }).Build();
    }
}
