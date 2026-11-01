using Juice.Observability.Data;

namespace Juice.Observability.Discovery
{
    /// <summary>
    /// Turns raw telemetry values into natural keys (D1, D3): trims, bounds lengths, lowercases keys that are
    /// case-insensitive by nature (environments, host and container ids), derives missing instance keys and applies
    /// service aliases. Pure, so the rules are unit-tested.
    /// </summary>
    public sealed class Normalizer(IReadOnlyList<ServiceAlias> aliases)
    {
        public const int MaxValueLength = 256;
        public const int MaxAttributes = 64;
        public const string UnspecifiedEnvironment = "unspecified";
        public const string DefaultApplication = "default";

        /// <summary>Values cut to <see cref="MaxValueLength"/> since this normalizer was created.</summary>
        public int Truncated { get; private set; }

        public string Value(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length <= MaxValueLength)
            {
                return value;
            }
            Truncated++;
            return value[..MaxValueLength];
        }

        public string EnvironmentKey(string? raw)
        {
            var value = Value(raw).ToLowerInvariant();
            return value.Length == 0 ? UnspecifiedEnvironment : value;
        }

        public static EnvironmentType EnvironmentTypeOf(string key) => key switch
        {
            UnspecifiedEnvironment => EnvironmentType.Unspecified,
            "production" or "prod" or "prd" or "live" => EnvironmentType.Production,
            "staging" or "stage" or "stg" or "uat" or "preprod" or "pre-production" => EnvironmentType.Staging,
            "development" or "dev" or "local" => EnvironmentType.Development,
            "test" or "testing" or "qa" or "ci" => EnvironmentType.Test,
            _ => EnvironmentType.Other,
        };

        /// <summary>The service a telemetry name maps to, after aliases. Null when the name is empty (host-only telemetry).</summary>
        public (string Namespace, string Name)? Service(string? rawNamespace, string? rawName)
        {
            var ns = Value(rawNamespace);
            var name = Value(rawName);
            if (name.Length == 0)
            {
                return null;
            }
            foreach (var alias in aliases)
            {
                if (string.Equals(alias.FromName, name, StringComparison.Ordinal)
                    && string.Equals(alias.FromNamespace ?? "", ns, StringComparison.Ordinal))
                {
                    return (Value(alias.ToNamespace), Value(alias.ToName));
                }
            }
            return (ns, name);
        }

        public static string ApplicationKey(string serviceNamespace)
            => serviceNamespace.Length == 0 ? DefaultApplication : serviceNamespace;

        /// <summary>
        /// <c>service.instance.id</c> when reported; otherwise host name and process id, marked derived (D1). Null when
        /// neither is available: the service is recorded without an instance.
        /// </summary>
        public (string Key, InstanceKeySource Source)? Instance(ResourceObservation o)
        {
            var id = Value(o.InstanceId);
            if (id.Length > 0)
            {
                return (id, InstanceKeySource.Observed);
            }
            var host = Value(o.HostName).ToLowerInvariant();
            var pid = Value(o.ProcessId);
            if (host.Length > 0 && pid.Length > 0)
            {
                return ($"{host}:{pid}", InstanceKeySource.Derived);
            }
            return host.Length > 0 ? (host, InstanceKeySource.Derived) : null;
        }

        /// <summary>The resources an observation names, keyed per type (D3). The host comes first.</summary>
        public IEnumerable<ResourceKey> Resources(ResourceObservation o)
        {
            var hostId = Value(o.HostId).ToLowerInvariant();
            var hostName = Value(o.HostName).ToLowerInvariant();
            if (hostId.Length > 0 || hostName.Length > 0)
            {
                yield return new ResourceKey(ResourceType.Host, hostId.Length > 0 ? hostId : hostName,
                    hostName.Length > 0 ? hostName : hostId,
                    Bounded(new() { ["host.name"] = hostName, ["host.id"] = hostId }));
            }
            var container = Value(o.ContainerId).ToLowerInvariant();
            if (container.Length > 0)
            {
                yield return new ResourceKey(ResourceType.Container, container, container.Length > 12 ? container[..12] : container,
                    Bounded(new() { ["container.id"] = container }));
            }
            var pod = Value(o.PodUid).ToLowerInvariant();
            if (pod.Length > 0)
            {
                var podNamespace = Value(o.PodNamespace);
                var podName = Value(o.PodName);
                yield return new ResourceKey(ResourceType.KubernetesPod, pod,
                    podName.Length > 0 ? $"{podNamespace}/{podName}" : pod,
                    Bounded(new() { ["k8s.pod.uid"] = pod, ["k8s.namespace.name"] = podNamespace, ["k8s.pod.name"] = podName }));
            }
        }

        /// <summary>The destination or database a span names, or null when it names neither.</summary>
        public ResourceKey? UsedResource(ResourceUseObservation o)
        {
            var destination = Value(o.MessagingDestination);
            if (destination.Length > 0)
            {
                var system = Value(o.MessagingSystem).ToLowerInvariant();
                return new ResourceKey(ResourceType.MessagingDestination, $"{system}:{destination}", destination,
                    Bounded(new() { ["messaging.system"] = system, ["messaging.destination.name"] = destination }));
            }
            var dbSystem = Value(o.DbSystem).ToLowerInvariant();
            if (dbSystem.Length > 0)
            {
                var server = Value(o.ServerAddress).ToLowerInvariant();
                var dbNamespace = Value(o.DbNamespace);
                // server/database, so a database named like a service ("payments") still reads as a database.
                var displayName = (server.Length > 0, dbNamespace.Length > 0) switch
                {
                    (true, true) => $"{server}/{dbNamespace}",
                    (false, true) => dbNamespace,
                    _ => $"{dbSystem} {server}".Trim(),
                };
                return new ResourceKey(ResourceType.Database, $"{dbSystem}:{server}:{dbNamespace}", displayName,
                    Bounded(new() { ["db.system.name"] = dbSystem, ["server.address"] = server, ["db.namespace"] = dbNamespace }));
            }
            return null;
        }

        /// <summary>Drops empty values and keeps at most <see cref="MaxAttributes"/> attributes.</summary>
        public static Dictionary<string, string> Bounded(Dictionary<string, string> attributes)
            => attributes.Where(kv => kv.Value.Length > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(MaxAttributes).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public sealed record ResourceKey(ResourceType Type, string Key, string DisplayName, Dictionary<string, string> Attributes);
}
