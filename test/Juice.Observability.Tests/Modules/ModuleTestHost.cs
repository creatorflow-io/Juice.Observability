using System.Net.Http.Headers;
using Juice.Observability.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>The platform's host as <c>Program.cs</c> builds it, started on a test server, with a module composed the way a host's own code would.</summary>
    internal sealed class ModuleTestHost(WebApplication app) : IAsyncDisposable
    {
        public static readonly string TenantKey = LocalStack.TenantKey;

        public Guid TenantId { get; init; }

        public WebApplication App { get; } = app;

        public HttpClient Client => App.GetTestClient();

        /// <summary>
        /// <paramref name="hostedServices"/> false removes every hosted service (nothing is started but the server): for tests that need no database.
        /// A database name starts the platform on it, with the discovery, health and rule passes off.
        /// </summary>
        public static async Task<ModuleTestHost> StartAsync(Action<WebApplicationBuilder>? compose = null, ScratchDatabase? database = null, bool hostedServices = false,
            IReadOnlyDictionary<string, string?>? settings = null)
        {
            var all = new Dictionary<string, string?>
            {
                ["Platform:TenantKey"] = LocalStack.TenantKey, ["Discovery:Enabled"] = "false", ["Health:Enabled"] = "false", ["Rules:Enabled"] = "false", ["Slos:Enabled"] = "false",
                ["Notifications:Stages:Run"] = "false", ["Notifications:ReleasePassInterval"] = "01:00:00", ["Notifications:HoldPassInterval"] = "01:00:00",
            };
            if (database is not null)
            {
                all["ConnectionStrings:Platform"] = database.ConnectionString;
                all["Database:Migrate"] = "true";
            }
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                all[key] = value;
            }
            var builder = CoreHost.CreateBuilder(all);
            builder.WebHost.UseTestServer();
            builder.Services.AddJuiceObservability(builder.Configuration);
            compose?.Invoke(builder);
            InboxHostTests.SignInByHeader(builder.Services);
            if (!hostedServices)
            {
                builder.Services.RemoveAll<IHostedService>();
            }
            var app = builder.Build();
            app.UseCorrelationId();
            app.UseExceptionHandler();
            app.UseStatusCodePages();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapJuiceObservability();
            try
            {
                await app.StartAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
            await using var db = database?.Context();
            return new ModuleTestHost(app) { TenantId = db is null ? Guid.Empty : (await db.Tenants.AsNoTracking().SingleAsync()).Id };
        }

        /// <summary>A client signed in as a caller of this deployment's tenant with the role (or no tenant claim when <paramref name="tenant"/> is null).</summary>
        public HttpClient As(string role, string? tenant = "")
        {
            var client = App.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-Claims", $"sub=s-{role};preferred_username={role}-user;roles={role}" + (tenant is null ? "" : $";platform_tenant={(tenant.Length == 0 ? TenantKey : tenant)}"));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
