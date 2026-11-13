using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Juice.Observability.Data;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Juice.Observability.Api
{
    /// <summary>The platform's own metrics (Phase 11 R2): <c>Telemetry:*</c>.</summary>
    public sealed class TelemetryOptions
    {
        public const string Section = "Telemetry";

        /// <summary>On unless turned off (owner, Phase 11 R14). With no <see cref="OtlpOptions.Endpoint"/> nothing is exported.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>How often the metrics are exported.</summary>
        public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(15);

        public OtlpOptions Otlp { get; set; } = new();

        /// <summary>
        /// Where the metrics go: the Collector's platform pipeline (<c>otlp/platform</c>), which stamps them with the reserved tenant
        /// <c>juice-platform</c>, so they never enter the tenant's own data.
        /// </summary>
        public sealed class OtlpOptions
        {
            /// <summary>The receiver's base URL, such as <c>https://otel-collector:4319</c> (OTLP over HTTP; <c>/v1/metrics</c> is added).</summary>
            public string? Endpoint { get; set; }

            /// <summary>A file holding the platform's bearer token (the Collector's <c>bearertokenauth/platform</c>).</summary>
            public string? TokenFile { get; set; }

            /// <summary>A CA certificate (PEM) to trust for the receiver's TLS certificate, besides the system's.</summary>
            public string? CaFile { get; set; }
        }
    }

    /// <summary>
    /// Exports what the platform measures about itself (docs/platform-metrics.md): its own meter <c>Juice.Observability</c>, the Juice framework's
    /// (<c>Juice</c>, <c>delivery.metrics</c>), and ASP.NET Core, HttpClient, the .NET runtime and Npgsql, which publish their metrics themselves.
    /// Every exported stream keeps only the attribute keys in <see cref="AllowedKeys"/> (Phase 11 R3): the standard instruments otherwise carry
    /// values such as a webhook receiver's host name or a connection pool named from its connection string.
    /// </summary>
    public static class PlatformTelemetry
    {
        public const string ServiceName = "juice-observability-platform";

        /// <summary>The meters exported.</summary>
        public static readonly IReadOnlyList<string> Meters =
        [
            WorkerPulse.MeterName, "Juice", "delivery.metrics",
            "Microsoft.AspNetCore.Hosting", "System.Net.Http", "System.Runtime", "Npgsql",
        ];

        /// <summary>
        /// The only attribute keys exported, on any instrument: the platform's own fixed sets (a worker, a caller, an outcome...), and the
        /// standard instruments' bounded ones. Not on the list, and so dropped: <c>server.address</c>, <c>url.*</c>, <c>dns.question.name</c>,
        /// <c>db.client.connection.pool.name</c>, and anything a framework adds later.
        /// </summary>
        public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            // the platform's own
            "worker", "caller", "outcome", "kind", "state", "user", "disk", "table", "subject_type", "template", "queued", "reason",
            // ASP.NET Core and HttpClient
            "http.request.method", "http.response.status_code", "http.route", "error.type",
            // the runtime and Npgsql
            "gc.heap.generation", "cpu.mode", "db.client.connection.state",
        };

        /// <summary>Boundaries for the platform's own duration histograms (seconds): a query in milliseconds to a pass of minutes.</summary>
        public static readonly double[] SecondsBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 20, 30, 60, 120, 300];

        private static readonly Meter Meter = new(WorkerPulse.MeterName);
        private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        // An explicit static constructor runs before any member is used, so the gauge exists once the platform's meters are added.
        static PlatformTelemetry()
        {
            // Requirements §9 availability: measured, from readiness probes and this.
            Meter.CreateObservableGauge("juice.observability.process.uptime", () => (DateTime.UtcNow - Started).TotalSeconds, "s",
                "Seconds since this platform process started");
        }

        /// <summary>
        /// The platform's meters with the allowlist, for a host that configures OpenTelemetry itself (<c>Telemetry:Enabled=false</c>, then
        /// this on its own <see cref="MeterProviderBuilder"/>). The allowlist applies to these meters only: the host's own instruments are left
        /// as they are.
        /// </summary>
        public static MeterProviderBuilder AddJuiceObservabilityMeters(this MeterProviderBuilder builder)
        {
            return builder.AddMeter([.. Meters]).AddView(View);
        }

        /// <summary>
        /// What an instrument of one of <see cref="Meters"/> is exported with: the allowlisted keys, and for the platform's own durations,
        /// <see cref="SecondsBuckets"/>. Any other meter's instrument is left to the provider's defaults (null).
        /// </summary>
        internal static MetricStreamConfiguration? View(Instrument instrument)
        {
            if (!Meters.Contains(instrument.Meter.Name))
            {
                return null;
            }
            var keys = AllowedKeys.ToArray();
            if (instrument.Meter.Name == WorkerPulse.MeterName && instrument.Unit == "s" && instrument is Histogram<double>)
            {
                return new ExplicitBucketHistogramConfiguration { Boundaries = SecondsBuckets, TagKeys = keys };
            }
            return new MetricStreamConfiguration { TagKeys = keys };
        }

        /// <summary>
        /// Called by <see cref="PlatformExtensions.AddJuiceObservability"/>: the exporter when <c>Telemetry:Enabled</c> (the default) and an endpoint
        /// is configured, and the platform sampler (<see cref="PlatformSampler"/>) either way, since the status the Console shows uses its samples
        /// too. What was decided is logged once at startup; a configuration problem stops the export, never the platform.
        /// <para>The exporter has a meter provider of its own (<see cref="PlatformMetricsExport"/>), not the host's: a host that also uses
        /// OpenTelemetry keeps its own instruments, exporters and views, and neither sees the other's metrics.</para>
        /// </summary>
        public static IServiceCollection AddPlatformTelemetry(this IServiceCollection services, IConfiguration configuration)
        {
            TelemetryOptions options;
            string? unreadable = null;
            try
            {
                options = configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new TelemetryOptions();
            }
            catch (InvalidOperationException ex)
            {
                // A value that doesn't convert (Telemetry:ExportInterval=15s, Telemetry:Enabled=yes): no export, the platform still starts.
                options = new TelemetryOptions { Enabled = false };
                unreadable = $"not exported: the Telemetry settings can't be read ({ex.InnerException?.Message ?? ex.Message})";
            }
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
            services.AddSingleton<PlatformSampler>();
            services.AddHostedService(sp => sp.GetRequiredService<PlatformSampler>());

            string state;
            if (unreadable is not null)
            {
                state = unreadable;
            }
            else if (!options.Enabled)
            {
                state = "off (Telemetry:Enabled is false)";
            }
            else if (string.IsNullOrWhiteSpace(options.Otlp.Endpoint))
            {
                state = "not exported: no Telemetry:Otlp:Endpoint";
            }
            else if (MetricsEndpoint(options.Otlp.Endpoint) is not { } endpoint)
            {
                state = $"not exported: Telemetry:Otlp:Endpoint '{options.Otlp.Endpoint}' isn't an absolute http or https URL";
            }
            else if (options.ExportInterval < TimeSpan.FromSeconds(1) || options.ExportInterval.TotalMilliseconds > int.MaxValue)
            {
                state = $"not exported: Telemetry:ExportInterval {options.ExportInterval} must be at least 1 s";
            }
            else if (string.IsNullOrWhiteSpace(options.Otlp.TokenFile))
            {
                // The platform receiver takes only the platform's token; without one every export would be refused.
                state = "not exported: no Telemetry:Otlp:TokenFile";
            }
            else if (ReadToken(options.Otlp.TokenFile, out var token) is { } tokenProblem)
            {
                state = $"not exported: {tokenProblem}";
            }
            else if (LoadCa(options.Otlp.CaFile, out var ca) is { } caProblem)
            {
                state = $"not exported: {caProblem}";
            }
            else
            {
                var interval = options.ExportInterval;
                // One client for the exporter's life.
                var client = Client(ca);
                // First among the hosted services: started before the workers and stopped after them (hosted services stop in reverse), so a
                // worker's last pass on shutdown (canceled) is still exported by the final flush.
                services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(_ => new PlatformMetricsExport(() => Sdk.CreateMeterProviderBuilder()
                    .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: WorkerLease.CurrentVersion,
                        autoGenerateServiceInstanceId: false, serviceInstanceId: Environment.MachineName))
                    .AddJuiceObservabilityMeters()
                    .AddOtlpExporter((exporter, reader) =>
                    {
                        exporter.Endpoint = endpoint;
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        exporter.Headers = $"Authorization=Bearer {token}";
                        exporter.HttpClientFactory = () => client;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = (int)interval.TotalMilliseconds;
                    })
                    .Build()!)));
                state = $"exported to {endpoint} every {interval.TotalSeconds:0} s";
            }
            services.AddHostedService(sp => new StartupNote(sp.GetRequiredService<ILogger<StartupNote>>(), state));
            return services;
        }

        /// <summary>The OTLP/HTTP metrics path on an absolute http or https base URL (a URL that already names it is kept); null for anything else.</summary>
        internal static Uri? MetricsEndpoint(string? endpoint)
        {
            var trimmed = (endpoint ?? "").Trim().TrimEnd('/');
            var full = trimmed.EndsWith("/v1/metrics", StringComparison.Ordinal) ? trimmed : trimmed + "/v1/metrics";
            return Uri.TryCreate(full, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
        }

        /// <summary>The token in <paramref name="file"/> (its first line); null with no file. Returns a problem to report, or null.</summary>
        internal static string? ReadToken(string? file, out string? token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }
            try
            {
                token = File.ReadLines(file).FirstOrDefault()?.Trim();
                return string.IsNullOrEmpty(token) ? $"the token file {file} is empty" : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return $"the token file {file} can't be read ({ex.GetType().Name})";
            }
        }

        /// <summary>The CA certificate in <paramref name="file"/>; null with no file. Returns a problem to report, or null.</summary>
        internal static string? LoadCa(string? file, out X509Certificate2? ca)
        {
            ca = null;
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }
            try
            {
                ca = X509CertificateLoader.LoadCertificateFromFile(file);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                return $"the CA file {file} can't be read ({ex.GetType().Name})";
            }
        }

        /// <summary>The exporter's client: the system's trust, plus <paramref name="ca"/> as a root when given.</summary>
        internal static HttpClient Client(X509Certificate2? ca)
        {
            var handler = new HttpClientHandler();
            if (ca is not null)
            {
                handler.ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) => Trusted(ca, certificate, errors, presented);
            }
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <summary>
        /// A server certificate the system trusts, or one that chains to <paramref name="ca"/>. Only a chain error is forgiven: a certificate for
        /// another name, or none at all, still fails.
        /// </summary>
        internal static bool Trusted(X509Certificate2 ca, X509Certificate2? certificate, SslPolicyErrors errors, X509Chain? presented = null)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }
            if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                return false;
            }
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            // The intermediates the receiver sent, so a certificate issued under the CA through one still chains.
            if (presented is not null)
            {
                foreach (var element in presented.ChainElements)
                {
                    if (!element.Certificate.Equals(certificate)) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // A certificate from the same CA that isn't for a server (a client certificate) doesn't pass for the receiver's.
            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        }

        /// <summary>The platform's own meter provider: built when the host starts, flushed and disposed when it stops.</summary>
        private sealed class PlatformMetricsExport(Func<MeterProvider> build) : IHostedService
        {
            private MeterProvider? _provider;

            public Task StartAsync(CancellationToken cancellationToken)
            {
                _provider = build();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                _provider?.Dispose();
                _provider = null;
                return Task.CompletedTask;
            }
        }

        /// <summary>Logs once, at startup, whether and where the platform's metrics are exported.</summary>
        private sealed class StartupNote(ILogger<StartupNote> logger, string state) : IHostedService
        {
            public Task StartAsync(CancellationToken cancellationToken)
            {
                logger.LogInformation("Platform metrics: {State}", state);
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
