using System.Net;
using Juice.Notifications;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M45-2 review fix, fbd832a): a real concurrent change, not a stubbed refusal. Another transaction changes the alert's correlation in the
    /// pipeline while an operator's acknowledgement or silence is being saved; the pipeline's lost update is a concurrent_change, the adapter's
    /// inline retry gets past it, the request succeeds, and the pipeline ends in the operator's state.
    /// </summary>
    public class M45ConflictQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, Caller Operator, Guid Tenant, Guid Alert)> StartAsync(ScratchDatabase database,
            Action<IServiceCollection>? configure = null)
        {
            var factory = ProducerAdapterTests.Factory(database, configure);
            var client = factory.CreateClient();
            var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, firing);
            Assert.Equal("Open", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            return (factory, client, @operator, tenant, alert);
        }

        /// <summary>
        /// Runs <paramref name="request"/> while another transaction holds and changes the alert's correlation row, committing a second later: the
        /// request's write to the row waits, then finds it changed (xmin), which the pipeline answers with concurrent_change.
        /// </summary>
        private static async Task<HttpResponseMessage> RacingAsync(ScratchDatabase database, Guid alert, Func<Task<HttpResponseMessage>> request)
        {
            await using var other = new NpgsqlConnection(database.ConnectionString);
            await other.OpenAsync(Ct);
            await using var transaction = await other.BeginTransactionAsync(Ct);
            await using (var change = new NpgsqlCommand("UPDATE notifications.correlations SET updated_at = now() WHERE key = @key", other, transaction))
            {
                change.Parameters.AddWithValue("key", $"alert:{alert}");
                Assert.Equal(1, await change.ExecuteNonQueryAsync(Ct));
            }
            var pending = request();
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.False(pending.IsCompleted, "the request didn't wait for the other transaction: no race");
            await transaction.CommitAsync(Ct);
            return await pending;
        }

        [Fact]
        public async Task An_acknowledgement_racing_a_change_to_its_correlation_succeeds_and_the_pipeline_is_acknowledged_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (factory, client, @operator, tenant, alert) = await StartAsync(database);
            await using (factory)
            using (client)
            {
                using var acknowledged = await RacingAsync(database, alert,
                    () => @operator.PostAsync($"/api/v1/tenants/{tenant}/alerts/{alert}/acknowledgement", new { comment = "on it" }, key: "qa-ack-race"));

                Assert.True(acknowledged.IsSuccessStatusCode, $"{(int)acknowledged.StatusCode} {await acknowledged.Content.ReadAsStringAsync(Ct)}");
                Assert.Equal("Acknowledged", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
                await using var db = database.Context();
                Assert.NotNull((await db.Alerts.SingleAsync(a => a.Id == alert, Ct)).AcknowledgedAt);
            }
        }

        [Fact]
        public async Task A_silence_racing_a_change_to_its_alerts_correlation_is_created_and_held_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (factory, client, @operator, tenant, alert) = await StartAsync(database);
            await using (factory)
            using (client)
            {
                using var created = await RacingAsync(database, alert, () => @operator.PostAsync($"/api/v1/tenants/{tenant}/silences",
                    new { comment = "maintenance", service = "shop/orders", endsAt = DateTimeOffset.UtcNow.AddHours(1) }));

                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                var silence = (await Caller.JsonAsync(created)).GetProperty("id").GetGuid();
                await using var db = database.Context();
                Assert.Equal((alert, silence), Assert.Single(await db.AlertHolds.AsNoTracking().ToListAsync(Ct)) is var row ? (row.AlertId, row.SilenceId) : default);
                await using var connection = new NpgsqlConnection(database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var read = new NpgsqlCommand("SELECT hold_reason FROM notifications.correlations WHERE key = @key", connection);
                read.Parameters.AddWithValue("key", $"alert:{alert}");
                Assert.Equal($"silence:{silence}", (string?)await read.ExecuteScalarAsync(Ct));
            }
        }

        /// <summary>Refuses every acknowledgement with a concurrent change while <see cref="On"/>.</summary>
        private sealed class AckRace(INotificationClient inner) : INotificationClient
        {
            public static volatile bool On;
            public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default) => inner.SendAsync(notification, cancellationToken);
            public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
                => On ? throw new NotificationRefusedException(ErrorCodes.ConcurrentChange, "The correlation changed; try again.") : inner.AcknowledgeAsync(correlationKey, by, cancellationToken);
            public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default) => inner.ResolveAsync(correlationKey, outcome, by, cancellationToken);
            public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => inner.CancelAsync(correlationKey, by, cancellationToken);
            public Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default) => inner.HoldAsync(correlationKey, reason, by, cancellationToken);
            public Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => inner.ReleaseAsync(correlationKey, by, cancellationToken);
        }

        /// <summary>
        /// An acknowledgement whose pipeline call stays refused past the inline retries is saved (2xx) and "left to heal". A silence's hold is
        /// healed by the hold pass; an acknowledgement has no such pass, so the pipeline's correlation stays open and its escalation goes on paging
        /// an alert someone acknowledged.
        /// </summary>
        [Fact(Skip = "QA defect D4: an acknowledgement left to heal after concurrent_change is never told to the pipeline.")]
        public async Task An_acknowledgement_left_to_heal_reaches_the_pipeline_once_the_race_is_over_Async()
        {
            LocalStack.RequireAvailable();
            AckRace.On = false;
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (factory, client, @operator, tenant, alert) = await StartAsync(database, services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(INotificationClient));
                services.Add(ServiceDescriptor.Describe(typeof(INotificationClient), sp => new AckRace(original.ImplementationInstance as INotificationClient
                    ?? (INotificationClient?)original.ImplementationFactory?.Invoke(sp)
                    ?? (INotificationClient)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)), original.Lifetime));
            });
            await using (factory)
            using (client)
            {
                AckRace.On = true;
                using var acknowledged = await @operator.PostAsync($"/api/v1/tenants/{tenant}/alerts/{alert}/acknowledgement", new { comment = "on it" }, key: "qa-ack-heal");
                Assert.True(acknowledged.IsSuccessStatusCode);
                AckRace.On = false;

                // Every pass that could heal it, run by hand: the hold pass and the release pass.
                var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
                await new SilenceHoldPass(scopes, Options.Create(new PlatformNotificationsOptions()), NullLogger<SilenceHoldPass>.Instance).RunOnceAsync(Ct);
                await new NotificationsReleasePass(scopes, Options.Create(new Webhooks.WebhookOptions()), TimeProvider.System, NullLogger<NotificationsReleasePass>.Instance)
                    .ReleaseOnceAsync(Ct);
                Assert.Equal("Acknowledged", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            }
        }
    }
}
