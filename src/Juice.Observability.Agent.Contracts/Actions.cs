using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// The actions an agent can be asked for, each with typed, bounded parameters: read-only diagnostics, the agent's own update, and two
    /// remediations (roadmap Phase 8). There is no action that runs a command, a script or an executable named by the platform: nothing here
    /// takes one. The only program the agent ever starts is systemctl, at a fixed path, for <see cref="ServiceRestart"/> on Linux.
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

        /// <summary>
        /// Restart one service the host lists (<c>Services</c> in its allowlist): a systemd unit on Linux, a Windows service on Windows. The command
        /// names the service and nothing else. High risk.
        /// </summary>
        public const string ServiceRestart = "service.restart";
        /// <summary>
        /// Delete old files from one folder the host lists (<c>Folders</c> in its allowlist), for services that don't rotate their own logs: only
        /// regular files matching the folder's pattern, last written longer ago than its keep time, never a link or anything outside it. At most
        /// <see cref="FilesCleanupParameters.MaxFilesPerRun"/> per run. Medium risk; a dry run only reports, and is Low.
        /// </summary>
        public const string FilesCleanup = "files.cleanup";
        /// <summary>
        /// A zip of the host's facts, its disk usage, the agent's version and allowlist, and the last lines of the log files the host lists
        /// (<c>LogFiles</c> in its allowlist), with what looks like a credential redacted. At most <see cref="BundleLimits.MaxBytes"/>; uploaded to the
        /// platform, which keeps it for a while (roadmap Phase 8). Read-only, Low risk.
        /// </summary>
        public const string DiagnosticsBundle = "diagnostics.bundle";

        public static readonly IReadOnlyList<string> All = [HostSummary, DiskUsage, TcpReachability, AgentUpdate, ServiceRestart, FilesCleanup, DiagnosticsBundle];

        /// <summary>Actions that change the host, rather than read it.</summary>
        public static readonly IReadOnlyList<string> Changing = [AgentUpdate, ServiceRestart, FilesCleanup];

        /// <summary>
        /// An action's risk for these parameters: its descriptor's, except that a <see cref="FilesCleanup"/> dry run, which changes nothing, is Low.
        /// </summary>
        public static string RiskOf(ActionDescriptor action, JsonElement parameters)
            => action.Name == FilesCleanup && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True
                ? ActionRisks.Low
                : action.RiskOf();

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
            new(ServiceRestart, "Restart a service the host lists, and report whether it came back running.", true,
                [new("service", ParameterTypes.String, true, "A systemd unit or Windows service the host lists.", MaxLength: ServiceRestartParameters.MaxNameLength,
                    Pattern: ServiceRestartParameters.NamePattern)], Risk: ActionRisks.High),
            new(FilesCleanup, "Delete old files from a folder the host lists: only those matching its pattern and older than its keep time.", true,
            [
                new("folder", ParameterTypes.String, true, "A folder the host lists for cleanup.", MaxLength: DiskUsageParameters.MaxPathLength),
                new("keepHours", ParameterTypes.Integer, false, "Keep files written in this many hours; never fewer than the host's own.", 1, FilesCleanupParameters.MaxKeepHours),
                new("dryRun", ParameterTypes.Boolean, false, "Only report what would be deleted.", Default: "false"),
            ], Risk: ActionRisks.Medium),
            new(DiagnosticsBundle, "A zip of the host's facts, disk usage, the agent's settings and the last lines of the logs the host lists, kept by the platform to download.", false,
                [new("logLines", ParameterTypes.Integer, false, "Lines from the end of each log.", DiagnosticsBundleParameters.MinLines, DiagnosticsBundleParameters.MaxLines, Default: "200")]),
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
                    case ServiceRestart:
                        var restart = parameters.Deserialize<ServiceRestartParameters>(CommandSignature.Json)!;
                        error = restart.Validate();
                        return error is null ? restart : null;
                    case FilesCleanup:
                        var cleanup = parameters.Deserialize<FilesCleanupParameters>(CommandSignature.Json)!;
                        error = cleanup.Validate();
                        return error is null ? cleanup : null;
                    case DiagnosticsBundle:
                        var bundle = parameters.Deserialize<DiagnosticsBundleParameters>(CommandSignature.Json)!;
                        error = bundle.Validate();
                        return error is null ? bundle : null;
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

    /// <param name="Service">A systemd unit (<c>orders.service</c>, <c>worker@2.service</c>) or a Windows service name, which the host must list.</param>
    public sealed partial record ServiceRestartParameters(string Service)
    {
        public const int MaxNameLength = 128;
        /// <summary>Letters, digits and <c>@ . _ : -</c>, not starting with <c>-</c>: never an option, a path or anything a shell would read.</summary>
        public const string NamePattern = "^[A-Za-z0-9@._:][A-Za-z0-9@._:-]{0,127}$";

        public string? Validate()
        {
            if (string.IsNullOrEmpty(Service) || Service.Length > MaxNameLength) return $"service is 1 to {MaxNameLength} characters";
            if (!Name().IsMatch(Service) || Service.Contains("..", StringComparison.Ordinal)) return "service is letters, digits and @ . _ : -, not starting with -";
            return null;
        }

        [GeneratedRegex(NamePattern)]
        private static partial Regex Name();
    }

    /// <param name="Folder">One of the folders the host lists for cleanup, exactly as it lists it.</param>
    /// <param name="KeepHours">Keep files written within this many hours; null for the host's own keep time, and never fewer.</param>
    /// <param name="DryRun">Only report what would be deleted.</param>
    public sealed record FilesCleanupParameters(string Folder, int? KeepHours, bool DryRun)
    {
        public const int MaxKeepHours = 24 * 365 * 10;
        public const int MaxFilesPerRun = 10_000;

        public string? Validate()
        {
            if (new DiskUsageParameters(Folder).Validate() is { } path) return path.Replace("pathPrefix", "folder", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(Folder)) return "folder is required";
            if (KeepHours is < 1 or > MaxKeepHours) return $"keepHours is 1 to {MaxKeepHours}";
            return null;
        }
    }

    /// <param name="LogLines">Lines from the end of each listed log; null for 200.</param>
    public sealed record DiagnosticsBundleParameters(int? LogLines)
    {
        public const int MinLines = 10;
        public const int MaxLines = 5000;
        public int Lines => LogLines ?? 200;

        public string? Validate() => LogLines is < MinLines or > MaxLines ? $"logLines is {MinLines} to {MaxLines}" : null;
    }

    /// <summary>How big a diagnostic bundle may get. The platform refuses a larger upload.</summary>
    public static class BundleLimits
    {
        public const int MaxBytes = 5 * 1024 * 1024;
        public const int MaxLogFiles = 20;
        /// <summary>Read from the end of each log; the lines asked for come from within this.</summary>
        public const int MaxBytesPerLog = 512 * 1024;
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
