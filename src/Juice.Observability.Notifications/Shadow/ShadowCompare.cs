using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Webhooks;
using NewStatus = Juice.Notifications.DeliveryStatus;

namespace Juice.Observability.Notifications.Shadow
{
    /// <summary>
    /// One legacy delivery of a compared unit, as it was sent: <c>Payload</c> is the body signed and sent (a grouped one's, its group's),
    /// <c>SentAt</c> when it first went out (its group's; null if never), <c>Recipients</c> an email endpoint's addresses (one message to all).
    /// </summary>
    public sealed record LegacySent(string EndpointKey, ChannelKind Kind, int GroupWindowSeconds, string Payload, DeliveryStatus Status, string? LastError,
        DateTimeOffset CreatedAt, DateTimeOffset? SentAt, int Recipients);

    /// <summary>
    /// One delivery the new pipeline planned for the unit, through the integration of the endpoint's key; <c>Content</c> is what it rendered
    /// (a webhook's body and type, a chat message, an email).
    /// </summary>
    public sealed record NewSent(string IntegrationKey, string Channel, string Content, NewStatus Status, DateTimeOffset CreatedAt, bool DefaultTemplate = false);

    /// <summary>
    /// What is compared: a transition's own notifications (step 0), or one escalation step of its alert. <c>ResolvedSince</c>: the alert
    /// resolved after it (a firing a resolve may have cancelled, Q2); <c>Held</c>: a silence held it in the new pipeline ("told" per alert, Q4).
    /// <c>FiredSince</c>: for a resolved transition, the release pass dropped its hold because the alert fired again after it (outcome
    /// <c>dropped_stale</c>, M46c), not for another reason.
    /// </summary>
    public sealed record ShadowUnit(Guid TenantId, Guid TransitionId, int EscalationStep, Guid AlertId, string Event, bool ResolvedSince, bool Held, bool FiredSince = false);

    /// <summary>A difference found, and the rule that explains it (null: unexplained).</summary>
    public sealed record ShadowFinding(string EndpointKey, string Kind, string? Rule, string Detail);

    /// <summary>
    /// The comparison itself, without a database (waves §6.1): each endpoint's legacy deliveries against the new pipeline's deliveries through
    /// the integration of the same key; bodies modulo ids and times, as each channel renders them; send times within a tolerance. A difference
    /// is explained only by a rule the report names (p5-d7).
    /// </summary>
    public static class ShadowCompare
    {
        public static IReadOnlyList<ShadowFinding> Compare(ShadowUnit unit, IReadOnlyList<LegacySent> legacy, IReadOnlyList<NewSent> @new,
            ShadowCompareOptions options)
        {
            var findings = new List<ShadowFinding>();
            var keys = legacy.Select(l => l.EndpointKey).Concat(@new.Select(n => n.IntegrationKey)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var l = legacy.Where(x => x.EndpointKey == key).ToList();
                var n = @new.Where(x => x.IntegrationKey == key).ToList();
                findings.AddRange(Endpoint(unit, key, l, n, options));
            }
            return findings;
        }

        /// <summary>Legacy's destination policy refused it (<c>blocked_address:…</c>, <c>private_address_not_allowlisted:…</c>, <c>https_required</c>).</summary>
        internal static bool BlockedByPolicy(LegacySent d)
            => d.Status == DeliveryStatus.Failed && d.LastError is { } error
                && (error.StartsWith("blocked", StringComparison.Ordinal) || error.StartsWith("private", StringComparison.Ordinal) || error == "https_required");

        /// <summary>Whether legacy sent it, or tried to (a receiver's refusal is still a send; a dry run never asks the receiver).</summary>
        private static bool Sent(LegacySent d)
            => d.Status is DeliveryStatus.Delivered or DeliveryStatus.Grouped || d.Status == DeliveryStatus.Failed && !BlockedByPolicy(d);

        private static bool Unsettled(LegacySent d) => d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying or DeliveryStatus.Suppressed;

        private static bool Unsettled(NewSent d) => d.Status is NewStatus.Pending or NewStatus.Sending or NewStatus.Retrying;

        private static IEnumerable<ShadowFinding> Endpoint(ShadowUnit unit, string key, List<LegacySent> legacy, List<NewSent> @new, ShadowCompareOptions options)
        {
            if (legacy.Any(Unsettled) || @new.Any(Unsettled))
            {
                yield return new(key, ShadowDifferenceKinds.Unsettled, null,
                    $"not settled when compared: legacy {string.Join(",", legacy.Select(d => d.Status))}; new {string.Join(",", @new.Select(d => d.Status))}");
                yield break;
            }
            var sent = legacy.Where(Sent).ToList();
            var would = @new.Where(d => d.Status == NewStatus.WouldSend).ToList();
            var grouped = legacy.Any(d => d.GroupWindowSeconds > 0);
            if (sent.Count == 0 && would.Count == 0)
            {
                yield break;
            }
            if (would.Count == 0)
            {
                var cancelled = @new.Any(d => d.Status == NewStatus.Cancelled);
                var rule = unit.Event == NotificationPlan.Firing && unit.ResolvedSince && (cancelled || @new.Count == 0) ? ShadowRules.ResolveCancelsFiring
                    : unit.Event == NotificationPlan.Resolved && unit.Held && unit.FiredSince ? ShadowRules.StaleResolveDropped
                    : unit.Held ? ShadowRules.ToldPerAlert
                    : null;
                yield return new(key, ShadowDifferenceKinds.MissingInNew, rule,
                    $"legacy sent {sent.Count} ({string.Join(",", sent.Select(d => d.Status))}); new: {(@new.Count == 0 ? "nothing planned" : string.Join(",", @new.Select(d => d.Status)))}");
                yield break;
            }
            if (sent.Count == 0)
            {
                var rule = legacy.Any(BlockedByPolicy) ? ShadowRules.BlockedByPolicyInLegacy : unit.Held ? ShadowRules.ToldPerAlert : null;
                yield return new(key, ShadowDifferenceKinds.MissingInLegacy, rule,
                    $"new would send {would.Count}; legacy: {(legacy.Count == 0 ? "nothing planned" : string.Join(",", legacy.Select(d => $"{d.Status}{(d.LastError is null ? "" : " " + d.LastError)}")))}");
                yield break;
            }
            var first = sent[0];
            // How many messages: one per delivery, except an email endpoint's, one per recipient in the new pipeline (Q13).
            if (would.Count != sent.Count)
            {
                var fanOut = first.Kind == ChannelKind.Email && sent.Count == 1 && would.Count == first.Recipients;
                yield return new(key, ShadowDifferenceKinds.Shape, fanOut ? ShadowRules.EmailFanOut : null,
                    $"legacy sent {sent.Count} message(s); new would send {would.Count}");
            }
            foreach (var body in Bodies(unit, first, would, grouped, options))
            {
                yield return body with { EndpointKey = key };
            }
            var legacyAt = first.SentAt ?? first.CreatedAt;
            var newAt = would.Min(d => d.CreatedAt);
            if ((newAt - legacyAt).Duration() > options.TimingTolerance)
            {
                yield return new(key, ShadowDifferenceKinds.Timing, grouped ? ShadowRules.DigestBoundary : null,
                    $"legacy at {legacyAt:O}; new at {newAt:O} ({(newAt - legacyAt).TotalSeconds:+0;-0} s, tolerance {options.TimingTolerance.TotalSeconds} s)");
            }
        }

        private static IEnumerable<ShadowFinding> Bodies(ShadowUnit unit, LegacySent legacy, List<NewSent> would, bool grouped, ShadowCompareOptions options)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in would)
            {
                var problem = legacy.Kind switch
                {
                    ChannelKind.Webhook => ShadowBodies.Webhook(unit.AlertId, legacy.Payload, d.Content, grouped),
                    ChannelKind.Slack or ChannelKind.Teams or ChannelKind.Email when IsGroup(legacy.Payload) || ShadowBodies.IsGroupContent(d.Content)
                        => grouped ? new ShadowBodies.Problem(ShadowDifferenceKinds.Shape, ShadowRules.DigestBoundary, "a group on one side only, or of other alerts") : null,
                    _ => ShadowBodies.Formatted(legacy.Kind, legacy.Payload, d.Content, options.ConsoleUrl),
                };
                if (problem is not null && seen.Add(problem.Detail))
                {
                    // Rendered by the type's default template (none ported for the channel yet): a different body is that, not a defect.
                    var rule = problem.Rule ?? (d.DefaultTemplate && problem.Kind is ShadowDifferenceKinds.Body or ShadowDifferenceKinds.Shape ? ShadowRules.TemplatesNotPorted : null);
                    yield return new(legacy.EndpointKey, problem.Kind, rule, problem.Detail);
                }
            }
        }

        private static bool IsGroup(string payload) => JsonNode.Parse(payload)?["type"]?.GetValue<string>() == NotificationView.GroupType;
    }

    /// <summary>What the comparison allows: how far apart the two send times may be, and the console link legacy's chat and email carry.</summary>
    public sealed record ShadowCompareOptions(TimeSpan TimingTolerance, string? ConsoleUrl = null);

    /// <summary>The channels' bodies, made comparable: a webhook's JSON modulo ids and times; a chat message's title and text; an email's subject and text.</summary>
    public static class ShadowBodies
    {
        public sealed record Problem(string Kind, string? Rule, string Detail);

        /// <summary>What a webhook body may differ in: its id (the delivery's in the new pipeline, p5-d8), and a group's own time.</summary>
        private static readonly string[] Ignored = ["id"];
        private static readonly string[] IgnoredInGroup = ["id", "occurredAt"];

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        /// <summary>The body a webhook delivery carries: the content's <c>body</c>, or the content itself if it is the body.</summary>
        internal static string WebhookBody(string content)
        {
            var root = Parse(content);
            return root.ValueKind == JsonValueKind.Object && Property(root, "body") is { ValueKind: JsonValueKind.String } body && Property(root, "contentType") is not null
                ? body.GetString()! : content;
        }

        internal static bool IsGroupContent(string content)
        {
            try
            {
                return Parse(WebhookBody(content)) is { ValueKind: JsonValueKind.Object } root && Property(root, "type")?.GetString() == NotificationView.GroupType;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static JsonElement? Property(JsonElement element, string name)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// A webhook's bodies, modulo ids and times. When either is a group (legacy's group window, the new pipeline's route digest), the alert
        /// this unit is about is compared on its own, and a different envelope or membership is the digest boundary.
        /// </summary>
        public static Problem? Webhook(Guid alertId, string legacyPayload, string newContent, bool grouped)
        {
            JsonElement legacy, @new;
            try
            {
                (legacy, @new) = (Parse(legacyPayload), Parse(WebhookBody(newContent)));
            }
            catch (JsonException ex)
            {
                return new(ShadowDifferenceKinds.Body, null, $"not JSON: {ex.Message}");
            }
            var legacyGroup = Type(legacy) == NotificationView.GroupType;
            var newGroup = Type(@new) == NotificationView.GroupType;
            if (!legacyGroup && !newGroup)
            {
                return Diff("$", legacy, @new, Ignored) is { } diff ? new(ShadowDifferenceKinds.Body, null, diff) : null;
            }
            // One alert of a group: the same alert either way; the envelope and the other members are the window's.
            var (l, n) = (Member(legacy, alertId), Member(@new, alertId));
            if (l is null || n is null)
            {
                return new(ShadowDifferenceKinds.Body, null, $"alert {alertId} isn't in the {(l is null ? "legacy" : "new")} body");
            }
            if (Diff("$.alert", l.Value, n.Value, []) is { } member)
            {
                return new(ShadowDifferenceKinds.Body, null, member);
            }
            if (legacyGroup && newGroup)
            {
                if (Diff("$", Envelope(legacy), Envelope(@new), IgnoredInGroup) is { } envelope)
                {
                    return new(ShadowDifferenceKinds.Body, null, envelope);
                }
                var (lm, nm) = (Members(legacy), Members(@new));
                return lm.SetEquals(nm) ? null
                    : new(ShadowDifferenceKinds.Shape, grouped ? ShadowRules.DigestBoundary : null, $"group of {lm.Count} in legacy, {nm.Count} in new, {lm.Except(nm).Count() + nm.Except(lm).Count()} alert(s) in one only");
            }
            return new(ShadowDifferenceKinds.Shape, grouped ? ShadowRules.DigestBoundary : null,
                $"{(legacyGroup ? "legacy" : "new")} sent a group of {Members(legacyGroup ? legacy : @new).Count}, the other a single alert");
        }

        private static string? Type(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) ? t.GetString() : null;

        private static JsonElement? Member(JsonElement body, Guid alertId)
        {
            if (Type(body) != NotificationView.GroupType)
            {
                return body.TryGetProperty("alert", out var alert) ? alert : null;
            }
            foreach (var a in body.GetProperty("alerts").EnumerateArray())
            {
                if (a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetGuid() == alertId)
                {
                    return a;
                }
            }
            return null;
        }

        private static HashSet<string> Members(JsonElement group)
            => [.. group.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("id").GetString()!)];

        private static JsonElement Envelope(JsonElement group)
        {
            var node = JsonNode.Parse(group.GetRawText())!.AsObject();
            node.Remove("alerts");
            node.Remove("count");
            return Parse(node.ToJsonString());
        }

        /// <summary>The first place two JSON values differ (paths in <paramref name="ignored"/> at the top level skipped), or null when they don't.</summary>
        internal static string? Diff(string path, JsonElement a, JsonElement b, IReadOnlyCollection<string> ignored)
        {
            if (a.ValueKind != b.ValueKind && !(IsNumber(a) && IsNumber(b)))
            {
                return $"{path}: legacy {Show(a)}, new {Show(b)}";
            }
            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var names = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)).Where(n => !ignored.Contains(n)).Order(StringComparer.Ordinal);
                    foreach (var name in names)
                    {
                        var (hasA, hasB) = (a.TryGetProperty(name, out var va), b.TryGetProperty(name, out var vb));
                        if (!hasA || !hasB)
                        {
                            return $"{path}.{name}: only in {(hasA ? "legacy" : "new")}";
                        }
                        if (Diff($"{path}.{name}", va, vb, []) is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                case JsonValueKind.Array:
                    var (la, lb) = (a.GetArrayLength(), b.GetArrayLength());
                    if (la != lb)
                    {
                        return $"{path}: {la} item(s) in legacy, {lb} in new";
                    }
                    for (var i = 0; i < la; i++)
                    {
                        if (Diff($"{path}[{i}]", a[i], b[i], []) is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                case JsonValueKind.Number:
                    return a.GetDecimal() == b.GetDecimal() ? null : $"{path}: legacy {a.GetRawText()}, new {b.GetRawText()}";
                case JsonValueKind.String:
                    // Times are compared as instants: the same moment may be written with another offset or precision.
                    if (a.TryGetDateTimeOffset(out var ta) && b.TryGetDateTimeOffset(out var tb))
                    {
                        return ta == tb ? null : $"{path}: legacy {a.GetString()}, new {b.GetString()}";
                    }
                    return a.GetString() == b.GetString() ? null : $"{path}: legacy {Show(a)}, new {Show(b)}";
                default:
                    return null;
            }
        }

        private static bool IsNumber(JsonElement e) => e.ValueKind == JsonValueKind.Number;

        private static string Show(JsonElement e)
        {
            var text = e.ValueKind == JsonValueKind.String ? $"\"{e.GetString()}\"" : e.GetRawText();
            return text.Length > 200 ? text[..200] + "…" : text;
        }

        /// <summary>
        /// A chat message or an email, as legacy formatted it at send time (<see cref="ChannelFormatter"/>) against what the new pipeline rendered:
        /// Slack and Teams by title and text, email by subject and text.
        /// </summary>
        public static Problem? Formatted(ChannelKind kind, string legacyPayload, string newContent, string? consoleUrl)
        {
            var view = NotificationView.Parse(legacyPayload);
            JsonElement content;
            try
            {
                content = Parse(newContent);
            }
            catch (JsonException ex)
            {
                return new(ShadowDifferenceKinds.Body, null, $"not JSON: {ex.Message}");
            }
            string?[] expected, actual;
            switch (kind)
            {
                case ChannelKind.Slack:
                    var blocks = Parse(ChannelFormatter.SlackBody(view, consoleUrl)).GetProperty("blocks");
                    // Legacy puts the alert's link in its line (<link|subject>); the provider shows it as a button: the link is compared as the content's url.
                    expected = [blocks[0].GetProperty("text").GetProperty("text").GetString(), WithoutInlineLinks(blocks[1].GetProperty("text").GetProperty("text").GetString()!)];
                    // Legacy escapes for mrkdwn itself; the Slack provider escapes the plain text the same way when it sends.
                    actual = [Text(content, "title"), Text(content, "text")?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")];
                    break;
                case ChannelKind.Teams:
                    var card = Parse(ChannelFormatter.TeamsBody(view, consoleUrl)).GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
                    expected = [card[0].GetProperty("text").GetString(), card[1].GetProperty("text").GetString()];
                    actual = [Text(content, "title"), Text(content, "text")];
                    break;
                default:
                    var (subject, body) = ChannelFormatter.Email(view, consoleUrl);
                    expected = [subject, body.TrimEnd().ReplaceLineEndings("\n")];
                    actual = [Text(content, "subject"), Text(content, "text")?.TrimEnd().ReplaceLineEndings("\n")];
                    break;
            }
            string[] fields = kind == ChannelKind.Email ? ["subject", "text"] : ["title", "text"];
            // The link of a single alert (a group's are the digest's): legacy's, when the Console's address is set, is the chat content's url.
            if (kind != ChannelKind.Email && view.Alerts is [{ AlertId: var alertId }] && alertId != Guid.Empty
                && Uri.TryCreate(consoleUrl, UriKind.Absolute, out var console) && console.Scheme is "https" or "http")
            {
                fields = [.. fields, "url"];
                expected = [.. expected, $"{consoleUrl!.TrimEnd('/')}/alerts/{alertId}"];
                actual = [.. actual, Text(content, "url")];
            }
            for (var i = 0; i < fields.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    return new(ShadowDifferenceKinds.Body, null, $"{fields[i]}: legacy \"{Clip(expected[i])}\", new \"{Clip(actual[i])}\"");
                }
            }
            return null;
        }

        /// <summary>Slack's inline links (<c>&lt;url|label&gt;</c>) as their label: what the Slack provider's text shows beside its button.</summary>
        private static string WithoutInlineLinks(string text)
            => System.Text.RegularExpressions.Regex.Replace(text, @"<https?://[^|>]*\|([^>]*)>", "$1");

        private static string? Text(JsonElement content, string name) => Property(content, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        private static string Clip(string? s) => s is null ? "(none)" : s.Length > 200 ? s[..200] + "…" : s;
    }
}
