using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Types;
using Juice.Observability.Data;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The platform's notification types (phase-5-plan I63), declared at start under the application <c>observability</c>. Their data is today's
    /// webhook payload less its id; the alert types' templates are the platform's own, ported (<see cref="DefaultTemplates"/>, M46c); the action request types keep the pipeline's defaults. Each takes email recipients
    /// (<c>externalRecipients</c>).
    /// </summary>
    public static class ObservabilityTypes
    {
        public const string AlertFiring = "observability.alert.firing";
        public const string AlertResolved = "observability.alert.resolved";

        /// <summary>
        /// A request waiting for an admin's decision, as an inbox item (M56): sent only to the people who may decide it, never to a route, and
        /// decided from the item with its two actions.
        /// </summary>
        public const string ActionApproval = "observability.action.approval";

        /// <summary>The approval's inbox actions: the ids the inbox handler dispatches on.</summary>
        public const string ApproveAction = "approve";
        public const string RejectAction = "reject";

        /// <summary>The label naming the escalation policy of a firing (<c>escalationPolicy</c>), which its workflow escalates by.</summary>
        public const string EscalationPolicyLabel = "escalationPolicy";

        /// <summary><c>observability.action.request.created</c> for the platform's <c>action.request.created</c>.</summary>
        public static string ForPlatformEvent(string type) => $"{NotificationsRegistration.ApplicationKey}.{type}";

        /// <summary>The action request events: each its own type. Only a new request opens the correlation; any later one may end it.</summary>
        public static readonly string[] ActionRequestEventTypes =
        [
            ActionRequestEvents.Created, ActionRequestEvents.Approved, ActionRequestEvents.Rejected, ActionRequestEvents.Cancelled,
            ActionRequestEvents.Expired, ActionRequestEvents.Verified, ActionRequestEvents.NotVerified,
        ];

        /// <summary>Every type the platform declares: the core's, then the action requests'. What the host's embedded options hold once the modules are composed, in this order.</summary>
        public static IEnumerable<TypeDeclaration> All() => Core().Concat(ActionRequests());

        /// <summary>The core's own types: the alerts'. Declared by <c>AddPlatformNotifications</c>.</summary>
        public static IEnumerable<TypeDeclaration> Core()
        {
            yield return new TypeDeclaration(AlertFiring, "Alert firing", "alerts", null, null, null, ExternalRecipients: true,
                Json(AlertSchema), CorrelationRules.Required, null,
                Json($$"""[{"step":"send"},{"step":"escalate","policyFrom":"labels.{{EscalationPolicyLabel}}"}]"""), DefaultTemplates.Declaration());
            yield return new TypeDeclaration(AlertResolved, "Alert resolved", "alerts", null, null, null, ExternalRecipients: true,
                Json(AlertSchema), CorrelationRules.Optional, null, Json(TypeRules.SendOnlyWorkflow), DefaultTemplates.Declaration());
        }

        /// <summary>
        /// The action request types: the approval and one per event. The agents module declares them (<c>AddNotificationTypes</c>, as the moved keys of
        /// <see cref="CoreMovedNotificationTypes"/>), exactly as they were declared when the core did.
        /// </summary>
        public static IEnumerable<TypeDeclaration> ActionRequests()
        {
            // Declared always, inert while the inbox is off: nothing sends it then.
            yield return new TypeDeclaration(ActionApproval, "Action approval", "action-requests", "approval", null, null, ExternalRecipients: false,
                Json(ActionRequestSchema), CorrelationRules.Required, null, Json(TypeRules.SendOnlyWorkflow), DefaultTemplates.ApprovalDeclaration(),
                Json($$"""[{"id":"{{ApproveAction}}","label":"Approve"},{"id":"{{RejectAction}}","label":"Reject"}]"""));
            foreach (var type in ActionRequestEventTypes)
            {
                yield return new TypeDeclaration(ForPlatformEvent(type), Name(type), "action-requests", null, null, null, ExternalRecipients: true,
                    Json(ActionRequestSchema), type == ActionRequestEvents.Created ? CorrelationRules.Required : CorrelationRules.Optional, null,
                    Json(TypeRules.SendOnlyWorkflow), null);
            }
        }

        private static string Name(string type) => "Action request " + type["action.request.".Length..].Replace('_', ' ');

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        /// <summary>The webhook payload's shape (<c>NotificationPlan.Payload</c>), less its id: what a template reads is required, the rest is open.</summary>
        private const string AlertSchema = """
            {
              "type": "object",
              "required": ["type", "occurredAt", "tenant", "alert", "links"],
              "properties": {
                "type": { "type": "string" },
                "occurredAt": { "type": "string" },
                "tenant": { "type": "string" },
                "alert": {
                  "type": "object",
                  "required": ["id", "rule", "severity", "state", "subject"],
                  "properties": {
                    "id": { "type": "string" }, "rule": { "type": "string" }, "ruleName": { "type": "string" }, "severity": { "type": "string" },
                    "state": { "type": "string" }, "subject": { "type": "string" }
                  }
                },
                "links": { "type": "object" }
              }
            }
            """;

        /// <summary>The action request notice's shape (<c>ActionRequestNotices.Payload</c>), less its id.</summary>
        private const string ActionRequestSchema = """
            {
              "type": "object",
              "required": ["type", "occurredAt", "tenant", "request", "links"],
              "properties": {
                "type": { "type": "string" },
                "occurredAt": { "type": "string" },
                "tenant": { "type": "string" },
                "request": {
                  "type": "object",
                  "required": ["id", "agent", "action", "risk", "policy", "state"],
                  "properties": {
                    "id": { "type": "string" }, "action": { "type": "string" }, "risk": { "type": "string" }, "policy": { "type": "string" },
                    "state": { "type": "string" }, "agent": { "type": "object" }
                  }
                },
                "links": { "type": "object" }
              }
            }
            """;
    }
}
