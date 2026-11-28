using Juice.Observability.Api.Modules;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Api
{
    public static class ObservabilityModuleExtensions
    {
        /// <summary>
        /// Adds a module to the host, after <c>AddJuiceObservability</c> and before <c>Build()</c>. Throws <see cref="ModuleCompositionException"/> when the
        /// module is outside this core's supported range, its name or policies break the contract, or it is composed too early, and then the host has
        /// none of its services.
        /// </summary>
        public static IHostApplicationBuilder AddObservabilityModule<T>(this IHostApplicationBuilder builder) where T : IObservabilityModule, new()
            => ModuleComposer.Compose<T>(builder);
    }
}
