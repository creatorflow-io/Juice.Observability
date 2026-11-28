using System.Text;
using Juice.Observability.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>
    /// The platform's host composed as <c>Program.cs</c> does, built and mapped but never started: its endpoints and hosted services can be read
    /// without a database. The shape of what a host exposes is pinned by <c>core-routes.snapshot.txt</c> and <c>hosted-services.snapshot.txt</c>.
    /// </summary>
    internal static class CoreHost
    {
        internal static WebApplicationBuilder CreateBuilder(IReadOnlyDictionary<string, string?>? settings = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
                ["ClickHouse:ConnectionString"] = "Host=127.0.0.1;Port=1",
                ["Platform:TenantKey"] = "0193f0aa-0000-7000-8000-0000000000a1",
                ["Auth:Authority"] = "http://127.0.0.1:1/realms/none",
                ["Auth:RequireHttpsMetadata"] = "false",
                ["Notifications:ConsoleUrl"] = "https://console.example.com",
            });
            if (settings is not null)
            {
                builder.Configuration.AddInMemoryCollection(settings);
            }
            return builder;
        }

        /// <summary>The host as <c>Program.cs</c> composes it: <c>AddJuiceObservability</c>, then what <paramref name="compose"/> adds, then the routes.</summary>
        internal static WebApplication Build(Action<WebApplicationBuilder>? compose = null, IReadOnlyDictionary<string, string?>? settings = null)
        {
            var builder = CreateBuilder(settings);
            builder.Services.AddJuiceObservability(builder.Configuration);
            compose?.Invoke(builder);
            var app = builder.Build();
            app.MapJuiceObservability();
            return app;
        }

        /// <summary>Every endpoint as <c>METHODS pattern | policies | anonymous</c>, sorted ordinally.</summary>
        internal static IReadOnlyList<string> Routes(IEndpointRouteBuilder app)
            => app.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Select(Describe).Order(StringComparer.Ordinal).ToList();

        private static string Describe(RouteEndpoint endpoint)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? (a.Roles is null ? "(authenticated)" : "roles:" + a.Roles));
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            return $"{(methods is null ? "*" : string.Join(',', methods.Order(StringComparer.Ordinal)))} {endpoint.RoutePattern.RawText} | {string.Join(',', policies)} | {(anonymous ? "anonymous" : "-")}";
        }

        internal const string TenantPrefix = "/api/v1/tenants/{tenantId:guid}/";

        /// <summary>The first segment after <c>/api/v1/tenants/{tenantId}/</c> of every tenant route.</summary>
        internal static IReadOnlySet<string> TenantRootSegments(IEndpointRouteBuilder app)
            => Routes(app).Select(r => r.Split(' ', 3)[1])
                .Where(p => p.StartsWith(TenantPrefix, StringComparison.Ordinal) && p.Length > TenantPrefix.Length)
                .Select(p => p[TenantPrefix.Length..].Split('/')[0]).ToHashSet(StringComparer.Ordinal);

        /// <summary>The hosted services in registration order: the implementation type, or the type a factory returns.</summary>
        internal static IReadOnlyList<string> HostedServices(IServiceCollection services, IServiceProvider? provider = null)
            => services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => (d.ImplementationType ?? Created(d.ImplementationFactory, provider) ?? FactoryType(d.ImplementationFactory))?.FullName ?? "?").Select(n => System.Text.RegularExpressions.Regex.Replace(n, @", Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=[^,\]]+", "")).ToList();

        /// <summary>With a built host, a factory registration is named by the service it makes (the factories only resolve or construct, they don't start anything).</summary>
        private static Type? Created(Func<IServiceProvider, object>? factory, IServiceProvider? provider)
        {
            try
            {
                return factory is null || provider is null ? null : factory(provider).GetType();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary><c>AddHostedService(sp =&gt; ...)</c> wraps the host's lambda: the type that lambda returns names the service.</summary>
        private static Type? FactoryType(Func<IServiceProvider, object>? factory)
        {
            var inner = factory?.Target?.GetType().GetFields().Select(f => f.GetValue(factory.Target)).OfType<Delegate>().FirstOrDefault();
            return (inner ?? factory)?.Method.ReturnType;
        }

        internal static string Text(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";

        /// <summary>The snapshot beside the sources. Set <c>JUICE_WRITE_SNAPSHOTS=1</c> to write it from the host under test (only against the base).</summary>
        internal static void AssertSnapshot(string file, IEnumerable<string> lines, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        {
            var path = Path.Combine(Path.GetDirectoryName(here)!, file);
            var actual = Text(lines);
            if (Environment.GetEnvironmentVariable("JUICE_WRITE_SNAPSHOTS") == "1")
            {
                File.WriteAllText(path, actual, new UTF8Encoding(false));
            }
            Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
        }
    }
}
