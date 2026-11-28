using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Authorization;

namespace Juice.Observability.Api.Modules
{
    /// <summary>Maps what modules add to the API, from the two places the platform maps its routes.</summary>
    internal static class ModuleEndpoints
    {
        /// <summary>
        /// Inside the tenant mapping of <c>MapInventoryEndpoints</c>: <c>/modules</c> on <paramref name="api"/> (the group with the read policy),
        /// the internal modules at <paramref name="tenant"/>'s root, and every external module under <c>{tenant}/{name}</c>. Each of those groups
        /// is the tenant group or a child of it, so the core's tenant filter and the read policy apply to every module route.
        /// </summary>
        public static void Map(RouteGroupBuilder api, RouteGroupBuilder tenant)
        {
            var registry = ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<ModuleRegistry>();
            api.MapGet("/modules", (ModuleRegistry modules) => Results.Ok(Describe(modules)));
            foreach (var entry in registry.Entries)
            {
                if (entry.IsInternal)
                {
                    entry.Module.MapEndpoints(new InternalEndpoints(entry.Info, tenant));
                    continue;
                }
                var group = tenant.MapGroup("/" + entry.Info.Name).WithMetadata(new ModuleEndpointMetadata(entry.Info.Name));
                entry.Module.MapEndpoints(new ExternalEndpoints(entry.Info, group));
            }
        }

        /// <summary>
        /// The floor can't be lowered: a module endpoint may add requirements, never allow anonymous callers past the core's read policy. Run once
        /// every route is mapped (<c>MapJuiceObservability</c>), so the refusal comes before the host starts, when endpoints are built.
        /// </summary>
        public static void VerifyFloor(IEndpointRouteBuilder app)
        {
            if (!app.ServiceProvider.GetRequiredService<ModuleRegistry>().Entries.Any(e => !e.IsInternal))
            {
                return;
            }
            foreach (var endpoint in app.DataSources.SelectMany(d => d.Endpoints))
            {
                if (endpoint.Metadata.GetMetadata<ModuleEndpointMetadata>() is { } module && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                {
                    throw new ModuleCompositionException($"Module '{module.Module}' maps an anonymous endpoint ({(endpoint as RouteEndpoint)?.RoutePattern.RawText}); module routes need the platform.read policy.");
                }
            }
        }

        /// <summary>Routes outside <c>/api/v1</c> that internal modules own (the agent listener's API), in the slot the platform always mapped them.</summary>
        public static void MapRoot(IEndpointRouteBuilder app)
        {
            foreach (var entry in app.ServiceProvider.GetRequiredService<ModuleRegistry>().Entries)
            {
                (entry.Module as IInternalModule)?.MapRoot(app);
            }
        }

        private static ModulesDto Describe(ModuleRegistry registry) => new(CoreVersion.Current.ToString(), registry.Entries.Select(e =>
            new ModuleDto(e.Info.Name, e.Info.DisplayName, e.Info.Version, e.Info.Name == "core" ? "core" : "module", e.Info.SupportedCore, e.Manifest)).ToList());

        private sealed class ExternalEndpoints(ModuleInfo module, RouteGroupBuilder tenant) : IModuleEndpointsBuilder
        {
            public ModuleInfo Module { get; } = module;

            public RouteGroupBuilder Tenant { get; } = tenant;
        }

        /// <summary>For the platform's own modules: <c>Tenant</c> is the tenant root, with no module metadata (they never use <see cref="IModuleMutations"/>).</summary>
        private sealed class InternalEndpoints(ModuleInfo module, RouteGroupBuilder tenant) : IModuleEndpointsBuilder
        {
            public ModuleInfo Module { get; } = module;

            public RouteGroupBuilder Tenant { get; } = tenant;
        }
    }
}
