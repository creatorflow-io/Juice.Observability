using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The Phase 6 gate end to end, against a real receiver and a real database: silences hold and release, failures stay visible, groups and chat channels send, and escalation stops
    /// when its policy says. Moments are moved by editing rows rather than waiting.
    /// </summary>
    public class NotificationFlowTests
    {
        private const string SecretVariable = "JUICE_WEBHOOK_TEST";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public NotificationFlowTests() => Environment.SetEnvironmentVariable(SecretVariable, "flow-secret");

        private static IServiceScopeFactory Scopes(ScratchDatabase database)
            => new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        private static IOptions<PlatformOptions> Platform => Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey });

        private static WebhookDispatcher Dispatcher(ScratchDatabase database, Action<WebhookOptions>? configure = null)
        {
            var options = new WebhookOptions { RetryBase = TimeSpan.FromMilliseconds(20), RetryMax = TimeSpan.FromMilliseconds(100), AllowHttpToAllowlisted = true, MaxAttempts = 3 };
            configure?.Invoke(options);
            return new WebhookDispatcher(Scopes(database), Options.Create(options), TimeProvider.System, NullLogger<WebhookDispatcher>.Instance);
        }

        private static NotificationWorker Worker(ScratchDatabase database)
            => new(Scopes(database), Options.Create(new WebhookOptions()), Platform, TimeProvider.System, NullLogger<NotificationWorker>.Instance);

        private static WebhookOutbox Outbox => new(Platform, TimeProvider.System);

        private static async Task<Guid> TenantAsync(PlatformDbContext db) => (await db.Tenants.SingleAsync(t => t.Key == LocalStack.TenantKey, Ct)).Id;

        private static async Task<WebhookEndpoint> EndpointAsync(ScratchDatabase database, Receiver? receiver, string key = "ops", ChannelKind kind = ChannelKind.Webhook, int group = 0, string[]? events = null, string[]? environments = null, string[]? recipients = null)
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(db);
            var endpoint = new WebhookEndpoint
            {
                TenantId = tenant, Key = key, Kind = kind, Url = kind == ChannelKind.Webhook ? receiver!.Url : "", SecretRef = kind == ChannelKind.Email ? "" : $"env:{SecretVariable}",
                Events = events ?? [WebhookOutbox.Firing, WebhookOutbox.Resolved], GroupWindowSeconds = group, Environments = environments ?? [], Recipients = recipients ?? [], DefinitionHash = "h", LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api,
            };
            db.WebhookEndpoints.Add(endpoint);
            if (receiver is not null)
            {
                var entry = await db.WebhookAllowlist.SingleOrDefaultAsync(a => a.TenantId == tenant && a.Destination == $"{receiver.Address}/32", Ct);
                if (entry is null) db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenant, Destination = $"{receiver.Address}/32", Ports = [receiver.Port], LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api });
                else entry.Ports = [.. entry.Ports, receiver.Port];
            }
            await db.SaveChangesAsync(Ct);
            return endpoint;
        }

        /// <summary>Fires an alert through the outbox, as the evaluator does.</summary>
        private static async Task<Guid> FireAsync(ScratchDatabase database, string subject = "service:shop/orders@production", AlertSeverity severity = AlertSeverity.Critical, string ruleKey = "orders-errors")
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(db);
            var now = DateTimeOffset.UtcNow;
            var rule = await db.Rules.SingleOrDefaultAsync(r => r.TenantId == tenant && r.Key == ruleKey, Ct);
            if (rule is null)
            {
                rule = new Rule { TenantId = tenant, Key = ruleKey, Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = severity, Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = ManagedBy.Api };
                db.Rules.Add(rule);
            }
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = $"{ruleKey}|{subject}", SubjectKey = subject, SubjectType = HealthSubject.Service, Severity = severity,
                State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, LastValue = 0.4,
            };
            db.Alerts.Add(alert);
            var transition = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now, Value = 0.4, Reason = "for_duration_met" };
            db.AlertTransitions.Add(transition);
            await db.SaveChangesAsync(Ct);
            Outbox.OnTransition(db, rule, alert, transition);
            await db.SaveChangesAsync(Ct);
            return alert.Id;
        }

        private static async Task ResolveAsync(ScratchDatabase database, Guid alertId)
        {
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(a => a.Id == alertId, Ct);
            var rule = await db.Rules.SingleAsync(r => r.Id == alert.RuleId, Ct);
            var now = DateTimeOffset.UtcNow;
            var transition = new AlertTransition { TenantId = alert.TenantId, AlertId = alert.Id, FromState = AlertState.Firing, ToState = AlertState.Resolved, At = now, Value = 0.01, Reason = "recovered" };
            db.AlertTransitions.Add(transition);
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = now;
            Outbox.OnTransition(db, rule, alert, transition);
            await db.SaveChangesAsync(Ct);
        }

        private static async Task<Silence> SilenceAsync(ScratchDatabase database, Action<SilenceBuilder>? configure = null)
        {
            await using var db = database.Context();
            var builder = new SilenceBuilder();
            configure?.Invoke(builder);
            var now = DateTimeOffset.UtcNow;
            var silence = new Silence
            {
                TenantId = await TenantAsync(db), Kind = SilenceKind.Silence, Comment = "test", CreatedBy = "erin", CreatedAt = now, StartsAt = now.AddMinutes(-1), EndsAt = now.AddHours(1),
                Service = builder.Service ?? "shop/orders", MaximumSeverity = builder.Maximum,
            };
            db.Silences.Add(silence);
            await db.SaveChangesAsync(Ct);
            return silence;
        }

        private sealed class SilenceBuilder
        {
            public string? Service { get; set; }
            public AlertSeverity? Maximum { get; set; }
        }

        private static async Task CancelAsync(ScratchDatabase database, Guid silenceId)
        {
            await using var db = database.Context();
            await db.Silences.Where(s => s.Id == silenceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow).SetProperty(x => x.CancelledBy, "erin"), Ct);
        }

        private static async Task<List<WebhookDelivery>> DeliveriesAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync(Ct);
        }

        // ─── Silences ─────────────────────────────────────────────────────────

        [Fact]
        public async Task A_silence_holds_the_notification_not_the_alert_and_sends_it_if_still_firing_when_it_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            var silence = await SilenceAsync(database);
            var alert = await FireAsync(database);
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));

            await dispatcher.DispatchOnceAsync(Ct);
            await worker.RunOnceAsync(Ct);
            Assert.Empty(receiver.Requests);
            var held = Assert.Single(await DeliveriesAsync(database));
            Assert.Equal((DeliveryStatus.Suppressed, silence.Id), (held.Status, held.SuppressedBy));
            await using (var db = database.Context())
            {
                Assert.Equal(AlertState.Firing, (await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert, Ct)).State);
            }

            await CancelAsync(database, silence.Id);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);

            Assert.Contains("alert.firing", Assert.Single(receiver.Requests).Body);
            Assert.Equal(DeliveryStatus.Delivered, Assert.Single(await DeliveriesAsync(database)).Status);
        }

        [Fact]
        public async Task An_alert_over_before_the_silence_ended_never_notifies_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            var silence = await SilenceAsync(database);
            var alert = await FireAsync(database);
            await ResolveAsync(database, alert);

            await CancelAsync(database, silence.Id);
            await Worker(database).RunOnceAsync(Ct);
            await Dispatcher(database).DispatchOnceAsync(Ct);

            Assert.Empty(receiver.Requests);
            Assert.All(await DeliveriesAsync(database), d => Assert.Equal(DeliveryStatus.Dropped, d.Status));
        }

        [Fact]
        public async Task A_receiver_told_of_firing_hears_about_the_resolve_a_silence_held_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            var alert = await FireAsync(database);
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));
            await dispatcher.DispatchOnceAsync(Ct);
            var silence = await SilenceAsync(database);
            await ResolveAsync(database, alert);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(receiver.Requests);

            await CancelAsync(database, silence.Id);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);

            Assert.Equal(2, receiver.Requests.Count);
            Assert.Contains("alert.resolved", receiver.Requests.Last().Body);
        }

        [Fact]
        public async Task A_silence_started_after_queueing_still_holds_and_a_second_silence_keeps_holding_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            await FireAsync(database);   // queued, not sent
            var (first, second) = (await SilenceAsync(database), await SilenceAsync(database, s => s.Service = "orders"));
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));

            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Empty(receiver.Requests);
            var held = Assert.Single(await DeliveriesAsync(database));
            Assert.Equal(DeliveryStatus.Suppressed, held.Status);

            await CancelAsync(database, held.SuppressedBy!.Value);
            await worker.RunOnceAsync(Ct);
            var still = Assert.Single(await DeliveriesAsync(database));
            Assert.Equal(DeliveryStatus.Suppressed, still.Status);
            Assert.NotEqual(held.SuppressedBy, still.SuppressedBy);
            Assert.Contains(still.SuppressedBy!.Value, new[] { first.Id, second.Id });

            await CancelAsync(database, still.SuppressedBy!.Value);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(receiver.Requests);
        }

        [Fact]
        public async Task A_silence_on_warnings_lets_critical_through_and_another_service_is_unaffected_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            await SilenceAsync(database, s => { s.Service = "shop/orders"; s.Maximum = AlertSeverity.Warning; });
            await FireAsync(database, "service:shop/orders@production", AlertSeverity.Critical);
            await FireAsync(database, "service:shop/billing@production", AlertSeverity.Warning, "other");
            await FireAsync(database, "service:shop/orders@staging", AlertSeverity.Warning, "third");

            await Dispatcher(database).DispatchOnceAsync(Ct);

            var deliveries = await DeliveriesAsync(database);
            Assert.Equal([DeliveryStatus.Delivered, DeliveryStatus.Delivered, DeliveryStatus.Suppressed], deliveries.Select(d => d.Status));
            Assert.Equal(2, receiver.Requests.Count);
        }

        // ─── Visibility of failure and retry ──────────────────────────────────

        [Fact]
        public async Task One_alerts_notifications_show_failure_retry_silence_and_escalation_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(503, 400);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver);
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy { TenantId = await TenantAsync(db), Key = "pager", Name = "Pager", UpdatedAt = DateTimeOffset.UtcNow, Steps = [new EscalationStep { AfterMinutes = 30, EndpointKeys = ["ops"] }] });
                await db.SaveChangesAsync(Ct);
            }
            var alert = await FireAsync(database);
            var dispatcher = Dispatcher(database);
            await dispatcher.DispatchOnceAsync(Ct);

            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);
            var path = $"/api/v1/tenants/{tenant}/alerts/{alert}/notifications";

            var retrying = (await Caller.JsonAsync(await viewer.GetAsync(path))).GetProperty("deliveries")[0];
            Assert.Equal(("ops", "Webhook", "Retrying", "status_503"), (retrying.GetProperty("endpointKey").GetString(), retrying.GetProperty("kind").GetString(),
                retrying.GetProperty("delivery").GetProperty("status").GetString(), retrying.GetProperty("delivery").GetProperty("lastError").GetString()));
            Assert.NotEqual(JsonValueKind.Null, retrying.GetProperty("delivery").GetProperty("nextAttemptAt").ValueKind);

            for (var i = 0; i < 40 && receiver.Requests.Count < 2; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(20, Ct);
            }
            var silence = await SilenceAsync(database);
            var seen = await Caller.JsonAsync(await viewer.GetAsync(path));
            var failed = seen.GetProperty("deliveries")[0].GetProperty("delivery");
            Assert.Equal("Failed", failed.GetProperty("status").GetString());
            Assert.Equal([503, 400], failed.GetProperty("history").EnumerateArray().Select(a => a.GetProperty("statusCode").GetInt32()));
            Assert.Equal(silence.Id, seen.GetProperty("silences")[0].GetProperty("id").GetGuid());
            var escalation = seen.GetProperty("escalations")[0];
            Assert.Equal(("pager", "Active", 0, 1), (escalation.GetProperty("policy").GetString(), escalation.GetProperty("state").GetString(), escalation.GetProperty("stepsSent").GetInt32(), escalation.GetProperty("steps").GetInt32()));

            var endpoints = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/webhooks"));
            Assert.Equal(1, endpoints.GetProperty("items")[0].GetProperty("recentFailures").GetInt32());
            await using var check = database.Context();
            Assert.Equal(AlertState.Firing, (await check.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert, Ct)).State);
        }

        // ─── Routing, grouping, channels ──────────────────────────────────────

        [Fact]
        public async Task An_endpoint_takes_only_its_environments_and_a_host_alert_isnt_in_any_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver, environments: ["production"]);
            await FireAsync(database, "service:shop/orders@production");
            await FireAsync(database, "service:shop/orders@staging", ruleKey: "two");
            await FireAsync(database, "host:db-1", ruleKey: "three");
            Assert.Single(await DeliveriesAsync(database));
        }

        [Fact]
        public async Task A_group_window_sends_what_gathered_once_and_a_retry_sends_the_same_bytes_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(503);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver, group: 30);
            for (var i = 0; i < 3; i++) await FireAsync(database, $"service:s{i}@production", ruleKey: $"rule-{i}");
            var dispatcher = Dispatcher(database);

            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Empty(receiver.Requests);   // the window hasn't passed

            await using (var db = database.Context())
            {
                var first = await db.WebhookDeliveries.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync(Ct);
                await db.WebhookDeliveries.Where(d => d.Id == first).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }
            for (var i = 0; i < 40 && receiver.Requests.Count < 2; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(20, Ct);
            }

            Assert.Equal(2, receiver.Requests.Count);
            Assert.Single(receiver.Requests.Select(r => r.Body).Distinct());
            var request = receiver.Requests.First();
            Assert.True(WebhookSigner.Verify("flow-secret", request.Id, request.Timestamp, request.Signature, request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal((NotificationView.GroupType, 3), (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("alerts").GetArrayLength()));

            var deliveries = await DeliveriesAsync(database);
            var primary = Assert.Single(deliveries, d => d.Status == DeliveryStatus.Delivered);
            Assert.All(deliveries.Where(d => d != primary), d => Assert.Equal((DeliveryStatus.Grouped, primary.Id), (d.Status, d.GroupedInto)));
        }

        [Fact]
        public async Task Dispatchers_racing_on_a_group_send_it_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, receiver, group: 30);
            for (var i = 0; i < 12; i++) await FireAsync(database, $"service:s{i}@production", ruleKey: $"rule-{i}");
            await using (var db = database.Context())
            {
                await db.WebhookDeliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }

            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Dispatcher(database).DispatchOnceAsync(Ct)));

            var request = Assert.Single(receiver.Requests);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(12, body.RootElement.GetProperty("alerts").GetArrayLength());
            var deliveries = await DeliveriesAsync(database);
            Assert.Equal(11, deliveries.Count(d => d.Status == DeliveryStatus.Grouped));
            Assert.Single(deliveries, d => d.Status == DeliveryStatus.Delivered);
        }

        [Theory]
        [InlineData(ChannelKind.Slack, "blocks")]
        [InlineData(ChannelKind.Teams, "attachments")]
        public async Task A_chat_channel_gets_its_own_format_at_the_address_behind_its_secret_Async(ChannelKind kind, string property)
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Environment.SetEnvironmentVariable(SecretVariable, receiver.Url);
            await EndpointAsync(database, receiver, kind: kind);
            await FireAsync(database, "service:<!channel>@production");

            await Dispatcher(database, o => o.AdditionalChatHosts = [receiver.Address.ToString()]).DispatchOnceAsync(Ct);

            var request = Assert.Single(receiver.Requests);
            using var body = JsonDocument.Parse(request.Body);
            Assert.True(body.RootElement.TryGetProperty(property, out _), request.Body);
            Assert.False(body.RootElement.TryGetProperty("alert", out _));
            Assert.True(string.IsNullOrEmpty(request.Signature));
            if (kind == ChannelKind.Slack) Assert.DoesNotContain("<!channel>", body.RootElement.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString());
            await using var db = database.Context();
            Assert.Equal(DeliveryStatus.Delivered, (await db.WebhookDeliveries.AsNoTracking().SingleAsync(Ct)).Status);
        }

        [Fact]
        public async Task A_chat_url_elsewhere_is_refused_visibly_and_never_stored_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var url = $"{receiver.Url}?token=chat-secret-value";
            Environment.SetEnvironmentVariable(SecretVariable, url);
            await EndpointAsync(database, receiver, kind: ChannelKind.Slack);
            await FireAsync(database);

            await Dispatcher(database).DispatchOnceAsync(Ct);

            Assert.Empty(receiver.Requests);
            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((DeliveryStatus.Failed, "chat_url_not_allowed"), (delivery.Status, delivery.LastError));
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(Ct);
            Assert.DoesNotContain("chat-secret-value", endpoint.Url + endpoint.SecretRef + delivery.Payload + delivery.LastError);
        }

        [Fact]
        public async Task A_private_address_behind_a_chat_secret_still_needs_the_allowlist_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Environment.SetEnvironmentVariable(SecretVariable, receiver.Url);
            await EndpointAsync(database, null, kind: ChannelKind.Slack);
            await FireAsync(database);

            await Dispatcher(database, o => o.AdditionalChatHosts = [receiver.Address.ToString()]).DispatchOnceAsync(Ct);

            Assert.Empty(receiver.Requests);
            await using var db = database.Context();
            Assert.StartsWith("private_address_not_allowlisted", (await db.WebhookDeliveries.AsNoTracking().SingleAsync(Ct)).LastError);
        }

        [Fact]
        public async Task Another_tenants_allowlist_opens_nothing_for_this_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, null, kind: ChannelKind.Slack);
            await using (var db = database.Context())
            {
                var other = new PlatformTenant { Key = "other-tenant", DisplayName = "Other" };
                db.Tenants.Add(other);
                db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = other.Id, Destination = $"{receiver.Address}/32", Ports = [receiver.Port], LoadedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(Ct);
            }
            Environment.SetEnvironmentVariable(SecretVariable, receiver.Url);
            await FireAsync(database);

            await Dispatcher(database, o => o.AdditionalChatHosts = [receiver.Address.ToString()]).DispatchOnceAsync(Ct);

            Assert.Empty(receiver.Requests);
        }

        [Theory]
        [InlineData(250, DeliveryStatus.Delivered, null, 1)]
        [InlineData(550, DeliveryStatus.Failed, "smtp_550", 1)]
        public async Task An_email_channel_sends_through_the_deployments_server_Async(int response, DeliveryStatus expected, string? error, int attempts)
        {
            LocalStack.RequireAvailable();
            await using var smtp = await FakeSmtp.StartAsync(response);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, null, kind: ChannelKind.Email, recipients: ["oncall@example.com"]);
            await FireAsync(database);

            await Dispatcher(database, o => o.Smtp = new SmtpOptions { Host = "127.0.0.1", Port = smtp.Port, From = "juice@example.com", UseTls = false, }).DispatchOnceAsync(Ct);

            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((expected, error, attempts), (delivery.Status, delivery.LastError, delivery.Attempts));
            if (response == 250)
            {
                Assert.Contains("oncall@example.com", smtp.Conversation);
                Assert.Contains("Subject: [Juice] Firing: Orders errors", smtp.Conversation);
                Assert.Contains("service:shop/orders@production", smtp.Conversation);
            }
        }

        [Fact]
        public async Task An_email_channel_without_a_server_fails_visibly_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, null, kind: ChannelKind.Email, recipients: ["oncall@example.com"]);
            await FireAsync(database);
            await Dispatcher(database).DispatchOnceAsync(Ct);
            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((DeliveryStatus.Failed, "smtp_not_configured"), (delivery.Status, delivery.LastError));
        }

        // ─── Escalation ───────────────────────────────────────────────────────

        private static async Task<Guid> PolicyAsync(ScratchDatabase database, bool stopOnAcknowledge = true, AlertSeverity minimum = AlertSeverity.Info)
        {
            await using var db = database.Context();
            var policy = new EscalationPolicy
            {
                TenantId = await TenantAsync(db), Key = "pager", Name = "Pager", StopOnAcknowledge = stopOnAcknowledge, MinimumSeverity = minimum, UpdatedAt = DateTimeOffset.UtcNow,
                Steps = [new EscalationStep { AfterMinutes = 5, EndpointKeys = ["first"] }, new EscalationStep { AfterMinutes = 15, EndpointKeys = ["second"] }],
            };
            db.EscalationPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
            return policy.Id;
        }

        /// <summary>Moves the whole alert back, as if it fired this long ago.</summary>
        private static async Task AgeAsync(ScratchDatabase database, Guid alert, int minutes)
        {
            await using var db = database.Context();
            var firedAt = DateTimeOffset.UtcNow.AddMinutes(-minutes);
            await db.Alerts.Where(a => a.Id == alert).ExecuteUpdateAsync(s => s.SetProperty(a => a.FiredAt, firedAt), Ct);
            var escalation = await db.AlertEscalations.SingleAsync(e => e.AlertId == alert, Ct);
            var policy = await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.Id == escalation.PolicyId, Ct);
            if (escalation.NextStep < policy.Steps.Count)
            {
                escalation.NextAt = firedAt.AddMinutes(policy.Steps[escalation.NextStep].AfterMinutes);
                await db.SaveChangesAsync(Ct);
            }
        }

        private static async Task<(EscalationState State, int NextStep, string? Reason)> EscalationAsync(ScratchDatabase database, Guid alert)
        {
            await using var db = database.Context();
            var escalation = await db.AlertEscalations.AsNoTracking().SingleAsync(e => e.AlertId == alert, Ct);
            return (escalation.State, escalation.NextStep, escalation.StopReason);
        }

        private static async Task<(Receiver First, Receiver Second)> PagersAsync(ScratchDatabase database)
        {
            var (first, second) = (await Receiver.StartAsync(), await Receiver.StartAsync());
            await EndpointAsync(database, first, "first", events: [WebhookOutbox.Resolved]);
            await EndpointAsync(database, second, "second", events: [WebhookOutbox.Resolved]);
            return (first, second);
        }

        [Fact]
        public async Task Steps_notify_in_order_each_once_and_then_it_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (first, second) = await PagersAsync(database);
            await using var _1 = first;
            await using var _2 = second;
            await PolicyAsync(database);
            var alert = await FireAsync(database);
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));

            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Empty(first.Requests);   // 5 minutes haven't passed

            await AgeAsync(database, alert, 6);
            await worker.RunOnceAsync(Ct);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            var request = Assert.Single(first.Requests);
            Assert.Contains("\"type\":\"alert.escalated\"", request.Body);
            Assert.Contains("\"step\":1", request.Body);
            Assert.Empty(second.Requests);
            Assert.Equal((EscalationState.Active, 1, (string?)null), await EscalationAsync(database, alert));

            await AgeAsync(database, alert, 16);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(first.Requests);
            Assert.Contains("\"step\":2", Assert.Single(second.Requests).Body);
            Assert.Equal((EscalationState.Completed, 2, (string?)null), await EscalationAsync(database, alert));

            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(second.Requests);
        }

        [Fact]
        public async Task A_host_down_past_several_steps_catches_up_one_pass_at_a_time_without_a_repeat_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (first, second) = await PagersAsync(database);
            await using var _1 = first;
            await using var _2 = second;
            await PolicyAsync(database);
            var alert = await FireAsync(database);
            await AgeAsync(database, alert, 60);
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));
            for (var i = 0; i < 4; i++)
            {
                await worker.RunOnceAsync(Ct);
                await dispatcher.DispatchOnceAsync(Ct);
            }
            Assert.Single(first.Requests);
            Assert.Single(second.Requests);
        }

        [Fact]
        public async Task Resolving_stops_an_escalation_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (first, second) = await PagersAsync(database);
            await using var _1 = first;
            await using var _2 = second;
            await PolicyAsync(database);
            var alert = await FireAsync(database);
            await AgeAsync(database, alert, 60);
            await ResolveAsync(database, alert);

            await Worker(database).RunOnceAsync(Ct);
            await Dispatcher(database).DispatchOnceAsync(Ct);

            Assert.Equal((EscalationState.Stopped, 0, "resolved"), await EscalationAsync(database, alert));
            Assert.DoesNotContain(first.Requests, r => r.Body.Contains("alert.escalated"));
        }

        [Theory]
        [InlineData(true, EscalationState.Stopped, 0, "acknowledged", 0)]
        [InlineData(false, EscalationState.Active, 1, null, 1)]
        public async Task Acknowledging_stops_an_escalation_only_if_the_policy_says_so_Async(bool stopOnAcknowledge, EscalationState state, int step, string? reason, int notified)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (first, second) = await PagersAsync(database);
            await using var _1 = first;
            await using var _2 = second;
            await PolicyAsync(database, stopOnAcknowledge);
            var alert = await FireAsync(database);
            await AgeAsync(database, alert, 6);

            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            await Caller.JsonAsync(await @operator.PostAsync($"/api/v1/tenants/{tenant}/alerts/{alert}/acknowledgement", new { comment = "on it" }));

            await Worker(database).RunOnceAsync(Ct);
            await Dispatcher(database).DispatchOnceAsync(Ct);

            Assert.Equal((state, step, reason), await EscalationAsync(database, alert));
            Assert.Equal(notified, first.Requests.Count);
        }

        [Fact]
        public async Task A_silence_holds_an_escalation_which_goes_on_if_the_alert_outlasts_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (first, second) = await PagersAsync(database);
            await using var _1 = first;
            await using var _2 = second;
            await PolicyAsync(database);
            var alert = await FireAsync(database);
            await AgeAsync(database, alert, 6);
            var silence = await SilenceAsync(database);
            var (dispatcher, worker) = (Dispatcher(database), Worker(database));

            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Equal((EscalationState.Active, 0, (string?)null), await EscalationAsync(database, alert));
            Assert.Empty(first.Requests);

            await CancelAsync(database, silence.Id);
            await worker.RunOnceAsync(Ct);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(first.Requests);
        }

        [Fact]
        public async Task A_policy_starts_only_on_what_it_matches_and_a_disabled_one_stops_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await EndpointAsync(database, null, "first", kind: ChannelKind.Email, recipients: ["a@example.com"]);
            var policy = await PolicyAsync(database, minimum: AlertSeverity.Critical);
            await FireAsync(database, "service:a@production", AlertSeverity.Warning, "warnings");
            var critical = await FireAsync(database, "service:b@production", AlertSeverity.Critical, "criticals");
            await using (var db = database.Context())
            {
                Assert.Equal(critical, (await db.AlertEscalations.AsNoTracking().SingleAsync(Ct)).AlertId);
                await db.EscalationPolicies.Where(p => p.Id == policy).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false), Ct);
            }
            await Worker(database).RunOnceAsync(Ct);
            Assert.Equal((EscalationState.Stopped, 0, "policy_disabled"), await EscalationAsync(database, critical));
        }

        /// <summary>Just enough SMTP to receive one message, or refuse it at the end with the given code.</summary>
        private sealed class FakeSmtp : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly StringBuilder _conversation = new();
            private readonly int _response;
            private Task _loop = Task.CompletedTask;

            private FakeSmtp(int response) => _response = response;

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public string Conversation { get { lock (_conversation) return _conversation.ToString(); } }

            public static Task<FakeSmtp> StartAsync(int response)
            {
                var smtp = new FakeSmtp(response);
                smtp._listener.Start();
                smtp._loop = smtp.ServeAsync();
                return Task.FromResult(smtp);
            }

            private async Task ServeAsync()
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                    await writer.WriteLineAsync("220 test");
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        lock (_conversation) _conversation.AppendLine(line);
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 test");
                        else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("354 go");
                            while (await reader.ReadLineAsync() is { } data && data != ".")
                            {
                                lock (_conversation) _conversation.AppendLine(data);
                            }
                            await writer.WriteLineAsync($"{_response} done");
                        }
                        else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("221 bye"); return; }
                        else await writer.WriteLineAsync("250 ok");
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
            }

            public async ValueTask DisposeAsync()
            {
                _listener.Stop();
                await _loop;
            }
        }
    }
}
