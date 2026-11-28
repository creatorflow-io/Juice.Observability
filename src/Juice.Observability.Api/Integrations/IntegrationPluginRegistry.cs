using System.Diagnostics.CodeAnalysis;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>The plugins compiled into this host, by key. Empty on a core-only install.</summary>
    internal interface IIntegrationPluginRegistry
    {
        IReadOnlyList<IntegrationDescriptor> All { get; }

        bool TryGet(string key, [NotNullWhen(true)] out IntegrationDescriptor? descriptor);

        /// <summary>The plugin object, from the run's scope. Throws for a key that isn't installed.</summary>
        IIntegrationPlugin Resolve(IServiceProvider services, string key);
    }

    /// <summary>What a module registered with <c>AddIntegrationPlugin</c>: read by the registry, and by the composer to refuse a key taken twice.</summary>
    internal sealed record IntegrationPluginRegistration(string Module, IntegrationDescriptor Descriptor, Type PluginType);

    internal sealed class IntegrationPluginRegistry : IIntegrationPluginRegistry
    {
        private readonly Dictionary<string, IntegrationPluginRegistration> _byKey;

        public IntegrationPluginRegistry(IEnumerable<IntegrationPluginRegistration> registrations)
        {
            _byKey = registrations.ToDictionary(r => r.Descriptor.Key, StringComparer.Ordinal);
            All = [.. _byKey.Values.Select(r => r.Descriptor).OrderBy(d => d.Key, StringComparer.Ordinal)];
        }

        public IReadOnlyList<IntegrationDescriptor> All { get; }

        public bool TryGet(string key, [NotNullWhen(true)] out IntegrationDescriptor? descriptor)
        {
            var found = _byKey.TryGetValue(key, out var registration);
            descriptor = registration?.Descriptor;
            return found;
        }

        public IIntegrationPlugin Resolve(IServiceProvider services, string key)
            => _byKey.TryGetValue(key, out var registration)
                ? (IIntegrationPlugin)services.GetRequiredService(registration.PluginType)
                : throw new InvalidOperationException($"No integration plugin '{key}' is installed.");

        /// <summary>The collector face of the plugin, when it has one.</summary>
        public static IIntegrationCollector? CollectorOf(IIntegrationPlugin plugin) => plugin as IIntegrationCollector;
    }

    /// <summary>The host's <see cref="IIntegrationPluginComposer"/>, registered as an instance by the <c>integrations</c> module.</summary>
    internal sealed class IntegrationPluginComposerService : IIntegrationPluginComposer
    {
        public void AddPlugin<TPlugin>(IModuleServicesBuilder b, IntegrationDescriptor descriptor) where TPlugin : class, IIntegrationPlugin
            => IntegrationPluginComposer.Add<TPlugin>(b, descriptor);
    }

    internal static class IntegrationPluginComposer
    {
        public static void Add<TPlugin>(IModuleServicesBuilder b, IntegrationDescriptor descriptor) where TPlugin : class, IIntegrationPlugin
        {
            b.Services.AddSingleton(new IntegrationPluginRegistration(b.Module.Name, descriptor, typeof(TPlugin)));
            b.Services.AddScoped<TPlugin>();
        }
    }
}
