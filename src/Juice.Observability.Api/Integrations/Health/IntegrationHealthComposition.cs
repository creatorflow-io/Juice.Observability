namespace Juice.Observability.Api.Integrations.Health
{
    /// <summary>
    /// What the <c>integrations</c> module registers for running instances and raising their alert: the leased worker, the claim and outcome writer, the
    /// integration alert source and the metrics. An empty stub in slice A; slice B owns this folder and fills it in.
    /// </summary>
    internal static class IntegrationHealthComposition
    {
        public static void Add(IModuleServicesBuilder module)
        {
        }
    }
}
