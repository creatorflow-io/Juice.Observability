using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

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

    internal static partial class IntegrationPluginComposer
    {
        [GeneratedRegex("^[a-z][a-z0-9-]{0,30}$", RegexOptions.CultureInvariant)]
        private static partial Regex PluginName();

        [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
        private static partial Regex SemVer();

        [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
        private static partial Regex Identifier();

        public const int MaxFields = 50, MaxSecrets = 10;

        private static readonly HashSet<IntegrationCapability> Collecting =
            [IntegrationCapability.Discovery, IntegrationCapability.Metadata, IntegrationCapability.Relationship, IntegrationCapability.Correlation, IntegrationCapability.Events];

        public static void Add<TPlugin>(IModuleServicesBuilder b, IntegrationDescriptor descriptor) where TPlugin : class, IIntegrationPlugin
        {
            var module = b.Module.Name;
            var refused = Check(module, descriptor, typeof(TPlugin), b.Services.Select(d => d.ImplementationInstance).OfType<IntegrationPluginRegistration>().Select(r => r.Descriptor.Key));
            if (refused is not null)
            {
                throw new ModuleCompositionException($"Module '{module}' integration plugin '{descriptor?.Key}' is refused: {refused}");
            }
            b.Services.AddSingleton(new IntegrationPluginRegistration(module, descriptor, typeof(TPlugin)));
            b.Services.AddScoped<TPlugin>();
        }

        /// <summary>Null when the descriptor is valid for the module (decision 2); otherwise the first reason.</summary>
        internal static string? Check(string module, IntegrationDescriptor d, Type plugin, IEnumerable<string> takenKeys)
        {
            if (d.Key is null || !d.Key.StartsWith(module + ".", StringComparison.Ordinal) || !PluginName().IsMatch(d.Key[(module.Length + 1)..]))
            {
                return $"the key must be '{module}.<name>' with a name of lower case letters, digits and '-' (at most 31 characters, starting with a letter)";
            }
            if (takenKeys.Contains(d.Key, StringComparer.Ordinal))
            {
                return "the key is already registered";
            }
            if (string.IsNullOrWhiteSpace(d.DisplayName) || d.DisplayName.Length > 64)
            {
                return "the display name is 1 to 64 characters";
            }
            if (d.Version is null || !SemVer().IsMatch(d.Version))
            {
                return "the version must be SemVer";
            }
            if (d.Capabilities is null || d.Capabilities.Count == 0)
            {
                return "at least one capability is required";
            }
            if (d.Capabilities.Contains(IntegrationCapability.Actions))
            {
                return "the Actions capability is not available to plugins";
            }
            if (d.Capabilities.Any(c => !Enum.IsDefined(c)))
            {
                return "a capability is not known";
            }
            if (d.Capabilities.Any(Collecting.Contains) && !typeof(IIntegrationCollector).IsAssignableFrom(plugin))
            {
                return "a collection capability needs the plugin to implement IIntegrationCollector";
            }
            if (SchemaError("config", d.Config) is { } config)
            {
                return config;
            }
            if (SchemaError("scope", d.Scope) is { } scope)
            {
                return scope;
            }
            if (d.Secrets is null || d.Secrets.Count > MaxSecrets || d.Secrets.Any(s => s is null || !Identifier().IsMatch(s.Name ?? "") || string.IsNullOrWhiteSpace(s.Label)))
            {
                return $"secrets are at most {MaxSecrets}, each with a name of letters, digits and '_' and a label";
            }
            if (d.Secrets.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != d.Secrets.Count)
            {
                return "a secret name is declared twice";
            }
            if (d.RequiredPermissions is null || d.RequiredPermissions.Count > 20 || d.RequiredPermissions.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200))
            {
                return "required permissions are at most 20 texts of at most 200 characters";
            }
            if (d.MinInterval < TimeSpan.FromMinutes(1) || d.DefaultInterval < d.MinInterval || d.DefaultInterval > TimeSpan.FromHours(24))
            {
                return "intervals must satisfy 1 minute <= MinInterval <= DefaultInterval <= 24 hours";
            }
            return null;
        }

        private static string? SchemaError(string what, IntegrationConfigSchema? schema)
        {
            if (schema is null || schema.Version < 1 || schema.Fields is null)
            {
                return $"the {what} schema needs a version of at least 1 and a field list";
            }
            if (schema.Fields.Count > MaxFields)
            {
                return $"the {what} schema has more than {MaxFields} fields";
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in schema.Fields)
            {
                if (field is null || !Identifier().IsMatch(field.Name ?? "") || !names.Add(field.Name!))
                {
                    return $"a {what} field name must be unique and made of letters, digits and '_' (at most 40)";
                }
                if (string.IsNullOrWhiteSpace(field.Label) || !Enum.IsDefined(field.Kind))
                {
                    return $"{what} field '{field.Name}' needs a label and a known kind";
                }
                if (field.Kind == IntegrationFieldKind.Enum && (field.Allowed is null || field.Allowed.Count == 0))
                {
                    return $"{what} field '{field.Name}' is an enum with no allowed values";
                }
                if (field.Min > field.Max)
                {
                    return $"{what} field '{field.Name}' has Min above Max";
                }
                if (field.Pattern is not null)
                {
                    try
                    {
                        _ = new Regex(field.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    }
                    catch (ArgumentException)
                    {
                        return $"{what} field '{field.Name}' has a pattern that does not compile";
                    }
                }
            }
            return null;
        }
    }
}
