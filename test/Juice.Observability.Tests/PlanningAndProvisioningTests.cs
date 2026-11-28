using System.Net;
using NotificationsDbContext = Juice.Notifications.Data.NotificationsDbContext;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The pipeline as the only one (M50): a transition planned once whoever hands it over, and what the provisioning file declares (an endpoint, its
    /// allowlist, the platform's own tenant only) reaching a real receiver with the 1.x body.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class PlanningAndProvisioningTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // ─── Planning ──────────────────────────────────────────────────────────

        [Fact]
        public async Task PlanNotifications_DeliveredTwice_PlannedOnce()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            _ = factory.Server;
            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);

            // The outbox delivers the same message again (right after the commit and from delivery, or on two replicas).
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            Assert.Equal(($"transition:{transition}", $"alert:{alert}"), (request.Key, request.Correlation));
            await using var db = database.Context();
            Assert.NotNull((await db.AlertTransitions.AsNoTracking().SingleAsync(t => t.Id == transition, Ct)).NotificationsPlannedAt);
        }

        [Fact]
        public async Task PlanNotifications_TwoReplicasRace_OneRequest()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var first = ProducerAdapterTests.Factory(database);
            await using var second = ProducerAdapterTests.Factory(database);
            _ = first.Server;
            _ = second.Server;
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);

            // At once, as two replicas handling the same message are: one wins at the transition's token, the other rolls back (and, tried again by the outbox, finds it planned).
            var raced = await Task.WhenAll(TryAsync(first, database, transition), TryAsync(second, database, transition));
            Assert.Contains(true, raced);
            foreach (var factory in new[] { first, second })
            {
                await ProducerAdapterTests.PlanAsync(factory, database, transition);
            }

            Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM notifications.requests"));
        }

        private static async Task<bool> TryAsync(WebApplicationFactory<Program> factory, ScratchDatabase database, Guid transition)
        {
            try
            {
                await ProducerAdapterTests.PlanAsync(factory, database, transition);
                return true;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is DbUpdateConcurrencyException)
            {
                return false;
            }
        }

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        // ─── The provisioning file ─────────────────────────────────────────────

        [Fact]
        public async Task Provisioning_FileEndpoint_SendsSigned_ReceiverSeesSameBodyAndSignature()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await TestReceiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var file = ProvisionedHost.Write(receiver);
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path));
            _ = factory.Server;
            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);

            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            await ProvisionedHost.WaitAsync(() => !receiver.Requests.IsEmpty, "the notification to reach the receiver");
            var received = Assert.Single(receiver.Requests);
            // Signed as every webhook was: the receiver's own check with the secret the file names.
            Assert.True(ProvisionedHost.Verifies(received), "the signature doesn't verify");
            // The body is the 1.x webhook's, byte for byte: the platform's payload with the delivery's id (the webhook's id).
            await using var db = database.Context();
            var storedAlert = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert, Ct);
            var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == storedAlert.RuleId, Ct);
            var storedTransition = await db.AlertTransitions.AsNoTracking().SingleAsync(t => t.Id == transition, Ct);
            Assert.Equal(NotificationPlan.Payload(Guid.Parse(received.Id), NotificationPlan.Firing, LocalStack.TenantKey, rule, storedAlert, storedTransition, null), received.Body);
            // The rows are the file's: read-only on the admin pages.
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Equal("config", (await notifications.Integrations.AsNoTracking().SingleAsync(Ct)).ManagedBy);
            Assert.Equal("config", (await notifications.Routes.AsNoTracking().SingleAsync(Ct)).ManagedBy);
        }

        [Fact]
        public async Task Provisioning_FileAllowlistPrivateHost_DefaultPortAndMixedCaseHost_Allowed_OtherPortRefused()
        {
            LocalStack.RequireAvailable();
            await using var allowed = await TestReceiver.StartAsync();
            await using var otherPort = await TestReceiver.StartAsync();
            Assert.NotEqual(allowed.Port, otherPort.Port);
            await using var database = await LocalStack.CreateDatabaseAsync();
            // The private address with its one port; a host name in mixed case with no port (443); the second receiver (same address, another port) not listed.
            await using var file = ProvisionedHost.Write(allowed, allowlist: [new { destination = "HOOKS.Internal.Example.COM" }], more: otherPort);
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path));
            _ = factory.Server;

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var entries = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Allowlist.AsNoTracking().ToListAsync(Ct);
                var host = Assert.Single(entries, e => e.Destination.Equals("hooks.internal.example.com", StringComparison.OrdinalIgnoreCase));
                // No port is 443 only; the host as the pipeline compares it.
                Assert.True(host.Ports.Length == 0 || host.Ports.SequenceEqual([443]));
                Assert.Equal([allowed.Port], Assert.Single(entries, e => e.Destination.EndsWith("/32", StringComparison.Ordinal)).Ports);
            }

            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            await ProvisionedHost.WaitAsync(() => !allowed.Requests.IsEmpty, "the notification to reach the allowlisted port");
            await ProvisionedHost.WaitAsync(() => AllDecidedAsync(factory).GetAwaiter().GetResult(), "both deliveries to be decided");
            // The other port is refused: nothing was posted, and the delivery says it failed.
            Assert.Empty(otherPort.Requests);
            await using var check = factory.Services.CreateAsyncScope();
            var deliveries = await check.ServiceProvider.GetRequiredService<NotificationsDbContext>().Deliveries.AsNoTracking().ToListAsync(Ct);
            Assert.Equal(2, deliveries.Count);
            Assert.Equal(Juice.Notifications.DeliveryStatus.Delivered, Assert.Single(deliveries, d => d.DestinationSnapshot.Contains($":{allowed.Port}", StringComparison.Ordinal)).Status);
            Assert.Equal(Juice.Notifications.DeliveryStatus.Failed, Assert.Single(deliveries, d => d.DestinationSnapshot.Contains($":{otherPort.Port}", StringComparison.Ordinal)).Status);
        }

        private static async Task<bool> AllDecidedAsync(WebApplicationFactory<Program> factory)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var deliveries = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Deliveries.AsNoTracking().ToListAsync(Ct);
            return deliveries.Count == 2 && deliveries.All(d => d.Status is Juice.Notifications.DeliveryStatus.Delivered or Juice.Notifications.DeliveryStatus.Failed);
        }

        [Fact]
        public async Task Provisioning_FileForOtherTenant_PipelineNotStarted_ReadinessDegraded()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await TestReceiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var foreign = Guid.NewGuid().ToString("D");
            await using var file = ProvisionedHost.Write(receiver, tenantKey: foreign);
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path));
            using var client = factory.CreateClient();

            var start = factory.Services.GetRequiredService<PipelineStart>();
            Assert.NotNull(start.Failure);
            Assert.Contains("other than the platform's", start.Failure);
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            // Nothing was written, for the other tenant or the platform's own.
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            Assert.Empty(await notifications.Integrations.AsNoTracking().ToListAsync(Ct));
            Assert.Empty(await notifications.Routes.AsNoTracking().ToListAsync(Ct));
            Assert.Empty(await notifications.Allowlist.AsNoTracking().ToListAsync(Ct));
            // The platform itself runs on: its API answers.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tenants", Ct)).StatusCode);
        }
    }
}
