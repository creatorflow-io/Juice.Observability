using Juice.MediatR;
using Juice.Messaging.Outbox.Delivery.Internal;
using Juice.Messaging.Policies;
using Juice.Observability.Agents;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Juice.Observability.Rules;
using Juice.Observability.Slos;
using Juice.Observability.Webhooks;

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
        /// Adds the rules, webhooks and SLO files named by <c>Rules:File</c>, <c>Webhooks:File</c> and <c>Slos:File</c>: version-controlled
        /// deployment configuration, loaded once at startup (requirements §6.1).
        /// </summary>
        public static IConfigurationManager AddJuiceObservabilityFiles(this IConfigurationManager configuration)
        {
            foreach (var key in new[] { "Rules:File", "Webhooks:File", "Slos:File" })
            {
                if (configuration[key] is { Length: > 0 } file)
                {
                    configuration.AddJsonFile(Path.GetFullPath(file), optional: false, reloadOnChange: false);
                }
            }
            return configuration;
        }

        /// <summary>
        /// The platform database, tenant provisioning, the discovery, health, rule and webhook workers (each switched by
        /// its own <c>Enabled</c> setting), OIDC authentication, problem details and the <c>ready</c> health check.
        /// </summary>
        public static IServiceCollection AddJuiceObservability(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDbContext<PlatformDbContext>(options =>
                PlatformDbContext.Configure(options, configuration.GetConnectionString("Platform")
                    ?? throw new InvalidOperationException("ConnectionStrings:Platform is required")));
            // Before the discovery worker: hosted services start in registration order, and discovery needs the tenant.
            services.AddHostedService<TenantProvisioner>();
            // After it, since the provisioner applies migrations: the outbox's delivery starts reading its tables at once.
            services.AddPlatformMessaging();
            services.AddPlatformDiscovery(configuration);
            services.AddPlatformHealth(configuration);
            services.AddPlatformRules(configuration);
            services.AddPlatformSlos(configuration);
            services.AddPlatformWebhooks(configuration);
            services.AddPlatformAgents(configuration);
            services.Configure<IntegrationOptions>(configuration.GetSection(IntegrationOptions.Section));
            services.AddPlatformAuthentication(configuration);
            services.AddPlatformProblemDetails();
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
            return services;
        }

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
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PlatformTransactionBehavior<,>));
            var messaging = services.AddMessaging();
            messaging.AddOutbox();
            messaging.AddLocalChannel();
            messaging.AddLocalPublisher();
            messaging.AddIdempotencyInMemory();
            messaging.AddMessageService<PlatformDbContext>();
            messaging.AddPublishingPolicies<LocalRoute>();
            messaging.AddDelivery(delivery =>
            {
                // The framework's default gives up after 3 retries, about 35 seconds: a database restart would lose an alert's notifications. These keep
                // trying for about 85 minutes (5 s doubling, 10 retries), check every 2 seconds as the webhook dispatcher does, and take back a message
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

        /// <summary><c>/health/live</c>, <c>/health/ready</c> (anonymous) and the platform API under <c>/api/v1</c>.</summary>
        public static IEndpointRouteBuilder MapJuiceObservability(this IEndpointRouteBuilder app)
        {
            app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            app.MapInventoryEndpoints();
            // Answers only on the agent listener, and only when agents are enabled (Agents:Enabled).
            app.MapAgentApi();
            return app;
        }
    }
}
