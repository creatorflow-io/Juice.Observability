using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Juice.Observability.Tests.ProducerAdapterTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Silences as holds in the pipeline (p5-d3, proof point 3 with notifications' CorrelationHoldTests): a silence covering an open alert whose
    /// firing was sent holds its correlation, in the silence's own transaction; once no silence covers it, it is released. The platform produces
    /// requests as in <c>new</c>; the pipeline's stages don't run here, so what a held or released step does is notifications' to test.
    /// </summary>
    public class SilenceHoldTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed record Hold(DateTimeOffset? HeldAt, string? Reason);

        private static async Task<Hold?> HoldAsync(ScratchDatabase database, Guid alert)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT held_at, hold_reason FROM notifications.correlations WHERE key = @key", connection);
            command.Parameters.AddWithValue("key", $"alert:{alert}");
            await using var reader = await command.ExecuteReaderAsync(Ct);
            return await reader.ReadAsync(Ct) ? new Hold(reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
        }

        private static async Task<List<AlertHold>> RowsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.AlertHolds.AsNoTracking().ToListAsync(Ct);
        }

        /// <summary>A firing planned and sent: the pipeline has its correlation.</summary>
        private static async Task<Guid> FiringSentAsync(WebApplicationFactory<Program> factory, ScratchDatabase database)
        {
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            Assert.NotNull(await HoldAsync(database, alert));
            return alert;
        }

        private static async Task<(Caller Operator, string Silences)> OperatorAsync(WebApplicationFactory<Program> factory, ScratchDatabase database, HttpClient client)
        {
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            return (@operator, $"/api/v1/tenants/{tenant}/silences");
        }

        private static async Task<Guid> CreateAsync(Caller @operator, string silences, string service = "shop/orders")
        {
            var created = await @operator.PostAsync(silences, new { comment = "maintenance", service, endsAt = DateTimeOffset.UtcNow.AddHours(1) });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await Caller.JsonAsync(created)).GetProperty("id").GetGuid();
        }

        private static SilenceHoldPass Pass(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()), NullLogger<SilenceHoldPass>.Instance);

        [Fact]
        public async Task Creating_a_silence_holds_the_alert_and_cancelling_it_releases_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);

            var silence = await CreateAsync(@operator, silences);

            // In the create's own transaction: held when the response comes back.
            var hold = await HoldAsync(database, alert);
            Assert.Equal(ProducerAdapter.SilenceReason(silence), hold!.Reason);
            Assert.NotNull(hold.HeldAt);
            Assert.Equal((alert, silence), Assert.Single(await RowsAsync(database)) is var row ? (row.AlertId, row.SilenceId) : default);

            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{silence}")).StatusCode);

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task Overlapping_silences_release_only_after_both_end_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var first = await CreateAsync(@operator, silences);
            var second = await CreateAsync(@operator, silences, service: "orders");

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{first}");

            // Still covered: the hold moves to the silence still covering it.
            Assert.Equal(ProducerAdapter.SilenceReason(second), (await HoldAsync(database, alert))!.Reason);
            Assert.Equal(second, Assert.Single(await RowsAsync(database)).SilenceId);

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{second}");
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_silence_that_starts_later_is_held_by_the_pass_once_it_starts_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var alert = await FiringSentAsync(factory, database);
            var starts = DateTimeOffset.UtcNow.AddSeconds(2);
            await using (var db = database.Context())
            {
                db.Silences.Add(new Silence
                {
                    TenantId = await TenantAsync(database), Kind = SilenceKind.Maintenance, Comment = "window", CreatedBy = "erin", CreatedAt = DateTimeOffset.UtcNow,
                    StartsAt = starts, EndsAt = starts.AddHours(1), Service = "shop/orders",
                });
                await db.SaveChangesAsync(Ct);
            }

            Assert.Equal(0, await Pass(factory).RunOnceAsync(Ct));
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            await Task.Delay(starts - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300), Ct);
            Assert.Equal(1, await Pass(factory).RunOnceAsync(Ct));

            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
            Assert.Single(await RowsAsync(database));
        }

        [Fact]
        public async Task A_silence_ending_by_time_is_released_by_the_pass_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var silence = await CreateAsync(@operator, silences);
            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
            await using (var db = database.Context())
            {
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }

            Assert.Equal(1, await Pass(factory).RunOnceAsync(Ct));

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_hold_survives_the_resolve_and_is_released_when_the_silence_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var silence = await CreateAsync(@operator, silences);

            await PlanAsync(factory, database, await ResolveAsync(database, alert));
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
            // Q6: the resolve leaves the hold; alert_holds still says what to release.
            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{silence}");
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_rolled_back_change_leaves_the_correlation_unheld_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var alert = await FiringSentAsync(factory, database);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var now = DateTimeOffset.UtcNow;
                db.Silences.Add(new Silence
                {
                    TenantId = await TenantAsync(database), Kind = SilenceKind.Silence, Comment = "x", CreatedBy = "erin", CreatedAt = now, StartsAt = now.AddMinutes(-1),
                    EndsAt = now.AddHours(1), Service = "shop/orders",
                });
                await db.SaveChangesAsync(Ct);
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SilenceHolds>().ReconcileAsync(null, Ct));
                await transaction.RollbackAsync(Ct);
            }

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task An_alert_whose_firing_a_silence_held_has_nothing_to_hold_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            await CreateAsync(@operator, silences);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            // Its firing is a notification hold (I65), never sent: no correlation, so no hold, and no failure.
            Assert.Equal(0, await Pass(factory).RunOnceAsync(Ct));
            Assert.Null(await HoldAsync(database, alert));
            Assert.Empty(await RowsAsync(database));
        }

        [Fact(Skip = "Needs the M45-n alpha (NotificationToSend.OccurredAt): a firing held past a[0] gets step 1 at release.")]
        public void A_firing_held_past_its_first_step_gets_it_at_release()
        {
        }
    }
}
