using Juice.MediatR;
using Juice.Messaging.Local;
using Juice.Messaging.Outbox.Delivery.Internal;
using Juice.Messaging.Policies;
using Juice.Observability.Agents;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Juice.Observability.Notifications;
using Juice.Observability.Rules;
using Juice.Observability.Slos;

namespace Juice.Observability.Api
{
    /// <summary>
    /// The platform in one call each, for a host of the customer's own (requirements §2.1: one deployment, one tenant):
    /// <see cref="AddJuiceObservabilityFiles"/> on the configuration, <see cref="AddJuiceObservability"/> on the services and
    /// <see cref="MapJuiceObservability"/> on the app. The host owns everything else in its pipeline: static files, the
    /// Console fallback route, security headers.
    /// </summary>
    public static class PlatformExtensions
    {
        /// <summary>
        /// Adds the rules and SLO files named by <c>Rules:File</c> and <c>Slos:File</c>: version-controlled
        /// deployment configuration, loaded once at startup (requirements §6.1).
        /// </summary>
        public static IConfigurationManager AddJuiceObservabilityFiles(this IConfigurationManager configuration)
        {
            foreach (var key in new[] { "Rules:File", "Slos:File" })
            {
                if (configuration[key] is { Length: > 0 } file)
                {
                    configuration.AddJsonFile(Path.GetFullPath(file), optional: false, reloadOnChange: false);
                }
            }
            return configuration;
        }

        /// <summary>
        /// The platform database, tenant provisioning, the discovery, health and rule workers (each switched by
        /// its own <c>Enabled</c> setting), OIDC authentication, problem details, the <c>ready</c> health check, and the platform's own metrics
        /// (<see cref="PlatformTelemetry.AddPlatformTelemetry"/>).
        /// </summary>
        public static IServiceCollection AddJuiceObservability(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<PlatformDbContext>((sp, options) =>
            {
                PlatformDbContext.Configure(options, configuration.GetConnectionString("Platform")
                    ?? throw new InvalidOperationException("ConnectionStrings:Platform is required"));
                // A notification request written in a command's transaction is taken at once when it commits.
                options.AddInterceptors(sp.GetRequiredService<PlatformCommitActions>());
            });
            // Before the discovery worker: hosted services start in registration order, and discovery needs the tenant.
            services.AddHostedService<TenantProvisioner>();
            // After it, since the provisioner applies migrations: the outbox's delivery starts reading its tables at once.
            services.AddPlatformMessaging();
            services.Configure<LocalChannelOptions>(configuration.GetSection(LocalChannelSection));
            // After the platform's messaging and the provisioner: its schema migrates after the platform's (I60).
            services.AddPlatformNotifications(configuration);
            services.AddPlatformNotificationsAdmin();
            // Opt-in (Notifications:Inbox:Enabled): the people of the platform's own sign-in get inbox items.
            services.AddPlatformNotificationsInbox(configuration);
            services.AddPlatformDiscovery(configuration);
            services.AddPlatformHealth(configuration);
            services.AddPlatformRules(configuration);
            services.AddPlatformSlos(configuration);
            services.AddPlatformAgents(configuration);
            services.Configure<IntegrationOptions>(configuration.GetSection(IntegrationOptions.Section));
            services.AddPlatformAuthentication(configuration);
            services.AddPlatformProblemDetails();
            // A telemetry query refused for load, or ClickHouse out of reach: 503 "Busy, try again" (Phase 11 R11), handled by the host's UseExceptionHandler.
            services.AddExceptionHandler<TelemetryStoreExceptionHandler>();
            // The notification pipeline's concurrent_change, should one still reach a request: 409, not 500.
            services.AddExceptionHandler<NotificationsConflictExceptionHandler>();
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
            // The platform's own metrics (Phase 11, docs/platform-metrics.md): on by default, exported when Telemetry:Otlp:Endpoint is set.
            services.AddPlatformTelemetry(configuration);
            // The workers' heartbeats and the platform's status (Phase 11 R6, R7): /health/platform and /api/v1/platform/status.
            services.AddSingleton<HeartbeatWriter>();
            services.AddHostedService(sp => sp.GetRequiredService<HeartbeatWriter>());
            services.AddSingleton<PlatformStatusService>();
            // The Overview's platform panel and the capacity view (Phase 11 R8, R9): /api/v1/platform/overview.
            services.Configure<CapacityOptions>(configuration.GetSection(CapacityOptions.Section));
            services.AddSingleton<CapacityCalibrationStore>();
            services.AddSingleton<PlatformOverviewService>();
            return services;
        }

        /// <summary>Configuration section of the local channel: <c>MaxConcurrency</c>, how many messages are handled at once.</summary>
        public const string LocalChannelSection = "Messaging:Local";

        /// <summary>Messages handled at once on the local channel unless <c>Messaging:Local:MaxConcurrency</c> sets another bound.</summary>
        public const int DefaultLocalConcurrency = 8;

        /// <summary>
        /// The mediator, with <see cref="PlatformTransactionBehavior{TRequest, TResponse}"/> around every command, and the outbox in the platform database.
        /// Every message goes on the <c>local</c> route: dispatched in this process right after its transaction commits, and delivered from the outbox, with
        /// retries, if that fails or the process stops first. A consumer's own check keeps a message handled twice (once each way, or on two replicas) from
        /// acting twice, so the in-memory guard only saves work.
        /// </summary>
        public static IServiceCollection AddPlatformMessaging(this IServiceCollection services)
        {
            services.AddMediatR(mediator => mediator
                .RegisterServicesFromAssemblyContaining<EvaluateRule>()
                .RegisterServicesFromAssemblyContaining<PlanNotifications>()
                .RegisterServicesFromAssemblyContaining<EvaluateSloBurns>()
                .RegisterServicesFromAssemblyContaining<ChangeRule>(includeNonPublicTypes: true));
            // Around the platform's own commands only: a product embedded in the host (the notification pipeline) has its own transaction.
            services.AddScoped(typeof(PlatformTransactionBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PlatformCommandsTransaction<,>));
            var messaging = services.AddMessaging();
            messaging.AddOutbox();
            messaging.AddLocalChannel();
            // The channel starts a handler for every message, with no bound unless one is set. 102 SLO burn alerts opened at once were 102
            // handlers planning notifications together, each holding a PostgreSQL connection, past the server's limit (docs/scale-tests.md,
            // the workers ceiling). Bounded, the rest wait in the channel. Messaging:Local:MaxConcurrency changes it.
            services.PostConfigure<LocalChannelOptions>(options =>
                options.MaxConcurrency = options.MaxConcurrency is > 0 ? options.MaxConcurrency : DefaultLocalConcurrency);
            messaging.AddLocalPublisher();
            messaging.AddIdempotencyInMemory();
            messaging.AddMessageService<PlatformDbContext>();
            messaging.AddPublishingPolicies<LocalRoute>();
            messaging.AddDelivery(delivery =>
            {
                // The framework's default gives up after 3 retries, about 35 seconds: a database restart would lose an alert's notifications. These keep
                // trying for about 85 minutes (5 s doubling, 10 retries), check every 2 seconds (as the 1.x webhook dispatcher did), and take back a message
                // whose handler has run a minute, which planning never needs.
                delivery.AddDeliveryPolicies(options => options.DefaultPolicy = new PolicyConfiguration
                {
                    Interval = TimeSpan.FromSeconds(2), BatchSize = 50, Timeout = TimeSpan.FromMinutes(1),
                    InitialRetryDelay = TimeSpan.FromSeconds(5), RetryDelayMultiplier = 2, MaxRetryAttempts = 10,
                });
                delivery.AddDeliveryProcessor<PlatformDbContext>(LocalRoute.Publisher);
            });
            return services;
        }

        /// <summary>The platform publishes only to itself, so every message takes the <c>local</c> route.</summary>
        private sealed class LocalRoute : IMessagePublishingPolicy
        {
            public const string Publisher = "local";
            private static readonly IReadOnlyCollection<PublishRoute> Routes = [new PublishRoute(Publisher, string.Empty)];

            public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context) => ValueTask.FromResult(Routes);
        }

        /// <summary>
        /// <c>/health/live</c>, <c>/health/ready</c> and <c>/health/platform</c> (anonymous), the platform API under <c>/api/v1</c>, and the platform's
        /// own status at <c>/api/v1/platform/status</c> (operators).
        /// </summary>
        public static IEndpointRouteBuilder MapJuiceObservability(this IEndpointRouteBuilder app)
        {
            app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            app.MapPlatformStatus();
            app.MapInventoryEndpoints();
            app.MapPlatformNotificationsAdmin();
            app.MapPlatformNotificationsInbox();
            // Answers only on the agent listener, and only when agents are enabled (Agents:Enabled).
            app.MapAgentApi();
            return app;
        }
    }
}
