using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// The agent listener: its own port, TLS with the deployment's server certificate, a client certificate requested from every connection. It
    /// serves the agent API and nothing else, and the agent API is served nowhere else. Behind a proxy, this port must pass TLS through untouched,
    /// since the client certificate is the agent's identity.
    /// </summary>
    public static class AgentListener
    {
        /// <summary>The agent CA and command key, when <c>Agents:Enabled</c>. Invalid settings stop the host.</summary>
        public static IServiceCollection AddPlatformAgents(this IServiceCollection services, IConfiguration configuration)
        {
            var section = configuration.GetSection(AgentPlatformOptions.Section);
            services.Configure<AgentPlatformOptions>(section);
            var options = section.Get<AgentPlatformOptions>() ?? new AgentPlatformOptions();
            if (!options.Enabled)
            {
                return services;
            }
            var errors = options.Validate().ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid agent configuration:\n" + string.Join("\n", errors));
            }
            services.AddSingleton<AgentAuthority>();
            services.AddSingleton<EnrollmentGuard>();
            services.AddSingleton<AgentWakeups>();
            services.AddSingleton<HostLinks>();
            services.AddSingleton<AgentPackageCatalog>();
            return services;
        }

        /// <summary>
        /// Adds the agent listener to Kestrel when agents are enabled. Once code names an endpoint, Kestrel listens only on the endpoints it names, so
        /// this names the host's own too, as it would have chosen them: <c>URLS</c> (<c>ASPNETCORE_URLS</c>, launchSettings) when set, otherwise
        /// <c>HTTP_PORTS</c> and <c>HTTPS_PORTS</c>, otherwise 8080. Does nothing when agents are off.
        /// </summary>
        public static void ListenForAgents(this KestrelServerOptions kestrel, IConfiguration configuration)
        {
            var options = configuration.GetSection(AgentPlatformOptions.Section).Get<AgentPlatformOptions>() ?? new AgentPlatformOptions();
            if (!options.Enabled)
            {
                return;
            }
            foreach (var endpoint in HostEndpoints(configuration))
            {
                if (endpoint.Port == options.Port)
                {
                    throw new InvalidOperationException($"The host's address {endpoint.Url} uses Agents:Port {options.Port}: the agent listener needs a port of its own.");
                }
                Action<ListenOptions> configure = endpoint.Https ? listen => listen.UseHttps() : _ => { };
                switch (endpoint.Host)
                {
                    case "localhost":
                        kestrel.ListenLocalhost(endpoint.Port, configure);
                        break;
                    case null:
                        kestrel.ListenAnyIP(endpoint.Port, configure);
                        break;
                    default:
                        kestrel.Listen(endpoint.Host, endpoint.Port, configure);
                        break;
                }
            }
            var (server, intermediates) = ServerCertificate(options);
            kestrel.ListenAnyIP(options.Port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = server;
                // Sent with it, so an agent can build the chain to the CA it trusts when the certificate comes from an intermediate.
                https.ServerCertificateChain = intermediates;
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                // Accepted here and checked on every request against the agent CA and the agent's record (AgentApi): enrollment comes without one.
                https.ClientCertificateValidation = (_, _, _) => true;
            }));
        }

        /// <summary>One address the host listens on. <see cref="Host"/> is null for every address, <c>localhost</c> for loopback, or an IP.</summary>
        internal sealed record HostEndpoint(string Url, string? Host, int Port, bool Https);

        /// <summary>
        /// The listener's certificate with its key, and any further certificates in the same file: the intermediates up to the CA agents trust,
        /// in the usual order (the server's first).
        /// </summary>
        internal static (X509Certificate2 Server, X509Certificate2Collection Intermediates) ServerCertificate(AgentPlatformOptions options)
        {
            using var pem = X509Certificate2.CreateFromPemFile(options.ServerCertificateFile, options.ServerKeyFile);
            var server = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            var all = new X509Certificate2Collection();
            all.ImportFromPemFile(options.ServerCertificateFile);
            var intermediates = new X509Certificate2Collection();
            foreach (var certificate in all)
            {
                if (certificate.Thumbprint != server.Thumbprint) intermediates.Add(certificate);
            }
            return (server, intermediates);
        }

        /// <summary>
        /// The host's own addresses, from the settings Kestrel would have used without the agent listener. None when the host names its endpoints in
        /// <c>Kestrel:Endpoints</c>: Kestrel binds those itself, beside the ones code adds.
        /// </summary>
        internal static IReadOnlyList<HostEndpoint> HostEndpoints(IConfiguration configuration)
        {
            static IEnumerable<string> List(string? value) => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            {
                return [];
            }
            if (List(configuration["URLS"]).ToList() is { Count: > 0 } urls)
            {
                return [.. urls.Select(Parse)];
            }
            var ports = List(configuration["HTTP_PORTS"]).Select(p => new HostEndpoint($"http://*:{p}", null, int.Parse(p, System.Globalization.CultureInfo.InvariantCulture), false))
                .Concat(List(configuration["HTTPS_PORTS"]).Select(p => new HostEndpoint($"https://*:{p}", null, int.Parse(p, System.Globalization.CultureInfo.InvariantCulture), true)))
                .ToList();
            return ports.Count > 0 ? ports : [new HostEndpoint("http://*:8080", null, 8080, false)];
        }

        /// <summary><c>http://+:5000</c>, <c>https://localhost:5001</c>, <c>http://10.0.0.5:80</c>: Uri can't read the wildcards, so it's split by hand.</summary>
        private static HostEndpoint Parse(string url)
        {
            var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) throw new InvalidOperationException($"URLS entry '{url}' has no scheme");
            var scheme = url[..schemeEnd].ToLowerInvariant();
            if (scheme is not ("http" or "https")) throw new InvalidOperationException($"URLS entry '{url}' must be http or https");
            var authority = url[(schemeEnd + 3)..].Split('/')[0];
            var colon = authority.LastIndexOf(':');
            var (host, port) = colon > 0 && !authority.EndsWith(']') ? (authority[..colon], int.Parse(authority[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture))
                : (authority, scheme == "https" ? 443 : 80);
            host = host.Trim('[', ']');
            // As Kestrel does: a literal IP binds that address, localhost the loopback, and any other name every address.
            var bind = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "localhost"
                : System.Net.IPAddress.TryParse(host, out var ip) && !ip.Equals(System.Net.IPAddress.Any) && !ip.Equals(System.Net.IPAddress.IPv6Any) ? host
                : null;
            return new HostEndpoint(url, bind, port, scheme == "https");
        }

        private static void Listen(this KestrelServerOptions kestrel, string address, int port, Action<ListenOptions> configure)
            => kestrel.Listen(System.Net.IPAddress.Parse(address), port, configure);

        /// <summary>Keeps everything but the agent API off the agent listener. Call it early in the pipeline.</summary>
        public static IApplicationBuilder UseAgentListener(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentPlatformOptions>>().Value;
            if (!options.Enabled)
            {
                return app;
            }
            return app.Use((context, next) =>
            {
                if (context.Connection.LocalPort == options.Port && !context.Request.Path.StartsWithSegments("/agent/v1"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }
                return next(context);
            });
        }
    }
}
