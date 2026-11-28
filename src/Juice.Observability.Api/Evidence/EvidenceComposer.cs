using System.Text.RegularExpressions;
using Juice.Observability.Api.Modules;
using Juice.Observability.Extensibility;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Evidence
{
    /// <summary>A contributor a module (or the core) declared: its type is resolved from the request's scope.</summary>
    internal sealed record EvidenceRegistration(string Module, Type Type);

    /// <summary>A registration after the host's start check: what the contributor said about itself.</summary>
    internal sealed record EvidenceContributorInfo(string Key, string Module, EvidenceTarget Target, string? Policy, Type Type);

    /// <summary><c>Evidence:ContributorTimeout</c> (0.5 to 10 s) and <c>Evidence:Budget</c> (1 to 60 s, spent across all contributors of a request).</summary>
    public sealed class EvidenceOptions
    {
        public const string Section = "Evidence";

        public TimeSpan ContributorTimeout { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>The host's <see cref="IEvidenceComposer"/>, registered as an instance before any module composes.</summary>
    internal sealed class EvidenceComposerService : IEvidenceComposer
    {
        public void AddContributor<T>(IModuleServicesBuilder b) where T : class, IEvidenceContributor
        {
            // Only the host's own builder: a stand-in could report another module's name.
            CoreBuilders.Require(b);
            var module = b.Module.Name;
            var declared = b.Services.Where(d => d.ServiceType == typeof(EvidenceRegistration)).Select(d => (EvidenceRegistration)d.ImplementationInstance!).ToList();
            if (declared.Any(r => r.Type == typeof(T)))
            {
                throw new ModuleCompositionException($"Module '{module}' adds evidence contributor {typeof(T).Name} twice, or another module already did.");
            }
            if (declared.Count(r => r.Module == module) >= EvidenceModuleExtensions.MaxContributorsPerModule)
            {
                throw new ModuleCompositionException($"Module '{module}' adds more than {EvidenceModuleExtensions.MaxContributorsPerModule} evidence contributors.");
            }
            b.Services.AddSingleton(new EvidenceRegistration(module, typeof(T)));
            b.Services.AddScoped<T>();
        }
    }

    public static class EvidenceServiceExtensions
    {
        /// <summary>The evidence composer, the core's own contributor and the runner. Before <c>AddInternalModules</c>: modules compose against the composer.</summary>
        public static IServiceCollection AddPlatformEvidence(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<EvidenceOptions>().Bind(configuration.GetSection(EvidenceOptions.Section))
                .Validate(o => o.ContributorTimeout >= TimeSpan.FromSeconds(0.5) && o.ContributorTimeout <= TimeSpan.FromSeconds(10),
                    "Evidence:ContributorTimeout is between 0.5 and 10 seconds.")
                .Validate(o => o.Budget >= TimeSpan.FromSeconds(1) && o.Budget <= TimeSpan.FromSeconds(60), "Evidence:Budget is between 1 and 60 seconds.")
                .ValidateOnStart();
            services.AddSingleton<IEvidenceComposer>(new EvidenceComposerService());
            // The core's contributors come first, in registration order, and are registered like a module's.
            services.AddSingleton(new EvidenceRegistration("core", typeof(ChangeEventsContributor)));
            services.AddScoped<ChangeEventsContributor>();
            services.AddSingleton<EvidenceCatalog>();
            services.AddScoped<EvidenceRunner>();
            return services;
        }
    }

    /// <summary>
    /// The contributors of a host in the order they run (the core's, then each module's in composition order), and the Console routes their links may
    /// point to. <see cref="Verify"/> runs when the routes are mapped, before the host starts.
    /// </summary>
    internal sealed partial class EvidenceCatalog(ModuleRegistry registry, IEnumerable<EvidenceRegistration> registrations)
    {
        private IReadOnlyList<EvidenceContributorInfo>? _verified;

        [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
        private static partial Regex Name();

        public IReadOnlyList<EvidenceContributorInfo> For(EvidenceTarget target)
            => (_verified ?? throw new InvalidOperationException("The evidence contributors are checked when the routes are mapped.")).Where(i => i.Target == target).ToList();

        /// <summary>Key under the module's prefix and unique, a defined target, and a policy that exists: a core policy, or one of the contributor's own module.</summary>
        public void Verify(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var infos = new List<EvidenceContributorInfo>();
            // The core's first; the sort is stable, so each module keeps its registration order.
            foreach (var registration in registrations.OrderBy(r => r.Module == "core" ? 0 : 1))
            {
                var module = registration.Module;
                var contributor = (IEvidenceContributor)scope.ServiceProvider.GetRequiredService(registration.Type);
                var key = contributor.Key;
                var prefix = module + ".";
                if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal) || !Name().IsMatch(key[prefix.Length..]))
                {
                    throw new ModuleCompositionException($"Module '{module}' evidence contributor {registration.Type.Name} has key '{key}': a key is '{module}.<name>' (lowercase letters, digits and '-').");
                }
                if (infos.Any(i => i.Key == key))
                {
                    throw new ModuleCompositionException($"Module '{module}' evidence contributor {registration.Type.Name} uses key '{key}', which another contributor has.");
                }
                if (!Enum.IsDefined(contributor.Target))
                {
                    throw new ModuleCompositionException($"Module '{module}' evidence contributor '{key}' has an unknown target.");
                }
                var policy = contributor.Policy;
                var own = registry.Entries.FirstOrDefault(e => e.Info.Name == module)?.Policies;
                if (policy is not null && policy is not (CorePolicies.Read or CorePolicies.Operate) && own?.Contains(policy) != true)
                {
                    throw new ModuleCompositionException($"Module '{module}' evidence contributor '{key}' names the policy '{policy}': it is neither a core policy nor one of the module's own.");
                }
                infos.Add(new EvidenceContributorInfo(key, module, contributor.Target, policy, registration.Type));
            }
            _verified = infos;
        }

        /// <summary>The route patterns of every composed manifest, split into segments (<c>:name</c> matches one segment).</summary>
        private IReadOnlyList<string[]> Patterns()
            => registry.Entries.Where(e => e.Manifest is not null).SelectMany(e => e.Manifest!.Routes).Select(r => r.Path.Split('/')).ToList();

        /// <summary>Whether a concrete relative route matches a route pattern of a composed manifest.</summary>
        public bool RouteMatches(string route)
        {
            var segments = route.Split('/');
            return Patterns().Any(pattern => pattern.Length == segments.Length
                && pattern.Zip(segments).All(p => p.First.StartsWith(':') || p.First == p.Second));
        }
    }
}
