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
    /// the host: the alert evaluator, the other workers and the API go on (phase-5-plan risk table). After a failure, what follows it is skipped, since it
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
        internal static IReadOnlyCollection<ServiceDescriptor> Guard(IServiceCollection services, bool migrate, string? provisioningPath, Guid tenantId)
        {
            services.AddSingleton<PipelineStart>();
            var guarded = new List<ServiceDescriptor>();
            guarded.Add(Replace(services, Migrator, (sp, _) => new SchemaMigrator(sp.GetRequiredService<IServiceScopeFactory>(), migrate,
                sp.GetRequiredService<ILogger<PipelineStart>>())));
            foreach (var name in Provisioning)
            {
                guarded.Add(Replace(services, name, (sp, type) =>
                {
                    var step = ActivatorUtilities.CreateInstance(sp, type) as IHostedService
                        ?? throw new InvalidOperationException($"{name} isn't a hosted service");
                    // The file may name the platform's tenant only: checked before the provisioner reads it, so nothing is written for another.
                    return name == Provisioning[0] ? new OwnTenantOnly(step, provisioningPath, tenantId) : step;
                }));
            }
            services.AddHealthChecks().AddCheck<PipelineStartCheck>("notifications", tags: ["ready"]);
            return guarded;
        }

        /// <summary>
        /// The pipeline's other hosted services (its stages, its outbox's delivery, its failure events, the platform's release pass): every one
        /// registered since <paramref name="before"/>, except the start's own steps, the health check publisher and the pipeline's own guard against
        /// a dry run. After a failed start they don't start: they would poll tables that aren't there. Call after <see cref="Guard"/> and after the
        /// last of them is registered.
        /// </summary>
        internal static void GateTheRest(IServiceCollection services, IReadOnlyCollection<ServiceDescriptor> before, IReadOnlyCollection<ServiceDescriptor> guarded)
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var descriptor = services[i];
                if (descriptor.ServiceType != typeof(IHostedService) || before.Contains(descriptor) || guarded.Contains(descriptor)
                    || descriptor.ImplementationType?.Name is "HealthCheckPublisherHostedService" or "DryRunGuard")
                {
                    continue;
                }
                services[i] = ServiceDescriptor.Singleton<IHostedService>(sp => new UnlessStartFailed(Create(sp, descriptor),
                    sp.GetRequiredService<PipelineStart>(), sp.GetRequiredService<ILogger<PipelineStart>>()));
            }
        }

        private static IHostedService Create(IServiceProvider sp, ServiceDescriptor descriptor)
            => (IHostedService)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

        /// <summary>A hosted service's name, through the start's wrappers (for its tests and logs).</summary>
        internal static string NameOf(IHostedService service) => service switch
        {
            UnlessStartFailed gated => UnlessStartFailed.Name(gated.Inner),
            GuardedStart guarded => guarded.Step,
            _ => UnlessStartFailed.Name(service),
        };

        private sealed class UnlessStartFailed(IHostedService inner, PipelineStart state, ILogger logger) : IHostedService
        {
            public IHostedService Inner => inner;

            private bool _started;

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                if (state.Failure is not null)
                {
                    state._skipped.Enqueue(Name(inner));
                    logger.LogWarning("Notification pipeline: {Service} not started, since its start failed ({Failure})", Name(inner), state.Failure);
                    return;
                }
                await inner.StartAsync(cancellationToken);
                _started = true;
            }

            public Task StopAsync(CancellationToken cancellationToken) => _started ? inner.StopAsync(cancellationToken) : Task.CompletedTask;

            internal static string Name(IHostedService service) => service.GetType() is { IsGenericType: true } generic
                ? $"{generic.Name[..generic.Name.IndexOf('`')]}<{string.Join(",", generic.GetGenericArguments().Select(a => a.Name))}>"
                : service.GetType().Name;
        }

        private static ServiceDescriptor Replace(IServiceCollection services, string name, Func<IServiceProvider, Type, IHostedService> create)
        {
            var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType?.FullName == name)
                ?? throw new InvalidOperationException($"The notification pipeline no longer registers {name} at start: review PipelineStart.");
            var type = descriptor.ImplementationType!;
            // In its place: hosted services start in the order they are registered.
            var guarded = ServiceDescriptor.Singleton<IHostedService>(sp => new GuardedStart(create(sp, type), type.Name,
                sp.GetRequiredService<PipelineStart>(), sp.GetRequiredService<ILogger<PipelineStart>>()));
            services[services.IndexOf(descriptor)] = guarded;
            return guarded;
        }

        /// <summary>The schema's pending migrations, applied by the platform (I60).</summary>
        private sealed class SchemaMigrator(IServiceScopeFactory scopes, bool migrate, ILogger logger) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
                => migrate ? NotificationsDatabase.MigrateAsync(scopes, logger, cancellationToken) : Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>
        /// Refuses a provisioning file that names a tenant other than the platform's, before the provisioner reads it: the platform embeds one tenant,
        /// and nothing is written for another. The failure is the pipeline's start failing (readiness <c>notifications</c>), not the host's.
        /// </summary>
        private sealed class OwnTenantOnly(IHostedService inner, string? path, Guid tenantId) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    var foreign = ForeignTenants(path, tenantId);
                    if (foreign > 0)
                    {
                        throw new InvalidOperationException($"The provisioning file names {foreign} tenant(s) other than the platform's ({tenantId:D}): the platform embeds one tenant, so nothing was provisioned.");
                    }
                }
                return inner.StartAsync(cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

            /// <summary>How many keys of the file's <c>tenants</c> aren't the platform's tenant; a file that can't be read as that is left to the provisioner to refuse.</summary>
            private static int ForeignTenants(string path, Guid tenantId)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path),
                        new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !document.RootElement.TryGetProperty("tenants", out var tenants)
                        || tenants.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        return 0;
                    }
                    return tenants.EnumerateObject().Count(t => !Guid.TryParse(t.Name, out var id) || id != tenantId);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    return 0;
                }
            }
        }

        private sealed class GuardedStart(IHostedService inner, string step, PipelineStart state, ILogger logger) : IHostedService
        {
            public string Step => step;

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
