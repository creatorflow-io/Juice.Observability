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

        public static IEnumerable<TypeDeclaration> All()
        {
            yield return new TypeDeclaration(AlertFiring, "Alert firing", "alerts", null, null, null, ExternalRecipients: true,
                Json(AlertSchema), CorrelationRules.Required, null,
                Json($$"""[{"step":"send"},{"step":"escalate","policyFrom":"labels.{{EscalationPolicyLabel}}"}]"""), DefaultTemplates.Declaration());
            yield return new TypeDeclaration(AlertResolved, "Alert resolved", "alerts", null, null, null, ExternalRecipients: true,
                Json(AlertSchema), CorrelationRules.Optional, null, Json(TypeRules.SendOnlyWorkflow), DefaultTemplates.Declaration());
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
