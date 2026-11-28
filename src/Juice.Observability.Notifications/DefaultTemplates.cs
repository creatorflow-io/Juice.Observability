using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Notifications.Providers.Chat;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The alert types' default templates (phase-5-plan p5-d8), ported from <c>ChannelFormatter</c> and <c>NotificationPlan.Payload</c>, for the
    /// <see cref="Juice.Notifications.Types.TypeDeclaration"/>s of <see cref="ObservabilityTypes.AlertFiring"/> and
    /// <see cref="ObservabilityTypes.AlertResolved"/> (M46c: the types declare them).
    /// <para>
    /// What they read: <c>data</c> is the legacy payload without its <c>id</c> (the producer adapter's request, M45); <c>delivery.id</c>,
    /// <c>escalation.step</c> and <c>escalation.policy</c> come from the render model (M46-0); <c>links.self</c> is the alert's page in the
    /// Console when the adapter knows it (<c>Notifications:ConsoleUrl</c>).
    /// </para>
    /// </summary>
    public static class DefaultTemplates
    {
        /// <summary>
        /// The signed webhook's body: the legacy payload (<c>NotificationPlan.Payload</c>), field for field and in order, so a receiver can't tell
        /// (proof point 7). <c>id</c> is the delivery's (the <c>webhook-id</c> it is signed with, unique per delivery); <c>type</c> is
        /// <c>alert.escalated</c> for an escalation step, else the transition's event; <c>escalation</c> is the step's policy and number, else
        /// null. Every value is written by <c>json.encode</c>, which escapes as System.Text.Json does for the legacy payload; times keep their
        /// <c>+</c> as is, as System.Text.Json writes a <see cref="DateTimeOffset"/>.
        /// </summary>
        public const string Webhook = """
            {{~ func time(t) ~}}
            {{~ ret t | json.encode | string.replace ("\\" + "u002B") "+" ~}}
            {{~ end ~}}
            {{~ a = data.alert ~}}
            {"id":{{ delivery.id | json.encode }},"type":{{ if escalation }}"alert.escalated"{{ else }}{{ data.type | json.encode }}{{ end }},"occurredAt":{{ time(data.occurredAt) }},"tenant":{{ data.tenant | json.encode }},"escalation":{{ if escalation }}{"policy":{{ escalation.policy | json.encode }},"step":{{ escalation.step | json.encode }}}{{ else }}null{{ end }},"alert":{"id":{{ a.id | json.encode }},"rule":{{ a.rule | json.encode }},"ruleName":{{ a.ruleName | json.encode }},"severity":{{ a.severity | json.encode }},"state":{{ a.state | json.encode }},"subject":{{ a.subject | json.encode }},"value":{{ a.value | json.encode }},"breachStartedAt":{{ time(a.breachStartedAt) }},"firedAt":{{ time(a.firedAt) }},"resolvedAt":{{ time(a.resolvedAt) }},"reason":{{ a.reason | json.encode }},"slo":{{ a.slo | json.encode }}},"links":{{ data.links | json.encode }}}
            """;

        // char.IsControl: C0 (U+0000-U+001F), DEL and C1 (U+007F-U+009F); the sandbox has no regex, so each is replaced in turn.
        private static readonly string Controls = string.Join(", ", Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0x7F, 0x21)).Select(c => $"\"{(char)92}u{c:X4}\""));

        // ChannelFormatter.Plain: every control character out as a space, trimmed, and more than 300 characters cut to 300 and an ellipsis.
        // Teams adds ChannelFormatter.Teams: no square brackets, so no markdown link.
        private static readonly string Functions = $$$"""
            {{~ func clean(t) ~}}
            {{~ $s = t ?? "" ~}}
            {{~ for $c in [{{{Controls}}}] ~}}
            {{~ $s = $s | string.replace $c " " ~}}
            {{~ end ~}}
            {{~ ret $s | string.strip ~}}
            {{~ end ~}}
            {{~ func plain(t) ~}}
            {{~ ret clean(t).size > 300 ? ((clean(t) | string.truncate 300 "") + "…") : clean(t) ~}}
            {{~ end ~}}
            {{~ func teams(t) ~}}
            {{~ ret plain(t) | string.replace "[" "(" | string.replace "]" ")" ~}}
            {{~ end ~}}
            {{~ func title_raw() ~}}
            {{~ ret (escalation ? ("Escalated (step " + escalation.step + ")") : (data.type == "alert.resolved" ? "Resolved" : "Firing")) + ": " + data.alert.ruleName ~}}
            {{~ end ~}}
            {{~ func title() ~}}
            {{~ ret plain(title_raw()) ~}}
            {{~ end ~}}
            {{~ func inbox_title() ~}}
            {{~ $t = clean("[" + data.alert.severity + "] " + (data.alert.ruleName ?? data.alert.rule) + ": " + data.alert.subject) ~}}
            {{~ ret $t.size > 250 ? (($t | string.truncate 249 "") + "…") : $t ~}}
            {{~ end ~}}
            {{~ func value() ~}}
            {{~ ret data.alert.value == null ? "no data" : (data.alert.value | math.format "G6") ~}}
            {{~ end ~}}
            {{~ func rule_and_slo() ~}}
            {{~ ret data.alert.rule + (data.alert.slo ? ", SLO " + data.alert.slo.key : "") ~}}
            {{~ end ~}}

            """;

        /// <summary>
        /// Slack: the formatter's header and its line, <c>• *Severity* subject: value (rule)</c>. Plain text: the provider escapes it for mrkdwn,
        /// and puts the alert's link (<c>links.self</c>) beside it.
        /// </summary>
        public static readonly string Slack = Functions + """
            Title: {{ title() }}
            • *{{ plain(data.alert.severity) }}* {{ plain(data.alert.subject) }}: {{ value() }} ({{ plain(rule_and_slo()) }})
            """;

        /// <summary>Teams: the formatter's card title and its line, <c>Severity subject: value (rule)</c>; the link is the card's action.</summary>
        public static readonly string Teams = Functions + """
            Title: {{ teams(title_raw()) }}
            {{ teams(data.alert.severity) }} {{ teams(data.alert.subject) }}: {{ value() }} ({{ teams(rule_and_slo()) }})
            """;

        /// <summary>Email: the formatter's subject (<c>[Juice] …</c>) and body, the alert's link last when there is one.</summary>
        public static readonly string Email = Functions + """
            Subject: [Juice] {{ title() }}
            {{ plain(data.alert.severity) }} {{ plain(data.alert.subject) }}: {{ value() }}
              Rule: {{ plain(data.alert.ruleName) }} ({{ plain(data.alert.rule) }}){{ if data.alert.slo }}
              SLO: {{ plain(data.alert.slo.key) }}{{ end }}{{ if plain(data.alert.reason) != "" }}
              Reason: {{ plain(data.alert.reason) }}{{ end }}{{ if links.self }}
              {{ links.self }}{{ end }}
            """;

        /// <summary>
        /// The inbox item (M54): its title is <c>[severity] rule name: subject</c> on one line, at most the 250 characters an item's title holds
        /// (cut with an ellipsis, not mid-word silently), and its text is the reason and the value. Plain text: the Console shows it as text, never as
        /// markup. The link is the request's <c>links.self</c>, the Console's address as written.
        /// </summary>
        public static readonly string Inbox = Functions + """
            Title: {{ inbox_title() }}
            {{ if plain(data.alert.reason) != "" }}Reason: {{ plain(data.alert.reason) }}
            {{ end }}Value: {{ value() }}
            """;

        /// <summary>
        /// The approval item (M56), inbox only: <c>Approve {action} on {hostname}?</c>, at most the 250 characters a title holds, and
        /// <c>{risk} risk · asked by {requestedBy}: {reason}</c>, with what a second-person request needs. Every value is plain text, control characters
        /// out as spaces; the Console shows it as text, never as markup.
        /// </summary>
        public static readonly string ApprovalInbox = Functions + """
            {{~ func approval_title() ~}}
            {{~ $t = clean("Approve " + data.request.action + " on " + data.request.agent.hostname + "?") ~}}
            {{~ ret $t.size > 250 ? (($t | string.truncate 249 "") + "…") : $t ~}}
            {{~ end ~}}
            Title: {{ approval_title() }}
            {{ clean(data.request.risk) }} risk · asked by {{ clean(data.request.requestedBy) }}: {{ clean(data.request.reason) }}{{ if data.request.policy == "SecondPerson" }}
            needs an admin other than {{ clean(data.request.requestedBy) }}{{ end }}
            """;

        /// <summary>The approval type's templates: the inbox's only, since an approval is never sent to a route.</summary>
        public static JsonElement ApprovalDeclaration() => JsonSerializer.SerializeToElement(new JsonObject { [Channels.Inbox] = ApprovalInbox });

        /// <summary>The templates by channel, as a <see cref="Juice.Notifications.Types.TypeDeclaration"/> takes them (the <c>en</c> locale).</summary>
        public static IReadOnlyDictionary<string, string> ByChannel { get; } = new Dictionary<string, string>
        {
            [Channels.Webhook] = Webhook,
            [ChatChannels.Slack] = Slack,
            [ChatChannels.Teams] = Teams,
            [Channels.Email] = Email,
            [Channels.Inbox] = Inbox,
        };

        /// <summary><see cref="ByChannel"/> as a declaration's <c>templates</c>.</summary>
        public static JsonElement Declaration()
        {
            var templates = new JsonObject();
            foreach (var (channel, body) in ByChannel)
            {
                templates[channel] = body;
            }
            return JsonSerializer.SerializeToElement(templates);
        }
    }
}
