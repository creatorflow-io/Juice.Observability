namespace Juice.Observability.Data
{
    // Service level objectives (roadmap Phase 9).

    /// <summary>Technical objectives (availability, latency) and business ones (a business outcome's success) are kept apart everywhere.</summary>
    public enum SloCategory { Technical, Business }

    /// <summary>
    /// The platform's three burn-rate rules (Phase 9 plan): every SLO's burn alerts belong to one of them, so routing and silences can name all of
    /// an SLO kind at once. Their keys are reserved: the rules file and the API refuse them.
    /// </summary>
    public static class SloBurnRules
    {
        public const string FastBurn = "slo-fast-burn";
        public const string SlowBurn = "slo-slow-burn";
        public const string SlowBurn3d = "slo-slow-burn-3d";
        public static readonly IReadOnlyList<string> Keys = [FastBurn, SlowBurn, SlowBurn3d];

        /// <summary>An SLO alert's dedup key: one open alert per rule and SLO, though several SLOs may watch one service.</summary>
        public static string DedupKey(string ruleKey, string sloKey) => $"{ruleKey}|slo:{sloKey}";
    }

    /// <summary>
    /// An SLO as the deployment's file names it, by <see cref="Key"/>. What it measures lives in its <see cref="SloVersion"/>s; this row is the key's
    /// current state: its newest version, whether the file still lists it, and how far its minutes have been counted.
    /// </summary>
    public sealed class Slo
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Key { get; init; }
        public required string Name { get; set; }
        public SloCategory Category { get; init; }
        /// <summary>The SLI kind: <c>Availability</c> or <c>Latency</c> today. Fixed for a key: measuring something else is a new SLO.</summary>
        public required string Sli { get; init; }
        /// <summary><c>namespace/name</c> or <c>name</c>.</summary>
        public required string Service { get; set; }
        public required string Environment { get; set; }
        /// <summary>The newest version's number, which may not be effective yet.</summary>
        public int CurrentVersion { get; set; }
        /// <summary>The file no longer lists it: nothing more is counted; what was counted stays until retention removes it.</summary>
        public bool Retired { get; set; }
        public DateTimeOffset? RetiredAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        /// <summary>Minutes before this are counted (the last few are counted again until they're sealed); null before the first pass.</summary>
        public DateTimeOffset? CountedThrough { get; set; }
    }

    /// <summary>
    /// One version of an SLO: its whole definition as loaded, immutable. It applies to minutes from <see cref="EffectiveFrom"/> until the next
    /// version's; the first version also covers the history before it.
    /// </summary>
    public sealed class SloVersion
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid SloId { get; init; }
        public int Version { get; init; }
        /// <summary>The definition as JSON, as it was loaded.</summary>
        public required string Definition { get; init; }
        public required string Hash { get; init; }
        /// <summary>The objective, as a percentage: 99.5.</summary>
        public double Target { get; init; }
        public DateTimeOffset EffectiveFrom { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        /// <summary>Set when a newer version replaced this one before it took effect (the file changed again while it waited): it never applies.</summary>
        public DateTimeOffset? WithdrawnAt { get; set; }
    }

    /// <summary>
    /// One minute of one SLO, counted under the version effective then (roadmap Phase 9, pre-aggregation). A minute with a row was read; a minute
    /// without one is unknown, never empty. <see cref="Total"/> and <see cref="Good"/> are the SLI's own: requests that counted for availability,
    /// or successful requests for latency (good when at or under the threshold).
    /// </summary>
    public sealed class SloBucket
    {
        public Guid TenantId { get; init; }
        public Guid SloId { get; init; }
        public DateTimeOffset Minute { get; init; }
        public int Version { get; set; }
        public long Total { get; set; }
        public long Good { get; set; }
        /// <summary>Requests that counted (not excluded).</summary>
        public long Eligible { get; set; }
        /// <summary>Eligible requests that failed.</summary>
        public long Bad { get; set; }
        public long ExcludedHealth { get; set; }
        public long ExcludedCancelled { get; set; }
        public long ExcludedClientErrors { get; set; }
        /// <summary>Durations of the eligible requests that succeeded, by the fixed boundaries of <c>SloHistogram</c>; empty for a minute with none.</summary>
        public long[] Histogram { get; set; } = [];
        public DateTimeOffset CountedAt { get; set; }
    }

    /// <summary>
    /// One UTC day of one SLO and version: its minute buckets summed (Phase 9, milestone 4). With <see cref="SloHour"/>, a window reads its whole
    /// days from here, the hours of its partial days, and only the minutes of its partial first and last hours: hundreds of rows per SLO, not
    /// 43,000. The counter keeps both in step with the buckets: each pass sums again the days and hours it wrote, in the same transaction.
    /// </summary>
    public sealed class SloDay
    {
        public Guid TenantId { get; init; }
        public Guid SloId { get; init; }
        /// <summary>Midnight UTC.</summary>
        public DateTimeOffset Day { get; init; }
        public int Version { get; init; }
        public long Total { get; init; }
        public long Good { get; init; }
        /// <summary>Minutes counted that day under this version.</summary>
        public long Minutes { get; init; }
        /// <summary>Requests that counted, and those of them that failed; then what was left out, by reason (as the buckets have).</summary>
        public long Eligible { get; init; }
        public long Bad { get; init; }
        public long ExcludedHealth { get; init; }
        public long ExcludedCancelled { get; init; }
        public long ExcludedClientErrors { get; init; }
    }

    /// <summary>One UTC hour of one SLO and version: its minute buckets summed. See <see cref="SloDay"/>.</summary>
    public sealed class SloHour
    {
        public Guid TenantId { get; init; }
        public Guid SloId { get; init; }
        /// <summary>The start of the hour, UTC.</summary>
        public DateTimeOffset Hour { get; init; }
        public int Version { get; init; }
        public long Total { get; init; }
        public long Good { get; init; }
        /// <summary>Minutes counted that hour under this version.</summary>
        public long Minutes { get; init; }
        /// <summary>Requests that counted, and those of them that failed; then what was left out, by reason (as the buckets have).</summary>
        public long Eligible { get; init; }
        public long Bad { get; init; }
        public long ExcludedHealth { get; init; }
        public long ExcludedCancelled { get; init; }
        public long ExcludedClientErrors { get; init; }
    }
}
