using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Shadow;
using Juice.Observability.Webhooks;
using NewStatus = Juice.Notifications.DeliveryStatus;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The shadow comparison without a database (M47, waves §6.1): what legacy sent against what the new pipeline would have sent, endpoint by
    /// endpoint. Matching bodies (modulo ids and times) and times find nothing; each explained rule names its difference (p5-d7); anything else is
    /// unexplained.
    /// </summary>
    [Trait("Milestone", "M46c")]
    public class ShadowCompareTests
    {
        private static readonly Guid TenantId = Guid.Parse("0193f0aa-0000-7000-8000-0000000000f1");
        private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 30, 15, 123, TimeSpan.Zero);
        private static readonly ShadowCompareOptions Options = new(TimeSpan.FromSeconds(60));

        private static (Rule Rule, Alert Alert, AlertTransition Transition) Alert(string ruleName = "Orders errors", string subject = "service:shop/orders@production")
        {
            var rule = new Rule { TenantId = TenantId, Key = "orders-errors", Name = ruleName, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = TenantId, RuleId = rule.Id, DedupKey = "d", SubjectKey = subject, Severity = AlertSeverity.Critical, State = AlertState.Firing,
                BreachStartedAt = At.AddMinutes(-3), FiredAt = At,
            };
            return (rule, alert, new AlertTransition { TenantId = TenantId, AlertId = alert.Id, ToState = AlertState.Firing, At = At, Value = 0.25, Reason = "error rate 25%" });
        }

        private static string Payload((Rule Rule, Alert Alert, AlertTransition Transition) a, string @event = NotificationPlan.Firing, (string, int)? escalation = null)
            => NotificationPlan.Payload(Guid.CreateVersion7(), @event, "t", a.Rule, a.Alert, a.Transition, escalation);

        /// <summary>The body the new pipeline renders for a webhook: legacy's but its id, the delivery's (p5-d8).</summary>
        private static string Webhook(string payload)
        {
            var node = JsonNode.Parse(payload)!.AsObject();
            node["id"] = Guid.CreateVersion7();
            return JsonSerializer.Serialize(new { ContentType = "application/json", Body = node.ToJsonString(JsonSerializerOptions.Web) });
        }

        private static ShadowUnit Unit(Guid alertId, string @event = NotificationPlan.Firing, bool resolved = false, bool held = false)
            => new(TenantId, Guid.CreateVersion7(), @event == NotificationPlan.Escalated ? 1 : 0, alertId, @event, resolved, held);

        private static LegacySent Legacy(string payload, string key = "ops", ChannelKind kind = ChannelKind.Webhook, DeliveryStatus status = DeliveryStatus.Delivered,
            string? error = null, int group = 0, int recipients = 0, DateTimeOffset? sentAt = null)
            => new(key, kind, group, payload, status, error, At, sentAt ?? At.AddSeconds(1), recipients);

        private static NewSent New(string content, string key = "ops", string channel = "webhook", NewStatus status = NewStatus.WouldSend, DateTimeOffset? at = null)
            => new(key, channel, content, status, at ?? At.AddSeconds(3));

        private static ShadowFinding Single(IReadOnlyList<ShadowFinding> findings, string kind, string? rule)
        {
            var finding = Assert.Single(findings);
            Assert.Equal((kind, rule), (finding.Kind, finding.Rule));
            return finding;
        }

        [Fact]
        public void The_same_body_but_its_id_at_about_the_same_time_is_no_difference()
        {
            var a = Alert();
            var payload = Payload(a);
            Assert.Empty(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload)], [New(Webhook(payload))], Options));
        }

        [Fact]
        public void An_escalation_step_is_compared_with_its_policy_and_step()
        {
            var a = Alert();
            var payload = Payload(a, NotificationPlan.Escalated, ("paging", 1));
            Assert.Empty(ShadowCompare.Compare(Unit(a.Alert.Id, NotificationPlan.Escalated), [Legacy(payload)], [New(Webhook(payload))], Options));
            var other = Payload(a, NotificationPlan.Escalated, ("paging", 2));
            var finding = Single(ShadowCompare.Compare(Unit(a.Alert.Id, NotificationPlan.Escalated), [Legacy(payload)], [New(Webhook(other))], Options),
                ShadowDifferenceKinds.Body, null);
            Assert.Contains("$.escalation.step", finding.Detail);
        }

        /// <summary>The unexplained one: a body that differs, which no rule names.</summary>
        [Fact]
        public void A_body_that_differs_is_unexplained_and_says_where()
        {
            var a = Alert();
            var renamed = Alert(ruleName: "Orders errors (renamed)");
            var finding = Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(Payload(a))], [New(Webhook(Payload((renamed.Rule, a.Alert, a.Transition))))], Options),
                ShadowDifferenceKinds.Body, null);
            Assert.Contains("$.alert.ruleName", finding.Detail);
        }

        [Fact]
        public void A_notification_only_one_side_sent_is_unexplained_without_a_rule()
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload)], [], Options), ShadowDifferenceKinds.MissingInNew, null);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [], [New(Webhook(payload))], Options), ShadowDifferenceKinds.MissingInLegacy, null);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload)], [New(Webhook(payload), status: NewStatus.Failed)], Options),
                ShadowDifferenceKinds.MissingInNew, null);
        }

        [Fact]
        public void A_receivers_refusal_is_still_a_send()
        {
            var a = Alert();
            var payload = Payload(a);
            Assert.Empty(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, status: DeliveryStatus.Failed, error: "status_500")], [New(Webhook(payload))], Options));
        }

        [Fact]
        public void Times_further_apart_than_the_tolerance_are_unexplained()
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload)], [New(Webhook(payload), at: At.AddMinutes(3))], Options), ShadowDifferenceKinds.Timing, null);
        }

        // ─── The explained rules ───────────────────────────────────────────────

        /// <summary>Q2: a resolve cancels a firing still queued; legacy sent it.</summary>
        [Fact]
        public void A_firing_a_resolve_cancelled_is_explained()
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id, resolved: true), [Legacy(payload, group: 30)], [New(Webhook(payload), status: NewStatus.Cancelled)], Options),
                ShadowDifferenceKinds.MissingInNew, ShadowRules.ResolveCancelsFiring);
            // Not resolved since: nothing explains it.
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, group: 30)], [New(Webhook(payload), status: NewStatus.Cancelled)], Options),
                ShadowDifferenceKinds.MissingInNew, null);
        }

        /// <summary>M44 Q7: legacy's destination policy refused it; a dry run stops before the provider's check.</summary>
        [Theory]
        [InlineData("private_address_not_allowlisted:10.20.0.5")]
        [InlineData("blocked_address:169.254.169.254")]
        [InlineData("https_required")]
        public void What_legacys_policy_blocked_is_explained(string error)
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, status: DeliveryStatus.Failed, error: error)], [New(Webhook(payload))], Options),
                ShadowDifferenceKinds.MissingInLegacy, ShadowRules.BlockedByPolicyInLegacy);
        }

        /// <summary>Q4: after a silence, the new pipeline decides "told" per alert, legacy per endpoint.</summary>
        [Fact]
        public void After_a_silence_told_per_alert_is_explained_either_way()
        {
            var a = Alert();
            var payload = Payload(a, NotificationPlan.Resolved);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id, NotificationPlan.Resolved, held: true), [Legacy(payload)], [], Options),
                ShadowDifferenceKinds.MissingInNew, ShadowRules.ToldPerAlert);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id, NotificationPlan.Resolved, held: true), [Legacy(payload, status: DeliveryStatus.Dropped)], [New(Webhook(payload))], Options),
                ShadowDifferenceKinds.MissingInLegacy, ShadowRules.ToldPerAlert);
        }

        /// <summary>Q13: an email endpoint's recipients each get their own message in the new pipeline; each says what legacy's said.</summary>
        [Fact]
        public void Email_fan_out_is_explained_and_each_message_is_still_compared()
        {
            var a = Alert();
            var payload = Payload(a);
            var (subject, body) = ChannelFormatter.Email(NotificationView.Parse(payload), null);
            var email = JsonSerializer.Serialize(new { Subject = subject, Text = body.TrimEnd().ReplaceLineEndings("\n"), Html = "" });
            var three = Enumerable.Range(0, 3).Select(_ => New(email, channel: "email")).ToList();

            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, kind: ChannelKind.Email, recipients: 3)], three, Options),
                ShadowDifferenceKinds.Shape, ShadowRules.EmailFanOut);
            // Not as many messages as recipients: unexplained.
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, kind: ChannelKind.Email, recipients: 4)], three, Options),
                ShadowDifferenceKinds.Shape, null);
            var wrong = JsonSerializer.Serialize(new { Subject = subject + "!", Text = body.TrimEnd().ReplaceLineEndings("\n"), Html = "" });
            var findings = ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, kind: ChannelKind.Email, recipients: 3)], [.. three.Take(2), New(wrong, channel: "email")], Options);
            Assert.Contains(findings, f => f.Kind == ShadowDifferenceKinds.Body && f.Rule is null && f.Detail.StartsWith("subject"));
        }

        /// <summary>The digest boundary: a route digest gathers and closes at other moments than legacy's group window.</summary>
        [Fact]
        public void A_digest_of_other_alerts_or_at_another_time_is_explained_and_its_alert_still_compared()
        {
            var (a, b, c) = (Alert(), Alert(subject: "service:shop/payments@production"), Alert(subject: "service:shop/cart@production"));
            string Group(params string[] payloads) => NotificationView.Group(Guid.CreateVersion7(), NotificationPlan.Firing, At, payloads);
            var legacy = Legacy(Group(Payload(a), Payload(b), Payload(c)), group: 30);
            var @new = New(Webhook(Group(Payload(a), Payload(b))), at: At.AddSeconds(95));

            var findings = ShadowCompare.Compare(Unit(a.Alert.Id), [legacy], [@new], Options);

            Assert.Equal([(ShadowDifferenceKinds.Shape, ShadowRules.DigestBoundary), (ShadowDifferenceKinds.Timing, ShadowRules.DigestBoundary)],
                findings.Select(f => (f.Kind, f.Rule)));
            // The alert itself still has to say the same.
            var changed = Alert(ruleName: "Other");
            var wrong = New(Webhook(Group(NotificationPlan.Payload(Guid.CreateVersion7(), NotificationPlan.Firing, "t", changed.Rule, a.Alert, a.Transition, null), Payload(b))));
            Assert.Contains(ShadowCompare.Compare(Unit(a.Alert.Id), [legacy], [wrong], Options), f => f.Kind == ShadowDifferenceKinds.Body && f.Rule is null);
        }

        [Fact]
        public void A_side_still_sending_is_unsettled()
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, status: DeliveryStatus.Retrying)], [New(Webhook(payload))], Options),
                ShadowDifferenceKinds.Unsettled, null);
            Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload)], [New(Webhook(payload), status: NewStatus.Pending)], Options),
                ShadowDifferenceKinds.Unsettled, null);
        }

        [Theory]
        [InlineData(ChannelKind.Slack)]
        [InlineData(ChannelKind.Teams)]
        public void A_chat_message_is_compared_by_title_and_text(ChannelKind kind)
        {
            var a = Alert();
            var payload = Payload(a);
            var view = NotificationView.Parse(payload);
            string title, text;
            if (kind == ChannelKind.Slack)
            {
                var blocks = JsonDocument.Parse(ChannelFormatter.SlackBody(view, null)).RootElement.GetProperty("blocks");
                (title, text) = (blocks[0].GetProperty("text").GetProperty("text").GetString()!,
                    blocks[1].GetProperty("text").GetProperty("text").GetString()!.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&"));
            }
            else
            {
                var card = JsonDocument.Parse(ChannelFormatter.TeamsBody(view, null)).RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
                (title, text) = (card[0].GetProperty("text").GetString()!, card[1].GetProperty("text").GetString()!);
            }
            var channel = kind == ChannelKind.Slack ? "slack" : "teams";
            Assert.Empty(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, kind: kind)], [New(JsonSerializer.Serialize(new { Title = title, Text = text }), channel: channel)], Options));
            var finding = Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, kind: kind)],
                [New(JsonSerializer.Serialize(new { Title = title + " (new)", Text = text }), channel: channel)], Options), ShadowDifferenceKinds.Body, null);
            Assert.StartsWith("title", finding.Detail);
        }

        [Fact]
        public void Endpoints_are_matched_by_key()
        {
            var a = Alert();
            var payload = Payload(a);
            var findings = ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(payload, key: "ops"), Legacy(payload, key: "sre")],
                [New(Webhook(payload), key: "ops"), New(Webhook(payload), key: "pager")], Options);
            Assert.Equal([("pager", ShadowDifferenceKinds.MissingInLegacy), ("sre", ShadowDifferenceKinds.MissingInNew)], findings.Select(f => (f.EndpointKey, f.Kind)));
        }


        /// <summary>
        /// A body rendered by the type's default template (template version 0: none ported for the channel yet) is explained as
        /// templates_not_ported; the same difference from a ported template is not.
        /// </summary>
        [Fact]
        public void A_body_from_a_default_template_is_explained()
        {
            var a = Alert();
            var envelope = JsonSerializer.Serialize(new { ContentType = "application/json", Body = """{"id":"x","type":"observability.alert.firing","data":{}}""" });
            var unported = ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(Payload(a))], [New(envelope) with { DefaultTemplate = true }], Options);
            Assert.Equal((ShadowDifferenceKinds.Body, ShadowRules.TemplatesNotPorted), (Assert.Single(unported).Kind, unported[0].Rule));
            var ported = ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(Payload(a))], [New(envelope)], Options);
            Assert.Null(Assert.Single(ported).Rule);
        }
        /// <summary>A type with its templates ported (a delivery's template version is 1, M46c): a body that differs is a finding, never excused.</summary>
        [Fact]
        public void ShadowCompare_PortedType_BodyDiff_NotExcused()
        {
            var a = Alert();
            var envelope = JsonSerializer.Serialize(new { ContentType = "application/json", Body = """{"id":"x","type":"observability.alert.firing","data":{}}""" });

            var finding = Assert.Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(Payload(a))], [New(envelope) with { DefaultTemplate = false }], Options));

            Assert.Equal((ShadowDifferenceKinds.Body, (string?)null), (finding.Kind, finding.Rule));
        }

        /// <summary>The deliveries made before the port (template version 0) are still explained as templates_not_ported.</summary>
        [Fact]
        public void ShadowCompare_TemplateVersion0_StillTemplatesNotPorted()
        {
            var a = Alert();
            var envelope = JsonSerializer.Serialize(new { ContentType = "application/json", Body = """{"id":"x","type":"observability.alert.firing","data":{}}""" });

            var finding = Assert.Single(ShadowCompare.Compare(Unit(a.Alert.Id), [Legacy(Payload(a))], [New(envelope) with { DefaultTemplate = true }], Options));

            Assert.Equal((ShadowDifferenceKinds.Body, ShadowRules.TemplatesNotPorted), (finding.Kind, finding.Rule));
        }

        private static ShadowUnit HeldUnit(Guid alertId, string @event, bool undecided, bool firedSince = false)
            => new(TenantId, Guid.CreateVersion7(), 0, alertId, @event, false, true, firedSince, undecided);

        /// <summary>M53: a hold the release pass hasn't decided isn't told per alert (legacy sent, the new side has nothing yet): the unit waits.</summary>
        [Fact, Trait("Milestone", "M53")]
        public void Compare_ResolvedHoldUndecided_LegacySent_IsUnsettled_NotToldPerAlert()
        {
            var a = Alert();
            var payload = Payload(a, NotificationPlan.Resolved);
            var findings = ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Resolved, undecided: true), [Legacy(payload)], [], Options);

            var finding = Single(findings, ShadowDifferenceKinds.Unsettled, null);
            Assert.NotEqual(ShadowRules.ToldPerAlert, finding.Rule);
            // Also when nothing is on either side yet.
            Single(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Resolved, undecided: true), [], [], Options), ShadowDifferenceKinds.Unsettled, null);
        }

        /// <summary>M53: decided, the same unit is labelled from the hold's outcome: dropped_stale is stale_resolve_dropped, released is a match.</summary>
        [Fact, Trait("Milestone", "M53")]
        public void Compare_UndecidedHold_ThenDecided_LabelledFromOutcome()
        {
            var a = Alert();
            var payload = Payload(a, NotificationPlan.Resolved);

            Single(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Resolved, undecided: true, firedSince: true), [Legacy(payload)], [], Options),
                ShadowDifferenceKinds.Unsettled, null);
            Single(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Resolved, undecided: false, firedSince: true), [Legacy(payload)], [], Options),
                ShadowDifferenceKinds.MissingInNew, ShadowRules.StaleResolveDropped);
            Assert.Empty(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Resolved, undecided: false), [Legacy(payload)], [New(Webhook(payload))], Options));
        }

        /// <summary>No regression: a decided hold of a firing is still told per alert.</summary>
        [Fact, Trait("Milestone", "M53")]
        public void Compare_FiringHeld_Decided_StillToldPerAlert()
        {
            var a = Alert();
            var payload = Payload(a);
            Single(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Firing, undecided: false), [], [New(Webhook(payload))], Options),
                ShadowDifferenceKinds.MissingInLegacy, ShadowRules.ToldPerAlert);
            Single(ShadowCompare.Compare(HeldUnit(a.Alert.Id, NotificationPlan.Firing, undecided: false), [Legacy(payload)], [], Options),
                ShadowDifferenceKinds.MissingInNew, ShadowRules.ToldPerAlert);
        }

        /// <summary>Only a unit with a hold waits on it: HoldUndecided without Held (no hold read) changes nothing.</summary>
        [Fact, Trait("Milestone", "M53")]
        public void Compare_UndecidedFlagWithoutHold_DoesNotWait()
        {
            var a = Alert();
            var unit = new ShadowUnit(TenantId, Guid.CreateVersion7(), 0, a.Alert.Id, NotificationPlan.Firing, false, false, false, true);
            Assert.Empty(ShadowCompare.Compare(unit, [Legacy(Payload(a))], [New(Webhook(Payload(a)))], Options));
        }

        [Fact]
        public void Every_rule_is_named_in_the_vocabulary()
            => Assert.Equal(7, ShadowRules.All.Distinct().Count());
    }
}
