using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>
    /// What the <c>integrations</c> module registers for running instances and raising their alert: the leased worker, the claim and outcome writer, the
    /// integration alert source and the metrics.
    /// </summary>
    internal static class IntegrationHealthComposition
    {
        public static void Add(IModuleServicesBuilder module)
        {
            module.Services.TryAddSingleton<IntegrationHealthState>();
            module.Services.TryAddScoped<IntegrationRunner>();
            module.AddAlertSource(IntegrationHealthRule.Template);
            module.AddLeasedWorker<IntegrationsWorker>("integrations");
        }
    }
}
