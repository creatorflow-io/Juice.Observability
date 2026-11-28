using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// What 2.0 removed from the configuration, refused at start rather than ignored: a deployment that still sets the legacy webhooks'
    /// <c>Webhooks:*</c> keys, <c>Actions:NotifyEndpoints</c> or a <c>Notifications:Pipeline</c> other than <c>new</c> would otherwise lose its alert
    /// configuration without a word. Each message names the key that replaces it. Keys compare case-insensitively, as configuration does, and a key with
    /// an empty value is still a key.
    /// </summary>
    public static class LegacyConfiguration
    {
        public const string PipelineKey = "Notifications:Pipeline";

        private const string Provisioning = "Notifications:Provisioning:Path (a provisioning file of integrations, routes and allowlist: docs/deployment.md)";

        /// <summary>Throws, naming every refused key and what replaces it, when the configuration holds any.</summary>
        public static void Check(IConfiguration configuration)
        {
            var problems = new List<string>();
            foreach (var key in Keys(configuration.GetSection("Webhooks")))
            {
                problems.Add($"{key} was removed in 2.0: use {Replacement(key)}.");
            }
            foreach (var key in Keys(configuration.GetSection("Actions:NotifyEndpoints")))
            {
                problems.Add($"{key} was removed in 2.0: add a route for the action request types to {Provisioning}.");
            }
            if (PipelineKeyPresent(configuration) && !string.Equals(configuration[PipelineKey], "new", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{PipelineKey} was removed in 2.0, when the legacy webhooks and the shadow run were: the notification pipeline is the only one. "
                    + "Remove the key (or set it to 'new'), and declare endpoints and the allowlist in " + Provisioning + ".");
            }
            if (problems.Count > 0)
            {
                throw new InvalidOperationException("The configuration holds keys 2.0 no longer reads, which would lose alert configuration if ignored. " + string.Join(" ", problems));
            }
        }

        /// <summary>Whether <c>Notifications:Pipeline</c> is set at all (to <c>new</c> it is only obsolete).</summary>
        public static bool PipelineKeyPresent(IConfiguration configuration) => configuration.GetSection(PipelineKey).Exists();

        /// <summary>Every key under a section: the section itself when it holds a value, and each leaf below it, as a full path.</summary>
        private static IEnumerable<string> Keys(IConfigurationSection section)
        {
            if (section.Value is not null)
            {
                yield return section.Path;
            }
            foreach (var child in section.GetChildren())
            {
                foreach (var key in Keys(child))
                {
                    yield return key;
                }
                if (!child.GetChildren().Any() && child.Value is null)
                {
                    yield return child.Path;
                }
            }
        }

        /// <summary>The key (or file) that replaces a legacy key.</summary>
        internal static string Replacement(string key)
        {
            var rest = key.Length > "Webhooks:".Length ? key["Webhooks:".Length..] : "";
            var name = rest.Split(':')[0];
            return name.ToLowerInvariant() switch
            {
                "file" or "endpoints" or "allowlist" => Provisioning,
                "consoleurl" => "Notifications:ConsoleUrl",
                "smtp" => "Notifications:Delivery:SmtpServers:<name> (Host, Port, Security, Username, PasswordRef), named by the integration's target",
                "allowhttptoallowlisted" => "Notifications:Delivery:AllowHttpToAllowlisted",
                "apisecretenvprefix" => "Notifications:Delivery:ApiSecretEnvPrefix",
                "apisecretdirectory" => "Notifications:Delivery:ApiSecretDirectory",
                "additionalchathosts" => "an allowlist entry for the host in " + Provisioning,
                "maxattempts" => "Notifications:Delivery:MaxAttempts (the same name: see docs/deployment.md)",
                "retrybase" => "Notifications:Delivery:RetryBase (the same name: see docs/deployment.md)",
                "retrymax" => "Notifications:Delivery:RetryMax (the same name: see docs/deployment.md)",
                "connecttimeout" => "Notifications:Delivery:ConnectTimeout (the same name: see docs/deployment.md)",
                "requesttimeout" => "Notifications:Delivery:RequestTimeout (the same name: see docs/deployment.md)",
                "maxresponsebytes" => "Notifications:Delivery:MaxResponseBytes (the same name: see docs/deployment.md)",
                "interval" => "Notifications:ReleasePassInterval (the platform's pass over held and kept notifications) or Notifications:Stages:<stage>:Interval (a pipeline stage's own: docs/deployment.md)",
                "enabled" or "batchsize" or "maxparallel" => "Notifications:Stages:<stage>:* (the pipeline's own stage settings: docs/deployment.md)",
                _ => "the notification pipeline's own settings under Notifications (docs/deployment.md)",
            };
        }
    }

    /// <summary>Tells once at start that <c>Notifications:Pipeline</c> is obsolete (it is only <c>new</c> here: anything else refused the start).</summary>
    internal sealed class ObsoleteKeyNotice(ILogger<ObsoleteKeyNotice> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning("{Key} is obsolete: the notification pipeline is the only one since 2.0, so the key does nothing. Remove it.", LegacyConfiguration.PipelineKey);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
