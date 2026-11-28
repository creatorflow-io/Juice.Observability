using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// Composes modules at registration, before <c>Build()</c>: a module outside the core's range, with a bad name or policy, or taking a name that is
    /// not its own is refused here, and a refused module leaves nothing behind in the services (a host never comes up half-composed).
    /// </summary>
    internal static class ModuleComposer
    {
        /// <summary>Creates the registry with <c>core</c>, then composes <c>slos</c>, <c>agents</c> and <c>integrations</c> in the slot the platform always gave them.</summary>
        public static IServiceCollection AddInternalModules(this IServiceCollection services, IConfiguration configuration)
        {
            var registry = new ModuleRegistry();
            services.AddSingleton(registry);
            services.AddScoped<IModuleMutations, ModuleMutations>();
            registry.Add(new ModuleEntry(new CoreModule(), true, null, new HashSet<string>(), []));
            ComposeInternal(services, configuration, registry, new SlosModule());
            ComposeInternal(services, configuration, registry, new AgentsModule());
            ComposeInternal(services, configuration, registry, new IntegrationsModule());
            return services;
        }

        private static void ComposeInternal(IServiceCollection services, IConfiguration configuration, ModuleRegistry registry, IObservabilityModule module)
        {
            var builder = new ModuleServicesBuilder(module.Info, services, configuration, registry);
            module.ConfigureServices(builder);
            registry.Add(builder.Entry(module, isInternal: true));
        }

        public static IHostApplicationBuilder Compose<T>(IHostApplicationBuilder host) where T : IObservabilityModule, new()
        {
            var registry = host.Services.Where(d => d.ServiceType == typeof(ModuleRegistry)).Select(d => d.ImplementationInstance).OfType<ModuleRegistry>().LastOrDefault()
                ?? throw new ModuleCompositionException("AddObservabilityModule must come after AddJuiceObservability: modules are composed in order, and their migrations run after the core's.");
            var module = new T();
            var info = module.Info ?? throw new ModuleCompositionException($"Module {typeof(T).FullName} has no Info.");
            var name = info.Name ?? "";

            if (ModuleValidation.Info(info) is { } invalid)
            {
                throw new ModuleCompositionException($"Module '{name}' is refused: {invalid}");
            }
            if (!CoreVersionRange.TryParse(info.SupportedCore, out var range))
            {
                throw new ModuleCompositionException($"Module '{name}' is refused: SupportedCore '{info.SupportedCore}' is not a version range such as [2.0.0,3.0.0).");
            }
            if (!range.Includes(CoreVersion.Current))
            {
                throw new ModuleCompositionException($"Module '{name}' {info.Version} supports core {info.SupportedCore}, and this is core {CoreVersion.Current}.");
            }
            if (registry.Has(name))
            {
                throw new ModuleCompositionException($"Module '{name}' is refused: a module of that name is already composed.");
            }
            if (CoreRouteSegments.All.Contains(name))
            {
                throw new ModuleCompositionException($"Module '{name}' is refused: '{name}' is a route of the core under /api/v1/tenants/{{tenantId}}.");
            }

            var services = host.Services;
            // What the module may change is the whole list (Replace, RemoveAll, Insert): a refusal puts back these very descriptors, in this order.
            var before = services.ToList();
            var runner = !registry.RunnerRegistered;
            try
            {
                if (runner)
                {
                    // Before any worker of a module: hosted services start in registration order, and a worker may read its module's tables.
                    services.AddHostedService<ModuleMigrationRunner>();
                }
                var builder = new ModuleServicesBuilder(info, services, host.Configuration, registry);
                module.ConfigureServices(builder);
                registry.Add(builder.Entry(module, isInternal: false));
            }
            catch (Exception e)
            {
                services.Clear();
                foreach (var descriptor in before)
                {
                    services.Add(descriptor);
                }
                throw e is ModuleCompositionException ? e : new ModuleCompositionException($"Module '{name}' failed to compose: {e.Message}");
            }
            registry.RunnerRegistered = true;
            return host;
        }
    }

    internal sealed class ModuleServicesBuilder(ModuleInfo module, IServiceCollection services, IConfiguration configuration, ModuleRegistry registry) : IModuleServicesBuilder, ICoreModuleBuilder
    {
        private readonly HashSet<string> _policies = new(StringComparer.Ordinal);
        private readonly List<Type> _migrations = [];
        private ConsoleManifest? _manifest;

        public ModuleInfo Module { get; } = module;

        public IServiceCollection Services { get; } = services;

        public IConfiguration Configuration { get; } = configuration;

        public IModuleServicesBuilder AddWorker<TWorker>() where TWorker : class, IHostedService
        {
            Services.AddHostedService<TWorker>();
            return this;
        }

        public IModuleServicesBuilder AddMigrations<TContext>() where TContext : DbContext
        {
            if (_migrations.Contains(typeof(TContext)))
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' declares migrations for {typeof(TContext).Name} twice.");
            }
            _migrations.Add(typeof(TContext));
            return this;
        }

        public IModuleServicesBuilder AddPolicy(string name, params string[] roles)
        {
            if (ModuleValidation.Policy(Module, name, roles) is { } invalid)
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' policy '{name}' is refused: {invalid}");
            }
            // The prefix rule alone lets a module named "platform" reach platform.read: no module redefines a policy that exists.
            if (name is CorePolicies.Read or CorePolicies.Operate || registry.PolicyNames.Contains(name) || !_policies.Add(name))
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' policy '{name}' is refused: the name is already a policy.");
            }
            var distinct = roles.Distinct(StringComparer.Ordinal).ToArray();
            Services.Configure<AuthorizationOptions>(options => options.AddPolicy(name, p => p.RequireRole(distinct)));
            return this;
        }

        public IModuleServicesBuilder SetConsoleManifest(ConsoleManifest manifest)
        {
            if (_manifest is not null)
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' sets its Console manifest twice.");
            }
            _manifest = manifest;
            return this;
        }

        /// <summary>What the module declared, validated: the manifest may name only core policies and the module's own.</summary>
        public ModuleEntry Entry(IObservabilityModule module, bool isInternal)
        {
            if (_manifest is not null && ModuleValidation.Manifest(_manifest, Module, _policies) is { } invalid)
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' Console manifest is refused: {invalid}");
            }
            if (_manifest is not null && registry.Entries.Any(e => e.Manifest?.Package == _manifest.Package))
            {
                throw new ModuleCompositionException($"Module '{Module.Name}' Console manifest is refused: package '{_manifest.Package}' belongs to another module.");
            }
            return new ModuleEntry(module, isInternal, _manifest, _policies, _migrations);
        }
    }
}
