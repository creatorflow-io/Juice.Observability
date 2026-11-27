using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Health.Delivery;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 6's limits on running channels, resolved: a test send, a failed delivery retried by an admin rather than by SQL, and an allowlist entry
    /// removed taking effect at the next send rather than after the pooled connection's minute.
    /// </summary>
    [Trait("Milestone", "M46c")]
    public sealed class WebhookOperationsTests
    {
        private const string Secret = "JUICE_WEBHOOK_OPSTEST";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public WebhookOperationsTests() => Environment.SetEnvironmentVariable(Secret, "ops-test-secret");

        private static WebhookDispatcher Dispatcher(ScratchDatabase database)
        {
            var scopes = new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
            var options = new WebhookOptions { RetryBase = TimeSpan.FromMilliseconds(20), RetryMax = TimeSpan.FromMilliseconds(100), AllowHttpToAllowlisted = true, MaxAttempts = 2 };
            return new WebhookDispatcher(scopes, Options.Create(options), TimeProvider.System, NullLogger<WebhookDispatcher>.Instance);
        }

        private static async Task<WebhookEndpoint> EndpointAsync(PlatformDbContext db, Receiver receiver, bool allowlisted = true, bool enabled = true)
        {
            var tenant = (await db.Tenants.SingleAsync(t => t.Key == LocalStack.TenantKey, Ct)).Id;
            var endpoint = new WebhookEndpoint
            {
                TenantId = tenant, Key = $"ops-{Guid.NewGuid():N}"[..12], Url = receiver.Url, SecretRef = $"env:{Secret}", Events = [NotificationPlan.Firing],
                DefinitionHash = "h", LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api, Enabled = enabled,
            };
            db.WebhookEndpoints.Add(endpoint);
            if (allowlisted)
            {
                db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenant, Destination = $"{receiver.Address}/32", Ports = [receiver.Port], LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api });
            }
            await db.SaveChangesAsync(Ct);
            return endpoint;
        }

        /// <summary>A delivery to the endpoint for a real alert, as planning writes it.</summary>
        private static async Task<WebhookDelivery> DeliveryAsync(PlatformDbContext db, WebhookEndpoint endpoint, DeliveryStatus status = DeliveryStatus.Pending, int attempts = 0)
        {
            var rule = new Rule { TenantId = endpoint.TenantId, Key = $"r-{Guid.NewGuid():N}"[..10], Name = "Errors", Definition = "{}", DefinitionHash = "h", Severity = AlertSeverity.Critical };
            db.Rules.Add(rule);
            var alert = new Alert { TenantId = endpoint.TenantId, RuleId = rule.Id, DedupKey = Guid.NewGuid().ToString(), SubjectKey = "service:shop/orders@production", Severity = AlertSeverity.Critical, State = AlertState.Firing, FiredAt = DateTimeOffset.UtcNow };
            db.Alerts.Add(alert);
            var transition = new AlertTransition { TenantId = endpoint.TenantId, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = DateTimeOffset.UtcNow, Reason = "test" };
            db.AlertTransitions.Add(transition);
            var id = Guid.CreateVersion7();
            var delivery = new WebhookDelivery
            {
                Id = id, TenantId = endpoint.TenantId, EndpointId = endpoint.Id, AlertId = alert.Id, TransitionId = transition.Id, Event = NotificationPlan.Firing,
                Payload = NotificationPlan.Payload(id, NotificationPlan.Firing, LocalStack.TenantKey, rule, alert, transition, null), CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow, Status = status, Attempts = attempts, LastError = status == DeliveryStatus.Failed ? "status_503" : null,
            };
            db.WebhookDeliveries.Add(delivery);
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

        [Fact]
        public async Task A_test_send_goes_the_way_a_delivery_does_and_stores_nothing_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            await using var db = database.Context();
            var endpoint = await EndpointAsync(db, receiver, enabled: false);   // a channel checked before it's turned on
            var dispatcher = Dispatcher(database);

            var result = await dispatcher.TestAsync(endpoint, "alice", Ct);

            Assert.True(result.Delivered, result.Error);
            Assert.Equal(200, result.StatusCode);
            var request = Assert.Single(receiver.Requests);
            Assert.Equal(result.Id.ToString(), request.Id);
            Assert.Contains("\"type\":\"webhook.test\"", request.Body);
            Assert.Contains("Sent by alice", request.Body);
            Assert.Equal(WebhookSigner.Sign("ops-test-secret", request.Id, long.Parse(request.Timestamp, System.Globalization.CultureInfo.InvariantCulture), request.Body), request.Signature);
            Assert.Empty(await db.WebhookDeliveries.ToListAsync(Ct));

            // The same checks as a delivery: a private address nobody allowlisted is refused, not reached.
            await using var unlisted = await Receiver.StartAsync();
            var blocked = await dispatcher.TestAsync(await EndpointAsync(db, unlisted, allowlisted: false), "alice", Ct);
            Assert.False(blocked.Delivered);
            Assert.StartsWith("private_address_not_allowlisted", blocked.Error);
            Assert.Empty(unlisted.Requests);
        }

        [Fact]
        public async Task Removing_an_allowlist_entry_stops_the_next_send_though_a_connection_is_open_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            await using var db = database.Context();
            var endpoint = await EndpointAsync(db, receiver);
            var dispatcher = Dispatcher(database);

            var first = await DeliveryAsync(db, endpoint);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Single(receiver.Requests);   // a pooled connection to the receiver is open now

            await db.WebhookAllowlist.Where(a => a.TenantId == endpoint.TenantId).ExecuteDeleteAsync(Ct);
            var second = await DeliveryAsync(db, endpoint);
            await dispatcher.DispatchOnceAsync(Ct);

            Assert.Single(receiver.Requests);
            var refused = await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == second.Id, Ct);
            Assert.Equal(DeliveryStatus.Failed, refused.Status);
            Assert.StartsWith("private_address_not_allowlisted", refused.LastError);
            Assert.Equal(DeliveryStatus.Delivered, (await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == first.Id, Ct)).Status);
        }

        [Fact]
        public async Task An_admin_retries_a_failed_delivery_and_tests_a_channel_through_the_api_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.200",
                // Legacy's own rows, seeded: the retry and test send of the legacy webhooks themselves. The same through whichever pipeline sends is
                // An_endpoint_made_through_the_api_is_retried_and_tested_through_whichever_pipeline_sends below.
                ["Notifications:Pipeline"] = "legacy",
            });
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            await using var db = database.Context();
            var endpoint = await EndpointAsync(db, receiver);
            var failed = await DeliveryAsync(db, endpoint, DeliveryStatus.Failed, attempts: 8);
            var delivered = await DeliveryAsync(db, endpoint, DeliveryStatus.Delivered, attempts: 1);
            var webhook = $"/api/v1/tenants/{tenant}/webhooks/{endpoint.Id}";

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync($"{webhook}/deliveries/{failed.Id}/retry", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{webhook}/test", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{webhook}/deliveries/{delivered.Id}/retry", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{webhook}/deliveries/{Guid.NewGuid()}/retry", new { })).StatusCode);

            var retried = await Caller.JsonAsync(await admin.PostAsync($"{webhook}/deliveries/{failed.Id}/retry", new { }));
            Assert.Equal("Pending", retried.GetProperty("status").GetString());
            WebhookDelivery? sent = null;
            for (var i = 0; i < 50 && sent?.Status != DeliveryStatus.Delivered; i++)
            {
                await Task.Delay(200, Ct);
                sent = await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Id, Ct);
            }
            Assert.Equal((DeliveryStatus.Delivered, 9), (sent!.Status, sent.Attempts));   // numbered after the earlier eight
            Assert.Equal(failed.Id.ToString(), Assert.Single(receiver.Requests).Id);

            var test = await Caller.JsonAsync(await admin.PostAsync($"{webhook}/test", new { }));
            Assert.True(test.GetProperty("delivered").GetBoolean());
            Assert.Equal(2, receiver.Requests.Count);

            var audit = await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "webhook.delivery.retry" || a.Operation == "webhook.test").ToListAsync(Ct);
            Assert.Contains(audit, a => a.Operation == "webhook.delivery.retry" && a.Outcome == AuditOutcome.Succeeded && a.Actor == "alice");
            Assert.Contains(audit, a => a.Operation == "webhook.delivery.retry" && a.Outcome == AuditOutcome.Denied);
            Assert.Contains(audit, a => a.Operation == "webhook.test" && a.Outcome == AuditOutcome.Succeeded && a.Metadata["delivered"] == "true");
        }

        /// <summary>
        /// While legacy sends (legacy, shadow), its rows are the endpoints (review of #10): one the alias could never map is listed and answered
        /// as legacy would, though notifications holds nothing of it: a digit-led key (changed, tested and retried through the API), and one the
        /// deployment's file declares with a secret outside the alias's limits (listed, and the deployment's: a change is 409, as legacy answers).
        /// </summary>
        [Theory]
        [InlineData("legacy")]
        [InlineData("shadow")]
        public async Task Endpoints_the_alias_cant_map_stay_in_the_api_while_legacy_sends(string pipeline)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            var folder = Directory.CreateTempSubdirectory("juice-webhook-secrets-").FullName;
            var file = Path.Combine(folder, "hook");
            await File.WriteAllTextAsync(file, "file-secret", Ct);
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = pipeline, ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.200",
            });
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            await using var db = database.Context();
            var digit = await EndpointAsync(db, receiver);
            var fromFile = await EndpointAsync(db, receiver, allowlisted: false);
            await db.WebhookEndpoints.Where(e => e.Id == digit.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Key, "1pager"), Ct);
            await db.WebhookEndpoints.Where(e => e.Id == fromFile.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Key, "from-file").SetProperty(e => e.SecretRef, "file:" + file)
                .SetProperty(e => e.ManagedBy, ManagedBy.Configuration), Ct);
            var failed = await DeliveryAsync(db, digit, DeliveryStatus.Failed, attempts: 8);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";

            var listed = await Caller.JsonAsync(await admin.GetAsync(webhooks));
            Assert.Equal(["1pager", "from-file"], listed.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("key").GetString()).Order());

            Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"{webhooks}/{fromFile.Id}", new { secretRef = $"env:{Secret}", enabled = false })).StatusCode);
            Assert.True((await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == fromFile.Id, Ct)).Enabled);
            await Caller.JsonAsync(await admin.PutAsync($"{webhooks}/{digit.Id}", new { secretRef = $"env:{Secret}", minimumSeverity = "warning" }));
            Assert.Equal(AlertSeverity.Warning, (await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == digit.Id, Ct)).MinimumSeverity);

            Assert.True((await Caller.JsonAsync(await admin.PostAsync($"{webhooks}/{digit.Id}/test", new { }))).GetProperty("delivered").GetBoolean());
            await Caller.JsonAsync(await admin.PostAsync($"{webhooks}/{digit.Id}/deliveries/{failed.Id}/retry", new { }));
            for (var i = 0; i < 50 && (await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Id, Ct)).Status != DeliveryStatus.Delivered; i++)
            {
                await Task.Delay(200, Ct);
            }
            Assert.Equal(DeliveryStatus.Delivered, (await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Id, Ct)).Status);
            // Notifications holds nothing of either: legacy's own, as before the alias.
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<Juice.Notifications.Data.NotificationsDbContext>();
            Assert.False(await notifications.Integrations.AnyAsync(i => i.Id == digit.Id || i.Id == fromFile.Id, Ct));
            Directory.Delete(folder, recursive: true);
        }

        /// <summary>
        /// With the pipeline down (its schema never migrated), legacy's webhooks API still works as legacy's (review of #10): endpoints are listed,
        /// made and changed, and the allowlist too, in legacy only; nothing of the pipeline is read or written. The next start with the pipeline
        /// up brings the notifications rows in step with legacy's (the alias provisioner), each endpoint's integration keeping its id.
        /// </summary>
        [Fact]
        public async Task With_the_pipeline_down_the_webhooks_api_works_in_legacy_and_a_later_start_catches_up()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            Guid made;
            await using (var down = new ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Database:Migrate"] = "false", ["Notifications:Pipeline"] = "legacy",
            }))
            {
                using var client = down.CreateClient();
                var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
                await using var db = database.Context();
                var seeded = await EndpointAsync(db, receiver);
                var webhooks = $"/api/v1/tenants/{tenant}/webhooks";

                var listed = await Caller.JsonAsync(await admin.GetAsync(webhooks));
                Assert.Equal(seeded.Key, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("key").GetString());
                made = (await Caller.JsonAsync(await admin.PostAsync(webhooks, new { key = "made-while-down", url = "https://hooks.example.com/down", secretRef = $"env:{Secret}" })))
                    .GetProperty("id").GetGuid();
                await Caller.JsonAsync(await admin.PutAsync($"{webhooks}/{made}", new { secretRef = $"env:{Secret}", minimumSeverity = "critical" }));
                await Caller.JsonAsync(await admin.SendAsync(HttpMethod.Put, $"{webhooks}/allowlist", new { destination = "10.9.0.0/16", ports = new[] { 8443 } }));

                var row = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == made, Ct);
                Assert.Equal(("made-while-down", AlertSeverity.Critical), (row.Key, row.MinimumSeverity));
                Assert.True(await db.WebhookAllowlist.AnyAsync(a => a.Destination == "10.9.0.0/16", Ct));
            }

            // Up again: the alias provisioner brings the notifications rows in step with legacy's.
            await using var up = new ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" });
            _ = up.CreateClient();
            await using var scope = up.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<Juice.Notifications.Data.NotificationsDbContext>();
            var integration = await notifications.Integrations.AsNoTracking().SingleAsync(i => i.Key == "made-while-down", Ct);
            Assert.Equal(made, integration.Id);
            Assert.True(await notifications.Allowlist.AnyAsync(a => a.Destination == "10.9.0.0/16", Ct));
        }

        /// <summary>The pipeline's host scope with its database failing mid-write: a real error (division by zero) that aborts the transaction.</summary>
        private sealed class FailingHostScope(Juice.Notifications.Data.NotificationsDbContext notifications) : Juice.Notifications.Embedded.INotificationsHostScope
        {
            public async Task<T> RunAsync<T>(Func<Juice.Notifications.Embedded.NotificationsHostWork, Task<T>> work, CancellationToken cancellationToken)
            {
                await notifications.Database.ExecuteSqlRawAsync("SELECT 1/0", cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        /// <summary>
        /// The alias's write failing for a reason that isn't a refusal (review of #10): in legacy, the change to an endpoint the alias doesn't hold,
        /// and a new one, are legacy's only. The answers are 2xx, legacy's rows are committed, and nothing of either is in notifications.
        /// </summary>
        [Fact]
        public async Task An_alias_write_that_fails_keeps_legacys_change()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string>
                {
                    ["Notifications:Pipeline"] = "legacy", ["Webhooks:AllowHttpToAllowlisted"] = "true",
                }, services: s => s.AddScoped<Juice.Notifications.Embedded.INotificationsHostScope, FailingHostScope>());
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            await using var db = database.Context();
            var unheld = await EndpointAsync(db, receiver);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";

            await Caller.JsonAsync(await admin.PutAsync($"{webhooks}/{unheld.Id}", new { secretRef = $"env:{Secret}", minimumSeverity = "critical" }));
            var made = (await Caller.JsonAsync(await admin.PostAsync(webhooks, new { key = "made-while-failing", url = "https://hooks.example.com/f", secretRef = $"env:{Secret}" })))
                .GetProperty("id").GetGuid();

            await using var check = database.Context();
            Assert.Equal(AlertSeverity.Critical, (await check.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == unheld.Id, Ct)).MinimumSeverity);
            Assert.True(await check.WebhookEndpoints.AsNoTracking().AnyAsync(e => e.Id == made && e.Key == "made-while-failing", Ct));
            Assert.Equal(2, await check.AuditRecords.AsNoTracking().CountAsync(a => (a.Operation == "webhook.update" || a.Operation == "webhook.create") && a.Outcome == AuditOutcome.Succeeded, Ct));
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<Juice.Notifications.Data.NotificationsDbContext>();
            Assert.False(await notifications.Integrations.AnyAsync(i => i.Id == unheld.Id || i.Id == made, Ct));
        }

        /// <summary>
        /// Through whichever pipeline sends (I74: <c>JUICE_TEST_NOTIFICATIONS_PIPELINE</c>; the alias maps the endpoint, M46b): an endpoint made
        /// through the API fails a delivery at the receiver (a 503, one attempt allowed), an admin retries it and it arrives under the same id,
        /// and a test send arrives signed with the endpoint's secret. Operators and viewers may do neither; each is audited.
        /// </summary>
        [Fact]
        public async Task An_endpoint_made_through_the_api_is_retried_and_tested_through_whichever_pipeline_sends()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync(503);
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.200", ["Webhooks:MaxAttempts"] = "1",
                ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true", ["Notifications:Delivery:MaxAttempts"] = "1",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            });
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
            await Caller.JsonAsync(await admin.SendAsync(HttpMethod.Put, $"{webhooks}/allowlist", new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } }));
            var created = await Caller.JsonAsync(await admin.PostAsync(webhooks, new { key = "ops", url = receiver.Url, secretRef = $"env:{Secret}", events = new[] { "alert.firing" } }));
            var webhook = $"{webhooks}/{created.GetProperty("id").GetGuid()}";

            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            var failed = await DeliveryAsync(admin, webhook, "Failed");
            Assert.Single(receiver.Requests);

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync($"{webhook}/deliveries/{failed}/retry", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{webhook}/test", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{webhook}/deliveries/{Guid.NewGuid()}/retry", new { })).StatusCode);
            await Caller.JsonAsync(await admin.PostAsync($"{webhook}/deliveries/{failed}/retry", new { }));
            Assert.Equal(failed, await DeliveryAsync(admin, webhook, "Delivered"));
            var requests = receiver.Requests.ToList();
            Assert.Equal(2, requests.Count);
            Assert.Equal(requests[0].Id, requests[1].Id);   // the same notification, retried
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{webhook}/deliveries/{failed}/retry", new { })).StatusCode);

            var test = await Caller.JsonAsync(await admin.PostAsync($"{webhook}/test", new { }));
            Assert.True(test.GetProperty("delivered").GetBoolean(), test.ToString());
            var sent = receiver.Requests.Last();
            Assert.Equal(3, receiver.Requests.Count);
            Assert.True(WebhookSigner.Verify("ops-test-secret", sent.Id, sent.Timestamp, sent.Signature, sent.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
            Assert.Contains("\"type\":\"webhook.test\"", sent.Body);

            await using var db = database.Context();
            var audit = await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "webhook.delivery.retry" || a.Operation == "webhook.test").ToListAsync(Ct);
            Assert.Contains(audit, a => a.Operation == "webhook.delivery.retry" && a.Outcome == AuditOutcome.Succeeded && a.Actor == "alice");
            Assert.Contains(audit, a => a.Operation == "webhook.delivery.retry" && a.Outcome == AuditOutcome.Denied);
            Assert.Contains(audit, a => a.Operation == "webhook.test" && a.Outcome == AuditOutcome.Succeeded && a.Metadata["delivered"] == "true");
        }

        /// <summary>The endpoint's one delivery, once it has the status, as the API lists it: legacy's, or the pipeline's through the alias.</summary>
        private static async Task<Guid> DeliveryAsync(Caller admin, string webhook, string status)
        {
            for (var i = 0; i < 150; i++)
            {
                var page = await Caller.JsonAsync(await admin.GetAsync($"{webhook}/deliveries"));
                if (page.GetProperty("items").EnumerateArray().FirstOrDefault(d => d.GetProperty("status").GetString() == status) is { ValueKind: System.Text.Json.JsonValueKind.Object } found)
                {
                    return found.GetProperty("id").GetGuid();
                }
                await Task.Delay(200, Ct);
            }
            throw new TimeoutException($"no {status} delivery");
        }

        /// <summary>
        /// Legacy's allowlist removal always stands while legacy sends (review of #10): when notifications keeps the same destination under
        /// another owner (its API's), the alias's delete is refused, that destination stays notifications', and legacy's entry is gone (204).
        /// </summary>
        [Theory]
        [InlineData("legacy")]
        [InlineData("shadow")]
        public async Task Legacys_allowlist_removal_stands_when_notifications_keeps_the_destination(string pipeline)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = pipeline });
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var notificationsTenant = factory.Services.GetRequiredService<Juice.Notifications.Embedded.NotificationsEmbeddedOptions>().TenantId;
            await using var db = database.Context();
            db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenant, Destination = "10.50.0.0/16", Ports = [8443], LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api });
            await db.SaveChangesAsync(Ct);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = Juice.Notifications.Data.NotificationsMessageContext.Begin("test");
                var outcome = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>()
                    .Send(new Juice.Notifications.Configuration.PutAllowlistEntry(notificationsTenant, "10.50.0.0/16", [8443]), Ct);
                Assert.True(outcome!.Kind is Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created, outcome.Code);
            }

            using var removed = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/tenants/{tenant}/webhooks/allowlist?destination=10.50.0.0%2F16");

            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.False(await db.WebhookAllowlist.AsNoTracking().AnyAsync(a => a.Destination == "10.50.0.0/16", Ct));
            await using var check = factory.Services.CreateAsyncScope();
            var kept = await check.ServiceProvider.GetRequiredService<Juice.Notifications.Data.NotificationsDbContext>().Allowlist.AsNoTracking()
                .SingleAsync(a => a.Destination == "10.50.0.0/16", Ct);
            Assert.Equal(Juice.Notifications.ManagedBy.Api, kept.ManagedBy);
        }
    }
}
