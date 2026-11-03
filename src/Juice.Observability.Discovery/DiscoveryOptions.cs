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
    }
}
