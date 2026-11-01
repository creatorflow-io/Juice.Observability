namespace Juice.Observability.Discovery
{
    /// <summary>
    /// One distinct set of resource attributes seen in a time window, across traces, logs and metrics. Values are raw
    /// (as stored); <see cref="Normalizer"/> cleans them. An empty <see cref="ServiceName"/> means host-only telemetry
    /// (the Collector's host metrics).
    /// </summary>
    public sealed record ResourceObservation
    {
        public string ServiceNamespace { get; init; } = "";
        public string ServiceName { get; init; } = "";
        public string InstanceId { get; init; } = "";
        public string Environment { get; init; } = "";
        public string ServiceVersion { get; init; } = "";
        public string HostName { get; init; } = "";
        public string HostId { get; init; } = "";
        public string ContainerId { get; init; } = "";
        public string PodUid { get; init; } = "";
        public string PodNamespace { get; init; } = "";
        public string PodName { get; init; } = "";
        public string ProcessId { get; init; } = "";
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
        public long Records { get; init; }
    }

    /// <summary>A service-to-service interaction: a client or producer span whose child server or consumer span is in another service.</summary>
    public sealed record ServiceLinkObservation
    {
        public string SourceNamespace { get; init; } = "";
        public string SourceName { get; init; } = "";
        public string Environment { get; init; } = "";
        public string TargetNamespace { get; init; } = "";
        public string TargetName { get; init; } = "";
        /// <summary>The source span kind: <c>Client</c> or <c>Producer</c>.</summary>
        public string SourceKind { get; init; } = "";
        public long Spans { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
    }

    /// <summary>A span that names something the service uses: a messaging destination or a database.</summary>
    public sealed record ResourceUseObservation
    {
        public string ServiceNamespace { get; init; } = "";
        public string ServiceName { get; init; } = "";
        public string Environment { get; init; } = "";
        /// <summary><c>Producer</c>, <c>Consumer</c> or <c>Client</c>.</summary>
        public string SpanKind { get; init; } = "";
        public string MessagingSystem { get; init; } = "";
        public string MessagingDestination { get; init; } = "";
        public string DbSystem { get; init; } = "";
        public string DbNamespace { get; init; } = "";
        public string ServerAddress { get; init; } = "";
        public long Spans { get; init; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; init; }
    }
}
