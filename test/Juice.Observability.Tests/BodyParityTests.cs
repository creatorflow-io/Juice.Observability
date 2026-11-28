using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Rendering;
using Juice.Notifications.Types;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Tests.Legacy;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The proof that the embedded pipeline's bodies are the 1.x webhooks' (M50): what the declared templates render is compared with
    /// <see cref="ChannelFormatter"/> (frozen in <c>Legacy/</c>) and <see cref="NotificationPlan.Payload"/>: the webhook and email bodies byte
    /// for byte, Slack and Teams by content (title, text, link url; the provider frames them itself, the lead's decision). And the Console's link is the
    /// raw address, as the frozen formatter builds it. Replaces the three-mode parity run, which proved the same on the 1.x tip.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class BodyParityTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static (Rule Rule, Alert Alert, AlertTransition Transition) Alert(AlertState state, string subject, string ruleName, double? value, string reason, Dictionary<string, string>? evidence = null)
        {
            // A zone ahead of UTC: its "+07:00" is the "+" the template must not escape.
            var at = new DateTimeOffset(2026, 11, 26, 9, 30, 15, 123, TimeSpan.FromHours(7)).AddTicks(4567);
            var rule = new Rule { TenantId = Guid.Parse(LocalStack.TenantKey), Key = "orders-errors", Name = ruleName, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = "d", SubjectKey = subject, Severity = AlertSeverity.Critical, State = state,
                BreachStartedAt = at.AddMinutes(-3), FiredAt = at, ResolvedAt = state == AlertState.Resolved ? at.AddMinutes(20) : null, Evidence = evidence ?? [],
            };
            return (rule, alert, new AlertTransition { TenantId = rule.TenantId, AlertId = alert.Id, ToState = state, At = alert.ResolvedAt ?? at, Value = value, Reason = reason });
        }

        public static TheoryData<string, string, string, double?, string> Variants => new()
        {
            { NotificationPlan.Firing, "service:shop/orders@production", "Orders errors", 0.25, "error rate 25% over 5%" },
            { NotificationPlan.Resolved, "service:shop/orders@production", "Orders errors", 0.01, "recovered" },
            { NotificationPlan.Escalated, "service:shop/orders@production", "Orders errors", null, "error rate 25% over 5%" },
            { NotificationPlan.Firing, "service:<a href=\"x\">&'é" + (char)0x2028 + "" + new string('x', 400) + "@prod", "Errors\r\nBcc: x\t+ a \\ b", 123456789.123456789, "a+b\tc\\d\"" },
            // Control characters other than line breaks and tabs, and the length at which legacy cuts (300 stays, 301 is cut).
            { NotificationPlan.Firing, "service:a\u000Bb\u0085c\u007Fd", "Orders errors", 0.25, "x" },
            { NotificationPlan.Firing, "s" + new string('y', 299), "Orders errors", 0.25, "exactly 300" },
            { NotificationPlan.Firing, "s" + new string('y', 300), "Orders errors", 0.25, "301" },
            { NotificationPlan.Firing, "  " + new string('z', 305) + "  ", new string('n', 200), 0.25, new string('r', 301) },
            // A subject is telemetry: it must not become a link in Teams.
            { NotificationPlan.Firing, "service:*bold*_it_ <b>&", new string('l', 255), 0.25, "255-character name" },
            { NotificationPlan.Firing, "service:*bold*_it_ <b>&", new string('l', 256), 0.25, "256-character name" },
            { NotificationPlan.Firing, "service:[click](http://evil)", "Orders [errors]", 0.25, "[a](b)" },
            { NotificationPlan.Escalated, "service:[click](http://evil)", "Orders errors", 0.25, "x" },
        };

        private static string DeclaredTemplate(string type, string channel)
            => ObservabilityTypes.All().Single(t => t.Key == type).Templates!.Value.GetProperty(channel).GetString()!;

        private static (string Payload, Guid Id, (string Policy, int Step)? Escalation) Frozen(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Resolved ? AlertState.Resolved : AlertState.Firing, subject, ruleName, value, reason,
                new() { ["slo"] = "orders-availability", ["sloName"] = "Orders <available> & \"fast\"" });
            var id = Guid.CreateVersion7();
            (string, int)? escalation = @event == NotificationPlan.Escalated ? ("paging", 2) : null;
            return (NotificationPlan.Payload(id, @event, LocalStack.TenantKey, rule, alert, transition, escalation), id, escalation);
        }

        private static async Task<string> RenderAsync(string type, string channel, string payload, Guid deliveryId, (string Policy, int Step)? escalation, IReadOnlyDictionary<string, string>? links = null)
        {
            await TemplateWarmUp.EnsureAsync();
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
            var legacyType = node["type"]!.GetValue<string>();
            node.Remove("id");
            // The adapter sends the firing/resolved event as data; an escalation's data is the firing transition's.
            if (escalation is not null) node["type"] = NotificationPlan.Firing;
            var model = new RenderModel(type, 1, "Alert", NotificationPriority.High, JsonSerializer.SerializeToElement(node), new Dictionary<string, string>(), links ?? new Dictionary<string, string>(),
                Delivery: new RenderDelivery(deliveryId), Escalation: escalation is { } e ? new RenderEscalation(e.Step, e.Policy) : null);
            _ = legacyType;
            return await TemplateRenderer.RenderAsync(DeclaredTemplate(type, channel), model, Ct);
        }

        [Theory]
        [MemberData(nameof(Variants))]
        public async Task BodyParity_SlackTeamsEmailWebhook_FrozenFormatter_BytesOrContentEqual(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Frozen(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;

            var body = await RenderAsync(type, Channels.Webhook, payload, id, escalation);

            // The delivery's id is the one the legacy payload carries: the whole body is legacy's, byte for byte.
            Assert.Equal(payload, body);
            await SlackTeamsContentAsync(@event, subject, ruleName, value, reason);
            await EmailBytesAsync(@event, subject, ruleName, value, reason);
        }

        /// <summary>
        /// A title or subject is bounded by the pipeline's content at <see cref="ChannelContent.MaxTitle"/> characters (1.x cut at 300): the one
        /// explained difference, and why a rule name of 255 or 256 characters (title "Firing: " + name) is cut.
        /// </summary>
        private static string? Bound(string? legacy) => ChannelContent.Cut(legacy, ChannelContent.MaxTitle);

        private const string ConsoleUrl ="https://console.example/";

        private static string LinkOf(string payload)
            => $"{ConsoleUrl}alerts/{JsonDocument.Parse(payload).RootElement.GetProperty("alert").GetProperty("id").GetString()}";

        /// <summary>
        /// Content parity: the same title and text, and the alert's link (legacy's, from the ConsoleUrl's address) as the content's url. The
        /// provider presents the link as its own (Slack's button for legacy's inline link, a card action), so only the content is compared.
        /// </summary>
        private static async Task SlackTeamsContentAsync(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Frozen(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;
            var view = NotificationView.Parse(payload);
            var link = LinkOf(payload);
            var links = new Dictionary<string, string> { ["self"] = link };

            var slack = JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await RenderAsync(type, "slack", payload, id, escalation, links), "x", links, null))!;
            var teams = JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await RenderAsync(type, "teams", payload, id, escalation, links), "x", links, null))!;

            var blocks = JsonDocument.Parse(ChannelFormatter.SlackBody(view, ConsoleUrl)).RootElement.GetProperty("blocks");
            var legacyLine = blocks[1].GetProperty("text").GetProperty("text").GetString()!;
            Assert.Equal(Bound(blocks[0].GetProperty("text").GetProperty("text").GetString()), slack.Title);
            Assert.Contains($"<{link}|", legacyLine);
            Assert.Equal(System.Text.RegularExpressions.Regex.Replace(legacyLine, @"<https?://[^|>]*\|([^>]*)>", "$1"),
                slack.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"));
            Assert.Equal(link, slack.Url);

            var card = JsonDocument.Parse(ChannelFormatter.TeamsBody(view, ConsoleUrl)).RootElement.GetProperty("attachments")[0].GetProperty("content");
            Assert.Equal(Bound(card.GetProperty("body")[0].GetProperty("text").GetString()), teams.Title);
            Assert.Equal(card.GetProperty("body")[1].GetProperty("text").GetString(), teams.Text);
            Assert.Equal(link, card.GetProperty("actions")[0].GetProperty("url").GetString());
            Assert.Equal(link, teams.Url);
            // A subject can't write a link of its own into the card.
            Assert.DoesNotContain("](", teams.Text);
        }

        private static async Task EmailBytesAsync(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Frozen(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;
            var link = LinkOf(payload);

            var (legacySubject, legacyBody) = ChannelFormatter.Email(NotificationView.Parse(payload), ConsoleUrl);
            var email = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email(
                await RenderAsync(type, Channels.Email, payload, id, escalation, new Dictionary<string, string> { ["self"] = link }), "x"))!;

            Assert.Equal(Bound(legacySubject), email.Subject);
            Assert.Contains(link, legacyBody);
            // Exact bytes, with the two differences that are explained rather than normalised away:
            //  - the frozen formatter's AppendLine is Environment.NewLine: "\n" on the Linux containers 1.x ran in, "\r\n" on a Windows test host;
            //  - the pipeline's email content ends at its last line (ChannelContent.Email trims the end), where 1.x's body ended with a blank line.
            var legacyOnLinux = legacyBody.Replace(Environment.NewLine, "\n");
            Assert.EndsWith("\n", legacyOnLinux);
            Assert.Equal(legacyOnLinux.TrimEnd(), email.Text);
        }

    }
}
