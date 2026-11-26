using Juice.MediatR;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ProducerAdapterTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Held notifications on the new path (phase-5-plan I65, M45 QA plan): a silenced transition's request is kept, and decided once no silence
    /// covers its alert, as legacy decides a held delivery.
    /// </summary>
    public class HeldNotificationTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static NotificationsReleasePass Pass(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new WebhookOptions()), TimeProvider.System,
                NullLogger<NotificationsReleasePass>.Instance);

        private static async Task EndAsync(ScratchDatabase database, Guid silence)
        {
            await using var db = database.Context();
            await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow).SetProperty(x => x.CancelledBy, "erin"), Ct);
        }

        private static async Task<List<NotificationHold>> HoldsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.NotificationHolds.AsNoTracking().OrderBy(h => h.Id).ToListAsync(Ct);
        }

        [Fact]
        public async Task A_silenced_firing_is_sent_once_the_silence_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var silence = await SilenceAsync(database);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            // Still silenced: nothing decided.
            Assert.Equal(0, await Pass(factory).ReleaseOnceAsync(Ct));
            await EndAsync(database, silence);
            Assert.Equal(1, await Pass(factory).ReleaseOnceAsync(Ct));

            var request = Assert.Single(await RequestsAsync(database));
            Assert.Equal(($"transition:{firing}", $"alert:{alert}"), (request.Key, request.Correlation));
            var hold = Assert.Single(await HoldsAsync(database));
            Assert.Equal(NotificationHoldOutcomes.Sent, hold.Outcome);
            Assert.NotNull(hold.DecidedAt);
            // Decided: a later pass leaves it.
            Assert.Equal(0, await Pass(factory).ReleaseOnceAsync(Ct));
        }

        [Fact]
        public async Task An_alert_resolved_while_silenced_has_nothing_to_say_after_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var silence = await SilenceAsync(database);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            await PlanAsync(factory, database, await ResolveAsync(database, alert));

            await EndAsync(database, silence);
            Assert.Equal(2, await Pass(factory).ReleaseOnceAsync(Ct));

            // The firing is stale, and the resolved notice tells of a firing nobody heard of: both dropped.
            Assert.Empty(await RequestsAsync(database));
            Assert.All(await HoldsAsync(database), h => Assert.Equal(NotificationHoldOutcomes.Dropped, h.Outcome));
        }

        [Fact]
        public async Task A_resolve_held_after_a_firing_that_was_sent_is_sent_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            var silence = await SilenceAsync(database);
            var resolved = await ResolveAsync(database, alert);
            await PlanAsync(factory, database, resolved);
            Assert.Single(await RequestsAsync(database));

            await EndAsync(database, silence);
            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Contains(await RequestsAsync(database), r => r.Key == $"transition:{resolved}" && r.Correlation is null);
        }

        [Fact]
        public async Task An_acknowledged_alert_has_its_held_firing_dropped_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var silence = await SilenceAsync(database);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var _ = PlatformMessageContext.Begin("test");
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new AcknowledgeAlert(await TenantAsync(database), alert, null, "erin", null, "", "ack", "h"), Ct);
            }

            await EndAsync(database, silence);
            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Empty(await RequestsAsync(database));
            Assert.Equal(NotificationHoldOutcomes.Dropped, Assert.Single(await HoldsAsync(database)).Outcome);
        }

        [Fact]
        public async Task Another_silence_still_covering_keeps_it_held_under_that_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var first = await SilenceAsync(database);
            var (_, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            var second = await SilenceAsync(database, "orders");

            await EndAsync(database, first);
            await Pass(factory).ReleaseOnceAsync(Ct);

            var hold = Assert.Single(await HoldsAsync(database));
            Assert.Equal((second, (DateTimeOffset?)null), (hold.SilenceId, hold.DecidedAt));
            Assert.Empty(await RequestsAsync(database));
            await EndAsync(database, second);
            await Pass(factory).ReleaseOnceAsync(Ct);
            Assert.Single(await RequestsAsync(database));
        }

        [Fact]
        public async Task Passes_on_two_replicas_send_one_request_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var one = Factory(database);
            await using var two = Factory(database);
            _ = one.Server;
            _ = two.Server;
            var silence = await SilenceAsync(database);
            var (_, firing) = await FireAsync(database);
            await PlanAsync(one, database, firing);
            await EndAsync(database, silence);

            var released = await Task.WhenAll(Pass(one).ReleaseOnceAsync(Ct), Pass(two).ReleaseOnceAsync(Ct));

            Assert.Equal(1, released.Sum());
            Assert.Single(await RequestsAsync(database));
        }
    }
}
