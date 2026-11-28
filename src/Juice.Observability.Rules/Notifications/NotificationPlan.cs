using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Rules.Notifications
{
    /// <summary>
    /// What a transition sends: its event names, the body's shape (<see cref="Payload"/>, whose bytes the pipeline's default webhook template
    /// reproduces) and the silence that covers an alert.
    /// </summary>
    public static class NotificationPlan
    {
        public const string Firing = "alert.firing";
        public const string Resolved = "alert.resolved";
        /// <summary>Sent by an escalation policy, to the endpoints it names, whatever events they take otherwise.</summary>
        public const string Escalated = "alert.escalated";
        /// <summary>A test an admin sent to check a channel; never planned for an alert.</summary>
        public const string Test = "webhook.test";

        /// <summary>The active silence that covers the alert now, or null.</summary>
        public static Silence? CoveringSilence(PlatformDbContext db, Alert alert, AlertContext context, DateTimeOffset now)
        {
            var silences = db.Silences.AsNoTracking().Where(s => s.TenantId == alert.TenantId && s.CancelledAt == null && s.StartsAt <= now && (s.RepeatWeekly || s.EndsAt > now)).ToList();
            return SilenceMatcher.Find(silences, context, now);
        }

        public static string Payload(Guid id, string type, string tenant, Rule rule, Alert alert, AlertTransition transition, (string Policy, int Step)? escalation)
            => JsonSerializer.Serialize(new
            {
                id,
                type,
                occurredAt = transition.At,
                tenant,
                escalation = escalation is { } e ? new { policy = e.Policy, step = e.Step } : null,
                alert = new
                {
                    id = alert.Id,
                    rule = rule.Key,
                    ruleName = rule.Name,
                    severity = alert.Severity.ToString(),
                    state = transition.ToState.ToString(),
                    subject = alert.SubjectKey,
                    value = transition.Value,
                    breachStartedAt = alert.BreachStartedAt,
                    firedAt = alert.FiredAt,
                    resolvedAt = alert.ResolvedAt,
                    reason = transition.Reason,
                    // An SLO burn alert's objective: several SLOs can watch one service, and its subject alone doesn't say which is burning.
                    slo = alert.Evidence.TryGetValue("slo", out var slo) ? new { key = slo, name = alert.Evidence.GetValueOrDefault("sloName") } : null,
                },
                links = new { alert = $"/api/v1/tenants/{alert.TenantId}/alerts/{alert.Id}" },
            }, JsonSerializerOptions.Web);
    }
}
