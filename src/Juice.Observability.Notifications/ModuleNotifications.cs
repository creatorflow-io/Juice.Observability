using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Types;
using Juice.Observability.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The types the platform declared as a core's and then moved to the module that owns them (core 2.0 M3): the action approval and the action
    /// request events. A module may claim each of them once; the agents module does. Outside these, a module's types live under
    /// <c>observability.&lt;module&gt;.</c>.
    /// </summary>
    public static class CoreMovedNotificationTypes
    {
        public static IReadOnlySet<string> All { get; } = new HashSet<string>(
            ObservabilityTypes.ActionRequests().Select(t => t.Key).OfType<string>(), StringComparer.Ordinal);
    }

    /// <summary>What a module declared, as composed (the host's own record: only <see cref="NotificationModuleExtensions"/> makes it).</summary>
    internal sealed record ModuleNotificationTypes(string Module, IReadOnlyList<TypeDeclaration> Types);

    /// <summary>The channels a module added, by key.</summary>
    internal sealed record ModuleNotificationChannels(string Module, IReadOnlyList<string> Keys);

    /// <summary>
    /// A module's notification types and channels (core 2.0 M3, W-25). Types are checked here, at composition, and declared with the platform's
    /// application at start like the core's own. A type the pipeline would refuse (a bad schema, a bad template) fails the pipeline's start, loudly
    /// (readiness <c>notifications</c> and the dead-man), until Juice.Notifications exposes a pre-check.
    /// </summary>
    public static class NotificationModuleExtensions
    {
        /// <summary>The most types one module declares.</summary>
        public const int MaxTypesPerModule = 50;

        private static readonly Regex ChannelKey = new(@"^[a-z][a-z0-9-]{1,31}\z", RegexOptions.CultureInvariant);

        /// <summary>The channels of the core: the built-ins and the chat providers' (a module's package can't replace one).</summary>
        private static readonly string[] ReservedChannels = ["webhook", "email", "inbox", "slack", "teams"];

        /// <summary>The first segment after <c>observability.</c> of every core type (<c>alert</c>, <c>action</c>): no module takes it as its name.</summary>
        private static readonly HashSet<string> CoreFamilies = ObservabilityTypes.All().Select(t => t.Key).OfType<string>()
            .Select(k => k.Split('.')[1]).ToHashSet(StringComparer.Ordinal);

        // What this host's options hold that a module put there: lets a later call take back what a refused module left.
        private static readonly ConditionalWeakTable<NotificationsEmbeddedOptions, List<TypeDeclaration>> Appended = new();

        /// <summary>
        /// Declares notification types. The key is <c>observability.&lt;module&gt;.&lt;name&gt;</c> or a moved key (<see cref="CoreMovedNotificationTypes"/>) that
        /// no module claimed yet; never a core type's, one another module declared, or one declared twice. At most <see cref="MaxTypesPerModule"/> per module.
        /// Refused, nothing is declared. Valid types are appended to the embedded options' <c>Types</c>, in composition order.
        /// </summary>
        public static IModuleServicesBuilder AddNotificationTypes(this IModuleServicesBuilder b, params TypeDeclaration[] types)
        {
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(types);
            RequireHostBuilder(b);
            var module = b.Module.Name;
            // A module named like a core family would own its types' prefix (observability.alert.*).
            if (CoreFamilies.Contains(module))
            {
                throw new ModuleCompositionException($"Module '{module}' notification types are refused: the module's name is a core type family ('observability.{module}.*').");
            }
            var options = Options(b);
            var records = b.Services.Where(d => d.ServiceType == typeof(ModuleNotificationTypes)).Select(d => d.ImplementationInstance).OfType<ModuleNotificationTypes>().ToList();
            TakeBackRefused(options, records);

            var core = ObservabilityTypes.Core().Select(t => t.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var taken = options.Types.Select(t => t.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var prefix = $"{ObservabilityTypes.ForPlatformEvent(module)}.";
            var own = records.Where(r => r.Module == module).Sum(r => r.Types.Count);
            if (own + types.Length > MaxTypesPerModule)
            {
                throw new ModuleCompositionException($"Module '{module}' notification types are refused: a module declares at most {MaxTypesPerModule}.");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                var key = type?.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ModuleCompositionException($"Module '{module}' notification type is refused: it has no key.");
                }
                if (core.Contains(key))
                {
                    throw new ModuleCompositionException($"Module '{module}' notification type '{key}' is refused: it is a core type.");
                }
                var moved = CoreMovedNotificationTypes.All.Contains(key);
                if (!moved && !(key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length))
                {
                    throw new ModuleCompositionException($"Module '{module}' notification type '{key}' is refused: the key must start with '{prefix}' (or be a moved key no module claimed).");
                }
                if (!seen.Add(key) || taken.Contains(key))
                {
                    var by = records.FirstOrDefault(r => r.Types.Any(t => t.Key == key))?.Module;
                    throw new ModuleCompositionException(moved
                        ? $"Module '{module}' notification type '{key}' is refused: the moved key is already claimed{(by is null ? "" : $" by module '{by}'")}."
                        : $"Module '{module}' notification type '{key}' is refused: the type is already declared{(by is null ? "" : $" by module '{by}'")}.");
                }
            }

            var declared = types.ToArray();
            b.Services.AddSingleton(new ModuleNotificationTypes(module, declared));
            options.Types.AddRange(declared);
            Appended.GetOrCreateValue(options).AddRange(declared);
            return b;
        }

        /// <summary>
        /// Adds channels to the embedded instance. <paramref name="register"/> runs on a scratch collection first. Every <see cref="ChannelDefinition"/> it adds
        /// must be an instance whose key matches <c>^[a-z][a-z0-9-]{1,31}$</c>, isn't <c>webhook</c>, <c>email</c>, <c>inbox</c>, <c>slack</c> or <c>teams</c>, and isn't a key
        /// already registered; otherwise the call is refused and nothing is added. Then the descriptors are copied over. The channel's provider is the module's
        /// own trusted code and must enforce its own destination allowlist (docs/extensibility-notifications.md). Every <see cref="INotificationProvider"/> the call
        /// registers must serve one of the module's own channels (an instance, or a type with a public parameterless constructor), never a core or another module's.
        /// </summary>
        public static IModuleServicesBuilder AddNotificationChannels(this IModuleServicesBuilder b, Action<IServiceCollection> register)
        {
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(register);
            RequireHostBuilder(b);
            var module = b.Module.Name;
            var scratch = new ServiceCollection();
            register(scratch);

            var registered = RegisteredChannels(b.Services);
            var keys = new List<string>();
            foreach (var descriptor in scratch)
            {
                var instance = descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance;
                var channelService = descriptor.ServiceType == typeof(ChannelDefinition);
                if (!channelService && instance is not ChannelDefinition)
                {
                    continue;
                }
                // A factory or a type is built later, from the container: its key can't be checked now.
                if (descriptor.IsKeyedService || !channelService || instance is not ChannelDefinition definition)
                {
                    throw new ModuleCompositionException($"Module '{module}' channel is refused: a ChannelDefinition must be registered as an instance (AddSingleton(new ChannelDefinition(...))), not by a factory, a type or a key.");
                }
                var key = definition.Key;
                if (key is null || !ChannelKey.IsMatch(key))
                {
                    throw new ModuleCompositionException($"Module '{module}' channel '{key}' is refused: the key must match ^[a-z][a-z0-9-]{{1,31}}$.");
                }
                if (Array.IndexOf(ReservedChannels, key) >= 0)
                {
                    throw new ModuleCompositionException($"Module '{module}' channel '{key}' is refused: it is a core channel.");
                }
                if (registered.Contains(key) || keys.Contains(key))
                {
                    throw new ModuleCompositionException($"Module '{module}' channel '{key}' is refused: a channel of that key is already registered.");
                }
                keys.Add(key);
            }

            // The module's providers serve its own channels only: a provider of another kind on a core channel would be handed that channel's deliveries.
            var own = keys.Concat(b.Services.Where(d => d.ServiceType == typeof(ModuleNotificationChannels)).Select(d => d.ImplementationInstance)
                .OfType<ModuleNotificationChannels>().Where(c => c.Module == module).SelectMany(c => c.Keys)).ToHashSet(StringComparer.Ordinal);
            foreach (var descriptor in scratch.Where(d => d.ServiceType == typeof(INotificationProvider) || d.ServiceType.IsAssignableTo(typeof(INotificationProvider))))
            {
                var channel = ProviderChannel(descriptor) ?? throw new ModuleCompositionException(
                    $"Module '{module}' provider is refused: register an INotificationProvider as an instance, or as a type with a public parameterless constructor, so its channel can be checked.");
                if (!own.Contains(channel))
                {
                    throw new ModuleCompositionException($"Module '{module}' provider for channel '{channel}' is refused: a provider serves its own module's channels only.");
                }
            }

            foreach (var descriptor in scratch)
            {
                b.Services.Add(descriptor);
            }
            b.Services.AddSingleton(new ModuleNotificationChannels(module, keys));
            return b;
        }

        /// <summary>The channel a provider descriptor serves, or null when it can't be told without building it from the container.</summary>
        private static string? ProviderChannel(ServiceDescriptor descriptor)
        {
            if (descriptor.IsKeyedService)
            {
                return null;
            }
            if (descriptor.ImplementationInstance is INotificationProvider instance)
            {
                return instance.Channel;
            }
            return descriptor.ImplementationType is { IsAbstract: false } type && type.GetConstructor(Type.EmptyTypes) is not null
                ? ((INotificationProvider)Activator.CreateInstance(type)!).Channel
                : null;
        }

        private static HashSet<string> RegisteredChannels(IServiceCollection services)
            => services.Where(d => !d.IsKeyedService && d.ImplementationInstance is ChannelDefinition).Select(d => ((ChannelDefinition)d.ImplementationInstance!).Key)
                .Concat(services.Where(d => d.ServiceType == typeof(ModuleNotificationChannels)).Select(d => d.ImplementationInstance).OfType<ModuleNotificationChannels>().SelectMany(c => c.Keys))
                .ToHashSet(StringComparer.Ordinal);

        private static NotificationsEmbeddedOptions Options(IModuleServicesBuilder b)
            => b.Services.LastOrDefault(d => d.ServiceType == typeof(NotificationsEmbeddedOptions))?.ImplementationInstance as NotificationsEmbeddedOptions
                ?? throw new ModuleCompositionException($"Module '{b.Module.Name}' notification types are refused: the notification pipeline isn't registered (AddJuiceObservability registers it).");

        /// <summary>A module refused after it declared types leaves its records out of the services (they are put back as they were): the types it appended go with them.</summary>
        private static void TakeBackRefused(NotificationsEmbeddedOptions options, IReadOnlyList<ModuleNotificationTypes> records)
        {
            if (!Appended.TryGetValue(options, out var appended))
            {
                return;
            }
            var kept = records.SelectMany(r => r.Types).ToHashSet();
            foreach (var type in appended.Where(t => !kept.Contains(t)).ToList())
            {
                options.Types.Remove(type);
                appended.Remove(type);
            }
        }

        /// <summary>The module's name is the contract's key: only the host's own builder can be trusted to say it (the same rule as workers and event handlers).</summary>
        private static void RequireHostBuilder(IModuleServicesBuilder b)
        {
            var marker = typeof(IModuleServicesBuilder).Assembly.GetType("Juice.Observability.Extensibility.ICoreModuleBuilder");
            if (marker is null || !marker.IsInstanceOfType(b))
            {
                throw new ModuleCompositionException($"{b.GetType().Name} is not the host's module builder: the module's name can't be trusted.");
            }
        }
    }
}
