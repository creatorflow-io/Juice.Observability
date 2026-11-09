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
                CustomInvocation custom => policy.AllowsValues(custom.Raw) ? null : RejectionReasons.OutsideLocalBounds,
                _ => null,
            };
        }

        /// <summary>
        /// Every action the agent can run as it reports them: permitted or not, each with its parameters narrowed to this host's bounds where a form
        /// can show them (allowed values, a lower maximum), and the rest of the bounds in words. <paramref name="notes"/> adds lines of the agent's own,
        /// such as why an update can't work here.
        /// </summary>
        public ActionsReport Report(AgentActionCatalog catalog, IReadOnlyDictionary<string, IReadOnlyList<string>>? notes = null)
            => new([.. catalog.Descriptors.Take(ActionsReport.MaxActions).Select(action =>
            {
                var permitted = Actions.TryGetValue(action.Name, out var policy);
                var (narrowed, limits) = permitted ? policy!.Describe(action) : (action, new List<string>());
                if (notes?.GetValueOrDefault(action.Name) is { } extra) limits.AddRange(extra);
                return new ReportedAction(narrowed, permitted, [.. limits.Take(ActionsReport.MaxLimits).Select(l => l.Length > ActionsReport.MaxText ? l[..ActionsReport.MaxText] : l)]);
            })]);
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

        /// <summary>
        /// A custom action: the values each named parameter may take here, as its JSON reads (<c>"orders"</c>, <c>5</c>, <c>true</c>). A parameter
        /// named here must be given and be one of them; one not named is bounded only by the action itself.
        /// </summary>
        public Dictionary<string, List<string>> Values { get; set; } = [];

        /// <summary>
        /// Run it only from a request the platform approved: a command without an approval reference is refused (<see cref="RejectionReasons.ApprovalRequired"/>),
        /// so a platform whose approval policy was loosened, or skipped, still can't make this host act.
        /// </summary>
        public bool RequireApproval { get; set; }

        internal bool AllowsValues(System.Text.Json.JsonElement parameters)
        {
            foreach (var (name, allowed) in Values)
            {
                if (!parameters.TryGetProperty(name, out var value)) return false;
                var text = value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => value.GetString(),
                    System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => value.GetRawText(),
                    _ => null,
                };
                if (text is null || !allowed.Contains(text, StringComparer.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>The action with its parameters narrowed to these bounds where a form can use them, and every bound in words.</summary>
        internal (ActionDescriptor Action, List<string> Limits) Describe(ActionDescriptor action)
        {
            var limits = new List<string>();
            var parameters = action.Parameters.ToList();
            ActionParameter? Find(string name) => parameters.FirstOrDefault(p => p.Name == name);
            void Replace(ActionParameter p, ActionParameter with) => parameters[parameters.IndexOf(p)] = with;
            switch (action.Name)
            {
                case AgentActions.DiskUsage:
                    if (PathPrefixes.Count > 0)
                    {
                        limits.Add($"pathPrefix must be under {string.Join(", ", PathPrefixes)}");
                        if (Find("pathPrefix") is { } path) Replace(path, path with { Required = true });
                    }
                    break;
                case AgentActions.TcpReachability:
                    limits.Add(Targets.Count == 0 ? "no target is allowed" : $"targets: {string.Join(", ", Targets)}");
                    if (MaxTimeoutMs is { } max && Find("timeoutMs") is { } timeout && max < (timeout.Maximum ?? long.MaxValue))
                    {
                        var ceiling = Math.Max(timeout.Minimum ?? 0, max);
                        Replace(timeout, timeout with
                        {
                            Maximum = ceiling,
                            Default = long.TryParse(timeout.Default, out var d) && d > ceiling ? ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture) : timeout.Default,
                        });
                        limits.Add($"timeoutMs at most {ceiling}");
                    }
                    break;
            }
            if (action.Custom)
            {
                foreach (var (name, allowed) in Values)
                {
                    if (Find(name) is not { } p)
                    {
                        limits.Add($"{name}: limited to values for a parameter the action doesn't have, so nothing is allowed");
                        continue;
                    }
                    var values = p.Allowed is { Count: > 0 } own ? [.. own.Intersect(allowed, StringComparer.Ordinal)] : allowed.Take(ActionsReport.MaxAllowed).ToList();
                    Replace(p, p with { Required = true, Allowed = values });
                    limits.Add(values.Count == 0 ? $"{name}: no value is allowed" : $"{name}: one of {string.Join(", ", values)}");
                }
            }
            if (RequireApproval) limits.Add("runs only from an approved request");
            return (action with { Parameters = parameters }, limits);
        }

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
