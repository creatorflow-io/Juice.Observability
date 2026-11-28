using Juice.Observability.Agents;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Extensibility;
using Juice.Observability.Notifications;
using Juice.Observability.Slos;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// A module compiled into the platform. Unlike an external module it may map at the tenant root, and at the application root (the agent
    /// listener's API), through an internal-only builder. It registers exactly what it registered before the contract existed, in the same slot.
    /// </summary>
    internal interface IInternalModule : IObservabilityModule
    {
        /// <summary>Routes outside <c>/api/v1</c>, mapped where the platform always mapped them.</summary>
        void MapRoot(IEndpointRouteBuilder app)
        {
        }
    }

    internal static class InternalModuleInfo
    {
        /// <summary>Version and range of the platform itself: an internal module is always the core's own version.</summary>
        public static ModuleInfo Of(string name, string displayName)
            => new(name, displayName, CoreVersion.Current.ToString(), $"[{CoreVersion.Current}]");
    }

    /// <summary>The core as a descriptor: <c>AddJuiceObservability</c> registers its services and routes as it always did.</summary>
    internal sealed class CoreModule : IInternalModule
    {
        public ModuleInfo Info { get; } = InternalModuleInfo.Of("core", "Juice Observability");

        public void ConfigureServices(IModuleServicesBuilder module)
        {
        }
    }

    /// <summary>Service level objectives: <c>AddPlatformSlos</c> and the <c>/slos</c> endpoints, unchanged.</summary>
    internal sealed class SlosModule : IInternalModule
    {
        public ModuleInfo Info { get; } = InternalModuleInfo.Of("slos", "Service level objectives");

        public void ConfigureServices(IModuleServicesBuilder module)
        {
            // The host's composers for module data and alert sources: the first module to compose (this one, before any external) registers them.
            module.Services.AddSingleton<IModuleDataComposer>(new Modules.ModuleData.ModuleDataComposerService());
            module.Services.AddSingleton<IAlertSourceComposer>(new Modules.AlertSources.AlertSourceComposerService());
            module.Services.AddPlatformSlos(module.Configuration);
            // The burn alerts are the core's first alert source (core 2.0 M2): the SLO counter's evaluation applies its signals through it.
            module.AddAlertSource(EvaluateSloBurnsHandler.Template);
        }

        public void MapEndpoints(IModuleEndpointsBuilder endpoints) => endpoints.Tenant.MapSloEndpoints();
    }

    /// <summary>Agents: <c>AddPlatformAgents</c>, the agent, action-request and bundle endpoints, and the agent listener's API.</summary>
    internal sealed class AgentsModule : IInternalModule
    {
        public ModuleInfo Info { get; } = InternalModuleInfo.Of("agents", "Agents");

        public void ConfigureServices(IModuleServicesBuilder module)
        {
            module.Services.AddPlatformAgents(module.Configuration);
            // The approval and the action request events: the core's until M3, declared by their module now (the same declarations, so no new type version).
            module.AddNotificationTypes(ObservabilityTypes.ActionRequests().ToArray());
        }

        public void MapEndpoints(IModuleEndpointsBuilder endpoints)
        {
            endpoints.Tenant.MapAgentEndpoints();
            endpoints.Tenant.MapActionRequestEndpoints();
            endpoints.Tenant.MapBundleEndpoints();
        }

        // Answers only on the agent listener, and only when agents are enabled (Agents:Enabled).
        void IInternalModule.MapRoot(IEndpointRouteBuilder app) => app.MapAgentApi();
    }
}
