using System.Net;
using Juice.Observability.Data;
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
    }
}
