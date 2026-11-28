using System.Text.Json;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Rules
{
    /// <summary>The core's own provider: the rule's own numbers, for every subject.</summary>
    internal sealed class FixedThresholdProvider : IRuleThresholdProvider
    {
        public const string Key = "fixed";

        string IRuleThresholdProvider.Key => Key;

        public ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, JsonElement? settings, IReadOnlyCollection<string> subjectKeys,
            DateTimeOffset at, CancellationToken ct)
        {
            var threshold = new RuleThreshold(rule.Threshold, rule.RecoveryThreshold);
            return ValueTask.FromResult<IReadOnlyDictionary<string, RuleThreshold>>(subjectKeys.Distinct().ToDictionary(k => k, _ => threshold));
        }
    }

    /// <summary>
    /// The only place the evaluator reads thresholds from (core 2.0 M2, W-23). A rule without a <see cref="RuleDefinition.ThresholdSource"/> has its own
    /// numbers for every subject, and nothing is asked. With one, the named provider supplies numbers only: never the comparison, template or scope.
    /// A subject the provider leaves out, or gives a non-finite or inverted pair, has no threshold, which reads as no data: it neither fires nor resolves.
    /// A provider that throws fails the evaluation, so its alerts stay as they are.
    /// </summary>
    public sealed class RuleThresholds(RuleTemplateRegistry? registry = null)
    {
        /// <summary>No module providers: the fixed thresholds only.</summary>
        public static RuleThresholds Default { get; } = new();

        private readonly RuleTemplateRegistry _registry = registry ?? RuleTemplateRegistry.BuiltInOnly;

        /// <summary>The numbers the rule itself holds.</summary>
        public static RuleThreshold Fixed(RuleDefinition rule) => new(rule.Threshold, rule.Recovery);

        /// <summary>
        /// The threshold of each subject the provider gave a usable pair for. A subject missing from the result has no threshold (no data).
        /// A rule with no source returns its own numbers for every subject.
        /// </summary>
        public async ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleDefinition rule, IReadOnlyCollection<string> subjectKeys,
            DateTimeOffset at, CancellationToken cancellationToken)
        {
            var provider = Provider(rule);
            var wanted = subjectKeys.ToHashSet(StringComparer.Ordinal);
            var supplied = await provider.ResolveAsync(rule.ToSpec(), rule.ThresholdSource?.Settings, wanted, at, cancellationToken);
            var result = new Dictionary<string, RuleThreshold>(StringComparer.Ordinal);
            if (supplied is null) return result;
            foreach (var (key, threshold) in supplied)
            {
                if (wanted.Contains(key) && Usable(rule, threshold)) result[key] = threshold;
            }
            return result;
        }

        /// <summary>Whether the provider the rule names is registered (a rule with no source: always).</summary>
        public bool HasProvider(RuleDefinition rule) => _registry.TryGetProvider(rule.ThresholdSource?.Provider ?? FixedThresholdProvider.Key, out _);

        /// <summary>The provider a rule names; a rule with no source has the fixed one. One that is not registered fails the evaluation.</summary>
        private IRuleThresholdProvider Provider(RuleDefinition rule)
        {
            var key = rule.ThresholdSource?.Provider ?? FixedThresholdProvider.Key;
            return _registry.TryGetProvider(key, out var provider)
                ? provider
                : throw new InvalidOperationException($"rule '{rule.Key}': the threshold provider '{key}' is not registered");
        }

        private static bool Usable(RuleDefinition rule, RuleThreshold t)
            => double.IsFinite(t.Threshold) && double.IsFinite(t.Recovery)
               && (rule.FiresAbove ? t.Recovery <= t.Threshold : t.Recovery >= t.Threshold);
    }
}
