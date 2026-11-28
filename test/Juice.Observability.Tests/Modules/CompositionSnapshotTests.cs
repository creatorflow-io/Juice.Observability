using Juice.Observability.Api;
using Microsoft.AspNetCore.Builder;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>The host's shape is unchanged by module composition: its routes and its hosted-service order are the base's.</summary>
    [Trait("Milestone", "C2-M1")]
    public sealed class CompositionSnapshotTests
    {
        [Fact]
        public void Composition_core_routes_unchanged()
        {
            using var app = CoreHost.Build();
            CoreHost.AssertSnapshot("core-routes.snapshot.txt", CoreHost.Routes(app).Where(r => !r.Contains(" /api/v1/modules |")));
        }

        [Fact]
        public void Internal_modules_keep_hosted_service_order()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            var services = builder.Services;
            // Built, so the factory registrations can be named by what they make.
            using var app = builder.Build();
            CoreHost.AssertSnapshot("hosted-services.snapshot.txt", CoreHost.HostedServices(services, app.Services));
        }
    }
}
