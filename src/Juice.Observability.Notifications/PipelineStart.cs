using Juice.Notifications.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// What the pipeline does at start before it takes anything: migrate its schema (I60, after the platform's), load its provisioning file, and
    /// declare the platform's application and types (I63). A failure here fails readiness (<c>/health/ready</c>, check <c>notifications</c>), not
    /// the host: the alert evaluator, the webhooks and the API go on (phase-5-plan risk table). After a failure, what follows it is skipped, since it
    /// needs what failed. Restart the host once the cause is fixed.
    /// <para>The pipeline's own migrator throws at start, so it is off (<c>Database:Migrate=false</c> in its settings) and the platform migrates
    /// the schema in its place, by <see cref="NotificationsDatabase.MigrateAsync"/>.</para>
    /// </summary>
    public sealed class PipelineStart
    {
        internal const string Migrator = "Juice.Notifications.Hosting.DatabaseMigrator";

        /// <summary>The pipeline's provisioning at start, after the migrator, in the order the pipeline registers them.</summary>
        internal static readonly string[] Provisioning =
        [
            "Juice.Notifications.Configuration.ConfigurationProvisioner",
            "Juice.Notifications.Embedded.EmbeddedApplicationProvisioner",
        ];

        private volatile string? _failure;

        /// <summary>What failed at start, or null.</summary>
        public string? Failure => _failure;

        internal void Fail(string step, Exception error) => _failure ??= $"{step}: {error.Message}";

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _skipped = new();

        /// <summary>The steps skipped because one before them failed.</summary>
        public IReadOnlyCollection<string> Skipped => _skipped;

        /// <summary>
        /// Puts the platform's migration of the schema (when <paramref name="migrate"/>) where the pipeline's migrator was, and records rather than
        /// throws a failure of it or of the provisioning after it. Call after the pipeline is registered.
        /// </summary>
        internal static void Guard(IServiceCollection services, bool migrate)
        {
            services.AddSingleton<PipelineStart>();
            Replace(services, Migrator, (sp, _) => new SchemaMigrator(sp.GetRequiredService<IServiceScopeFactory>(), migrate,
                sp.GetRequiredService<ILogger<PipelineStart>>()));
            foreach (var name in Provisioning)
            {
                Replace(services, name, (sp, type) => ActivatorUtilities.CreateInstance(sp, type) as IHostedService
                    ?? throw new InvalidOperationException($"{name} isn't a hosted service"));
            }
            services.AddHealthChecks().AddCheck<PipelineStartCheck>("notifications", tags: ["ready"]);
        }

        private static void Replace(IServiceCollection services, string name, Func<IServiceProvider, Type, IHostedService> create)
        {
            var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType?.FullName == name)
                ?? throw new InvalidOperationException($"The notification pipeline no longer registers {name} at start: review PipelineStart.");
            var type = descriptor.ImplementationType!;
            // In its place: hosted services start in the order they are registered.
            services[services.IndexOf(descriptor)] = ServiceDescriptor.Singleton<IHostedService>(sp => new GuardedStart(create(sp, type), type.Name,
                sp.GetRequiredService<PipelineStart>(), sp.GetRequiredService<ILogger<PipelineStart>>()));
        }

        /// <summary>The schema's pending migrations, applied by the platform (I60).</summary>
        private sealed class SchemaMigrator(IServiceScopeFactory scopes, bool migrate, ILogger logger) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
                => migrate ? NotificationsDatabase.MigrateAsync(scopes, logger, cancellationToken) : Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class GuardedStart(IHostedService inner, string step, PipelineStart state, ILogger logger) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                if (state.Failure is not null)
                {
                    state._skipped.Enqueue(step);
                    logger.LogWarning("Notification pipeline: {Step} skipped, since its start failed ({Failure})", step, state.Failure);
                    return;
                }
                try
                {
                    await inner.StartAsync(cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    state.Fail(step, ex);
                    logger.LogError(ex, "Notification pipeline: {Step} failed at start; the platform runs on, not ready, until the host is restarted", step);
                }
            }

            public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);
        }
    }

    internal sealed class PipelineStartCheck(PipelineStart start) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(start.Failure is { } failure
                ? HealthCheckResult.Unhealthy($"The notification pipeline didn't start: {failure}")
                : HealthCheckResult.Healthy());
    }
}
