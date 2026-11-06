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
