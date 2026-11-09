using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;

namespace Juice.Observability.Webhooks
{
    /// <summary>One alert in a notification.</summary>
    public sealed record NotificationItem(Guid AlertId, string Rule, string RuleName, string Severity, string State, string Subject, double? Value, string? Reason);

    /// <summary>A delivery's stored payload, as the chat and email channels read it. <see cref="Event"/> is the alert event, also for a group.</summary>
    public sealed record NotificationView(string Event, int? EscalationStep, IReadOnlyList<NotificationItem> Alerts)
    {
        /// <summary>The body of a group: one payload holding the alerts of several deliveries.</summary>
        public const string GroupType = "alerts.grouped";

        public static NotificationView Parse(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString()!;
            var alerts = type == GroupType
                ? root.GetProperty("alerts").EnumerateArray().Select(Item).ToList()
                : [Item(root.GetProperty("alert"))];
            return new NotificationView(type == GroupType ? root.GetProperty("event").GetString()! : type,
                root.TryGetProperty("escalation", out var escalation) && escalation.ValueKind == JsonValueKind.Object ? escalation.GetProperty("step").GetInt32() : null, alerts);
        }

        private static NotificationItem Item(JsonElement a) => new(a.GetProperty("id").GetGuid(), a.GetProperty("rule").GetString()!, a.GetProperty("ruleName").GetString()!,
            a.GetProperty("severity").GetString()!, a.GetProperty("state").GetString()!, a.GetProperty("subject").GetString()!,
            a.GetProperty("value").ValueKind == JsonValueKind.Number ? a.GetProperty("value").GetDouble() : null,
            a.TryGetProperty("reason", out var reason) ? reason.GetString() : null);

        /// <summary>Several deliveries' payloads as one, for a signed webhook's body as for a chat message.</summary>
        public static string Group(Guid id, string @event, DateTimeOffset at, IEnumerable<string> payloads)
        {
            var nodes = payloads.Select(p => JsonNode.Parse(p)!).ToList();
            return new JsonObject
            {
                ["id"] = id,
                ["type"] = GroupType,
                ["event"] = @event,
                ["occurredAt"] = at,
                ["tenant"] = nodes[0]["tenant"]?.DeepClone(),
                ["count"] = nodes.Count,
                ["alerts"] = new JsonArray(nodes.Select(n => n["alert"]!.DeepClone()).ToArray()),
            }.ToJsonString(JsonSerializerOptions.Web);
        }
    }

    /// <summary>A request notification's payload (<see cref="ActionRequestEvents"/>), as the chat and email channels read it.</summary>
    public sealed record RequestNotice(string Event, string Hostname, string Action, string Parameters, string Risk, string Policy, string Reason, string RequestedBy,
        DateTimeOffset ExpiresAt, string? DecidedBy, string? Comment)
    {
        /// <summary>The notice, or null when the payload is an alert's.</summary>
        public static RequestNotice? TryParse(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!ActionRequestEvents.IsRequestEvent(root.GetProperty("type").GetString() ?? "")) return null;
            var r = root.GetProperty("request");
            static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new RequestNotice(root.GetProperty("type").GetString()!, r.GetProperty("agent").GetProperty("hostname").GetString() ?? "", Text(r, "action") ?? "",
                r.GetProperty("parameters").GetRawText(), Text(r, "risk") ?? "", Text(r, "policy") ?? "", Text(r, "reason") ?? "", Text(r, "requestedBy") ?? "",
                r.GetProperty("expiresAt").GetDateTimeOffset(), Text(r, "decidedBy"), Text(r, "comment"));
        }
    }

    /// <summary>
    /// Writes notifications for people. Rule names are configuration, but subjects come from telemetry, so anything an application can name itself is
    /// made harmless for the channel: a service called <c>&lt;!channel&gt;</c> must not page a Slack channel, and one called <c>[click](http://evil)</c>
    /// must not become a link in Teams.
    /// </summary>
    public static class ChannelFormatter
    {
        public const int MaxListed = 10;
        private const int MaxText = 300;

        /// <summary>Control characters out (a line break in a mail subject would add a header), length bounded.</summary>
        public static string Plain(string? text)
        {
            var clean = new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            return clean.Length <= MaxText ? clean : clean[..MaxText] + "…";
        }

        private static string Slack(string? text) => Plain(text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Adaptive cards render markdown links; without the brackets there's no link.</summary>
        private static string Teams(string? text) => Plain(text).Replace('[', '(').Replace(']', ')');

        public static string Title(NotificationView view)
        {
            var what = view.Event switch
            {
                NotificationPlan.Firing => "Firing",
                NotificationPlan.Resolved => "Resolved",
                NotificationPlan.Escalated => view.EscalationStep is { } step ? $"Escalated (step {step})" : "Escalated",
                NotificationPlan.Test => "Test",
                _ => view.Event,
            };
            return view.Alerts.Count == 1 ? $"{what}: {view.Alerts[0].RuleName}" : $"{what}: {view.Alerts.Count} alerts";
        }

        private static string Value(NotificationItem alert) => alert.Value is { } value ? value.ToString("G6", CultureInfo.InvariantCulture) : "no data";

        private static string? Link(string? consoleUrl, NotificationItem alert)
            => string.IsNullOrEmpty(consoleUrl) || alert.AlertId == Guid.Empty ? null : $"{consoleUrl.TrimEnd('/')}/alerts/{alert.AlertId}";

        private static IEnumerable<NotificationItem> Listed(NotificationView view) => view.Alerts.Take(MaxListed);

        private static string? More(NotificationView view) => view.Alerts.Count > MaxListed ? $"and {view.Alerts.Count - MaxListed} more" : null;

        public static string SlackBody(NotificationView view, string? consoleUrl)
        {
            var title = Slack(Title(view));
            var lines = new List<string>();
            foreach (var alert in Listed(view))
            {
                var subject = Slack(alert.Subject);
                var label = Link(consoleUrl, alert) is { } link ? $"<{link}|{subject}>" : subject;
                lines.Add($"• *{Slack(alert.Severity)}* {label}: {Slack(Value(alert))} ({Slack(alert.Rule)})");
            }
            if (More(view) is { } more) lines.Add(more);
            return new JsonObject
            {
                ["text"] = title,
                ["blocks"] = new JsonArray(
                    new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Plain(Title(view)) } },
                    new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = string.Join('\n', lines) } }),
            }.ToJsonString();
        }

        public static string TeamsBody(NotificationView view, string? consoleUrl)
        {
            var body = new JsonArray(new JsonObject { ["type"] = "TextBlock", ["text"] = Teams(Title(view)), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true });
            foreach (var alert in Listed(view))
            {
                body.Add(new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["text"] = $"{Teams(alert.Severity)} {Teams(alert.Subject)}: {Teams(Value(alert))} ({Teams(alert.Rule)})" });
            }
            if (More(view) is { } more) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = more });
            var card = new JsonObject
            {
                ["type"] = "AdaptiveCard",
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["version"] = "1.4",
                ["body"] = body,
            };
            if (view.Alerts.Count == 1 && Link(consoleUrl, view.Alerts[0]) is { } link)
            {
                card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open alert", ["url"] = link });
            }
            return new JsonObject
            {
                ["type"] = "message",
                ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card }),
            }.ToJsonString();
        }

        public static (string Subject, string Body) Email(NotificationView view, string? consoleUrl)
        {
            var body = new StringBuilder();
            foreach (var alert in Listed(view))
            {
                body.Append(Plain(alert.Severity)).Append(' ').Append(Plain(alert.Subject)).Append(": ").Append(Value(alert)).AppendLine();
                body.Append("  Rule: ").Append(Plain(alert.RuleName)).Append(" (").Append(Plain(alert.Rule)).AppendLine(")");
                if (Plain(alert.Reason) is { Length: > 0 } reason) body.Append("  Reason: ").AppendLine(reason);
                if (Link(consoleUrl, alert) is { } link) body.Append("  ").AppendLine(link);
                body.AppendLine();
            }
            if (More(view) is { } more) body.AppendLine(more);
            return ($"[Juice] {Plain(Title(view))}", body.ToString());
        }

        public static string Title(RequestNotice notice)
        {
            var what = notice.Event switch
            {
                ActionRequestEvents.Created => "Approval needed",
                ActionRequestEvents.Approved => "Approved",
                ActionRequestEvents.Rejected => "Rejected",
                ActionRequestEvents.Cancelled => "Cancelled",
                ActionRequestEvents.Expired => "Expired without a decision",
                _ => notice.Event,
            };
            return $"{what}: {notice.Action} on {notice.Hostname}";
        }

        /// <summary>The facts, one per line, unescaped: each channel escapes them its own way.</summary>
        private static List<(string Label, string Text)> Facts(RequestNotice notice)
        {
            var facts = new List<(string, string)>
            {
                ("Requested by", $"{notice.RequestedBy}: {notice.Reason}"),
                ("Risk", $"{notice.Risk}, needs {notice.Policy switch { "Admin" => "an admin's approval", "SecondPerson" => "an admin other than the requester", _ => notice.Policy }}"),
                ("Parameters", notice.Parameters),
            };
            if (notice.Event == ActionRequestEvents.Created)
                facts.Add(("Expires", notice.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)));
            else if (notice.DecidedBy is { } by)
                facts.Add(("Decided by", notice.Comment is { Length: > 0 } comment ? $"{by}: {comment}" : by));
            return facts;
        }

        private static string? Link(string? consoleUrl) => string.IsNullOrEmpty(consoleUrl) ? null : $"{consoleUrl.TrimEnd('/')}/approvals";

        public static string SlackBody(RequestNotice notice, string? consoleUrl)
        {
            var lines = Facts(notice).Select(f => $"*{f.Label}:* {Slack(f.Text)}").ToList();
            if (Link(consoleUrl) is { } link) lines.Add($"<{link}|Open Approvals>");
            return new JsonObject
            {
                ["text"] = Slack(Title(notice)),
                ["blocks"] = new JsonArray(
                    // A host names itself: no angle brackets, so it can't write a mention even where Slack would read one.
                    new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Plain(Title(notice)).Replace('<', '‹').Replace('>', '›') } },
                    new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = string.Join('\n', lines) } }),
            }.ToJsonString();
        }

        public static string TeamsBody(RequestNotice notice, string? consoleUrl)
        {
            var body = new JsonArray(new JsonObject { ["type"] = "TextBlock", ["text"] = Teams(Title(notice)), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true });
            foreach (var (label, text) in Facts(notice))
            {
                body.Add(new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["text"] = $"{label}: {Teams(text)}" });
            }
            var card = new JsonObject { ["type"] = "AdaptiveCard", ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["version"] = "1.4", ["body"] = body };
            if (Link(consoleUrl) is { } link)
            {
                card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open Approvals", ["url"] = link });
            }
            return new JsonObject
            {
                ["type"] = "message",
                ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card }),
            }.ToJsonString();
        }

        public static (string Subject, string Body) Email(RequestNotice notice, string? consoleUrl)
        {
            var body = new StringBuilder();
            foreach (var (label, text) in Facts(notice)) body.Append(label).Append(": ").AppendLine(Plain(text));
            if (Link(consoleUrl) is { } link) body.AppendLine().AppendLine(link);
            return ($"[Juice] {Plain(Title(notice))}", body.ToString());
        }
    }
}
