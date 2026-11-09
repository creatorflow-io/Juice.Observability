using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agents
{
    /// <summary>What an action needs before it's signed (roadmap Phase 8).</summary>
    public static class ApprovalPolicies
    {
        /// <summary>Runs on request, by anyone allowed to issue it.</summary>
        public const string None = "None";
        /// <summary>An admin's request runs at once; an operator's waits for an admin to approve it.</summary>
        public const string Admin = "Admin";
        /// <summary>Waits for an admin other than the requester, whoever asked.</summary>
        public const string SecondPerson = "SecondPerson";

        public static readonly IReadOnlyList<string> All = [None, Admin, SecondPerson];
    }

    /// <summary>
    /// Configuration section <c>Actions</c>: what each risk needs before a command is signed, with per-action overrides, and how long a request may
    /// wait for approval. Read at start; invalid settings stop the host. Defaults: Low runs on request, Medium needs an admin, High needs a second admin.
    /// </summary>
    public sealed class ActionApprovalOptions
    {
        public const string Section = "Actions";

        /// <summary>By risk (<see cref="ActionRisks"/>): one of <see cref="ApprovalPolicies.All"/>. A risk left out keeps its default.</summary>
        public Dictionary<string, string> Approval { get; set; } = [];

        /// <summary>By action name, over its risk's policy: <c>"service.restart": "SecondPerson"</c>.</summary>
        public Dictionary<string, string> Overrides { get; set; } = [];

        /// <summary>How long a request waits for approval before it expires.</summary>
        public TimeSpan ApprovalLifetime { get; set; } = TimeSpan.FromMinutes(30);

        private static readonly Dictionary<string, string> Defaults = new()
        {
            [ActionRisks.Low] = ApprovalPolicies.None, [ActionRisks.Medium] = ApprovalPolicies.Admin, [ActionRisks.High] = ApprovalPolicies.SecondPerson,
        };

        public IEnumerable<string> Validate()
        {
            foreach (var (risk, policy) in Approval)
            {
                if (!ActionRisks.All.Contains(risk)) yield return $"Actions:Approval:{risk}: risk is one of {string.Join(", ", ActionRisks.All)}";
                if (!ApprovalPolicies.All.Contains(policy)) yield return $"Actions:Approval:{risk}: policy is one of {string.Join(", ", ApprovalPolicies.All)}";
            }
            foreach (var (action, policy) in Overrides)
            {
                if (!ActionSchema.IsValidName(action)) yield return $"Actions:Overrides:{action}: not an action name";
                if (!ApprovalPolicies.All.Contains(policy)) yield return $"Actions:Overrides:{action}: policy is one of {string.Join(", ", ApprovalPolicies.All)}";
            }
            if (ApprovalLifetime < TimeSpan.FromSeconds(1) || ApprovalLifetime > TimeSpan.FromDays(1)) yield return "Actions:ApprovalLifetime is 1 second to 1 day";
        }

        /// <summary>What this action needs: its override, else its risk's policy, else that risk's default.</summary>
        public string PolicyFor(string action, string risk)
            => Overrides.TryGetValue(action, out var overridden) ? overridden
                : Approval.TryGetValue(risk, out var configured) ? configured
                : Defaults.GetValueOrDefault(risk, ApprovalPolicies.SecondPerson);
    }
}
