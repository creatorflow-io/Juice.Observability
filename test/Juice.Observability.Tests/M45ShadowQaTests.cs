using Juice.MediatR;
using Juice.Notifications;
using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M45-2, waves §5.2): shadow live with legacy and the pipeline pointed at the same real receiver (as a migrated setup is), so the
    /// receiver hears legacy's sends only while the pipeline records WouldSend; and a concurrent change forced on the adapter's first try, which
    /// the host's own outbox retries until the transition is planned once.
    /// </summary>
    public class M45ShadowQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string LegacySecretVariable = "JUICE_WEBHOOK_QA_SHADOW_LEGACY";
        private const string LegacySecret = "qa-shadow-legacy-secret";
        private const string PipelineSecretVariable = "JUICE_WEBHOOK_QA_SHADOW_PIPELINE";

        private static async Task<List<string>> StatusesAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var read = new NpgsqlCommand("SELECT status FROM notifications.deliveries ORDER BY created_at", connection);
            await using var reader = await read.ExecuteReaderAsync(Ct);
            var statuses = new List<string>();
            while (await reader.ReadAsync(Ct)) statuses.Add(reader.GetString(0));
            return statuses;
        }

        private static async Task WaitAsync(Func<Task<bool>> done, string what, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!await done())
            {
                Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
                await Task.Delay(200, Ct);
            }
        }

        [Fact]
        public async Task In_shadow_one_receiver_behind_both_pipelines_hears_legacy_only_and_the_pipeline_would_send_each_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            Environment.SetEnvironmentVariable(LegacySecretVariable, LegacySecret);
            Environment.SetEnvironmentVariable(PipelineSecretVariable, "qa-shadow-pipeline-secret");
            await using var database = await LocalStack.CreateDatabaseAsync();
            var tenant = await ProducerAdapterTests.TenantAsync(database);
            // Legacy: an endpoint at the receiver, allowlisted (the setup the migration maps).
            await using (var db = database.Context())
            {
                db.WebhookEndpoints.Add(new WebhookEndpoint
                {
                    TenantId = tenant, Key = "ops", Kind = ChannelKind.Webhook, Url = receiver.Url, SecretRef = $"env:{LegacySecretVariable}",
                    Events = [NotificationPlan.Firing, NotificationPlan.Resolved], DefinitionHash = "h",
                });
                db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenant, Destination = $"{receiver.Address}/32", Ports = [receiver.Port] });
                await db.SaveChangesAsync(Ct);
            }
            // The pipeline: the same receiver, every observability type.
            var provisioning = Path.Combine(Path.GetTempPath(), $"qa-shadow-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(provisioning, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": {
                    "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{PipelineSecretVariable}}",
                                                 "targets": { "ops": { "address": "{{receiver.Url}}" } } } },
                    "routes": { "alerts": { "match": { "type": "observability.*" }, "targets": ["ops"] } },
                    "allowlist": [ { "destination": "{{receiver.Address}}", "ports": [{{receiver.Port}}] } ]
                } } }
                """, Ct);
            try
            {
                await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
                {
                    ["Notifications:Pipeline"] = "shadow", ["Notifications:Provisioning:Path"] = provisioning,
                    ["Notifications:Delivery:ApiSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                    ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                    ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2",
                    // Legacy dispatches (its http to the allowlisted receiver carries over to the pipeline, F1).
                    ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.5",
                });
                _ = factory.Server;

                var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(factory, database, firing);
                await WaitAsync(async () => (await StatusesAsync(database)).Count == 1 && (await StatusesAsync(database))[0] == "WouldSend", "the pipeline's WouldSend");
                await WaitAsync(() => Task.FromResult(receiver.Requests.Count >= 1), "legacy's firing at the receiver");

                var resolved = await ProducerAdapterTests.ResolveAsync(database, alert);
                await ProducerAdapterTests.PlanAsync(factory, database, resolved);
                await WaitAsync(async () => (await StatusesAsync(database)).Count(s => s == "WouldSend") == 2, "the pipeline's second WouldSend");
                await WaitAsync(() => Task.FromResult(receiver.Requests.Count >= 2), "legacy's resolved at the receiver");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);

                // Exactly legacy's two, each signed with legacy's secret; the pipeline's two are WouldSend, with nothing attempted.
                var heard = receiver.Requests.ToArray();
                Assert.Equal(2, heard.Length);
                Assert.All(heard, r => Assert.True(WebhookSigner.Verify(LegacySecret, r.Id, r.Timestamp, r.Signature, r.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)),
                    "a request not signed by legacy"));
                Assert.Contains("\"type\":\"alert.firing\"", heard[0].Body);
                Assert.Contains("\"type\":\"alert.resolved\"", heard[1].Body);
                await using var check = database.Context();
                Assert.Equal(2, await check.WebhookDeliveries.CountAsync(d => d.Status == Data.DeliveryStatus.Delivered, Ct));
                Assert.Equal(["WouldSend", "WouldSend"], await StatusesAsync(database));
                await using var connection = new NpgsqlConnection(database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var attempts = new NpgsqlCommand("SELECT count(*) FROM notifications.delivery_attempts", connection);
                Assert.Equal(0L, Convert.ToInt64(await attempts.ExecuteScalarAsync(Ct)));
            }
            finally
            {
                File.Delete(provisioning);
            }
        }

        /// <summary>The pipeline's client, refusing the first send with a concurrent change, then itself.</summary>
        private sealed class RacingOnce(INotificationClient inner) : INotificationClient
        {
            public static int Refused;

            public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default)
                => Interlocked.Increment(ref Refused) == 1
                    ? throw new NotificationRefusedException(ErrorCodes.ConcurrentChange, "The correlation changed; try again.")
                    : inner.SendAsync(notification, cancellationToken);

            public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => inner.AcknowledgeAsync(correlationKey, by, cancellationToken);
            public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default) => inner.ResolveAsync(correlationKey, outcome, by, cancellationToken);
            public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => inner.CancelAsync(correlationKey, by, cancellationToken);
            public Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default) => inner.HoldAsync(correlationKey, reason, by, cancellationToken);
            public Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => inner.ReleaseAsync(correlationKey, by, cancellationToken);
        }

        [Fact]
        public async Task A_concurrent_change_on_the_first_try_is_retried_by_the_outbox_and_the_transition_planned_once_Async()
        {
            LocalStack.RequireAvailable();
            RacingOnce.Refused = 0;
            await using var database = await LocalStack.CreateDatabaseAsync();
            var tenant = await ProducerAdapterTests.TenantAsync(database);
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "new" })
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    var real = services.Last(s => s.ServiceType == typeof(INotificationClient));
                    services.Remove(real);
                    services.AddScoped<INotificationClient>(sp => new RacingOnce(real.ImplementationType is { } type
                        ? (INotificationClient)ActivatorUtilities.CreateInstance(sp, type)
                        : (INotificationClient)real.ImplementationFactory!(sp)));
                    services.AddTransient<IRequestHandler<M45CoexistenceQaTests.FireAlert, bool>, M45CoexistenceQaTests.FireAlertHandler>();
                }));
            _ = factory.Server;

            // The platform fires an alert as the evaluator does: the transition goes out through the outbox, and its consumer plans it.
            var marker = Guid.NewGuid();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = PlatformMessageContext.Begin("qa");
                Assert.True(await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new M45CoexistenceQaTests.FireAlert(tenant, marker), Ct));
            }
            Guid transition;
            await using (var db = database.Context())
            {
                transition = (await db.AlertTransitions.SingleAsync(t => t.Reason == marker.ToString(), Ct)).Id;
            }
            // Retried: planned, with one request, after the first try was refused.
            await WaitAsync(async () =>
            {
                await using var db = database.Context();
                return (await db.AlertTransitions.SingleAsync(t => t.Id == transition, Ct)).NotificationsPlannedAt is not null;
            }, "the transition to be planned on a retry", seconds: 120);
            Assert.True(RacingOnce.Refused >= 2, $"tries: {RacingOnce.Refused}");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Single(await ProducerAdapterTests.RequestsAsync(database), r => r.Key == $"transition:{transition}");
            Assert.Equal("Open", await ProducerAdapterTests.CorrelationStateAsync(database, (await ProducerAdapterTests.RequestsAsync(database)).Single().Correlation!));
        }
    }
}
