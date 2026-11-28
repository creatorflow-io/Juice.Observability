using Juice.Observability.Data;

namespace Juice.Observability.Rules.Notifications
{
    /// <summary>
    /// Which escalation policy an alert escalates by. The platform keeps <i>which</i> (a policy's severity, rules and environments), and the
    /// notification pipeline keeps <i>what it does</i> (its steps), under the same key.
    /// </summary>
    public static class EscalationSelection
    {
        /// <summary>
        /// The enabled policies that match the alert (severity, rule keys, environments), by key (ordinal). The pipeline runs one escalation per
        /// notification, so the first is the one used; more than one is for the caller to warn of.
        /// </summary>
        public static IReadOnlyList<EscalationPolicy> Match(IEnumerable<EscalationPolicy> enabled, AlertContext context, AlertSeverity severity)
            => [.. enabled
                .Where(policy => policy.Enabled && severity >= policy.MinimumSeverity
                    && (policy.RuleKeys.Length == 0 || policy.RuleKeys.Contains(context.RuleKey))
                    && (policy.Environments.Length == 0 || context.Environment is { } environment && policy.Environments.Contains(environment)))
                .OrderBy(policy => policy.Key, StringComparer.Ordinal)];
    }
}
