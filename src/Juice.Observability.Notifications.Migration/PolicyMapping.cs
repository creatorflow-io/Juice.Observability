using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications.Configuration;
using Juice.Observability.Data;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// A legacy escalation policy's steps as a notifications policy (phase-5-plan I66, waves §5.3), and back. Pure.
    /// <list type="bullet">
    /// <item>Legacy step times are minutes after the alert fired; notifications' are after the step before (the first: after the escalation
    /// started, which is when the firing was sent). <c>a[0], a[1] − a[0], …</c>. Legacy spaces a late step by the same difference
    /// (<c>EscalationDecision.NextAt</c>), so the two schedules are the same on time and after a hold.</item>
    /// <item>Two steps at the same time stay so (0 minutes apart). A step before the one ahead of it is refused (Q3): legacy sends it with no
    /// wait (<c>Max(0, …)</c>), and that can't be written relative, nor mapped back.</item>
    /// <item>A step's endpoints become route recipients <c>{ kind: route, key }</c>, the route of each migrated endpoint.</item>
    /// <item><c>StopOnAcknowledge</c> is kept. The selection (severity, rules, environments, enabled) and the name stay in Observability: the
    /// producer adapter picks the policy and names it in the <c>escalationPolicy</c> label (M45).</item>
    /// </list>
    /// </summary>
    public static class PolicyMapping
    {
        /// <param name="policy">The legacy policy's steps.</param>
        /// <param name="endpoints">The keys of the endpoints that migrate: a step may name only those.</param>
        public static Mapped<PolicyDefinition> Map(LegacyPolicy policy, IReadOnlySet<string> endpoints)
        {
            var problems = new List<MappingProblem>();
            if (!ConfigurationRules.Key().IsMatch(policy.Key))
            {
                problems.Add(new(ProblemCodes.InvalidKey, $"The key '{policy.Key}' isn't a notifications key: lowercase letters, digits and hyphens, starting with a letter."));
            }
            if (policy.Steps.Count is 0 or > EscalationRules.MaxSteps)
            {
                problems.Add(new(ProblemCodes.Steps, $"A policy has 1–{EscalationRules.MaxSteps} steps; this one has {policy.Steps.Count}."));
            }
            var steps = new JsonArray();
            var relative = ToRelative(policy.Steps.Select(s => s.AfterMinutes).ToList());
            for (var i = 0; i < policy.Steps.Count; i++)
            {
                var step = policy.Steps[i];
                var at = $"Step {i + 1}";
                if (step.AfterMinutes < 0)
                {
                    problems.Add(new(ProblemCodes.Steps, $"{at} is {step.AfterMinutes} minutes after firing; a step is 0 or more."));
                }
                else if (relative[i] < 0)
                {
                    problems.Add(new(ProblemCodes.NonIncreasingSteps,
                        $"{at} is at {step.AfterMinutes} minutes, before step {i} at {policy.Steps[i - 1].AfterMinutes}: put the steps in time order."));
                }
                else if (relative[i] > EscalationRules.MaxAfterMinutes)
                {
                    problems.Add(new(ProblemCodes.Steps, $"{at} waits {relative[i]} minutes after the step before; a step waits at most {EscalationRules.MaxAfterMinutes} (7 days)."));
                }
                if (step.EndpointKeys.Count is 0 or > EscalationRules.MaxRecipients || step.EndpointKeys.Distinct(StringComparer.Ordinal).Count() != step.EndpointKeys.Count)
                {
                    problems.Add(new(ProblemCodes.Steps, $"{at} names 1–{EscalationRules.MaxRecipients} endpoints, each once."));
                }
                foreach (var key in step.EndpointKeys.Where(k => !endpoints.Contains(k)))
                {
                    problems.Add(new(ProblemCodes.UnknownEndpoint, $"{at} names the endpoint '{key}', which doesn't migrate (it doesn't exist, or it has problems of its own)."));
                }
                steps.Add(new JsonObject
                {
                    ["afterMinutes"] = relative[i],
                    ["recipients"] = new JsonArray([.. step.EndpointKeys.Select(k => new JsonObject { ["kind"] = "route", ["key"] = k })]),
                });
            }
            return problems.Count > 0 ? Mapped<PolicyDefinition>.Refused(problems)
                : Mapped<PolicyDefinition>.Ok(new PolicyDefinition(policy.Key, EndpointMapping.Canonical(steps), policy.StopOnAcknowledge));
        }

        /// <summary>The legacy policy's steps a notifications policy stands for, or <see cref="ProblemCodes.NotAliasShaped"/>.</summary>
        public static Mapped<LegacyPolicy> Unmap(PolicyDefinition policy)
        {
            static Mapped<LegacyPolicy> NotOurs(string why) => Mapped<LegacyPolicy>.Refused(new MappingProblem(ProblemCodes.NotAliasShaped, why));

            using var document = JsonDocument.Parse(policy.Steps);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return NotOurs("The steps aren't a list.");
            }
            var waits = new List<int>();
            var keys = new List<ValueList<string>>();
            foreach (var step in document.RootElement.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("afterMinutes", out var after) || !after.TryGetInt32(out var wait)
                    || !step.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array || step.EnumerateObject().Count() != 2)
                {
                    return NotOurs("A step isn't { afterMinutes, recipients }.");
                }
                var endpointKeys = new List<string>();
                foreach (var r in recipients.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || r.EnumerateObject().Count() != 2 || !r.TryGetProperty("kind", out var kind) || kind.GetString() != "route"
                        || !r.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                    {
                        return NotOurs("A step names someone other than a route by its key.");
                    }
                    endpointKeys.Add(key.GetString()!);
                }
                waits.Add(wait);
                keys.Add(endpointKeys);
            }
            var absolute = ToAbsolute(waits);
            return Mapped<LegacyPolicy>.Ok(new LegacyPolicy(policy.Key, policy.StopOnAcknowledge,
                absolute.Select((a, i) => new LegacyStep(a, keys[i])).ToList()));
        }

        /// <summary>Minutes after firing as minutes after the step before: <c>a[0], a[1] − a[0], …</c> (negative where a step is out of order).</summary>
        public static IReadOnlyList<int> ToRelative(IReadOnlyList<int> absolute) => [.. absolute.Select((a, i) => i == 0 ? a : a - absolute[i - 1])];

        /// <summary>The inverse of <see cref="ToRelative"/>: the running sums.</summary>
        public static IReadOnlyList<int> ToAbsolute(IReadOnlyList<int> relative)
        {
            var sum = 0;
            return [.. relative.Select(r => sum += r)];
        }

        /// <summary>
        /// Whether one alert can match both policies (Q1): legacy runs every matching policy, a notification runs one escalation, so the report
        /// refuses overlapping policies until an admin narrows them. Only enabled policies with steps run. Severities always overlap (a critical
        /// alert passes every minimum); rules and environments overlap when either list is empty (any) or they share one. Two policies that both
        /// leave environments empty overlap on host alerts too, which have none.
        /// </summary>
        public static bool Overlap(EscalationPolicy a, EscalationPolicy b)
        {
            static bool Runs(EscalationPolicy p) => p.Enabled && p.Steps.Count > 0;
            static bool Meet(string[] x, string[] y) => x.Length == 0 || y.Length == 0 || x.Intersect(y, StringComparer.Ordinal).Any();
            return a.Key != b.Key && Runs(a) && Runs(b) && Meet(a.RuleKeys, b.RuleKeys) && Meet(a.Environments, b.Environments);
        }
    }
}
