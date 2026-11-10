using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Juice.Observability.Data;

namespace Juice.Observability.Slos
{
    /// <summary>How an HTTP 4xx other than 408, 429 and 499 counts: the client's mistake, answered correctly, by default.</summary>
    public enum ClientErrorPolicy { Good, Bad, Exclude }

    /// <summary>How a request the client gave up on (408, 499) counts: nobody was waiting for the answer, so it's left out by default.</summary>
    public enum CancellationPolicy { Exclude, Bad }

    /// <summary>
    /// An SLO as written in deployment configuration (<c>Slos:Definitions</c>), validated at startup: an invalid one stops the host. Each change of a
    /// key's definition becomes a new version from <see cref="EffectiveFrom"/>; see the Phase 9 plan for what counts as a request.
    /// </summary>
    public sealed partial class SloDefinition
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>One of <see cref="SliKinds.Supported"/>; a reserved kind is refused as not supported yet.</summary>
        public string Sli { get; set; } = "";
        /// <summary><c>namespace/name</c> or <c>name</c>.</summary>
        public string Service { get; set; } = "";
        /// <summary>Environment key (lowercase).</summary>
        public string Environment { get; set; } = "";
        /// <summary>The objective, as a percentage of good requests: 99.5.</summary>
        public double Target { get; set; }
        /// <summary>Latency only: a request is good at or under this, one of <see cref="SloHistogram.Boundaries"/>.</summary>
        public int? ThresholdMs { get; set; }
        /// <summary>Only these operations (route or span name, <c>*</c> as a wildcard); empty for all.</summary>
        public List<string> Operations { get; set; } = [];
        /// <summary>
        /// More operations that are never requests, on top of the health checks. (Configuration can't replace a list with an empty one, so the
        /// health checks have their own switch.)
        /// </summary>
        public List<string> ExcludeOperations { get; set; } = [];

        /// <summary>Leave out <see cref="DefaultExclusions"/>, the health checks; false counts them as requests.</summary>
        [JsonIgnore]
        public bool ExcludeHealthChecks { get; set; } = true;

        /// <summary>Everything excluded: the health checks unless switched off, then <see cref="ExcludeOperations"/>.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> Exclusions => [.. (ExcludeHealthChecks ? DefaultExclusions : []).Concat(ExcludeOperations).Distinct()];
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ClientErrorPolicy ClientErrors { get; set; } = ClientErrorPolicy.Good;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CancellationPolicy Cancellations { get; set; } = CancellationPolicy.Exclude;
        /// <summary>When this definition applies; null for when it's loaded. A new version can't start in the past.</summary>
        public DateTimeOffset? EffectiveFrom { get; set; }

        /// <summary>
        /// Health checks: each path and what's under it, not every route that starts the same way (<c>/livestream</c> and <c>/healthcare/claims</c>
        /// are real requests).
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultExclusions =
            ["/health", "/health/*", "/healthz", "/healthz/*", "/ready", "/ready/*", "/live", "/live/*", "/metrics", "/metrics/*"];

        public IEnumerable<string> Validate()
        {
            var at = string.IsNullOrEmpty(Key) ? "slo" : $"slo '{Key}'";
            if (!KeyPattern().IsMatch(Key)) yield return $"{at}: Key must match {KeyPattern()}";
            if (Name.Length is 0 or > 200) yield return $"{at}: Name is required (at most 200 characters)";
            if (SliKinds.Reserved.Contains(Sli)) yield return $"{at}: Sli '{Sli}' is reserved for a later version and not supported yet";
            else if (SliKinds.Find(Sli) is not { } kind) yield return $"{at}: Sli is one of {string.Join(", ", SliKinds.Supported.Select(k => k.Name))}";
            else foreach (var error in kind.Validate(this)) yield return $"{at}: {error}";
            if (Service.Length is 0 or > 256) yield return $"{at}: Service is required (namespace/name or name, at most 256 characters)";
            if (Environment.Length is 0 or > 256 || Environment != Environment.ToLowerInvariant()) yield return $"{at}: Environment is a lowercase key";
            if (!(Target > 0 && Target < 100)) yield return $"{at}: Target is a percentage above 0 and below 100, such as 99.5";
            if (Operations.Count > 50 || ExcludeOperations.Count > 50) yield return $"{at}: at most 50 Operations and 50 ExcludeOperations";
            foreach (var pattern in Operations.Concat(Exclusions).Where(p => !OperationPattern().IsMatch(p)))
                yield return $"{at}: '{pattern}' isn't an operation pattern: a route or span name of letters, digits, spaces and / _ . : {{ }} - with * as a wildcard";
            if (!Enum.IsDefined(ClientErrors)) yield return $"{at}: ClientErrors is Good, Bad or Exclude";
            if (!Enum.IsDefined(Cancellations)) yield return $"{at}: Cancellations is Exclude or Bad";
        }

        /// <summary>The category its SLI kind belongs to.</summary>
        [JsonIgnore]
        public SloCategory Category => SliKinds.Find(Sli)?.Category ?? SloCategory.Technical;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        /// <summary>
        /// What a version stores and hashes: everything but when it takes effect, which is the version's own. Every exclusion is written out in
        /// <see cref="ExcludeOperations"/>, health checks included, so the same exclusions however they were written are the same version.
        /// </summary>
        public string ToJson()
        {
            var copy = (SloDefinition)MemberwiseClone();
            copy.EffectiveFrom = null;
            copy.ExcludeOperations = [.. Exclusions];
            return JsonSerializer.Serialize(copy, Json);
        }

        /// <summary>A stored version: its <see cref="ExcludeOperations"/> is already the whole list.</summary>
        public static SloDefinition FromJson(string json)
        {
            var definition = JsonSerializer.Deserialize<SloDefinition>(json, Json)!;
            definition.ExcludeHealthChecks = false;
            return definition;
        }

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

        /// <summary><c>/health*</c> → <c>/health%</c> for LIKE, with LIKE's own characters escaped.</summary>
        public static string ToLike(string pattern) => pattern.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%');

        public (string Namespace, string Name) SplitService() => Discovery.ClickHouseTelemetrySource.SplitService(Service);

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex KeyPattern();

        [GeneratedRegex(@"^[A-Za-z0-9 /_.:{}*-]{1,200}$")]
        private static partial Regex OperationPattern();
    }

    /// <summary>
    /// Fixed latency boundaries, in milliseconds, roughly log-spaced from 1 ms to a minute, with one slot more for anything slower. A latency SLO's
    /// threshold is one of them, so its good count reads straight from the histogram.
    /// </summary>
    public static class SloHistogram
    {
        public static readonly IReadOnlyList<int> Boundaries =
            [1, 2, 5, 10, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 3000, 5000, 7500, 10000, 15000, 30000, 60000];

        /// <summary>Slots: one per boundary, then one for slower than the last.</summary>
        public static int Slots => Boundaries.Count + 1;

        /// <summary>Requests at or under <paramref name="thresholdMs"/>, which must be a boundary.</summary>
        public static long AtOrUnder(IReadOnlyList<long> histogram, int thresholdMs)
        {
            var slots = Boundaries.TakeWhile(b => b <= thresholdMs).Count();
            return histogram.Take(slots).Sum();
        }
    }

    /// <summary>
    /// One kind of SLI: which category it's in, what its definition must say, and how a counted minute becomes its total and good. Adding a kind
    /// (a business success rate, message processing, queue latency, data freshness) is a new class here; budgets, burn rates and storage don't change.
    /// </summary>
    public abstract class SliKind
    {
        public abstract string Name { get; }
        public abstract SloCategory Category { get; }
        public virtual IEnumerable<string> Validate(SloDefinition definition) => [];
        /// <summary>The SLI's total and good for a minute's counts, under the definition effective then.</summary>
        public abstract (long Total, long Good) Score(MinuteCounts counts, SloDefinition definition);
    }

    /// <summary>A minute's requests as counted: eligible and bad, excluded by reason, and the latency histogram of eligible successes.</summary>
    public sealed record MinuteCounts(long Eligible, long Bad, long ExcludedHealth, long ExcludedCancelled, long ExcludedClientErrors, long[] Histogram)
    {
        public static MinuteCounts Empty => new(0, 0, 0, 0, 0, []);
        public long Successful => Eligible - Bad;
    }

    /// <summary>Requests that counted, good when they didn't fail.</summary>
    public sealed class AvailabilitySli : SliKind
    {
        public override string Name => SliKinds.Availability;
        public override SloCategory Category => SloCategory.Technical;

        public override IEnumerable<string> Validate(SloDefinition definition)
        {
            if (definition.ThresholdMs is not null) yield return "ThresholdMs is for Latency, not Availability";
        }

        public override (long Total, long Good) Score(MinuteCounts counts, SloDefinition definition) => (counts.Eligible, counts.Successful);
    }

    /// <summary>Successful requests, good when at or under the threshold. A failure's speed means nothing: availability counts it.</summary>
    public sealed class LatencySli : SliKind
    {
        public override string Name => SliKinds.Latency;
        public override SloCategory Category => SloCategory.Technical;

        public override IEnumerable<string> Validate(SloDefinition definition)
        {
            if (definition.ThresholdMs is not { } threshold || !SloHistogram.Boundaries.Contains(threshold))
                yield return $"ThresholdMs is required for Latency and is one of {string.Join(", ", SloHistogram.Boundaries)}";
        }

        public override (long Total, long Good) Score(MinuteCounts counts, SloDefinition definition)
            => (counts.Histogram.Sum(), SloHistogram.AtOrUnder(counts.Histogram, definition.ThresholdMs ?? 0));
    }

    public static class SliKinds
    {
        public const string Availability = "Availability";
        public const string Latency = "Latency";

        public static readonly IReadOnlyList<SliKind> Supported = [new AvailabilitySli(), new LatencySli()];

        /// <summary>Kinds the model has room for and a later version will add; a definition naming one is refused for now.</summary>
        public static readonly IReadOnlyList<string> Reserved = ["BusinessSuccess", "MessageProcessing", "QueueLatency", "DataFreshness"];

        public static SliKind? Find(string name) => Supported.FirstOrDefault(k => k.Name == name);
    }

    /// <summary>Configuration section <c>Slos</c>.</summary>
    public sealed class SloOptions
    {
        public const string Section = "Slos";

        /// <summary>Counting; definitions are loaded and audited either way, so the API can show them.</summary>
        public bool Enabled { get; set; } = true;
        public List<SloDefinition> Definitions { get; set; } = [];
        /// <summary>How often the counter runs.</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>The newest minutes are counted again on each pass for this long, for telemetry that arrives late, then sealed.</summary>
        public TimeSpan Lateness { get; set; } = TimeSpan.FromMinutes(5);
        /// <summary>Minutes counted per SLO per pass at most: a new SLO's 30 days are counted over several passes.</summary>
        public int MaxMinutesPerPass { get; set; } = 720;
        /// <summary>Buckets older than this are deleted.</summary>
        public TimeSpan Retention { get; set; } = TimeSpan.FromDays(35);
        /// <summary>One SLO's ClickHouse query and save; beyond it the pass gives up and the minutes stay unknown.</summary>
        public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Fewer SLI events than this in the 30 days is <c>InsufficientData</c>.</summary>
        public long MinimumEvents { get; set; } = 1000;
        /// <summary>Fewer events than this in a burn window says nothing about its rate.</summary>
        public long MinimumBurnEvents { get; set; } = 10;
        /// <summary>More of a window's minutes unknown than this share makes it <c>Unknown</c>.</summary>
        public double UnknownTolerance { get; set; } = 0.10;

        /// <summary>SLOs a deployment may define: the API lists them all on one page.</summary>
        public const int MaxSlos = 200;

        /// <summary>The budget window: 30 days, rolling (owner's decision).</summary>
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);

        public IEnumerable<string> Validate()
        {
            if (Definitions.Count > MaxSlos) yield return $"Slos:Definitions: at most {MaxSlos} SLOs";
            foreach (var error in Definitions.SelectMany(d => d.Validate())) yield return error;
            foreach (var key in Definitions.GroupBy(d => d.Key).Where(g => g.Count() > 1).Select(g => g.Key)) yield return $"slo '{key}': defined more than once";
            // Windows end two minutes before now (SloReader.Settle): a pass at least every minute keeps the minutes before that counted.
            if (Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromMinutes(1)) yield return "Slos:Interval is 1 second to 1 minute";
            if (Lateness < TimeSpan.FromMinutes(1) || Lateness > TimeSpan.FromHours(1)) yield return "Slos:Lateness is 1 minute to 1 hour";
            if (MaxMinutesPerPass is < 10 or > 10_080) yield return "Slos:MaxMinutesPerPass is 10 to 10,080";
            // A pass counts the last Lateness again before reaching the newest minutes: shorter than that, the newest are never counted.
            if (MaxMinutesPerPass < Lateness.TotalMinutes + 5) yield return "Slos:MaxMinutesPerPass is at least Slos:Lateness plus 5 minutes";
            if (Retention < Window) yield return "Slos:Retention is at least the 30-day window";
            if (MinimumEvents < 1 || MinimumBurnEvents < 1) yield return "Slos:MinimumEvents and MinimumBurnEvents are at least 1";
            if (UnknownTolerance is < 0 or > 1) yield return "Slos:UnknownTolerance is 0 to 1";
        }
    }
}
