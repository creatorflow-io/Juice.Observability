using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Shadow;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Juice.Notifications;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// What the shadow comparer finds due on the escalation side (M46c): the new pipeline's escalation steps legacy never sent, read a page at a
    /// time so escalations already compared cannot hide a newer one that wasn't. Also the explained rule for a stale resolve the new side drops.
    /// </summary>
    public class ShadowComparerDueTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly Guid TenantId = Guid.Parse("0193f0aa-0000-7000-8000-0000000000f1");

        private static WebApplicationFactory<Program> Factory(ScratchDatabase database)
            => new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "Shadow",
                ["Notifications:Shadow:Interval"] = "01:00:00",
            });

        /// <summary>One alert with a transition per escalation, and the pipeline's rows for it.</summary>
        private sealed class Seed(WebApplicationFactory<Program> factory, ScratchDatabase database)
        {
            public Guid Tenant { get; private set; }
            public Guid Application { get; private set; }
            private Guid _platformTenant, _alert;
            public DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

            public async Task StartAsync()
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var embedded = scope.ServiceProvider.GetRequiredService<NotificationsEmbeddedOptions>();
                var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                Tenant = embedded.TenantId;
                Application = await pipeline.Applications.AsNoTracking().Where(a => a.Key == embedded.ApplicationKey).Select(a => a.Id).SingleAsync(Ct);
                (_alert, _) = await ProducerAdapterTests.FireAsync(database);
                _platformTenant = await ProducerAdapterTests.TenantAsync(database);
            }

            /// <summary>A transition with an escalation of the given tenant and application at <c>nextStep</c>; steps in <c>compared</c> already have a comparison.</summary>
            public async Task<Guid> EscalationAsync(DateTimeOffset updatedAt, int nextStep = 1, int[]? compared = null, Guid? tenant = null, Guid? application = null)
            {
                var transition = new AlertTransition { TenantId = _platformTenant, AlertId = _alert, FromState = AlertState.Pending, ToState = AlertState.Firing, At = Base, Value = 1, Reason = "seed" };
                await using (var db = database.Context())
                {
                    db.AlertTransitions.Add(transition);
                    foreach (var step in compared ?? [])
                    {
                        db.ShadowComparisons.Add(new ShadowComparison
                        {
                            TenantId = _platformTenant, TransitionId = transition.Id, EscalationStep = step, AlertId = _alert, Event = NotificationPlan.Escalated, ComparedAt = DateTimeOffset.UtcNow,
                        });
                    }
                    await db.SaveChangesAsync(Ct);
                }
                await using var scope = factory.Services.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var (t, app) = (tenant ?? Tenant, application ?? Application);
                // Rows whose parents are not the point of the test: the keys are not enforced for the seed.
                await pipeline.Database.OpenConnectionAsync(Ct);
                await pipeline.Database.ExecuteSqlRawAsync("SET session_replication_role = replica", Ct);
                var request = new NotificationRequest
                {
                    TenantId = t, ApplicationId = app, IdempotencyKey = $"transition:{transition.Id}", TypeId = Guid.CreateVersion7(), TypeKey = "observability.alert.firing",
                    TypeVersion = 1, SchemaHash = "h", Data = "{}", Recipients = "[]", AcceptedAt = updatedAt,
                };
                var notification = new Notification { TenantId = t, RequestId = request.Id, CreatedAt = updatedAt, UpdatedAt = updatedAt };
                pipeline.Requests.Add(request);
                pipeline.Notifications.Add(notification);
                pipeline.Escalations.Add(new Escalation
                {
                    TenantId = t, NotificationId = notification.Id, CorrelationId = Guid.CreateVersion7(), PolicyId = Guid.CreateVersion7(), NextStep = nextStep,
                    CreatedAt = updatedAt, UpdatedAt = updatedAt,
                });
                await pipeline.SaveChangesAsync(Ct);
                await pipeline.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT", Ct);
                return transition.Id;
            }

            public async Task<List<(Guid Transition, int Step)>> DueAsync(int batch, params Guid[] deferred)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var due = await ShadowComparer.DueAsync(db, pipeline, Tenant, Application, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, batch, [.. deferred], Ct);
                return [.. due.Select(u => (u.Transition, u.Step))];
            }
        }

        private static async Task<(ScratchDatabase Database, WebApplicationFactory<Program> Factory, Seed Seed)> StartAsync()
        {
            var database = await LocalStack.CreateDatabaseAsync();
            // The seed switches foreign-key enforcement off (session_replication_role), which only a superuser may do: skip, don't fail, on any other role.
            bool superuser;
            await using (var probe = database.Context())
            {
                superuser = await probe.Database.SqlQueryRaw<bool>("SELECT rolsuper AS \"Value\" FROM pg_roles WHERE rolname = current_user").SingleAsync(Ct);
            }
            if (!superuser)
            {
                await database.DisposeAsync();
            }
            Assert.SkipUnless(superuser, "the test database's role is not a superuser, which seeding rows without their parents (session_replication_role) needs");
            var factory = Factory(database);
            _ = factory.Server;
            var seed = new Seed(factory, database);
            await seed.StartAsync();
            return (database, factory, seed);
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task DueAsync_MoreThanBatchCompared_PlusOneNewOnlyStep_ReturnsNewOnly()
        {
            LocalStack.RequireAvailable();
            var (database, factory, seed) = await StartAsync();
            await using var _ = database;
            await using var __ = factory;
            // Batch = 3, and Batch + 2 fully compared escalations older than the one that is not.
            for (var i = 0; i < 5; i++)
            {
                await seed.EscalationAsync(seed.Base.AddMinutes(i), compared: [1]);
            }
            var fresh = await seed.EscalationAsync(seed.Base.AddMinutes(10));

            Assert.Equal([(fresh, 1)], await seed.DueAsync(3));
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task DueAsync_EqualUpdatedAt_PagesWithoutLoopingOrSkipping()
        {
            LocalStack.RequireAvailable();
            var (database, factory, seed) = await StartAsync();
            await using var _ = database;
            await using var __ = factory;
            // Every row at the same instant: the keyset's second column keeps the order stable, so the pages meet neither a repeat nor a gap.
            var at = seed.Base.AddMinutes(1);
            var fresh = new List<Guid>();
            for (var i = 0; i < 7; i++)
            {
                var compared = i is 1 or 2 or 3 or 4 or 5;
                var id = await seed.EscalationAsync(at, compared: compared ? [1] : null);
                if (!compared)
                {
                    fresh.Add(id);
                }
            }

            var due = await seed.DueAsync(2);

            Assert.Equal(fresh.Order().ToList(), due.Select(d => d.Transition).Order().ToList());
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task DueAsync_PartlyComparedEscalation_ReturnsOnlyMissingSteps()
        {
            LocalStack.RequireAvailable();
            var (database, factory, seed) = await StartAsync();
            await using var _ = database;
            await using var __ = factory;
            var transition = await seed.EscalationAsync(seed.Base, nextStep: 3, compared: [1]);

            Assert.Equal([(transition, 2), (transition, 3)], (await seed.DueAsync(10)).OrderBy(d => d.Step).ToList());
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task DueAsync_DeferredTransitions_StillSkipped()
        {
            LocalStack.RequireAvailable();
            var (database, factory, seed) = await StartAsync();
            await using var _ = database;
            await using var __ = factory;
            var deferred = await seed.EscalationAsync(seed.Base);
            var other = await seed.EscalationAsync(seed.Base.AddMinutes(1));

            Assert.Equal([(other, 1)], await seed.DueAsync(10, deferred));
        }

        [Fact, Trait("Milestone", "M46c")]
        public async Task DueAsync_OtherTenantOrApplicationEscalations_Ignored()
        {
            LocalStack.RequireAvailable();
            var (database, factory, seed) = await StartAsync();
            await using var _ = database;
            await using var __ = factory;
            await seed.EscalationAsync(seed.Base, tenant: Guid.CreateVersion7());
            await seed.EscalationAsync(seed.Base.AddMinutes(1), application: Guid.CreateVersion7());
            var mine = await seed.EscalationAsync(seed.Base.AddMinutes(2));

            Assert.Equal([(mine, 1)], await seed.DueAsync(10));
        }

        private static ShadowUnit Unit(string @event, bool held, bool firedSince)
            => new(TenantId, Guid.CreateVersion7(), 0, Guid.CreateVersion7(), @event, false, held, firedSince);

        private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 30, 15, TimeSpan.Zero);

        private static LegacySent Sent() => new("ops", ChannelKind.Webhook, 0, "{}", Juice.Observability.Data.DeliveryStatus.Delivered, null, At, At, 0);

        /// <summary>
        /// The comparer's FiredSince is the release pass's own reason (outcome dropped_stale), not a later firing: a resolve hold dropped plain
        /// (another reason) with the alert firing later is not stale_resolve_dropped, and so is not explained by that rule.
        /// </summary>
        [Fact, Trait("Milestone", "M46c")]
        public async Task StaleDropped_OnlyForDroppedStaleOutcome_NotForALaterFiring()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, _) = await ProducerAdapterTests.FireAsync(database);
            Guid plain, stale;
            await using (var db = database.Context())
            {
                var a = await db.Alerts.SingleAsync(x => x.Id == alert, Ct);
                var t = new AlertTransition[]
                {
                    new() { TenantId = a.TenantId, AlertId = alert, FromState = AlertState.Firing, ToState = AlertState.Resolved, At = DateTimeOffset.UtcNow, Value = 0, Reason = "r1" },
                    new() { TenantId = a.TenantId, AlertId = alert, FromState = AlertState.Firing, ToState = AlertState.Resolved, At = DateTimeOffset.UtcNow.AddSeconds(2), Value = 0, Reason = "r2" },
                    // The alert fires again after both.
                    new() { TenantId = a.TenantId, AlertId = alert, FromState = AlertState.Resolved, ToState = AlertState.Firing, At = DateTimeOffset.UtcNow.AddSeconds(4), Value = 1, Reason = "again" },
                };
                db.AlertTransitions.AddRange(t);
                (plain, stale) = (t[0].Id, t[1].Id);
                foreach (var (transition, outcome) in new[] { (plain, NotificationHoldOutcomes.Dropped), (stale, NotificationHoldOutcomes.DroppedStale) })
                {
                    db.NotificationHolds.Add(new NotificationHold
                    {
                        TenantId = a.TenantId, AlertId = alert, TransitionId = transition, Event = NotificationPlan.Resolved, SilenceId = Guid.CreateVersion7(), Request = "{}",
                        CreatedAt = DateTimeOffset.UtcNow, DecidedAt = DateTimeOffset.UtcNow, Reason = NotificationHoldReasons.Silence, Outcome = outcome,
                    });
                }
                await db.SaveChangesAsync(Ct);
            }

            await using var check = database.Context();
            Assert.False(await ShadowComparer.StaleDroppedAsync(check, plain, Ct));
            Assert.True(await ShadowComparer.StaleDroppedAsync(check, stale, Ct));
        }

        [Fact, Trait("Milestone", "M46c")]
        public void ShadowCompare_StaleResolveDroppedInNew_IsExplained()
        {
            var options = new ShadowCompareOptions(TimeSpan.FromSeconds(60));

            // Legacy sent the held resolve; the alert fired again after it, and the new pipeline dropped it on purpose.
            var stale = Assert.Single(ShadowCompare.Compare(Unit(NotificationPlan.Resolved, held: true, firedSince: true), [Sent()], [], options));
            Assert.Equal((ShadowDifferenceKinds.MissingInNew, ShadowRules.StaleResolveDropped), (stale.Kind, stale.Rule));
            Assert.Contains(ShadowRules.StaleResolveDropped, ShadowRules.All);

            // Never hides a real missing resolve: not held, or not fired again, or not a resolve, is not this rule.
            Assert.Null(Assert.Single(ShadowCompare.Compare(Unit(NotificationPlan.Resolved, held: false, firedSince: true), [Sent()], [], options)).Rule);
            Assert.Equal(ShadowRules.ToldPerAlert, Assert.Single(ShadowCompare.Compare(Unit(NotificationPlan.Resolved, held: true, firedSince: false), [Sent()], [], options)).Rule);
            Assert.NotEqual(ShadowRules.StaleResolveDropped, Assert.Single(ShadowCompare.Compare(Unit(NotificationPlan.Firing, held: true, firedSince: true), [Sent()], [], options)).Rule);
        }
    }
}
