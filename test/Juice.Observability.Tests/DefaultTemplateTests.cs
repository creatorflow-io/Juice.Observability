using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Notifications.Rendering;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The ported default templates (phase-5-plan p5-d8, waves §5.3 M46a) rendered in notifications' own sandbox against the request the producer
    /// adapter sends (the legacy payload without its id), compared with what legacy sends: the webhook body byte for byte but its id, and the chat
    /// and email text as <c>ChannelFormatter</c> writes it.
    /// </summary>
    public class DefaultTemplateTests
    {
        private static readonly Guid TenantId = Guid.Parse("0193f0aa-0000-7000-8000-0000000000f1");

        private static (Rule Rule, Alert Alert, AlertTransition Transition) Alert(AlertState state = AlertState.Firing, double? value = 0.25,
            string subject = "service:shop/orders@production", string ruleName = "Orders errors", string reason = "error rate 25% over 5%",
            Dictionary<string, string>? evidence = null)
        {
            var at = new DateTimeOffset(2026, 11, 26, 9, 30, 15, 123, TimeSpan.Zero);
            var rule = new Rule { TenantId = TenantId, Key = "orders-errors", Name = ruleName, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = TenantId, RuleId = rule.Id, DedupKey = "d", SubjectKey = subject, Severity = AlertSeverity.Critical, State = state,
                BreachStartedAt = at.AddMinutes(-3), FiredAt = at, ResolvedAt = state == AlertState.Resolved ? at.AddMinutes(20) : null, Evidence = evidence ?? [],
            };
            var transition = new AlertTransition { TenantId = TenantId, AlertId = alert.Id, ToState = state, At = alert.ResolvedAt ?? at, Value = value, Reason = reason };
            return (rule, alert, transition);
        }

        /// <summary>What the producer adapter sends as <c>data</c>: today's payload minus its id (waves §5.2).</summary>
        private static JsonElement Data(string payload)
        {
            var node = JsonNode.Parse(payload)!.AsObject();
            node.Remove("id");
            return JsonSerializer.SerializeToElement(node);
        }

        private static Task<string> RenderAsync(string template, string payload, IReadOnlyDictionary<string, string>? links = null)
            => TemplateRenderer.RenderAsync(template, new RenderModel(AliasVocabulary.FiringType, 1, "Alert firing", NotificationPriority.High, Data(payload),
                new Dictionary<string, string>(), links ?? new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        /// <summary>The legacy payload as the template writes it before M46-0 gives it the delivery id: the same bytes, with <c>"id":null</c>.</summary>
        private static string WithoutId(string payload, Guid id) => payload.Replace($"\"id\":\"{id}\",\"type\"", "\"id\":null,\"type\"", StringComparison.Ordinal);

        public static TheoryData<string> Events => [NotificationPlan.Firing, NotificationPlan.Resolved];

        [Theory]
        [MemberData(nameof(Events))]
        public async Task The_webhook_body_is_the_legacy_payload_byte_for_byte_but_its_id(string @event)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Firing ? AlertState.Firing : AlertState.Resolved,
                evidence: new() { ["slo"] = "orders-availability", ["sloName"] = "Orders <available> & \"fast\"" });
            var id = Guid.CreateVersion7();
            var legacy = NotificationPlan.Payload(id, @event, "0193f0aa-0000-7000-8000-0000000000f1", rule, alert, transition, null);

            var body = await RenderAsync(DefaultTemplates.Webhook, legacy);

            Assert.Equal(WithoutId(legacy, id), body);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0.0)]
        [InlineData(1.5)]
        [InlineData(0.30000000000000004)]
        [InlineData(-42.0)]
        [InlineData(123456789.0)]
        public async Task Values_are_written_as_legacy_writes_them(double? value)
        {
            var (rule, alert, transition) = Alert(value: value);
            var id = Guid.CreateVersion7();
            var legacy = NotificationPlan.Payload(id, NotificationPlan.Firing, "t", rule, alert, transition, null);

            Assert.Equal(WithoutId(legacy, id), await RenderAsync(DefaultTemplates.Webhook, legacy));
        }

        [Fact]
        public async Task Text_a_receiver_could_misread_is_escaped_as_legacy_escapes_it()
        {
            var subject = "service:<script>&\"quote\"+'" + (char)0xE9 + "'" + (char)0x2028 + "@production";
            var (rule, alert, transition) = Alert(subject: subject, ruleName: "Errors\r\nBcc: x", reason: @"a	b\c");
            var id = Guid.CreateVersion7();
            var legacy = NotificationPlan.Payload(id, NotificationPlan.Firing, "t", rule, alert, transition, null);

            Assert.Equal(WithoutId(legacy, id), await RenderAsync(DefaultTemplates.Webhook, legacy));
        }

        [Fact]
        public void Every_template_passes_the_sandbox_check()
        {
            foreach (var (channel, body) in DefaultTemplates.ByChannel)
            {
                Assert.True(TemplateRenderer.Check(body) is null, $"{channel}: {TemplateRenderer.Check(body)}");
            }
        }

        [Fact]
        public async Task An_escalated_body_has_the_escalated_type_its_step_and_the_deliverys_id()
        {
            var (rule, alert, transition) = Alert();
            var id = Guid.CreateVersion7();
            // Legacy's escalated payload: the firing transition, type alert.escalated, the policy and the step.
            var legacy = NotificationPlan.Payload(id, NotificationPlan.Escalated, "t", rule, alert, transition, ("paging", 2));
            var firing = NotificationPlan.Payload(Guid.CreateVersion7(), NotificationPlan.Firing, "t", rule, alert, transition, null);
            var model = new RenderModel(AliasVocabulary.FiringType, 1, "Alert firing", NotificationPriority.High, Data(firing),
                new Dictionary<string, string>(), new Dictionary<string, string>(), Delivery: new RenderDelivery(id), Escalation: new RenderEscalation(2, "paging"));

            Assert.Equal(legacy, await TemplateRenderer.RenderAsync(DefaultTemplates.Webhook, model, TestContext.Current.CancellationToken));
            var chat = JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await TemplateRenderer.RenderAsync(DefaultTemplates.Slack, model, TestContext.Current.CancellationToken), "x", null, null))!;
            Assert.Equal(JsonDocument.Parse(ChannelFormatter.SlackBody(NotificationView.Parse(legacy), null)).RootElement.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString(), chat.Title);
        }

        [Fact]
        public async Task The_webhook_body_carries_the_deliverys_id()
        {
            var (rule, alert, transition) = Alert();
            var id = Guid.CreateVersion7();
            var legacy = NotificationPlan.Payload(id, NotificationPlan.Firing, "t", rule, alert, transition, null);
            var model = new RenderModel(AliasVocabulary.FiringType, 1, "Alert firing", NotificationPriority.High, Data(legacy),
                new Dictionary<string, string>(), new Dictionary<string, string>(), Delivery: new RenderDelivery(id));

            Assert.Equal(legacy, await TemplateRenderer.RenderAsync(DefaultTemplates.Webhook, model, TestContext.Current.CancellationToken));
        }

        private static async Task<ChatContent> ChatAsync(string template, string payload)
            => JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await RenderAsync(template, payload), "Alert firing", null, null))!;

        public static TheoryData<string, double?, string?> Cases => new()
        {
            { NotificationPlan.Firing, 0.25, null },
            { NotificationPlan.Resolved, null, "orders-availability" },
            { NotificationPlan.Firing, 1234567.0, null },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Slack_says_what_the_formatter_says(string @event, double? value, string? slo)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Firing ? AlertState.Firing : AlertState.Resolved, value,
                evidence: slo is null ? null : new() { ["slo"] = slo });
            var payload = NotificationPlan.Payload(Guid.CreateVersion7(), @event, "t", rule, alert, transition, null);
            var legacy = JsonDocument.Parse(ChannelFormatter.SlackBody(NotificationView.Parse(payload), null)).RootElement.GetProperty("blocks");

            var chat = await ChatAsync(DefaultTemplates.Slack, payload);

            Assert.Equal(legacy[0].GetProperty("text").GetProperty("text").GetString(), chat.Title);
            // Legacy escapes for mrkdwn itself; the Slack provider escapes the plain text the same way.
            Assert.Equal(legacy[1].GetProperty("text").GetProperty("text").GetString(), chat.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Teams_says_what_the_formatter_says(string @event, double? value, string? slo)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Firing ? AlertState.Firing : AlertState.Resolved, value,
                evidence: slo is null ? null : new() { ["slo"] = slo });
            var payload = NotificationPlan.Payload(Guid.CreateVersion7(), @event, "t", rule, alert, transition, null);
            var card = JsonDocument.Parse(ChannelFormatter.TeamsBody(NotificationView.Parse(payload), null)).RootElement
                .GetProperty("attachments")[0].GetProperty("content").GetProperty("body");

            var chat = await ChatAsync(DefaultTemplates.Teams, payload);

            Assert.Equal(card[0].GetProperty("text").GetString(), chat.Title);
            Assert.Equal(card[1].GetProperty("text").GetString(), chat.Text);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Email_says_what_the_formatter_says(string @event, double? value, string? slo)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Firing ? AlertState.Firing : AlertState.Resolved, value,
                evidence: slo is null ? null : new() { ["slo"] = slo });
            var payload = NotificationPlan.Payload(Guid.CreateVersion7(), @event, "t", rule, alert, transition, null);
            var (subject, body) = ChannelFormatter.Email(NotificationView.Parse(payload), null);

            var email = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email(await RenderAsync(DefaultTemplates.Email, payload), "Alert firing"))!;

            Assert.Equal(subject, email.Subject);
            Assert.Equal(body.TrimEnd().ReplaceLineEndings("\n"), email.Text);
        }

        [Fact]
        public async Task Email_ends_with_the_alerts_link_as_the_formatter_does_with_a_console()
        {
            var (rule, alert, transition) = Alert();
            var payload = NotificationPlan.Payload(Guid.CreateVersion7(), NotificationPlan.Firing, "t", rule, alert, transition, null);
            var (_, body) = ChannelFormatter.Email(NotificationView.Parse(payload), "https://console.example/");
            var link = $"https://console.example/alerts/{alert.Id}";

            var rendered = await RenderAsync(DefaultTemplates.Email, payload, new Dictionary<string, string> { ["self"] = link });
            var email = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email(rendered, "Alert firing"))!;

            Assert.Equal(body.TrimEnd().ReplaceLineEndings("\n"), email.Text);
        }

        [Fact]
        public async Task A_rule_name_cant_add_a_mail_header_or_a_second_chat_title()
        {
            var (rule, alert, transition) = Alert(ruleName: "Errors\r\nBcc: evil@example.com");
            var payload = NotificationPlan.Payload(Guid.CreateVersion7(), NotificationPlan.Firing, "t", rule, alert, transition, null);

            var email = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email(await RenderAsync(DefaultTemplates.Email, payload), "x"))!;
            var chat = await ChatAsync(DefaultTemplates.Slack, payload);

            Assert.Equal("[Juice] Firing: Errors  Bcc: evil@example.com", email.Subject);
            Assert.Equal("Firing: Errors  Bcc: evil@example.com", chat.Title);
        }

        [Fact]
        public void The_declaration_has_a_template_per_legacy_channel()
        {
            var templates = DefaultTemplates.Declaration();
            Assert.Equal(["webhook", "slack", "teams", "email"], templates.EnumerateObject().Select(p => p.Name));
        }
    }
}
