using Juice.Observability.Data;

namespace Juice.Observability.Webhooks
{
    /// <summary>What a silence or a route is matched against, read from an alert.</summary>
    public sealed record AlertContext(string RuleKey, string SubjectKey, AlertSeverity Severity)
    {
        private const string ServicePrefix = "service:";
        private const string HostPrefix = "host:";

        /// <summary>The environment of <c>service:shop/orders@production</c>; null for a host.</summary>
        public string? Environment => SubjectKey.StartsWith(ServicePrefix, StringComparison.Ordinal) && SubjectKey.LastIndexOf('@') is >= 0 and var at
            ? SubjectKey[(at + 1)..] : null;

        /// <summary><c>shop/orders</c>; null for a host.</summary>
        public string? Service => !SubjectKey.StartsWith(ServicePrefix, StringComparison.Ordinal) ? null
            : SubjectKey.LastIndexOf('@') is >= 0 and var at ? SubjectKey[ServicePrefix.Length..at] : SubjectKey[ServicePrefix.Length..];

        public string? Host => SubjectKey.StartsWith(HostPrefix, StringComparison.Ordinal) ? SubjectKey[HostPrefix.Length..] : null;

        /// <summary>A service is named <c>namespace/name</c> or just <c>name</c>, as in a rule.</summary>
        public bool IsService(string name)
            => Service is { } service && (service == name || service[(service.LastIndexOf('/') + 1)..] == name);
    }

    /// <summary>Whether a silence holds an alert's notifications at a moment. Pure, so every path is unit-tested.</summary>
    public static class SilenceMatcher
    {
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);

        public static bool IsActive(Silence silence, DateTimeOffset at)
        {
            if (silence.CancelledAt is not null || at < silence.StartsAt)
            {
                return false;
            }
            if (!silence.RepeatWeekly)
            {
                return at < silence.EndsAt;
            }
            var intoWeek = TimeSpan.FromTicks((at - silence.StartsAt).Ticks % Week.Ticks);
            return intoWeek < silence.EndsAt - silence.StartsAt;
        }

        public static bool Matches(Silence silence, AlertContext alert)
        {
            if (silence.MaximumSeverity is { } maximum && alert.Severity > maximum)
            {
                return false;
            }
            if (silence.RuleKeys.Length > 0 && !silence.RuleKeys.Contains(alert.RuleKey))
            {
                return false;
            }
            if (silence.Service is { Length: > 0 } service && !alert.IsService(service))
            {
                return false;
            }
            if (silence.Environment is { Length: > 0 } environment && alert.Environment != environment)
            {
                return false;
            }
            if (silence.Host is { Length: > 0 } host && alert.Host != host)
            {
                return false;
            }
            // A row with no scope can only come from AllAlerts: the API refuses any other.
            return silence.AllAlerts || HasScope(silence);
        }

        public static bool HasScope(Silence silence)
            => silence.RuleKeys.Length > 0 || silence.Service is { Length: > 0 } || silence.Environment is { Length: > 0 } || silence.Host is { Length: > 0 }
            || silence.MaximumSeverity is not null;

        public static Silence? Find(IEnumerable<Silence> silences, AlertContext alert, DateTimeOffset at)
            => silences.FirstOrDefault(s => IsActive(s, at) && Matches(s, alert));
    }

    public enum HeldOutcome { Keep, Send, Drop }

    /// <summary>What happens to a notification a silence held, once no silence holds it.</summary>
    public static class HeldNotification
    {
        /// <param name="event">The delivery's event.</param>
        /// <param name="state">The alert's state now.</param>
        /// <param name="receiverToldOfFiring">The endpoint was already sent, or was being sent, this alert's firing.</param>
        /// <param name="acknowledged">Someone acknowledged the alert while it was held.</param>
        /// <param name="stopsOnAcknowledge">For an escalation step: its policy stops when the alert is acknowledged.</param>
        public static HeldOutcome Decide(string @event, AlertState state, bool receiverToldOfFiring, bool acknowledged, bool stopsOnAcknowledge = true) => @event switch
        {
            // Saying "resolved" about a firing it never heard of is noise; leaving it believing a resolved alert still fires is worse.
            NotificationPlan.Resolved => receiverToldOfFiring ? HeldOutcome.Send : HeldOutcome.Drop,
            _ when state != AlertState.Firing => HeldOutcome.Drop,
            // Someone is already on it: announcing the firing now only adds noise.
            NotificationPlan.Firing => acknowledged ? HeldOutcome.Drop : HeldOutcome.Send,
            // An escalation step follows its policy, as it would have if no silence had held it.
            NotificationPlan.Escalated => acknowledged && stopsOnAcknowledge ? HeldOutcome.Drop : HeldOutcome.Send,
            // Still firing when the silence ends: this is how a real outage after a maintenance window gets noticed.
            _ => HeldOutcome.Send,
        };
    }

    public enum EscalationAction { Wait, Hold, Notify, Stop }

    /// <summary>What an active escalation does now. Pure, so every path is unit-tested.</summary>
    public static class EscalationDecision
    {
        public static (EscalationAction Action, string? Reason) Next(AlertState state, bool acknowledged, bool stopOnAcknowledge, bool policyEnabled,
            bool silenced, DateTimeOffset nextAt, DateTimeOffset now)
        {
            if (state == AlertState.Resolved) return (EscalationAction.Stop, "resolved");
            if (acknowledged && stopOnAcknowledge) return (EscalationAction.Stop, "acknowledged");
            if (!policyEnabled) return (EscalationAction.Stop, "policy_disabled");
            if (now < nextAt) return (EscalationAction.Wait, null);
            return silenced ? (EscalationAction.Hold, "silenced") : (EscalationAction.Notify, null);
        }

        /// <summary>
        /// When step <paramref name="next"/> is due, after the one before it went out at <paramref name="sentAt"/>: its time after firing, and never sooner
        /// after that step than the policy spaces them. Running late (a silence held the steps, or no replica ran them) then delays the rest instead of
        /// sending them all at once, so whoever comes later still gets the time the policy gives the people before them.
        /// </summary>
        public static DateTimeOffset NextAt(IReadOnlyList<EscalationStep> steps, int next, DateTimeOffset firedAt, DateTimeOffset sentAt)
        {
            var onSchedule = firedAt + TimeSpan.FromMinutes(steps[next].AfterMinutes);
            if (next == 0)
            {
                return onSchedule;
            }
            var spaced = sentAt + TimeSpan.FromMinutes(Math.Max(0, steps[next].AfterMinutes - steps[next - 1].AfterMinutes));
            return spaced > onSchedule ? spaced : onSchedule;
        }
    }
}
