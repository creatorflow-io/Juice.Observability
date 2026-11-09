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
        [InlineData(NotificationPlan.Firing, AlertState.Firing, false, false, true, HeldOutcome.Send)]
        [InlineData(NotificationPlan.Firing, AlertState.Resolved, false, false, true, HeldOutcome.Drop)]
        [InlineData(NotificationPlan.Escalated, AlertState.Firing, true, false, true, HeldOutcome.Send)]
        [InlineData(NotificationPlan.Escalated, AlertState.Resolved, true, false, true, HeldOutcome.Drop)]
        [InlineData(NotificationPlan.Resolved, AlertState.Resolved, true, false, true, HeldOutcome.Send)]
        [InlineData(NotificationPlan.Resolved, AlertState.Resolved, false, false, true, HeldOutcome.Drop)]
        // Acknowledged while held: someone is on it, so the firing is noise. A step follows its policy.
        [InlineData(NotificationPlan.Firing, AlertState.Firing, false, true, true, HeldOutcome.Drop)]
        [InlineData(NotificationPlan.Firing, AlertState.Firing, false, true, false, HeldOutcome.Drop)]
        [InlineData(NotificationPlan.Escalated, AlertState.Firing, true, true, true, HeldOutcome.Drop)]
        [InlineData(NotificationPlan.Escalated, AlertState.Firing, true, true, false, HeldOutcome.Send)]
        // A resolve still reaches whoever heard the firing, acknowledged or not.
        [InlineData(NotificationPlan.Resolved, AlertState.Resolved, true, true, true, HeldOutcome.Send)]
        public void What_a_silence_held_is_sent_only_if_it_still_matters(string @event, AlertState state, bool told, bool acknowledged, bool stopsOnAcknowledge, HeldOutcome expected)
            => Assert.Equal(expected, HeldNotification.Decide(@event, state, told, acknowledged, stopsOnAcknowledge));

        public static TheoryData<int, int, int, int> Spacing => new()
        {
            // Steps at 5, 15 and 30 minutes after firing. Sent on time: the schedule stands.
            { 1, 5, 15, 15 },
            { 2, 15, 30, 30 },
            // Sent late, after an hour's silence or a host down: the next step waits its gap from then, not all at once.
            { 1, 60, 15, 70 },
            { 2, 70, 30, 85 },
            // A little late: still the later of the two.
            { 1, 12, 15, 22 },
        };

        [Theory]
        [MemberData(nameof(Spacing))]
        public void Steps_keep_their_spacing_when_one_goes_out_late(int next, int sentMinutes, int scheduled, int dueMinutes)
        {
            List<EscalationStep> steps = [new() { AfterMinutes = 5 }, new() { AfterMinutes = 15 }, new() { AfterMinutes = 30 }];
            Assert.Equal(scheduled, steps[next].AfterMinutes);
            Assert.Equal(Start.AddMinutes(dueMinutes), EscalationDecision.NextAt(steps, next, Start, Start.AddMinutes(sentMinutes)));
        }

        [Fact]
        public void The_first_step_is_due_by_its_time_after_firing()
        {
            List<EscalationStep> steps = [new() { AfterMinutes = 5 }];
            Assert.Equal(Start.AddMinutes(5), EscalationDecision.NextAt(steps, 0, Start, Start.AddMinutes(60)));
        }

        [Theory]
        [InlineData("{\"type\":\"alert.escalated\",\"escalation\":{\"policy\":\"pager\",\"step\":1}}", "pager")]
        [InlineData("{\"type\":\"alert.firing\",\"escalation\":null}", null)]
        [InlineData("not json", null)]
        public void A_held_step_names_its_policy(string payload, string? expected)
            => Assert.Equal(expected, NotificationWorker.PolicyKey(payload));

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

        private static string Payload(string subject = "service:shop/orders@production", string type = NotificationPlan.Firing, string ruleName = "Orders errors", double? value = 0.25) => JsonSerializer.Serialize(new
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

        private static string RequestPayload(string type, string hostname = "web-01", string reason = "disk full", string? comment = null)
            => Juice.Observability.Agents.ActionRequestNotices.Payload(Guid.NewGuid(), type, "local", new ActionRequest
            {
                TenantId = Guid.NewGuid(), AgentId = Guid.NewGuid(), Action = "service.restart", Parameters = """{"service":"orders"}""", Risk = "High",
                Policy = "SecondPerson", Reason = reason, RequestedBy = "olivia", RequestedAt = DateTimeOffset.UtcNow,
                ExpiresAt = new DateTimeOffset(2026, 11, 9, 10, 30, 0, TimeSpan.Zero), State = ActionRequestState.Rejected, DecidedBy = comment is null ? null : "adam", Comment = comment,
            }, hostname, DateTimeOffset.UtcNow);

        [Fact]
        public void An_alert_payload_isnt_read_as_a_request() => Assert.Null(RequestNotice.TryParse(Payload()));

        [Fact]
        public void A_waiting_request_says_who_asked_why_and_until_when_with_a_link_to_approvals()
        {
            var notice = RequestNotice.TryParse(RequestPayload(ActionRequestEvents.Created))!;
            var (subject, body) = ChannelFormatter.Email(notice, "https://console.example/");
            Assert.Equal("[Juice] Approval needed: service.restart on web-01", subject);
            Assert.Contains("Requested by: olivia: disk full", body);
            Assert.Contains("an admin other than the requester", body);
            Assert.Contains("""{"service":"orders"}""", body);
            Assert.Contains("Expires: 2026-11-09 10:30 UTC", body);
            Assert.Contains("https://console.example/approvals", body);
            Assert.Contains("Open Approvals", ChannelFormatter.TeamsBody(notice, "https://console.example"));
        }

        [Fact]
        public void A_decided_request_says_who_decided_and_why()
            => Assert.Contains("Decided by: adam: not now", ChannelFormatter.Email(RequestNotice.TryParse(RequestPayload(ActionRequestEvents.Rejected, comment: "not now"))!, null).Body);

        [Fact]
        public void A_hostname_or_reason_cant_page_a_slack_channel_add_a_link_or_a_mail_header()
        {
            var notice = RequestNotice.TryParse(RequestPayload(ActionRequestEvents.Created, hostname: "<!channel>", reason: "[click](http://evil.example) <http://evil.example|x>\r\nBcc: x@example.com"))!;
            var slack = JsonDocument.Parse(ChannelFormatter.SlackBody(notice, null)).RootElement;
            var blocks = slack.GetProperty("blocks");
            foreach (var text in new[] { slack.GetProperty("text").GetString()!, blocks[0].GetProperty("text").GetProperty("text").GetString()!, blocks[1].GetProperty("text").GetProperty("text").GetString()! })
            {
                Assert.DoesNotContain("<!channel>", text);
                Assert.DoesNotContain("<http://evil.example", text);
            }
            Assert.DoesNotContain("[click]", ChannelFormatter.TeamsBody(notice, null));
            var (subject, body) = ChannelFormatter.Email(notice, null);
            Assert.DoesNotContain('\n', subject);
            Assert.DoesNotContain("\nBcc:", body);
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
            var payload = NotificationView.Group(id, NotificationPlan.Firing, Start, Enumerable.Range(0, 15).Select(i => Payload($"service:s{i}@production")));
            var view = NotificationView.Parse(payload);
            Assert.Equal((NotificationPlan.Firing, 15), (view.Event, view.Alerts.Count));
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
