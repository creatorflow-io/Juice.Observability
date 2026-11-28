using System.Diagnostics.CodeAnalysis;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// The rule templates the host knows (core 2.0 M2): the built-ins under their <see cref="Juice.Observability.Data.RuleTemplate"/> names, and the
    /// modules' under <c>&lt;module&gt;.&lt;name&gt;</c>; and the threshold providers: the core's <c>fixed</c> and the modules'. A key is held once.
    /// Built at composition: a module's template or provider with a key outside its module's prefix, or a key held twice, stops the host.
    /// </summary>
    public sealed class RuleTemplateRegistry : IRuleTemplateRegistry
    {
        private readonly Dictionary<string, IRuleTemplate> _templates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IRuleThresholdProvider> _providers = new(StringComparer.Ordinal);

        public RuleTemplateRegistry(IEnumerable<IRuleTemplate> builtIns, IEnumerable<IRuleTemplate>? modules = null,
            IEnumerable<IRuleThresholdProvider>? providers = null, IEnumerable<ModuleRuleContribution>? contributions = null,
            IEnumerable<ModuleAlertSourceDeclaration>? alertSources = null)
        {
            var owners = (contributions ?? []).ToList();
            var declared = (alertSources ?? []).Select(a => a.Template).ToHashSet(StringComparer.Ordinal);
            var builtInInfos = new List<RuleTemplateInfo>();
            foreach (var template in builtIns)
            {
                Add(template, null);
                builtInInfos.Add(template.Info);
            }
            foreach (var template in modules ?? [])
            {
                Add(template, Owner(owners, template.GetType(), "rule template"));
            }
            _providers[FixedThresholdProvider.Key] = new FixedThresholdProvider();
            foreach (var provider in providers ?? [])
            {
                var owner = Owner(owners, provider.GetType(), "threshold provider");
                if (RuleTemplateKeys.Error(owner, provider.Key) is { } error)
                {
                    throw new ModuleCompositionException($"module '{owner}': threshold provider {provider.GetType().Name}: {error}");
                }
                if (!_providers.TryAdd(provider.Key, provider))
                {
                    throw new ModuleCompositionException($"module '{owner}': threshold provider key '{provider.Key}' is already taken");
                }
            }
            All = [.. builtInInfos, .. _templates.Values.Select(t => t.Info).Where(i => !builtInInfos.Contains(i)).OrderBy(i => i.Key, StringComparer.Ordinal)];

            void Add(IRuleTemplate template, string? owner)
            {
                var info = template.Info ?? throw new ModuleCompositionException($"rule template {template.GetType().Name} has no Info");
                if (owner is not null)
                {
                    if (RuleTemplateKeys.Error(owner, info.Key) is { } error)
                    {
                        throw new ModuleCompositionException($"module '{owner}': rule template {template.GetType().Name}: {error}");
                    }
                    if (declared.Contains(info.Key))
                    {
                        throw new ModuleCompositionException($"module '{owner}': rule template {template.GetType().Name}: '{info.Key}' is declared as an alert source, and a template key is not an alert source's");
                    }
                    if (string.IsNullOrWhiteSpace(info.DisplayName) || info.DisplayName.Length > 64)
                    {
                        throw new ModuleCompositionException($"module '{owner}': rule template '{info.Key}': the display name must be 1 to 64 characters");
                    }
                }
                if (!_templates.TryAdd(info.Key, template))
                {
                    throw new ModuleCompositionException($"{(owner is null ? "core" : $"module '{owner}'")}: rule template key '{info.Key}' is already taken");
                }
            }
        }

        /// <summary>Built-ins first, in the order they were given, then the modules' by key.</summary>
        public IReadOnlyList<RuleTemplateInfo> All { get; }

        public bool TryGet(string key, [NotNullWhen(true)] out IRuleTemplate? template) => _templates.TryGetValue(key ?? "", out template);

        internal bool TryGetProvider(string key, [NotNullWhen(true)] out IRuleThresholdProvider? provider) => _providers.TryGetValue(key ?? "", out provider);

        /// <summary>The core's templates only, with no telemetry behind them: enough to validate a rule definition.</summary>
        internal static RuleTemplateRegistry BuiltInOnly { get; } = new(BuiltInTemplates.Create(null));

        private static string Owner(List<ModuleRuleContribution> owners, Type type, string what)
            => owners.FirstOrDefault(o => o.Type == type)?.Module
               ?? throw new ModuleCompositionException($"{what} {type.Name} was not registered by a module (AddRuleTemplate / AddThresholdProvider)");
    }
}
