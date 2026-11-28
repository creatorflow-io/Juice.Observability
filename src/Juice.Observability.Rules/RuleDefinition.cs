using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Signals;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// A typed rule (decision D5) as written in deployment configuration (<c>Rules:Definitions</c>). Every field is
    /// validated at startup; an invalid rule stops the host instead of being skipped.
    /// <para>
    /// Values per template: <c>ErrorRate</c> and <c>DeliveryFailureRate</c> a ratio (0–1), <c>LatencyP95</c>
    /// milliseconds, <c>HostMemory</c> the used ratio (0–1), <c>MediatorErrors</c> and <c>ConsumerFailures</c> a count in
    /// the window: all fire above <see cref="Threshold"/>.
    /// <c>ThroughputAbsent</c> fires when a service that had traffic in the hour before the window has at most
    /// <see cref="Threshold"/> requests in it.
    /// </para>
    /// </summary>
    public sealed partial class RuleDefinition
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RuleTemplate Template { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

        /// <summary><c>namespace/name</c> or <c>name</c>; empty for every service. Not for host rules.</summary>
        public string Service { get; set; } = "";
        /// <summary>Environment key (lowercase); empty for every environment. Not for host rules.</summary>
        public string Environment { get; set; } = "";
        /// <summary>Host key; empty for every host. Host rules only.</summary>
        public string Host { get; set; } = "";

        /// <summary>How often it's evaluated; the scheduler rounds it up to whole seconds (<see cref="RuleScheduler.Schedule"/>).</summary>
        public TimeSpan Schedule { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
        public double Threshold { get; set; }
        /// <summary>Recovery needs the value on the safe side of this (hysteresis). Defaults to <see cref="Threshold"/>.</summary>
        public double? RecoveryThreshold { get; set; }
        /// <summary>Fewer samples than this in the window is "no data", never a value (ErrorRate, LatencyP95, DeliveryFailureRate).</summary>
        public int MinimumSamples { get; set; } = 10;
        public TimeSpan ForDuration { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan RecoveryDuration { get; set; } = TimeSpan.FromMinutes(2);
        /// <summary>After an alert that fired resolves, a new one for the same subject cannot fire for this long.</summary>
        public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(10);
        public bool Enabled { get; set; } = true;

        /// <summary>The metric a <see cref="RuleTemplate.MetricThreshold"/> rule watches; required for it, refused for any other template.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MetricSpec? Metric { get; set; }

        /// <summary>
        /// Where the thresholds come from when not from <see cref="Threshold"/> and <see cref="RecoveryThreshold"/>; null means those, fixed. Never written
        /// when null, so a rule without one has the same JSON and <see cref="Hash"/> as before the seam. The evaluator reads thresholds only through
        /// <see cref="RuleThresholds"/>.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RuleThresholdSource? ThresholdSource { get; set; }

        /// <summary>True when a higher value is worse (every template but ThroughputAbsent).</summary>
        [JsonIgnore]
        public bool FiresAbove => Template == RuleTemplate.MetricThreshold ? Metric?.Comparison != Comparison.Below : Template != RuleTemplate.ThroughputAbsent;

        /// <summary>HostMemory, HostCpu, HostDisk, or a MetricThreshold over hosts: scoped by <see cref="Host"/>, not Service or Environment.</summary>
        [JsonIgnore]
        public bool IsHostRule => Template is RuleTemplate.HostMemory or RuleTemplate.HostCpu or RuleTemplate.HostDisk
            || Template == RuleTemplate.MetricThreshold && Metric?.Subject == MetricSubject.Host;

        [JsonIgnore]
        public double Recovery => RecoveryThreshold ?? Threshold;

        [JsonIgnore]
        public bool UsesSamples => Template is RuleTemplate.ErrorRate or RuleTemplate.LatencyP95 or RuleTemplate.DeliveryFailureRate
            || Template == RuleTemplate.MetricThreshold
            && Metric is { Kind: MetricKind.Histogram, Aggregation: MetricAggregation.P50 or MetricAggregation.P90 or MetricAggregation.P95 or MetricAggregation.P99 or MetricAggregation.Avg };

        /// <summary>
        /// What is wrong with the definition. With <paramref name="packs"/>, an enabled rule whose template's signal pack is off is wrong too; a
        /// disabled one isn't, so a rule made before the pack was switched off can still be disabled.
        /// </summary>
        public IEnumerable<string> Validate(SignalPacks? packs = null, IRuleTemplateRegistry? registry = null)
        {
            var at = string.IsNullOrEmpty(Key) ? "rule" : $"rule '{Key}'";
            if (Enabled && Enum.IsDefined(Template) && packs?.Refusal(Template) is { } refusal) yield return $"{at}: {refusal}";
            if (!KeyPattern().IsMatch(Key)) yield return $"{at}: Key must match {KeyPattern()}";
            if (Name.Length is 0 or > 200) yield return $"{at}: Name is required (at most 200 characters)";
            if (!Enum.IsDefined(Template)) yield return $"{at}: unknown Template";
            if (Template == RuleTemplate.SloBurn) yield return $"{at}: SloBurn rules are the platform's own; define an SLO instead";
            if (Enum.IsDefined(Template) && Template != RuleTemplate.SloBurn)
            {
                // The template's own checks live with the template, on the registry.
                if (!(registry ?? RuleTemplateRegistry.BuiltInOnly).TryGet(Template.ToString(), out var template)) yield return $"{at}: unknown Template";
                else foreach (var error in template is IBuiltInRuleTemplate builtIn ? builtIn.ValidateDefinition(this) : template.Validate(ToSpec())) yield return $"{at}: {error}";
            }
            foreach (var error in ValidateThresholdSource(registry)) yield return $"{at}: {error}";
            if (Schedule < TimeSpan.FromSeconds(5) || Schedule > TimeSpan.FromHours(1)) yield return $"{at}: Schedule must be 5 s to 1 h";
            if (Window < Schedule || Window > TimeSpan.FromHours(6)) yield return $"{at}: Window must be at least Schedule and at most 6 h";
            if (ForDuration < TimeSpan.Zero || ForDuration > TimeSpan.FromDays(1)) yield return $"{at}: ForDuration must be 0 to 24 h";
            if (RecoveryDuration < TimeSpan.Zero || RecoveryDuration > TimeSpan.FromDays(1)) yield return $"{at}: RecoveryDuration must be 0 to 24 h";
            if (Cooldown < TimeSpan.Zero || Cooldown > TimeSpan.FromDays(1)) yield return $"{at}: Cooldown must be 0 to 24 h";
            if (MinimumSamples is < 1 or > 1_000_000) yield return $"{at}: MinimumSamples must be 1 to 1,000,000";
            if (FiresAbove && Recovery > Threshold) yield return $"{at}: RecoveryThreshold must not be above Threshold";
            if (IsHostRule && (Service.Length > 0|| Environment.Length > 0)) yield return $"{at}: host rules take Host, not Service or Environment";
            if (!IsHostRule && Host.Length > 0) yield return $"{at}: only host rules take Host";
            if (Service.Length > 256 || Host.Length > 256 || Environment.Length > 256) yield return $"{at}: scope values are at most 256 characters";
            if (Environment != Environment.ToLowerInvariant()) yield return $"{at}: Environment is a lowercase key";
        }

        /// <summary>The aggregations each kind of metric can take (R3); Counter's Rate is per second.</summary>
        public static IReadOnlyList<MetricAggregation> AggregationsOf(MetricKind kind) => kind switch
        {
            MetricKind.Gauge => [MetricAggregation.Last, MetricAggregation.Avg, MetricAggregation.Max, MetricAggregation.Min],
            MetricKind.Counter => [MetricAggregation.Rate, MetricAggregation.Increase],
            MetricKind.UpDownCounter => [MetricAggregation.Last],
            _ => [MetricAggregation.P50, MetricAggregation.P90, MetricAggregation.P95, MetricAggregation.P99, MetricAggregation.Avg, MetricAggregation.Rate],
        };

        // .NET's $ also matches before a final newline; a name, key or value never ends in one.
        private static bool Matches(string pattern, string text) => !text.EndsWith('\n') && Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

        /// <summary>What is wrong with the metric. Names, keys and values reach ClickHouse only as typed parameters; these limits keep them small and plain.</summary>
        internal static IEnumerable<string> ValidateMetric(MetricSpec metric)
        {
            if (!Matches(MetricSpec.NamePattern, metric.Name ?? "")) yield return $"Metric.Name must match {MetricSpec.NamePattern}";
            if (metric.Kind is not { } kind || !Enum.IsDefined(kind)) yield return "Metric.Kind is required (Gauge, Counter, UpDownCounter or Histogram)";
            else if (!Enum.IsDefined(metric.Aggregation) || !AggregationsOf(kind).Contains(metric.Aggregation))
                yield return $"Metric.Aggregation {metric.Aggregation} doesn't fit a {kind} (use {string.Join(", ", AggregationsOf(kind))})";
            if (!Enum.IsDefined(metric.Subject)) yield return "Metric.Subject is Service or Host";
            if (!Enum.IsDefined(metric.Comparison)) yield return "Metric.Comparison is Above or Below";
            if (metric.Filters is null) yield break;
            if (metric.Filters.Count > MetricSpec.MaxFilters) yield return $"Metric.Filters has at most {MetricSpec.MaxFilters} filters";
            foreach (var filter in metric.Filters.Take(MetricSpec.MaxFilters * 4))
            {
                if (filter is null) { yield return "Metric.Filters has an empty filter"; continue; }
                var key = filter.Key ?? "";
                var named = $"Metric.Filters {(key.Length > 40 ? key[..40] : key)}";
                if (!Matches(MetricSpec.KeyPattern, key)) yield return $"{named}: Key must match {MetricSpec.KeyPattern}";
                if (!Enum.IsDefined(filter.Op)) yield return $"{named}: Op is Equals or In";
                else if (filter.Op == FilterOp.Equals && (filter.Values ??= []).Count != 1) yield return $"{named}: Equals takes exactly one value";
                else if (filter.Op == FilterOp.In && (filter.Values ??= []).Count is < 1 or > MetricSpec.MaxValues) yield return $"{named}: In takes 1 to {MetricSpec.MaxValues} values";
                if (filter.Values.Any(v => v is null || v.Length is < 1 or > MetricSpec.MaxValueLength))
                    yield return $"{named}: each value is 1 to {MetricSpec.MaxValueLength} characters";
            }
            if (metric.Filters.Where(f => f is not null).GroupBy(f => f.Key).Any(g => g.Count() > 1)) yield return "Metric.Filters has a key more than once";
        }

        /// <summary>
        /// A key the SLO burn rules hold (or will). Checked only when a rule is made: a rule that held one before they were reserved keeps it, and can
        /// still be changed or disabled, since rules can be neither deleted nor renamed.
        /// </summary>
        public static bool IsReservedKey(string key) => SloBurnRules.Keys.Contains(key);

        public static string ReservedKeyError(string key) => $"rule '{key}': the key is reserved for SLO burn alerts";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static RuleDefinition FromJson(string json) => JsonSerializer.Deserialize<RuleDefinition>(json, Json)!;

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex KeyPattern();

        /// <summary>The most a threshold source's settings may be, as JSON text.</summary>
        public const int MaxThresholdSettingsLength = 4096;

        [GeneratedRegex(@"^(fixed|[a-z][a-z0-9-]{0,30}[a-z0-9]\.[a-z0-9][a-z0-9-]{0,62})\z")]
        private static partial Regex ProviderPattern();

        private IEnumerable<string> ValidateThresholdSource(IRuleTemplateRegistry? registry)
        {
            if (ThresholdSource is not { } source) yield break;
            if (source.Provider is null || !ProviderPattern().IsMatch(source.Provider))
            {
                yield return "ThresholdSource.Provider must be 'fixed' or '<module>.<name>'";
                yield break;
            }
            if (source.Settings is { ValueKind: not JsonValueKind.Undefined } settings && settings.GetRawText().Length > MaxThresholdSettingsLength)
            {
                yield return $"ThresholdSource.Settings is at most {MaxThresholdSettingsLength} characters";
            }
            if (!(registry as RuleTemplateRegistry ?? RuleTemplateRegistry.BuiltInOnly).TryGetProvider(source.Provider, out var provider))
            {
                yield return $"ThresholdSource.Provider '{source.Provider}' is not a known provider";
                yield break;
            }
            foreach (var error in provider.Validate(ToSpec(), source.Settings)) yield return $"ThresholdSource: {error}";
        }

        /// <summary>The rule as the template and provider contracts see it: the scope, the window and the numbers, and the whole definition as JSON.</summary>
        internal RuleSpec ToSpec()
        {
            using var document = JsonDocument.Parse(ToJson());
            return new RuleSpec(Key, Template.ToString(), Service, Environment, Host, Window, Threshold, Recovery, MinimumSamples, document.RootElement.Clone());
        }
    }

    /// <summary>Where a rule's thresholds come from instead of its own numbers: a provider's key (<c>&lt;module&gt;.&lt;name&gt;</c>) and its settings.</summary>
    public sealed class RuleThresholdSource
    {
        /// <summary><c>fixed</c> (the rule's own numbers) or a module's provider key.</summary>
        public string Provider { get; set; } = "";

        /// <summary>The provider's own settings, at most <see cref="RuleDefinition.MaxThresholdSettingsLength"/> characters of JSON.</summary>
        public JsonElement? Settings { get; set; }
    }

    /// <summary>Configuration section <c>Rules</c>.</summary>
    public sealed class RulesOptions
    {
        public const string Section = "Rules";

        public bool Enabled { get; set; } = true;
        public List<RuleDefinition> Definitions { get; set; } = [];
        /// <summary>How often the scheduler looks for due rules.</summary>
        public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>Rule evaluations running at once; due rules beyond it wait (counted as lag).</summary>
        public int MaxConcurrency { get; set; } = 4;
        /// <summary>One rule's ClickHouse query and persistence; beyond it the evaluation is cancelled and counted.</summary>
        public TimeSpan EvaluationTimeout { get; set; } = TimeSpan.FromSeconds(20);
        /// <summary>The window ends this long before now, so telemetry still being ingested is not missing.</summary>
        public TimeSpan Lag { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Subjects one evaluation may return.</summary>
        public int RowLimit { get; set; } = 5_000;
    }

    public enum Condition
    {
        /// <summary>The value is on the firing side of the threshold.</summary>
        Breach,
        /// <summary>Between threshold and recovery threshold: not breaching, not yet recovered (hysteresis band).</summary>
        Band,
        /// <summary>The value is on the safe side of the recovery threshold.</summary>
        Recovered,
        /// <summary>No value, or too few samples: never read as zero, never fires, never resolves.</summary>
        NoData,
    }

    /// <summary>What an evaluation does to an alert: <see cref="To"/> is the new state, or null to keep the current one.</summary>
    public sealed record Decision(AlertState? To, string Reason, DateTimeOffset? RecoveringSince);

    /// <summary>The alert state machine (requirements §8), pure so every path is unit-tested.</summary>
    public static class AlertLifecycle
    {
        /// <summary>Against the rule's own thresholds.</summary>
        public static Condition Classify(RuleDefinition rule, double? value, long samples)
            => Classify(rule, RuleThresholds.Fixed(rule), value, samples);

        /// <summary>Against <paramref name="threshold"/>, which the evaluator got from <see cref="RuleThresholds"/>.</summary>
        public static Condition Classify(RuleDefinition rule, RuleThreshold threshold, double? value, long samples)
        {
            if (value is not { } v || double.IsNaN(v) || (rule.UsesSamples && samples < rule.MinimumSamples))
            {
                return Condition.NoData;
            }
            if (rule.FiresAbove)
            {
                return v > threshold.Threshold ? Condition.Breach : v <= threshold.Recovery ? Condition.Recovered : Condition.Band;
            }
            return v <= threshold.Threshold ? Condition.Breach : v > threshold.Recovery ? Condition.Recovered : Condition.Band;
        }

        /// <summary>
        /// <list type="bullet">
        /// <item>No alert: a breach opens one as Pending.</item>
        /// <item>Pending: a breach sustained for ForDuration fires, unless the cooldown of a previous firing alert is
        /// still running; any evaluation that does not breach clears it (Resolved, never fired), so a transient breach
        /// never fires.</item>
        /// <item>Firing: resolves after RecoveryDuration of continuous recovery; a breach or the hysteresis band
        /// restarts recovery.</item>
        /// <item>No data holds every state: it neither fires, clears, recovers nor resets recovery.</item>
        /// </list>
        /// <paramref name="at"/> is the end of the evidence window.
        /// </summary>
        public static Decision Next(RuleDefinition rule, AlertState? current, DateTimeOffset breachStartedAt,
            DateTimeOffset? recoveringSince, Condition condition, DateTimeOffset at, DateTimeOffset? cooldownUntil)
        {
            switch (current)
            {
                case null:
                    return condition == Condition.Breach ? new(AlertState.Pending, "breach", null) : new(null, "no_alert", null);

                case AlertState.Pending:
                    return condition switch
                    {
                        Condition.Breach when at - breachStartedAt >= rule.ForDuration && (cooldownUntil is null || at >= cooldownUntil)
                            => new(AlertState.Firing, "for_duration_met", null),
                        Condition.Breach when at - breachStartedAt >= rule.ForDuration => new(null, "cooldown", null),
                        Condition.Breach => new(null, "breach_pending", null),
                        Condition.NoData => new(null, "no_data", null),
                        _ => new(AlertState.Resolved, "cleared_before_firing", null),
                    };

                case AlertState.Firing:
                    switch (condition)
                    {
                        case Condition.Recovered:
                            var since = recoveringSince ?? at;
                            return at - since >= rule.RecoveryDuration
                                ? new(AlertState.Resolved, "recovered", since)
                                : new(null, "recovering", since);
                        case Condition.NoData:
                            return new(null, "no_data", recoveringSince);
                        default:
                            return new(null, condition == Condition.Breach ? "breach" : "hysteresis_band", null);
                    }

                default:
                    return new(null, "resolved", null);
            }
        }
    }
}
