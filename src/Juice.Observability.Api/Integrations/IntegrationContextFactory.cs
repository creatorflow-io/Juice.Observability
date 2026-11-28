using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>Builds what a validation or run hands the plugin. The tenant is the instance's, and the secrets resolve only when the plugin asks.</summary>
    internal static class IntegrationContextFactory
    {
        public static IntegrationContext Create(IntegrationInstance instance, IServiceProvider services, RunCheckpoints checkpoints)
        {
            var registry = services.GetRequiredService<IIntegrationPluginRegistry>();
            var options = services.GetRequiredService<IOptions<IntegrationsOptions>>().Value;
            var declared = registry.TryGet(instance.PluginKey, out var descriptor)
                ? descriptor.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal)
                : [];
            var references = JsonSerializer.Deserialize<Dictionary<string, string>>(instance.SecretRefs) ?? [];
            var secrets = new InstanceSecrets(references, declared, options);
            var logger = new ScrubbingLogger(services.GetRequiredService<ILoggerFactory>().CreateLogger($"Juice.Integration.{instance.PluginKey}"), () => secrets.Values);
            return new IntegrationContext(instance.Id, instance.TenantId, instance.PluginKey, Parse(instance.Config), instance.ConfigVersion, Parse(instance.Scope),
                secrets, checkpoints, services.GetRequiredService<TimeProvider>(), logger);
        }

        /// <summary>The secret values this context handed out so far: what its plugin's text is scrubbed of.</summary>
        public static IReadOnlyCollection<string> ResolvedValues(IntegrationContext context) => (context.Secrets as InstanceSecrets)?.Values ?? [];

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    /// <summary>A logger that removes the secret values resolved so far from messages, exceptions and scopes before they reach the real one.</summary>
    internal sealed class ScrubbingLogger(ILogger inner, Func<IReadOnlyCollection<string>> values) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => inner.BeginScope(IntegrationScrubber.Scrub(state.ToString(), values()) ?? "");

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel))
            {
                return;
            }
            var secrets = values();
            var message = IntegrationScrubber.Scrub(formatter(state, exception), secrets) ?? "";
            // The exception is text too: its message and stack can carry a value.
            var detail = exception is null ? null : IntegrationScrubber.Scrub(exception.ToString(), secrets);
            inner.Log(logLevel, eventId, message + (detail is null ? "" : Environment.NewLine + detail), null, static (text, _) => text);
        }
    }
}
