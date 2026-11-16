namespace Juice.Observability.Data
{
    /// <summary>
    /// The time window of a load test. The platform generates no load: the test marks its start and end, and the results are
    /// read from the telemetry of that window. <see cref="EndedAt"/> stays null if the test died without marking its end.
    /// </summary>
    public sealed class LoadRun
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public required string Name { get; init; }
        /// <summary>The environment key the run is read from; null reads every environment.</summary>
        public string? Environment { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
        public required string StartedBy { get; init; }
    }
}

namespace Juice.Observability.Data
{
    /// <summary>
    /// One worker in one replica, as that replica last reported it (Phase 11 R6): written every few seconds by the replica's heartbeat writer, so
    /// the platform's status reads PostgreSQL, not ClickHouse. Not a tenant's: the platform's own. Every replica reports every worker the platform
    /// has, enabled or not. Times are the database's (the writer turns each replica's monotonic ages into them), so replicas' clocks don't matter.
    /// A replica not seen for an hour is pruned.
    /// </summary>
    public sealed class WorkerHeartbeat
    {
        public required string Worker { get; init; }
        /// <summary>The replica: its host name (the metrics' <c>service.instance.id</c>).</summary>
        public required string Replica { get; init; }
        public required string Version { get; set; }
        /// <summary>The worker is enabled in this replica (<c>*:Enabled</c>).</summary>
        public bool Enabled { get; set; }
        /// <summary>Runs under a lease (one replica at a time); false for a worker that runs in every replica.</summary>
        public bool Leased { get; set; }
        /// <summary>This replica holds the worker's lease.</summary>
        public bool Held { get; set; }
        /// <summary>When this replica took the lease it holds: the latest taker is the holder, should a replica that lost it not know yet.</summary>
        public DateTimeOffset? HeldSince { get; set; }
        public double IntervalSeconds { get; set; }
        /// <summary>When the pass in progress started; null between passes.</summary>
        public DateTimeOffset? RunningSince { get; set; }
        public DateTimeOffset? LastEndedAt { get; set; }
        public string? LastOutcome { get; set; }
        /// <summary>How long the worker's recent passes took here (a moving mean; Phase 11 R9), against its interval: its utilisation.</summary>
        public double? PassSeconds { get; set; }
        /// <summary>When the worker was registered in this replica (its loop started); null while it isn't enabled.</summary>
        public DateTimeOffset? RegisteredAt { get; set; }
        /// <summary>When this replica's process started: a replica that started after another was last seen replaced it (a redeploy).</summary>
        public DateTimeOffset? ReplicaStartedAt { get; set; }
        /// <summary>When this replica last wrote the row.</summary>
        public DateTimeOffset SeenAt { get; set; }
    }
}
