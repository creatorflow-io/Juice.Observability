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
    /// Console when the adapter knows it (legacy <c>Webhooks:ConsoleUrl</c>).
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

        // ChannelFormatter.Plain: control characters out (here line breaks and tabs, which are what a subject can carry), trimmed, 300
        // characters and an ellipsis.
        private const string Functions = """
            {{~ func plain(t) ~}}
            {{~ ret (t ?? "") | string.replace "\r" " " | string.replace "\n" " " | string.replace "\t" " " | string.strip | string.truncate 301 "…" ~}}
            {{~ end ~}}
            {{~ func title() ~}}
            {{~ ret (escalation ? ("Escalated (step " + escalation.step + ")") : (data.type == "alert.resolved" ? "Resolved" : "Firing")) + ": " + plain(data.alert.ruleName) ~}}
            {{~ end ~}}
            {{~ func value() ~}}
            {{~ ret data.alert.value == null ? "no data" : (data.alert.value | math.format "G6") ~}}
            {{~ end ~}}
            {{~ func rule_and_slo() ~}}
            {{~ ret plain(data.alert.rule) + (data.alert.slo ? ", SLO " + plain(data.alert.slo.key) : "") ~}}
            {{~ end ~}}

            """;

        /// <summary>
        /// Slack: the formatter's header and its line, <c>• *Severity* subject: value (rule)</c>. Plain text: the provider escapes it for mrkdwn,
        /// and puts the alert's link (<c>links.self</c>) beside it.
        /// </summary>
        public const string Slack = Functions + """
            Title: {{ title() }}
            • *{{ plain(data.alert.severity) }}* {{ plain(data.alert.subject) }}: {{ value() }} ({{ rule_and_slo() }})
            """;

        /// <summary>Teams: the formatter's card title and its line, <c>Severity subject: value (rule)</c>; the link is the card's action.</summary>
        public const string Teams = Functions + """
            Title: {{ title() }}
            {{ plain(data.alert.severity) }} {{ plain(data.alert.subject) }}: {{ value() }} ({{ rule_and_slo() }})
            """;

        /// <summary>Email: the formatter's subject (<c>[Juice] …</c>) and body, the alert's link last when there is one.</summary>
        public const string Email = Functions + """
            Subject: [Juice] {{ title() }}
            {{ plain(data.alert.severity) }} {{ plain(data.alert.subject) }}: {{ value() }}
              Rule: {{ plain(data.alert.ruleName) }} ({{ plain(data.alert.rule) }}){{ if data.alert.slo }}
              SLO: {{ plain(data.alert.slo.key) }}{{ end }}{{ if plain(data.alert.reason) != "" }}
              Reason: {{ plain(data.alert.reason) }}{{ end }}{{ if links.self }}
              {{ links.self }}{{ end }}
            """;

        /// <summary>The templates by channel, as a <see cref="Juice.Notifications.Types.TypeDeclaration"/> takes them (the <c>en</c> locale).</summary>
        public static IReadOnlyDictionary<string, string> ByChannel { get; } = new Dictionary<string, string>
        {
            [Channels.Webhook] = Webhook,
            [ChatChannels.Slack] = Slack,
            [ChatChannels.Teams] = Teams,
            [Channels.Email] = Email,
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
