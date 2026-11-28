namespace Juice.Observability.Api.Integrations
{
    /// <summary><c>/api/v1/tenants/{tenantId}/integrations</c>: plugins, and instances with an admin-only audited lifecycle.</summary>
    internal static class IntegrationEndpoints
    {
        public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder tenant)
        {
            // The module's metadata lets the instance mutations use IModuleMutations (audit and idempotency in the module's transaction).
            var group = tenant.MapGroup("/integrations").WithMetadata(new ModuleEndpointMetadata("integrations"));
            return group;
        }
    }
}
