namespace Juice.Observability.Discovery
{
    /// <summary>Configuration section <c>Discovery</c>.</summary>
    public sealed class DiscoveryOptions
    {
        public const string Section = "Discovery";

        /// <summary>Whether the background worker runs. Tests run <see cref="DiscoveryRunner"/> directly.</summary>
        public bool Enabled { get; set; } = true;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How far behind now a window ends, so telemetry still in the Collector's queue is not skipped.
        /// </summary>
        public TimeSpan Lag { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How far before its checkpoint each window starts reading. Telemetry is stored by its own time, so a row can
        /// land after its window was read: a span is written when it ends but timed by its start, and the Collector's
        /// queue holds data back while ClickHouse is slow or down. Re-reading the overlap finds the services, instances,
        /// resources and dependencies in such rows; their spans are counted only for a dependency first seen then.
        /// Telemetry later than <see cref="Lag"/> plus this is still missed.
        /// </summary>
        public TimeSpan Overlap { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Where the first run starts, when there is no checkpoint.</summary>
        public TimeSpan InitialLookback { get; set; } = TimeSpan.FromHours(1);

        /// <summary>The longest window one query reads; a run reads at most <see cref="MaxWindowsPerRun"/> of them.</summary>
        public TimeSpan MaxWindow { get; set; } = TimeSpan.FromMinutes(15);

        public int MaxWindowsPerRun { get; set; } = 8;

        /// <summary>A window that returns this many rows is split in two, down to <see cref="MinWindow"/>.</summary>
        public int RowLimit { get; set; } = 10_000;

        public TimeSpan MinWindow { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>An instance without telemetry for this long becomes inactive (D3).</summary>
        public TimeSpan InstanceInactiveAfter { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Inactive instances older than this are deleted (D3).</summary>
        public TimeSpan InstanceRetention { get; set; } = TimeSpan.FromDays(30);

        /// <summary>Renamed services: telemetry under <c>From</c> is recorded on <c>To</c> (D3).</summary>
        public List<ServiceAlias> Aliases { get; set; } = [];
    }

    public sealed class ServiceAlias
    {
        public string FromNamespace { get; set; } = "";
        public required string FromName { get; set; }
        public string ToNamespace { get; set; } = "";
        public required string ToName { get; set; }
    }

    /// <summary>Configuration section <c>Platform</c>.</summary>
    public sealed class PlatformOptions
    {
        public const string Section = "Platform";

        /// <summary>The tenant slug the Collector stamps (<c>JUICE_PLATFORM_TENANT_ID</c>).</summary>
        public string TenantKey { get; set; } = "";

        public string TenantDisplayName { get; set; } = "";
    }

    /// <summary>Configuration section <c>ClickHouse</c>.</summary>
    public sealed class ClickHouseOptions
    {
        public const string Section = "ClickHouse";

        /// <summary>For example <c>Host=localhost;Port=8123;Username=platform_reader;Password=...;Database=otel</c>.</summary>
        public string ConnectionString { get; set; } = "";

        /// <summary>
        /// The platform sampler's ClickHouse user (Phase 11 R5), <c>platform_monitor</c>: a few system tables, no otel data. Empty: ClickHouse
        /// isn't sampled (its CPU, memory, queries and disks don't show), and everything else works.
        /// </summary>
        public string MonitorConnectionString { get; set; } = "";

        /// <summary>The API's own queries (searches, series, operations, the Console's pages): <c>ClickHouse:Api</c> (Phase 11 R11).</summary>
        public ClickHouseApiOptions Api { get; set; } = new();
    }

    /// <summary>
    /// Bounds on the queries the API makes for its callers, so a burst of searches waits or is refused instead of taking ClickHouse from ingest and
    /// the workers: at 16 searches a second, 428 ran at once and the platform lost a third of its metric points (docs/scale-tests.md, the searches
    /// ceiling). Per replica. The workers' queries aren't bounded here.
    /// </summary>
    public sealed class ClickHouseApiOptions
    {
        /// <summary>API queries running at once on this replica; the rest wait up to <see cref="QueueTimeout"/>.</summary>
        public int MaxConcurrent { get; set; } = 8;
        /// <summary>How long an API query waits for a free slot before the API answers "busy, try again" (503).</summary>
        public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>ClickHouse's <c>max_execution_time</c> for an API query, in seconds; within the profile's 60.</summary>
        public int MaxExecutionSeconds { get; set; } = 20;
        /// <summary>ClickHouse's <c>max_threads</c> for an API query: <see cref="MaxConcurrent"/> × this is what searches can take of its cores.</summary>
        public int MaxThreads { get; set; } = 2;

        public IEnumerable<string> Validate()
        {
            if (MaxConcurrent is < 1 or > 256) yield return "ClickHouse:Api:MaxConcurrent must be 1 to 256";
            if (QueueTimeout < TimeSpan.Zero || QueueTimeout > TimeSpan.FromMinutes(1)) yield return "ClickHouse:Api:QueueTimeout must be 0 to 1 minute";
            if (MaxExecutionSeconds is < 1 or > 60) yield return "ClickHouse:Api:MaxExecutionSeconds must be 1 to 60 (the reader profile's limit)";
            if (MaxThreads is < 1 or > 64) yield return "ClickHouse:Api:MaxThreads must be 1 to 64";
        }
    }

    /// <summary>The API's queries are at their bound, or ClickHouse refused or timed one out under load: answered "busy, try again" (503).</summary>
    public sealed class TelemetryBusyException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>ClickHouse can't be reached for an API query: answered "unavailable" (503), not a 500.</summary>
    public sealed class TelemetryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
}
