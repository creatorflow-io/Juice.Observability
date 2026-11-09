using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// The actions an agent can be asked for: read-only diagnostics, each with typed, bounded parameters. There is no action that runs a command,
    /// a script or an executable: nothing here takes one, and the agent has no code that could.
    /// </summary>
    public static partial class AgentActions
    {
        /// <summary>The OS, uptime, CPUs, load and memory of the host, and the agent's version. No parameters.</summary>
        public const string HostSummary = "host.summary";
        /// <summary>Mounted filesystems with their size and free space, optionally those under one path.</summary>
        public const string DiskUsage = "disk.usage";
        /// <summary>Whether this host can open a TCP connection to a host and port, and how long it took.</summary>
        public const string TcpReachability = "tcp.reachability";
        /// <summary>
        /// Install a signed agent package and restart into it, going back to the version before if it doesn't come up healthy. The one action that
        /// changes the host: admins only on the platform, and only where the host's allowlist lists it. The package must carry a signature from a
        /// release key this host trusts, which the platform can't supply.
        /// </summary>
        public const string AgentUpdate = "agent.update";

        public static readonly IReadOnlyList<string> All = [HostSummary, DiskUsage, TcpReachability, AgentUpdate];

        /// <summary>Actions that change the host, rather than read it.</summary>
        public static readonly IReadOnlyList<string> Changing = [AgentUpdate];

        /// <summary>
        /// The built-in actions as descriptors, for forms and for agents to report. Their bounds are the ones <see cref="Parse"/> enforces; for
        /// <see cref="AgentUpdate"/> only the version is asked for, since the platform adds the package's hash.
        /// </summary>
        public static readonly IReadOnlyList<ActionDescriptor> Descriptors =
        [
            new(HostSummary, "The OS, uptime, CPUs, load and memory of the host, and the agent version.", false, []),
            new(DiskUsage, "Mounted filesystems with their size and free space.", false,
                [new("pathPrefix", ParameterTypes.String, false, "Only filesystems at or under this absolute path; all if empty.", MaxLength: DiskUsageParameters.MaxPathLength)]),
            new(TcpReachability, "Whether this host can open a TCP connection to a host and port, and how long it took.", false,
            [
                new("host", ParameterTypes.String, true, "A DNS name or an IP address.", MaxLength: 253),
                new("port", ParameterTypes.Integer, true, null, 1, 65535),
                new("timeoutMs", ParameterTypes.Integer, true, "How long to wait for the connection.", TcpReachabilityParameters.MinTimeoutMs, TcpReachabilityParameters.MaxTimeoutMs, Default: "2000"),
            ]),
            new(AgentUpdate, "Install a signed agent package and restart into it, going back to the version before if it doesn't come up healthy.", true,
                [new("version", ParameterTypes.String, true, "A version the platform offers.", MaxLength: 64)], Risk: ActionRisks.Medium),
        ];

        /// <summary>
        /// Reads an action's parameters strictly: an unknown action, an unknown member, a missing value or a value out of bounds is an error.
        /// Returns the typed parameters, or null with the reason.
        /// </summary>
        public static object? Parse(string action, JsonElement parameters, out string? error)
        {
            error = null;
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                error = "parameters must be an object";
                return null;
            }
            try
            {
                switch (action)
                {
                    case HostSummary:
                        if (parameters.EnumerateObject().Any())
                        {
                            error = $"{HostSummary} takes no parameters";
                            return null;
                        }
                        return new HostSummaryParameters();
                    case DiskUsage:
                        var disk = parameters.Deserialize<DiskUsageParameters>(CommandSignature.Json)!;
                        error = disk.Validate();
                        return error is null ? disk : null;
                    case TcpReachability:
                        var tcp = parameters.Deserialize<TcpReachabilityParameters>(CommandSignature.Json)!;
                        error = tcp.Validate();
                        return error is null ? tcp : null;
                    case AgentUpdate:
                        var update = parameters.Deserialize<AgentUpdateParameters>(CommandSignature.Json)!;
                        error = update.Validate();
                        return error is null ? update : null;
                    default:
                        error = $"unknown action '{action}'";
                        return null;
                }
            }
            catch (JsonException e)
            {
                error = $"unreadable parameters: {e.Message}";
                return null;
            }
        }
    }

    public sealed record HostSummaryParameters;

    /// <param name="Version">The agent version to install, as its package names it.</param>
    /// <param name="Sha256">The package's SHA-256, lowercase hex: the agent installs only those exact bytes.</param>
    public sealed partial record AgentUpdateParameters(string Version, string Sha256)
    {
        public string? Validate()
        {
            if (string.IsNullOrEmpty(Version) || Version.Length > 64 || !SemanticVersion().IsMatch(Version)) return "version is a version like 1.2.0 or 1.2.0-alpha.3";
            if (string.IsNullOrEmpty(Sha256) || !Hex64().IsMatch(Sha256)) return "sha256 is 64 lowercase hex characters";
            return null;
        }

        [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z.-]{1,40})?$")]
        private static partial Regex SemanticVersion();

        [GeneratedRegex("^[0-9a-f]{64}$")]
        private static partial Regex Hex64();
    }

    /// <param name="PathPrefix">Only filesystems mounted under this absolute path; null for all.</param>
    public sealed partial record DiskUsageParameters(string? PathPrefix)
    {
        public const int MaxPathLength = 256;

        public string? Validate()
        {
            if (PathPrefix is null)
            {
                return null;
            }
            if (PathPrefix.Length is 0 or > MaxPathLength) return $"pathPrefix is 1 to {MaxPathLength} characters";
            if (!AbsolutePath().IsMatch(PathPrefix)) return "pathPrefix is an absolute path of letters, digits and . _ - / \\ :";
            if (PathPrefix.Split('/', '\\').Contains("..")) return "pathPrefix can't contain '..'";
            return null;
        }

        [GeneratedRegex(@"^(/|[A-Za-z]:\\)[A-Za-z0-9._\-/\\:]*$")]
        private static partial Regex AbsolutePath();
    }

    /// <param name="Host">A DNS name or an IP address.</param>
    /// <param name="Port">1 to 65535.</param>
    /// <param name="TimeoutMs">How long to wait for the connection, 100 to 10,000 ms.</param>
    public sealed partial record TcpReachabilityParameters(string Host, int Port, int TimeoutMs)
    {
        public const int MinTimeoutMs = 100;
        public const int MaxTimeoutMs = 10_000;

        public string? Validate()
        {
            if (string.IsNullOrEmpty(Host) || Host.Length > 253) return "host is 1 to 253 characters";
            if (!IPAddress.TryParse(Host, out _) && !DnsName().IsMatch(Host)) return "host is a DNS name or an IP address";
            if (Port is < 1 or > 65535) return "port is 1 to 65535";
            if (TimeoutMs is < MinTimeoutMs or > MaxTimeoutMs) return $"timeoutMs is {MinTimeoutMs} to {MaxTimeoutMs}";
            return null;
        }

        [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
        private static partial Regex DnsName();
    }
}
