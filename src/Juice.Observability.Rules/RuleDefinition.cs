using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Juice.Observability.Data;

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

        /// <summary>True when a higher value is worse (every template but ThroughputAbsent).</summary>
        [JsonIgnore]
        public bool FiresAbove => Template != RuleTemplate.ThroughputAbsent;

        [JsonIgnore]
        public double Recovery => RecoveryThreshold ?? Threshold;

        [JsonIgnore]
        public bool UsesSamples => Template is RuleTemplate.ErrorRate or RuleTemplate.LatencyP95 or RuleTemplate.DeliveryFailureRate;

        public IEnumerable<string> Validate()
        {
            var at = string.IsNullOrEmpty(Key) ? "rule" : $"rule '{Key}'";
            if (!KeyPattern().IsMatch(Key)) yield return $"{at}: Key must match {KeyPattern()}";
            if (Name.Length is 0 or > 200) yield return $"{at}: Name is required (at most 200 characters)";
            if (!Enum.IsDefined(Template)) yield return $"{at}: unknown Template";
            if (Schedule < TimeSpan.FromSeconds(5) || Schedule > TimeSpan.FromHours(1)) yield return $"{at}: Schedule must be 5 s to 1 h";
            if (Window < Schedule || Window > TimeSpan.FromHours(6)) yield return $"{at}: Window must be at least Schedule and at most 6 h";
            if (ForDuration < TimeSpan.Zero || ForDuration > TimeSpan.FromDays(1)) yield return $"{at}: ForDuration must be 0 to 24 h";
            if (RecoveryDuration < TimeSpan.Zero || RecoveryDuration > TimeSpan.FromDays(1)) yield return $"{at}: RecoveryDuration must be 0 to 24 h";
            if (Cooldown < TimeSpan.Zero || Cooldown > TimeSpan.FromDays(1)) yield return $"{at}: Cooldown must be 0 to 24 h";
            if (MinimumSamples is < 1 or > 1_000_000) yield return $"{at}: MinimumSamples must be 1 to 1,000,000";
            if (Template is RuleTemplate.ErrorRate or RuleTemplate.DeliveryFailureRate or RuleTemplate.HostMemory
                && (Threshold is < 0 or > 1 || Recovery is < 0 or > 1))
                yield return $"{at}: Threshold and RecoveryThreshold are ratios from 0 to 1";
            if (Template == RuleTemplate.LatencyP95 && (Threshold <= 0 || Recovery <= 0)) yield return $"{at}: latency thresholds are positive milliseconds";
            if (Template is RuleTemplate.MediatorErrors or RuleTemplate.ConsumerFailures && (Threshold < 0 || Recovery < 0)) yield return $"{at}: error thresholds are counts, not negative";
            if (Template == RuleTemplate.ThroughputAbsent && (Threshold < 0 || Recovery < Threshold)) yield return $"{at}: throughput thresholds are request counts, recovery at least the threshold";
            if (FiresAbove && Recovery > Threshold) yield return $"{at}: RecoveryThreshold must not be above Threshold";
            if (Template == RuleTemplate.HostMemory && (Service.Length > 0 || Environment.Length > 0)) yield return $"{at}: host rules take Host, not Service or Environment";
            if (Template != RuleTemplate.HostMemory && Host.Length > 0) yield return $"{at}: only host rules take Host";
            if (Service.Length > 256 || Host.Length > 256 || Environment.Length > 256) yield return $"{at}: scope values are at most 256 characters";
            if (Environment != Environment.ToLowerInvariant()) yield return $"{at}: Environment is a lowercase key";
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static RuleDefinition FromJson(string json) => JsonSerializer.Deserialize<RuleDefinition>(json, Json)!;

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex KeyPattern();
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
        public static Condition Classify(RuleDefinition rule, double? value, long samples)
        {
            if (value is not { } v || double.IsNaN(v) || (rule.UsesSamples && samples < rule.MinimumSamples))
            {
                return Condition.NoData;
            }
            if (rule.FiresAbove)
            {
                return v > rule.Threshold ? Condition.Breach : v <= rule.Recovery ? Condition.Recovered : Condition.Band;
            }
            return v <= rule.Threshold ? Condition.Breach : v > rule.Recovery ? Condition.Recovered : Condition.Band;
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
