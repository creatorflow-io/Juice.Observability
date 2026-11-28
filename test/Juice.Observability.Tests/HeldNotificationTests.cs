using Juice.MediatR;
using Juice.Notifications;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
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
        public async Task One_bad_kept_call_doesnt_block_the_others_and_is_given_up_after_a_few_tries_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            var tenant = await TenantAsync(database);
            Guid bad;
            await using (var db = database.Context())
            {
                // Ahead of the good one: a kept request that can't be read.
                var broken = new NotificationParkedCall { TenantId = tenant, Kind = NotificationParkedCalls.Send, Request = """{"type": 1}""", CreatedAt = DateTimeOffset.UtcNow };
                db.NotificationParkedCalls.Add(broken);
                await db.SaveChangesAsync(Ct);
                bad = broken.Id;
                db.NotificationParkedCalls.Add(new NotificationParkedCall
                {
                    TenantId = tenant, Kind = NotificationParkedCalls.Acknowledge, CorrelationKey = $"alert:{alert}", By = "erin", CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }

            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Equal("Acknowledged", await CorrelationStateAsync(database, $"alert:{alert}"));
            await using (var db = database.Context())
            {
                var kept = await db.NotificationParkedCalls.AsNoTracking().SingleAsync(c => c.Id == bad, Ct);
                Assert.Equal((1, (DateTimeOffset?)null), (kept.Attempts, kept.ReplayedAt));
                Assert.NotNull(kept.LastError);
            }
            for (var i = 1; i < NotificationHoldOutcomes.MaxAttempts; i++)
            {
                await Pass(factory).ReleaseOnceAsync(Ct);
            }
            await using (var db = database.Context())
            {
                var given = await db.NotificationParkedCalls.AsNoTracking().SingleAsync(c => c.Id == bad, Ct);
                Assert.Equal(NotificationHoldOutcomes.MaxAttempts, given.Attempts);
                Assert.NotNull(given.ReplayedAt);
            }
            Assert.Equal(0, await Pass(factory).ReleaseOnceAsync(Ct));
        }

        /// <summary>A pipeline whose every send meets a correlation changed under it (acknowledged or resolved elsewhere): transient.</summary>
        private sealed class ConcurrentlyChanged : INotificationClient
        {
            private static NotificationRefusedException Refusal() => new(ErrorCodes.ConcurrentChange, "The correlation changed; try again.");
            public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default) => throw Refusal();
            public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => throw Refusal();
            public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default) => throw Refusal();
            public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => throw Refusal();
            public Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default) => throw Refusal();
            public Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default) => throw Refusal();
        }

        [Fact]
        public async Task A_concurrent_change_rolls_the_plan_back_to_be_tried_again_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database, s => s.AddScoped<INotificationClient, ConcurrentlyChanged>());
            _ = factory.Server;
            var (_, firing) = await FireAsync(database);

            var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() => PlanAsync(factory, database, firing));

            // Not planned, so the outbox's retry plans it again against the new state.
            Assert.Equal(ErrorCodes.ConcurrentChange, refused.Code);
            await using var db = database.Context();
            Assert.Null((await db.AlertTransitions.SingleAsync(t => t.Id == firing, Ct)).NotificationsPlannedAt);
        }

        [Fact]
        public async Task A_concurrent_change_at_release_is_an_attempt_not_a_drop_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid silence;
            await using (var healthy = Factory(database))
            {
                _ = healthy.Server;
                silence = await SilenceAsync(database);
                var (_, firing) = await FireAsync(database);
                await PlanAsync(healthy, database, firing);
            }
            await EndAsync(database, silence);
            await using var factory = Factory(database, s => s.AddScoped<INotificationClient, ConcurrentlyChanged>());
            _ = factory.Server;

            await Pass(factory).ReleaseOnceAsync(Ct);

            var hold = Assert.Single(await HoldsAsync(database));
            Assert.Equal((1, (DateTimeOffset?)null, (string?)null), (hold.Attempts, hold.DecidedAt, hold.Outcome));
            Assert.Contains(ErrorCodes.ConcurrentChange, hold.LastError);
        }

        /// <summary>The alert fires again after its resolve: a new firing transition, the alert firing.</summary>
        private static async Task<Guid> FireAgainAsync(ScratchDatabase database, Guid alertId)
        {
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(a => a.Id == alertId, Ct);
            var at = DateTimeOffset.UtcNow.AddSeconds(1);
            var transition = new AlertTransition { TenantId = alert.TenantId, AlertId = alert.Id, FromState = AlertState.Resolved, ToState = AlertState.Firing, At = at, Value = 0.5, Reason = "again" };
            db.AlertTransitions.Add(transition);
            alert.State = AlertState.Firing;
            alert.ResolvedAt = null;
            alert.FiredAt = at;
            await db.SaveChangesAsync(Ct);
            return transition.Id;
        }

        /// <summary>A firing that was sent, then a silence, then a resolve it held; returns the alert, the held resolve's transition and the silence.</summary>
        private static async Task<(Guid Alert, Guid Resolved, Guid Silence)> HeldResolveAsync(WebApplicationFactory<Program> factory, ScratchDatabase database)
        {
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            var silence = await SilenceAsync(database);
            var resolved = await ResolveAsync(database, alert);
            await PlanAsync(factory, database, resolved);
            return (alert, resolved, silence);
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_HeldResolve_AlertFiredAgain_NoticeDropped_NoResolveCall()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid alert, silence;
            await using (var healthy = Factory(database))
            {
                _ = healthy.Server;
                (alert, _, silence) = await HeldResolveAsync(healthy, database);
                await FireAgainAsync(database, alert);
                await EndAsync(database, silence);
            }
            // Any call to the pipeline throws here: the stale notice is dropped with none made (neither its resolve nor its send).
            await using var factory = Factory(database, s => s.AddScoped<INotificationClient, ConcurrentlyChanged>());
            _ = factory.Server;

            await Pass(factory).ReleaseOnceAsync(Ct);

            var hold = Assert.Single(await HoldsAsync(database), h => h.Event == NotificationPlan.Resolved);
            Assert.Equal((NotificationHoldOutcomes.DroppedStale, 0), (hold.Outcome, hold.Attempts));
            Assert.Single(await RequestsAsync(database));
        }

        /// <summary>The hold's resolve was not asked at plan (the pipeline was down), so the pass would make it, unless the alert fired again since: the guard on that call.</summary>
        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_HeldResolve_ResolveNotAsked_AlertFiredAgain_NoResolveCall()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid alert, silence;
            await using (var healthy = Factory(database))
            {
                _ = healthy.Server;
                (alert, _, silence) = await HeldResolveAsync(healthy, database);
                await using (var db = database.Context())
                {
                    Assert.Equal(1, await db.NotificationHolds.Where(h => h.Event == NotificationPlan.Resolved).ExecuteUpdateAsync(h => h.SetProperty(x => x.ResolveAsked, false), Ct));
                }
                await FireAgainAsync(database, alert);
                await EndAsync(database, silence);
            }
            // Any call to the pipeline throws here: a resolve made for the stale hold would leave it retried, not dropped.
            await using var factory = Factory(database, s => s.AddScoped<INotificationClient, ConcurrentlyChanged>());
            _ = factory.Server;

            await Pass(factory).ReleaseOnceAsync(Ct);

            var hold = Assert.Single(await HoldsAsync(database), h => h.Event == NotificationPlan.Resolved);
            Assert.Equal((NotificationHoldOutcomes.DroppedStale, 0), (hold.Outcome, hold.Attempts));
        }

        /// <summary>
        /// A held resolve dropped for another reason (the receiver was never told of the firing) is plain <c>dropped</c> though the alert fired
        /// again later: it is not the stale-resolve rule's (legacy would have sent it: told_per_alert).
        /// </summary>
        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_HeldResolve_DroppedForAnotherReason_AlertFiredLater_NotTaggedStale()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, _, silence) = await HeldResolveAsync(factory, database);
            await using (var db = database.Context())
            {
                // The firing was held and dropped earlier: the receiver was never told of it.
                var held = await db.NotificationHolds.AsNoTracking().SingleAsync(h => h.Event == NotificationPlan.Resolved, Ct);
                var firing = await db.AlertTransitions.AsNoTracking().FirstAsync(t => t.AlertId == alert && t.ToState == AlertState.Firing, Ct);
                db.NotificationHolds.Add(new NotificationHold
                {
                    TenantId = held.TenantId, AlertId = alert, TransitionId = firing.Id, Event = NotificationPlan.Firing, SilenceId = silence, Request = held.Request,
                    CreatedAt = DateTimeOffset.UtcNow, DecidedAt = DateTimeOffset.UtcNow, Reason = NotificationHoldReasons.Silence, Outcome = NotificationHoldOutcomes.Dropped,
                });
                await db.SaveChangesAsync(Ct);
            }
            await FireAgainAsync(database, alert);
            await EndAsync(database, silence);

            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Equal(NotificationHoldOutcomes.Dropped, Assert.Single(await HoldsAsync(database), h => h.Event == NotificationPlan.Resolved).Outcome);
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_HeldResolve_NotFiredSince_NoticeSent()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, resolved, silence) = await HeldResolveAsync(factory, database);
            await EndAsync(database, silence);

            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Equal(NotificationHoldOutcomes.Sent, Assert.Single(await HoldsAsync(database)).Outcome);
            Assert.Contains(await RequestsAsync(database), r => r.Key == $"transition:{resolved}");
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_HeldResolve_FiredAndResolvedAgainSince_NoticeDropped()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, resolved, silence) = await HeldResolveAsync(factory, database);
            // It fired again and resolved again before the silence ended: the alert is resolved now, but a firing came after the held resolve.
            await FireAgainAsync(database, alert);
            await ResolveAsync(database, alert);
            await EndAsync(database, silence);

            await Pass(factory).ReleaseOnceAsync(Ct);

            Assert.Equal(NotificationHoldOutcomes.DroppedStale, Assert.Single(await HoldsAsync(database)).Outcome);
            Assert.DoesNotContain(await RequestsAsync(database), r => r.Key == $"transition:{resolved}");
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task ReleasePass_Retry_AfterError_SameDecision()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid alert, silence, again;
            await using (var healthy = Factory(database))
            {
                _ = healthy.Server;
                (alert, _, silence) = await HeldResolveAsync(healthy, database);
                // The recurrence's own firing is held too: a send that fails on the first pass and goes through on the second.
                again = await FireAgainAsync(database, alert);
                await PlanAsync(healthy, database, again);
                await EndAsync(database, silence);
            }
            await using (var failing = Factory(database, s => s.AddScoped<INotificationClient, ConcurrentlyChanged>()))
            {
                _ = failing.Server;
                await Pass(failing).ReleaseOnceAsync(Ct);
            }
            var first = await HoldsAsync(database);
            // (Dropped, not stale: the recurrence's firing is still unsent, so the receiver is not told of a firing, which is why the resolve is dropped.)
            Assert.Equal(NotificationHoldOutcomes.Dropped, first.Single(h => h.Event == NotificationPlan.Resolved).Outcome);
            Assert.Equal((1, (string?)null), (first.Single(h => h.Event == NotificationPlan.Firing).Attempts, first.Single(h => h.Event == NotificationPlan.Firing).Outcome));

            await using var factory = Factory(database);
            _ = factory.Server;
            await Pass(factory).ReleaseOnceAsync(Ct);

            var second = await HoldsAsync(database);
            Assert.Equal(NotificationHoldOutcomes.Dropped, second.Single(h => h.Event == NotificationPlan.Resolved).Outcome);
            Assert.Equal(NotificationHoldOutcomes.Sent, second.Single(h => h.Event == NotificationPlan.Firing).Outcome);
            Assert.Equal(0, await Pass(factory).ReleaseOnceAsync(Ct));
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
