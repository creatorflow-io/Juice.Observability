using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// What this host permits, set by whoever runs the host and read only from its own disk. It is checked after the platform's signature, and
    /// wins over it: a validly signed command for an action not listed here, or with parameters outside these bounds, is refused. An action that
    /// isn't listed is not allowed, so an empty or missing allowlist allows nothing.
    /// </summary>
    public sealed class LocalAllowlist
    {
        public const string Section = "Allowlist";

        /// <summary>Allowed actions by name (<see cref="AgentActions"/>), each with its local bounds.</summary>
        public Dictionary<string, ActionPolicy> Actions { get; set; } = [];

        /// <returns>Null when allowed; otherwise <see cref="RejectionReasons.ActionNotAllowed"/> or <see cref="RejectionReasons.OutsideLocalBounds"/>.</returns>
        public string? Check(string action, object parameters)
        {
            if (!Actions.TryGetValue(action, out var policy))
            {
                return RejectionReasons.ActionNotAllowed;
            }
            return parameters switch
            {
                DiskUsageParameters disk => policy.AllowsPath(disk.PathPrefix) ? null : RejectionReasons.OutsideLocalBounds,
                TcpReachabilityParameters tcp => policy.AllowsTarget(tcp) ? null : RejectionReasons.OutsideLocalBounds,
                _ => null,
            };
        }
    }

    /// <summary>One allowed action and the bounds this host puts on it, on top of the action's own.</summary>
    public sealed class ActionPolicy
    {
        /// <summary>
        /// <see cref="AgentActions.DiskUsage"/>: the paths it may report on. Empty for any, including all filesystems at once; otherwise each
        /// command must name a path under one of these.
        /// </summary>
        public List<string> PathPrefixes { get; set; } = [];

        /// <summary>
        /// <see cref="AgentActions.TcpReachability"/>: the targets it may try, as <c>host:port</c>. The host may be <c>*.example.com</c> for any
        /// name under example.com, and the port <c>*</c> for any port. Empty allows no target.
        /// </summary>
        public List<string> Targets { get; set; } = [];

        /// <summary><see cref="AgentActions.TcpReachability"/>: a lower ceiling on its timeout than the action's own.</summary>
        public int? MaxTimeoutMs { get; set; }

        internal bool AllowsPath(string? path)
        {
            if (PathPrefixes.Count == 0)
            {
                return true;
            }
            if (path is null)
            {
                return false;
            }
            return PathPrefixes.Any(prefix => AgentPaths.IsUnder(AgentPaths.Normalize(path), AgentPaths.Normalize(prefix), AgentPaths.Comparison));
        }

        internal bool AllowsTarget(TcpReachabilityParameters target)
        {
            if (MaxTimeoutMs is { } max && target.TimeoutMs > max)
            {
                return false;
            }
            return Targets.Any(t => Matches(t, target.Host, target.Port));
        }

        private static bool Matches(string allowed, string host, int port)
        {
            var colon = allowed.LastIndexOf(':');
            if (colon <= 0)
            {
                return false;
            }
            var (allowedHost, allowedPort) = (allowed[..colon], allowed[(colon + 1)..]);
            if (allowedPort != "*" && (!int.TryParse(allowedPort, out var p) || p != port))
            {
                return false;
            }
            return allowedHost.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(allowedHost[1..], StringComparison.OrdinalIgnoreCase) && host.Length > allowedHost.Length - 1
                : string.Equals(allowedHost, host, StringComparison.OrdinalIgnoreCase);
        }
    }
}
