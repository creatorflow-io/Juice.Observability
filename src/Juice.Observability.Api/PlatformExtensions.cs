using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Juice.Observability.Rules;
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
        /// Adds the rules and webhooks files named by <c>Rules:File</c> and <c>Webhooks:File</c>: version-controlled
        /// deployment configuration, loaded once at startup (requirements §6.1).
        /// </summary>
        public static IConfigurationManager AddJuiceObservabilityFiles(this IConfigurationManager configuration)
        {
            foreach (var key in new[] { "Rules:File", "Webhooks:File" })
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
            services.AddPlatformDiscovery(configuration);
            services.AddPlatformHealth(configuration);
            services.AddPlatformRules(configuration);
            services.AddPlatformWebhooks(configuration);
            services.Configure<IntegrationOptions>(configuration.GetSection(IntegrationOptions.Section));
            services.AddPlatformAuthentication(configuration);
            services.AddPlatformProblemDetails();
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
            return services;
        }

        /// <summary><c>/health/live</c>, <c>/health/ready</c> (anonymous) and the platform API under <c>/api/v1</c>.</summary>
        public static IEndpointRouteBuilder MapJuiceObservability(this IEndpointRouteBuilder app)
        {
            app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            app.MapInventoryEndpoints();
            return app;
        }
    }
}
