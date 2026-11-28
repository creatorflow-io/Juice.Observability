using Juice.Notifications.Embedded;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationsPipeline = Juice.Observability.Data.NotificationsPipeline;
using PlatformNotificationsOptions = Juice.Observability.Data.PlatformNotificationsOptions;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// <c>Webhooks:Endpoints</c> loaded through the alias (phase-5-plan I69, M46b): wraps <see cref="WebhookProvisioner"/> rather than changing it.
    /// <list type="bullet">
    /// <item><c>legacy</c> and <c>shadow</c>: the legacy provisioner loads the file into <c>webhook_endpoints</c> as before, then the migration
    /// brings the notifications rows in step with what it loaded (each endpoint's integration keeping its id).</item>
    /// <item><c>new</c>: the legacy tables aren't written; the file's endpoints and allowlist go to notifications only.</item>
    /// </list>
    /// Conflicts (a key the notifications API holds, a digit-led key) are logged with the report's reasons, never thrown: the platform starts. So
    /// is a failure here, and when the pipeline's own start failed (its readiness says so), the alias leaves the notifications rows as they are.
    /// </summary>
    internal sealed class AliasProvisioner(WebhookProvisioner legacy, IServiceScopeFactory scopes, IOptions<PlatformNotificationsOptions> mode,
        IOptions<WebhookOptions> webhooks, NotificationsEmbeddedOptions embedded, PipelineStart pipeline, ILogger<AliasProvisioner> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var writesLegacy = mode.Value.Pipeline != NotificationsPipeline.New;
            if (writesLegacy)
            {
                await legacy.StartAsync(cancellationToken);
            }
            if (pipeline.Failure is { } failure)
            {
                logger.LogWarning("Webhooks:Endpoints aren't loaded into notifications: the pipeline didn't start ({Failure})", failure);
                return;
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var key = embedded.TenantId.ToString("D");
                var tenant = await db.Tenants.AsNoTracking().Where(t => t.Key == key).Select(t => t.Id).SingleAsync(cancellationToken);
                // What legacy holds once the file is loaded; in new, the file itself (legacy's tables stay as they were).
                var setup = writesLegacy ? await LegacySetup.ReadAsync(db, tenant, cancellationToken) : FromFile(tenant, key);
                // As the provisioner: what the deployment's file declares is written as the alias's configuration (M46c, alias-config).
                var planner = scope.ServiceProvider.GetRequiredService<MigrationPlanner>();
                planner.AsProvisioner = true;
                // Legacy's tables are the whole of what the alias mirrors while legacy sends; in new, the file is the whole of alias-config.
                planner.Reconciles = writesLegacy
                    ? new HashSet<string> { AliasVocabulary.WebhooksAlias, AliasVocabulary.AliasConfig }
                    : new HashSet<string> { AliasVocabulary.AliasConfig };
                MigrationReport? report;
                try
                {
                    report = await planner.ApplyAsync(setup, cancellationToken);
                }
                catch (HandBackConcurrentChange)
                {
                    // An alias row changed owner while it was handed back; nothing moved. Planned once more from what is there now.
                    logger.LogInformation("An alias row changed owner during the hand-back; planning again");
                    try
                    {
                        report = await planner.ApplyAsync(setup, cancellationToken);
                    }
                    catch (HandBackConcurrentChange)
                    {
                        logger.LogWarning("The alias's rows of endpoints the file dropped are still changing owner; the next start hands them back");
                        return;
                    }
                }
                foreach (var item in report.Items.Where(i => i.Action == MigrationAction.Conflict))
                {
                    logger.LogWarning("Webhook {Subject} {Key} isn't in notifications: {Problems}", item.Subject, item.Key,
                        string.Join("; ", item.Problems.Select(p => $"{p.Code}: {p.Message}")));
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Webhooks:Endpoints couldn't be loaded into notifications; legacy's rows are as the file says");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>The file's endpoints and allowlist as legacy rows, in memory: what <see cref="WebhookProvisioner"/> would have written.</summary>
        private LegacySetup FromFile(Guid tenant, string key)
        {
            var settings = webhooks.Value;
            var endpoints = settings.Endpoints.Select(d => new WebhookEndpoint
            {
                TenantId = tenant, Key = d.Key, Kind = d.Kind, Url = d.Url, SecretRef = d.SecretRef, Events = [.. d.Events], RuleKeys = [.. d.RuleKeys],
                Environments = [.. d.Environments], Recipients = [.. d.Recipients], GroupWindowSeconds = d.GroupWindowSeconds, MinimumSeverity = d.MinimumSeverity,
                Enabled = d.Enabled, DefinitionHash = d.Hash(), ManagedBy = ManagedBy.Configuration,
            }).ToList();
            var allowlist = settings.Allowlist.Select(a => new WebhookAllowlistEntry
            {
                TenantId = tenant, Destination = a.Destination, Ports = [.. a.Ports], ManagedBy = ManagedBy.Configuration,
            }).ToList();
            return new LegacySetup(tenant, key, endpoints, [], allowlist);
        }

        /// <summary>
        /// Puts the alias provisioner where legacy's was (hosted services start in order), with legacy's as its inner one. A no-op when the
        /// webhooks aren't registered (<c>Webhooks:Enabled=false</c> registers no provisioner).
        /// </summary>
        internal static void Wrap(IServiceCollection services)
        {
            var index = services.ToList().FindIndex(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WebhookProvisioner));
            if (index < 0)
            {
                return;
            }
            services.RemoveAt(index);
            services.AddSingleton<WebhookProvisioner>();
            services.Insert(index, ServiceDescriptor.Singleton<IHostedService, AliasProvisioner>());
        }
    }
}
