using System.Text.Json;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Tests
{
    /// <summary>The rules of Phase 6 that need no database: what a silence matches, what happens to what it held, how an escalation moves, and what a channel writes.</summary>
    public class NotificationLogicTests
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);
        private static readonly AlertContext Orders = new("errors", "service:shop/orders@production", AlertSeverity.Warning);

        private static Silence Silence(string[]? rules = null, string? service = null, string? environment = null, string? host = null, AlertSeverity? maximum = null, bool all = false,
            bool weekly = false, TimeSpan? length = null, DateTimeOffset? cancelled = null) => new()
            {
                Comment = "x", CreatedBy = "x", RuleKeys = rules ?? [], Service = service, Environment = environment, Host = host, MaximumSeverity = maximum, AllAlerts = all,
                StartsAt = Start, EndsAt = Start + (length ?? TimeSpan.FromHours(2)), RepeatWeekly = weekly, CancelledAt = cancelled,
            };

        [Theory]
        [InlineData("service:shop/orders@production", "shop/orders", "production", null)]
        [InlineData("service:orders@staging", "orders", "staging", null)]
        [InlineData("service:a@b@production", "a@b", "production", null)]
        [InlineData("host:db-1", null, null, "db-1")]
        public void A_subject_is_read_from_its_key(string key, string? service, string? environment, string? host)
        {
            var context = new AlertContext("r", key, AlertSeverity.Info);
            Assert.Equal((service, environment, host), (context.Service, context.Environment, context.Host));
        }

        [Fact]
        public void A_one_off_silence_is_active_only_between_its_times()
        {
            var silence = Silence();
            Assert.False(SilenceMatcher.IsActive(silence, Start.AddSeconds(-1)));
            Assert.True(SilenceMatcher.IsActive(silence, Start));
            Assert.True(SilenceMatcher.IsActive(silence, Start.AddHours(2).AddSeconds(-1)));
            Assert.False(SilenceMatcher.IsActive(silence, Start.AddHours(2)));
            Assert.False(SilenceMatcher.IsActive(Silence(cancelled: Start), Start.AddHours(1)));
        }

        [Fact]
        public void A_weekly_window_comes_back_each_week_until_cancelled()
        {
            var window = Silence(weekly: true);
            Assert.True(SilenceMatcher.IsActive(window, Start.AddHours(1)));
            Assert.False(SilenceMatcher.IsActive(window, Start.AddDays(3)));
            Assert.True(SilenceMatcher.IsActive(window, Start.AddDays(7).AddHours(1)));
            Assert.False(SilenceMatcher.IsActive(window, Start.AddDays(7).AddHours(2)));
            Assert.True(SilenceMatcher.IsActive(window, Start.AddDays(70).AddMinutes(1)));
            Assert.False(SilenceMatcher.IsActive(Silence(weekly: true, cancelled: Start), Start.AddDays(7).AddHours(1)));
            Assert.False(SilenceMatcher.IsActive(window, Start.AddDays(-7).AddHours(1)));
        }

        public static TheoryData<string, bool> Scopes => new()
        {
            { "rule", true }, { "other-rule", false }, { "full-service", true }, { "short-service", true }, { "other-service", false }, { "environment", true }, { "other-environment", false },
            { "host", false }, { "at-severity", true }, { "below-severity", false }, { "all", true }, { "none", false }, { "two-fields-one-wrong", false },
        };

        [Theory]
        [MemberData(nameof(Scopes))]
        public void A_silence_matches_only_its_scope(string scope, bool expected)
        {
            var silence = scope switch
            {
                "rule" => Silence(rules: ["errors"]),
                "other-rule" => Silence(rules: ["latency"]),
                "full-service" => Silence(service: "shop/orders"),
                "short-service" => Silence(service: "orders"),
                "other-service" => Silence(service: "shop/billing"),
                "environment" => Silence(environment: "production"),
                "other-environment" => Silence(environment: "staging"),
                "host" => Silence(host: "db-1"),
                "at-severity" => Silence(maximum: AlertSeverity.Warning),
                "below-severity" => Silence(maximum: AlertSeverity.Info),
                "all" => Silence(all: true),
                "none" => Silence(),
                _ => Silence(service: "shop/orders", environment: "staging"),
            };
            Assert.Equal(expected, SilenceMatcher.Matches(silence, Orders));
        }

        [Fact]
        public void A_silence_on_warnings_lets_a_critical_alert_through()
            => Assert.False(SilenceMatcher.Matches(Silence(maximum: AlertSeverity.Warning), Orders with { Severity = AlertSeverity.Critical }));

        [Theory]
        [InlineData(WebhookOutbox.Firing, AlertState.Firing, false, HeldOutcome.Send)]
        [InlineData(WebhookOutbox.Firing, AlertState.Resolved, false, HeldOutcome.Drop)]
        [InlineData(WebhookOutbox.Escalated, AlertState.Firing, true, HeldOutcome.Send)]
        [InlineData(WebhookOutbox.Escalated, AlertState.Resolved, true, HeldOutcome.Drop)]
        [InlineData(WebhookOutbox.Resolved, AlertState.Resolved, true, HeldOutcome.Send)]
        [InlineData(WebhookOutbox.Resolved, AlertState.Resolved, false, HeldOutcome.Drop)]
        public void What_a_silence_held_is_sent_only_if_it_still_matters(string @event, AlertState state, bool told, HeldOutcome expected)
            => Assert.Equal(expected, HeldNotification.Decide(@event, state, told));

        [Theory]
        [InlineData(AlertState.Resolved, false, true, true, false, true, EscalationAction.Stop, "resolved")]
        [InlineData(AlertState.Firing, true, true, true, false, true, EscalationAction.Stop, "acknowledged")]
        [InlineData(AlertState.Firing, true, false, true, false, true, EscalationAction.Notify, null)]
        [InlineData(AlertState.Firing, false, true, false, false, true, EscalationAction.Stop, "policy_disabled")]
        [InlineData(AlertState.Firing, false, true, true, false, false, EscalationAction.Wait, null)]
        [InlineData(AlertState.Firing, false, true, true, true, true, EscalationAction.Hold, "silenced")]
        [InlineData(AlertState.Firing, false, true, true, false, true, EscalationAction.Notify, null)]
        // A stop comes before anything else: being silenced or not yet due doesn't keep a finished escalation alive.
        [InlineData(AlertState.Resolved, false, true, true, true, false, EscalationAction.Stop, "resolved")]
        public void An_escalation_moves_according_to_its_policy(AlertState state, bool acknowledged, bool stopOnAcknowledge, bool enabled, bool silenced, bool due, EscalationAction action, string? reason)
        {
            var next = EscalationDecision.Next(state, acknowledged, stopOnAcknowledge, enabled, silenced, due ? Start : Start.AddMinutes(1), Start);
            Assert.Equal((action, reason), next);
        }

        public static TheoryData<IncidentState, IncidentState, bool> Moves => new()
        {
            { IncidentState.Open, IncidentState.Investigating, true }, { IncidentState.Open, IncidentState.Mitigated, true }, { IncidentState.Open, IncidentState.Resolved, true },
            { IncidentState.Investigating, IncidentState.Mitigated, true }, { IncidentState.Investigating, IncidentState.Resolved, true }, { IncidentState.Mitigated, IncidentState.Investigating, true },
            { IncidentState.Mitigated, IncidentState.Resolved, true }, { IncidentState.Resolved, IncidentState.Investigating, true },
            { IncidentState.Open, IncidentState.Open, false }, { IncidentState.Investigating, IncidentState.Open, false }, { IncidentState.Mitigated, IncidentState.Open, false },
            { IncidentState.Resolved, IncidentState.Open, false }, { IncidentState.Resolved, IncidentState.Mitigated, false }, { IncidentState.Resolved, IncidentState.Resolved, false },
        };

        [Theory]
        [MemberData(nameof(Moves))]
        public void An_incident_moves_only_along_its_lifecycle(IncidentState from, IncidentState to, bool allowed)
            => Assert.Equal(allowed, IncidentLifecycle.CanMove(from, to));

        [Theory]
        [InlineData("env:JUICE_WEBHOOK_OPS", true)]
        [InlineData("env:JUICE_WEBHOOK_", false)]
        [InlineData("env:DB_PASSWORD", false)]
        [InlineData("file:/run/juice/ops", true)]
        [InlineData("file:/run/juice/../../etc/passwd", false)]
        [InlineData("file:/etc/passwd", false)]
        [InlineData("file:/run/juice/deep/ops", false)]
        [InlineData("file:relative", false)]
        [InlineData("hunter2", false)]
        public void A_reference_from_the_api_can_only_name_what_the_deployment_set_aside(string reference, bool allowed)
        {
            var directory = OperatingSystem.IsWindows() ? "D:\\run\\juice" : "/run/juice";
            var path = OperatingSystem.IsWindows() ? reference.Replace("file:/run/juice", "file:D:\\run\\juice").Replace("/", "\\").Replace("file:\\etc", "file:D:\\etc") : reference;
            Assert.Equal(allowed, SecretReference.IsAllowedFromApi(path, new WebhookOptions { ApiSecretDirectory = directory }));
        }

        [Fact]
        public void Without_a_folder_the_api_cant_name_files_at_all()
            => Assert.False(SecretReference.IsAllowedFromApi("file:/run/juice/ops", new WebhookOptions()));

        [Theory]
        [InlineData(ChannelKind.Slack, "https://hooks.slack.com/services/T/B/x", true)]
        [InlineData(ChannelKind.Slack, "http://hooks.slack.com/services/T/B/x", false)]
        [InlineData(ChannelKind.Slack, "https://hooks.slack.com.evil.example/x", false)]
        [InlineData(ChannelKind.Slack, "https://acme.webhook.office.com/x", false)]
        [InlineData(ChannelKind.Teams, "https://acme.webhook.office.com/x", true)]
        [InlineData(ChannelKind.Teams, "https://prod-1.westus.logic.azure.com/x", true)]
        [InlineData(ChannelKind.Teams, "https://webhook.office.com.evil.example/x", false)]
        [InlineData(ChannelKind.Teams, "https://evilwebhook.office.com/x", false)]
        [InlineData(ChannelKind.Slack, "https://bot:pw@hooks.slack.com/x", false)]
        [InlineData(ChannelKind.Slack, "not a url", false)]
        [InlineData(ChannelKind.Slack, "https://chat.internal.example/hook", true)]
        public void A_chat_channel_posts_only_to_its_own_service(ChannelKind kind, string url, bool allowed)
            => Assert.Equal(allowed, ChatDestination.IsAllowed(kind, url, ["chat.internal.example"]));

        private static string Payload(string subject = "service:shop/orders@production", string type = WebhookOutbox.Firing, string ruleName = "Orders errors", double? value = 0.25) => JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(), type, tenant = "t", escalation = (object?)null,
            alert = new { id = Guid.NewGuid(), rule = "orders-errors", ruleName, severity = "Critical", state = "Firing", subject, value, reason = "breach" },
        }, JsonSerializerOptions.Web);

        [Fact]
        public void A_service_name_cant_page_a_slack_channel_or_add_a_link()
        {
            var body = ChannelFormatter.SlackBody(NotificationView.Parse(Payload("service:<!channel> <http://evil.example|x>@production")), null);
            var text = JsonDocument.Parse(body).RootElement.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString()!;
            Assert.DoesNotContain("<!channel>", text);
            Assert.DoesNotContain("<http://evil.example", text);
            Assert.Contains("&lt;!channel&gt;", text);
        }

        [Fact]
        public void A_service_name_cant_add_a_link_in_teams()
        {
            var body = ChannelFormatter.TeamsBody(NotificationView.Parse(Payload("service:[click](http://evil.example)@production")), null);
            Assert.DoesNotContain("[click]", body);
            var content = JsonDocument.Parse(body).RootElement.GetProperty("attachments")[0];
            Assert.Equal("application/vnd.microsoft.card.adaptive", content.GetProperty("contentType").GetString());
            Assert.Equal("AdaptiveCard", content.GetProperty("content").GetProperty("type").GetString());
        }

        [Fact]
        public void A_rule_name_cant_add_a_mail_header()
        {
            var (subject, body) = ChannelFormatter.Email(NotificationView.Parse(Payload(ruleName: "Errors\r\nBcc: evil@example.com")), null);
            Assert.DoesNotContain('\n', subject);
            Assert.DoesNotContain('\r', subject);
            Assert.Contains("service:shop/orders@production: 0.25", body);
        }

        [Fact]
        public void A_notification_says_no_data_instead_of_zero()
            => Assert.Contains("no data", ChannelFormatter.Email(NotificationView.Parse(Payload(value: null)), null).Body);

        [Fact]
        public void A_link_appears_only_when_the_console_address_is_known()
        {
            var view = NotificationView.Parse(Payload());
            Assert.DoesNotContain("/alerts/", ChannelFormatter.SlackBody(view, null));
            Assert.Contains($"https://console.example/alerts/{view.Alerts[0].AlertId}", ChannelFormatter.SlackBody(view, "https://console.example/"));
            Assert.Contains($"https://console.example/alerts/{view.Alerts[0].AlertId}", ChannelFormatter.TeamsBody(view, "https://console.example"));
            Assert.Contains($"https://console.example/alerts/{view.Alerts[0].AlertId}", ChannelFormatter.Email(view, "https://console.example").Body);
        }

        [Fact]
        public void A_group_counts_everything_and_lists_a_bounded_few()
        {
            var id = Guid.NewGuid();
            var payload = NotificationView.Group(id, WebhookOutbox.Firing, Start, Enumerable.Range(0, 15).Select(i => Payload($"service:s{i}@production")));
            var view = NotificationView.Parse(payload);
            Assert.Equal((WebhookOutbox.Firing, 15), (view.Event, view.Alerts.Count));
            Assert.Equal("Firing: 15 alerts", ChannelFormatter.Title(view));
            var body = ChannelFormatter.SlackBody(view, null);
            Assert.Contains("s9@", body);
            Assert.DoesNotContain("s10@", body);
            Assert.Contains("and 5 more", body);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal((id, NotificationView.GroupType, 15), (document.RootElement.GetProperty("id").GetGuid(), document.RootElement.GetProperty("type").GetString(), document.RootElement.GetProperty("count").GetInt32()));
        }
    }
}
