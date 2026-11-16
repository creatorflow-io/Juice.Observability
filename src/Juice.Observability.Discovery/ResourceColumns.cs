namespace Juice.Observability.Discovery
{
    /// <summary>
    /// The resource attributes the platform reads, as columns of their own on the Collector's tables (Phase 11 R12): each is
    /// <c>LowCardinality(String) MATERIALIZED ResourceAttributes['key']</c>, added by <c>deploy/local/clickhouse/resource-columns.sh</c>.
    /// Looking a key up in the map row by row was most of what the workers' queries cost; the column holds the same value, so a
    /// query reads it instead and gets the same answer (a part written before the column existed computes it from the map). A key
    /// not listed here is still read from <c>ResourceAttributes</c>. A test checks this list against the script.
    /// </summary>
    public static class ResourceColumns
    {
        public const string Tenant = "JuiceTenant";
        public const string ServiceNamespace = "JuiceServiceNamespace";
        public const string Environment = "JuiceEnvironment";
        public const string ServiceInstance = "JuiceServiceInstance";
        public const string ServiceVersion = "JuiceServiceVersion";
        public const string HostName = "JuiceHostName";
        public const string HostId = "JuiceHostId";
        public const string ProcessPid = "JuiceProcessPid";
        public const string ContainerId = "JuiceContainerId";
        public const string PodUid = "JuicePodUid";
        public const string PodNamespace = "JuicePodNamespace";
        public const string PodName = "JuicePodName";

        /// <summary>Each column and the resource attribute it holds.</summary>
        public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
        {
            [Tenant] = "juice.platform_tenant_id",
            [ServiceNamespace] = "service.namespace",
            [Environment] = "deployment.environment.name",
            [ServiceInstance] = "service.instance.id",
            [ServiceVersion] = "service.version",
            [HostName] = "host.name",
            [HostId] = "host.id",
            [ProcessPid] = "process.pid",
            [ContainerId] = "container.id",
            [PodUid] = "k8s.pod.uid",
            [PodNamespace] = "k8s.namespace.name",
            [PodName] = "k8s.pod.name",
        };
    }
}
