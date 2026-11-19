using Juice.Observability.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Signals
{
    /// <summary>Configuration section <c>Signals</c>: which signal packs this deployment reads.</summary>
    public sealed class SignalOptions
    {
        public const string Section = "Signals";

        public SignalPackOptions Juice { get; set; } = new();
    }

    public sealed class SignalPackOptions
    {
        /// <summary>
        /// Off, the pack's rule templates can't be used by a new or changed rule (and observe nothing for an existing one), and the features
        /// it adds are left out. On by default: a deployment without Juice services only reads nothing through it.
        /// </summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>A signal pack as <c>GET /api/v1/signal-packs</c> describes it: the names it reads, so a service can be checked against it.</summary>
    public sealed record SignalPackDto(string Name, int Version, bool Enabled, IReadOnlyList<string> RuleTemplates, IReadOnlyList<string> Features,
        IReadOnlyList<string> Metrics, IReadOnlyList<string> Attributes, IReadOnlyList<string> Scopes);

    /// <summary>Which signal packs are on. Without configuration (as in a test) every pack is.</summary>
    public sealed class SignalPacks(IOptions<SignalOptions> options)
    {
        public static readonly SignalPacks All = new(Options.Create(new SignalOptions()));

        public bool Juice => options.Value.Juice.Enabled;

        /// <summary>The pack that provides a template, or null for one that reads only OpenTelemetry's conventions.</summary>
        public static string? PackOf(RuleTemplate template) => JuiceSignals.RuleTemplates.Contains(template) ? JuiceSignals.Name : null;

        public bool Provides(RuleTemplate template) => PackOf(template) is not JuiceSignals.Name || Juice;

        /// <summary>Why a rule can't use its template here, or null when it can.</summary>
        public string? Refusal(RuleTemplate template)
            => Provides(template) ? null : $"template {template} reads the {PackOf(template)} signal pack, which is off (Signals:Juice:Enabled)";

        public IReadOnlyList<SignalPackDto> Describe() => [JuiceSignals.Describe(Juice)];
    }

    public static class SignalServiceCollectionExtensions
    {
        public static IServiceCollection AddSignalPacks(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<SignalOptions>(configuration.GetSection(SignalOptions.Section));
            services.TryAddSingleton<SignalPacks>();
            return services;
        }
    }
}
