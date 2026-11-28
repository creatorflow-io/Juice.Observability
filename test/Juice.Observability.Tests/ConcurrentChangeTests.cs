using System.Net;
using Juice.Notifications;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ProducerAdapterTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The pipeline's <c>concurrent_change</c> on a person's call (review #8 round 1): the alert's correlation changed under a silence or an
    /// acknowledgement. Tried again at once; still refused, it is no failure of their change (saved, and healed later), never a 500.
    /// </summary>
    public sealed class ConcurrentChangeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>How many of the pipeline's calls are still refused with a concurrent change (0: none; each refusal takes one).</summary>
        private sealed class Race
        {
            private int _left;
            public int Refused;
            public int Left { set => Volatile.Write(ref _left, value); }

            public void Check()
            {
                if (Interlocked.Decrement(ref _left) >= 0)
                {
                    Interlocked.Increment(ref Refused);
                    throw new NotificationRefusedException(ErrorCodes.ConcurrentChange, "The correlation changed; try again.");
                }
                Interlocked.Exchange(ref _left, 0);
            }
        }

        /// <summary>The embedded client, refused while the race says so.</summary>
        private sealed class Racing(INotificationClient inner, Race race) : INotificationClient
        {
            public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default)
            { race.Check(); return inner.SendAsync(notification, cancellationToken); }
            public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            { race.Check(); return inner.AcknowledgeAsync(correlationKey, by, cancellationToken); }
            public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default)
            { race.Check(); return inner.ResolveAsync(correlationKey, outcome, by, cancellationToken); }
            public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            { race.Check(); return inner.CancelAsync(correlationKey, by, cancellationToken); }
            public Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default)
            { race.Check(); return inner.HoldAsync(correlationKey, reason, by, cancellationToken); }
            public Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            { race.Check(); return inner.ReleaseAsync(correlationKey, by, cancellationToken); }
        }

        private static WebApplicationFactory<Program> RacingFactory(ScratchDatabase database, Race race) => Factory(database, services =>
        {
            var original = services.Last(d => d.ServiceType == typeof(INotificationClient));
            services.Add(ServiceDescriptor.Describe(typeof(INotificationClient), sp => new Racing(original.ImplementationInstance as INotificationClient
                ?? (INotificationClient?)original.ImplementationFactory?.Invoke(sp)
                ?? (INotificationClient)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), race), original.Lifetime));
        });

        private static async Task<(Caller Operator, Guid Tenant)> OperatorAsync(HttpClient client, ScratchDatabase database)
        {
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            return (@operator, tenant);
        }

        private static async Task<Guid> FiringSentAsync(WebApplicationFactory<Program> factory, ScratchDatabase database)
        {
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));
            return alert;
        }

        private static async Task<HttpResponseMessage> SilenceAsync(Caller @operator, Guid tenant)
            => await @operator.PostAsync($"/api/v1/tenants/{tenant}/silences", new { comment = "maintenance", service = "shop/orders", endsAt = DateTimeOffset.UtcNow.AddHours(1) });

        private static async Task<List<AlertHold>> RowsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.AlertHolds.AsNoTracking().ToListAsync(Ct);
        }

        [Fact]
        public async Task A_silence_created_while_its_alerts_correlation_changes_is_saved_and_held_later_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var race = new Race();
            await using var factory = RacingFactory(database, race);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, tenant) = await OperatorAsync(client, database);
            race.Left = int.MaxValue;

            using var created = await SilenceAsync(@operator, tenant);

            // The operator's silence is saved; the hold isn't, and nothing says it is.
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(1 + ProducerAdapter.ConflictRetries, race.Refused);
            var silence = (await Caller.JsonAsync(created)).GetProperty("id").GetGuid();
            await using (var db = database.Context())
            {
                Assert.True(await db.Silences.AnyAsync(s => s.Id == silence, Ct));
            }
            Assert.Empty(await RowsAsync(database));

            // The race over, the hold pass holds it.
            race.Left = 0;
            await new SilenceHoldPass(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()),
                NullLogger<SilenceHoldPass>.Instance).RunOnceAsync(Ct);
            Assert.Equal((alert, silence), Assert.Single(await RowsAsync(database)) is var row ? (row.AlertId, row.SilenceId) : default);
        }

        [Fact]
        public async Task A_change_that_clears_on_a_retry_holds_in_the_silences_own_transaction_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var race = new Race();
            await using var factory = RacingFactory(database, race);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, tenant) = await OperatorAsync(client, database);
            race.Left = ProducerAdapter.ConflictRetries;

            using var created = await SilenceAsync(@operator, tenant);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(ProducerAdapter.ConflictRetries, race.Refused);
            Assert.Equal(alert, Assert.Single(await RowsAsync(database)).AlertId);
        }

        [Fact]
        public async Task An_acknowledgement_while_the_correlation_changes_is_saved_kept_and_replayed_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var race = new Race();
            await using var factory = RacingFactory(database, race);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, tenant) = await OperatorAsync(client, database);
            race.Left = int.MaxValue;

            using var acknowledged = await @operator.PostAsync($"/api/v1/tenants/{tenant}/alerts/{alert}/acknowledgement", new { comment = "on it" }, key: "ack-race");

            Assert.True(acknowledged.IsSuccessStatusCode, $"{(int)acknowledged.StatusCode} {await acknowledged.Content.ReadAsStringAsync(Ct)}");
            Assert.Equal(1 + ProducerAdapter.ConflictRetries, race.Refused);
            await using (var db = database.Context())
            {
                Assert.NotNull((await db.Alerts.SingleAsync(a => a.Id == alert, Ct)).AcknowledgedAt);
                // Not lost (review #8 round 2): kept in the acknowledgement's own transaction, with the mode it was made in.
                var kept = Assert.Single(await db.NotificationParkedCalls.AsNoTracking().ToListAsync(Ct));
                Assert.Equal((NotificationParkedCalls.Acknowledge, $"alert:{alert}", "new", (DateTimeOffset?)null), (kept.Kind, kept.CorrelationKey, kept.Pipeline, kept.ReplayedAt));
            }
            Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));

            // The race over, the release pass replays it: the correlation is acknowledged, so its escalation stops.
            race.Left = 0;
            await new NotificationsReleasePass(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()),
                TimeProvider.System, NullLogger<NotificationsReleasePass>.Instance).ReleaseOnceAsync(Ct);
            Assert.Equal("Acknowledged", await CorrelationStateAsync(database, $"alert:{alert}"));
            await using (var db = database.Context())
            {
                Assert.NotNull(Assert.Single(await db.NotificationParkedCalls.AsNoTracking().ToListAsync(Ct)).ReplayedAt);
            }
        }

        [Fact]
        public async Task A_concurrent_change_that_reaches_a_request_is_a_409_not_a_500_Async()
        {
            var http = new DefaultHttpContext();
            http.Response.Body = new MemoryStream();
            var handler = new NotificationsConflictExceptionHandler(new NoProblemWriter());
            var refused = new NotificationRefusedException(ErrorCodes.ConcurrentChange, "The correlation changed; try again.");

            Assert.True(await handler.TryHandleAsync(http, new InvalidOperationException("wrapped", refused), Ct));
            Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
            Assert.False(await handler.TryHandleAsync(new DefaultHttpContext(), new NotificationRefusedException(ErrorCodes.NotFound, "none"), Ct));
            Assert.False(await handler.TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("other"), Ct));
        }

        private sealed class NoProblemWriter : IProblemDetailsService
        {
            public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
            public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(false);
        }
    }
}
