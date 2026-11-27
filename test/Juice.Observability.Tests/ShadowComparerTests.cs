using System.Net;
using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Shadow;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Flow = Juice.Observability.Tests.NotificationFlowTests;
using NewStatus = Juice.Notifications.DeliveryStatus;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The comparer in a real host in <c>shadow</c> (M47): legacy sends a firing to its endpoints while the embedded pipeline plans it in dry run;
    /// once both settled, the unit is compared once, endpoint by endpoint (an integration of the same key), recorded with its differences, and
    /// shown by the shadow report to operators.
    /// </summary>
    [Trait("Milestone", "M46c")]
    public class ShadowComparerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string PipelineSecret = "NOTIFICATIONS_SECRET_SHADOW";
        /// <summary>Legacy's endpoints' own variable: NotificationFlowTests sets its shared one to chat URLs while other classes run.</summary>
        private const string LegacySecret = "JUICE_WEBHOOK_SHADOW";

        static ShadowComparerTests()
        {
            Environment.SetEnvironmentVariable(LegacySecret, "flow-secret");
            Environment.SetEnvironmentVariable(PipelineSecret, "flow-secret");
        }

        private static WebApplicationFactory<Program> Factory(ScratchDatabase database)
            => new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "Shadow",
                ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
                // The hosted comparer waits; the test compares when it says.
                ["Notifications:Shadow:Interval"] = "01:00:00",
            });

        private static ShadowComparer Comparer(WebApplicationFactory<Program> factory, int batch = 100)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ShadowOptions { Grace = TimeSpan.Zero, Batch = batch }),
                factory.Services.GetRequiredService<IOptions<WebhookOptions>>(), TimeProvider.System, NullLogger<ShadowComparer>.Instance);

        private static async Task SendAsync<T>(WebApplicationFactory<Program> factory, Juice.MediatR.IRequest<Juice.Notifications.Outcome<T>> command)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var context = Juice.Notifications.Data.NotificationsMessageContext.Begin("test");
            var outcome = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
            Assert.True(outcome!.Kind is Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created, $"{outcome.Kind} {outcome.Code} {outcome.Message}");
        }

        [Fact]
        public async Task A_shadowed_firing_is_compared_once_endpoint_by_endpoint_and_reported()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var notifications = factory.Services.GetRequiredService<Juice.Notifications.Embedded.NotificationsEmbeddedOptions>().TenantId;
            // Legacy: "ops" and "sre"; the new pipeline has only "ops" (as if "sre" were never migrated).
            await Flow.EndpointAsync(database, receiver, "ops", secretVariable: LegacySecret);
            await Flow.EndpointAsync(database, receiver, "sre", secretVariable: LegacySecret);
            await SendAsync(factory, new PutAllowlistEntry(notifications, $"{receiver.Address}/32", [receiver.Port]));
            await SendAsync(factory, new PutIntegration(notifications, "ops", "webhook", "webhook", JsonDocument.Parse("{}").RootElement, $"env:{PipelineSecret}", 0, true));
            await SendAsync(factory, new PutTarget(notifications, "ops", "ops", receiver.Url, null, true));
            await SendAsync(factory, new PutRoute(notifications, "ops", JsonDocument.Parse("{}").RootElement, ["ops"], true));

            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            var dispatcher = Flow.Dispatcher(database);
            for (var i = 0; i < 100 && receiver.Requests.Count < 2; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(20, Ct);
            }
            Assert.Equal(2, receiver.Requests.Count);
            await WouldSendAsync(factory);
            var comparer = Comparer(factory);

            Assert.Equal(1, await comparer.CompareOnceAsync(Ct));
            Assert.Equal(0, await comparer.CompareOnceAsync(Ct));   // once

            await using (var db = database.Context())
            {
                var comparison = await db.ShadowComparisons.AsNoTracking().SingleAsync(c => c.TransitionId == transition, Ct);
                Assert.Equal((0, NotificationPlan.Firing, alert, 2, 1), (comparison.EscalationStep, comparison.Event, comparison.AlertId, comparison.Legacy, comparison.New));
                var differences = await db.ShadowDifferences.AsNoTracking().Where(d => d.TransitionId == transition).ToListAsync(Ct);
                var sre = Assert.Single(differences, d => d.EndpointKey == "sre");
                Assert.Equal((ShadowDifferenceKinds.MissingInNew, (string?)null), (sre.Kind, sre.Rule));
                // "ops" reached both, at about the same time. (Its body is the pipeline's default template until M46b ports legacy's.)
                Assert.DoesNotContain(differences, d => d.EndpointKey == "ops" && d.Kind is ShadowDifferenceKinds.MissingInNew or ShadowDifferenceKinds.MissingInLegacy
                    or ShadowDifferenceKinds.Timing or ShadowDifferenceKinds.Unsettled);
                Assert.Equal(differences.Count, comparison.Differences);
                Assert.Equal(differences.Count(d => d.Rule is null), comparison.Unexplained);
            }

            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            using (var denied = await viewer.GetAsync($"/api/v1/tenants/{tenant}/notifications/shadow-report"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            var report = await Caller.JsonAsync(await @operator.GetAsync($"/api/v1/tenants/{tenant}/notifications/shadow-report"));
            Assert.Equal(1, report.GetProperty("compared").GetInt32());
            var today = Assert.Single(report.GetProperty("days").EnumerateArray());
            Assert.Equal((DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), 1), (today.GetProperty("day").GetString(), today.GetProperty("compared").GetInt32()));
            Assert.Equal(report.GetProperty("unexplained").GetInt32(), today.GetProperty("unexplained").GetInt32());
            Assert.True(report.GetProperty("unexplained").GetInt32() >= 1);
            Assert.Contains(report.GetProperty("latest").EnumerateArray(), d => d.GetProperty("endpointKey").GetString() == "sre");
            Assert.Contains(report.GetProperty("byRule").EnumerateArray(),
                r => r.GetProperty("rule").GetString() == ShadowComparer.Unexplained && r.GetProperty("kind").GetString() == ShadowDifferenceKinds.MissingInNew);

            await using var status = factory.Services.CreateAsyncScope();
            var shadowReport = status.ServiceProvider.GetRequiredService<ShadowReport>();
            var shadow = await shadowReport.StatusAsync(tenant, TimeSpan.FromHours(1), Ct);
            Assert.Equal(1, shadow.Compared);
            Assert.True(shadow.Unexplained >= 1);
            Assert.NotNull(shadow.LastComparedAt);
            // The dead-man's shadow cause (M49): the unexplained difference is a reason to look.
            Assert.Contains("no rule explains", await status.ServiceProvider.GetRequiredService<Juice.Observability.Health.DeadMan.IShadowReport>().FailingAsync(Ct));
        }

        /// <summary>
        /// An escalation step is its own unit, matched by its number: legacy's worker and the pipeline's escalation stage both page "pager" for
        /// step 1 of "paging", and the step is compared as <c>alert.escalated</c>, apart from the firing.
        /// </summary>
        [Fact]
        public async Task An_escalation_step_is_compared_as_its_own_unit()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var notifications = factory.Services.GetRequiredService<Juice.Notifications.Embedded.NotificationsEmbeddedOptions>().TenantId;
            await Flow.EndpointAsync(database, receiver, "pager", events: [NotificationPlan.Resolved], secretVariable: LegacySecret);
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new Juice.Observability.Data.EscalationPolicy
                {
                    TenantId = await Flow.TenantAsync(db), Key = "paging", Name = "Paging", UpdatedAt = DateTimeOffset.UtcNow,
                    Steps = [new Juice.Observability.Data.EscalationStep { AfterMinutes = 5, EndpointKeys = ["pager"] }],
                });
                await db.SaveChangesAsync(Ct);
            }
            await SendAsync(factory, new PutAllowlistEntry(notifications, $"{receiver.Address}/32", [receiver.Port]));
            await SendAsync(factory, new PutIntegration(notifications, "pager", "webhook", "webhook", JsonDocument.Parse("{}").RootElement, $"env:{PipelineSecret}", 0, true));
            await SendAsync(factory, new PutTarget(notifications, "pager", "pager", receiver.Url, null, true));
            // The route takes no request of its own (legacy's "pager" hears only resolves); the policy's step names it by key.
            await SendAsync(factory, new PutRoute(notifications, "pager", JsonDocument.Parse("""{"type":"observability.none"}""").RootElement, ["pager"], true));
            await SendAsync(factory, new PutEscalationPolicy(notifications, "paging",
                JsonDocument.Parse("""[{"afterMinutes":5,"recipients":[{"kind":"route","key":"pager"}]}]""").RootElement, true));

            // Fired 6 minutes ago: step 1 is due in both pipelines at once (the request carries when it fired, M45-2).
            var alert = await Flow.FireAsync(database, plan: false);
            Guid transition;
            await using (var db = database.Context())
            {
                var fired = DateTimeOffset.UtcNow.AddMinutes(-6);
                var t = await db.AlertTransitions.SingleAsync(x => x.AlertId == alert, Ct);
                await db.Alerts.Where(a => a.Id == alert).ExecuteUpdateAsync(s => s.SetProperty(a => a.FiredAt, fired), Ct);
                await db.AlertTransitions.Where(x => x.Id == t.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.At, fired), Ct);
                transition = t.Id;
            }
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            await Flow.AgeAsync(database, alert, 6);
            var (dispatcher, worker) = (Flow.Dispatcher(database), Flow.Worker(database));
            for (var i = 0; i < 100 && receiver.Requests.Count < 1; i++)
            {
                await worker.RunOnceAsync(Ct);
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(50, Ct);
            }
            Assert.Contains("\"step\":1", Assert.Single(receiver.Requests).Body);
            await WouldSendAsync(factory);

            var comparer = Comparer(factory);
            while (await comparer.CompareOnceAsync(Ct) > 0 && !await ComparedAsync(database, transition, 1))
            {
            }

            await using var check = database.Context();
            var step = await check.ShadowComparisons.AsNoTracking().SingleAsync(c => c.TransitionId == transition && c.EscalationStep == 1, Ct);
            Assert.Equal((NotificationPlan.Escalated, 1, 1), (step.Event, step.Legacy, step.New));
            var differences = await check.ShadowDifferences.AsNoTracking().Where(d => d.TransitionId == transition && d.EscalationStep == 1).ToListAsync(Ct);
            Assert.DoesNotContain(differences, d => d.Kind is ShadowDifferenceKinds.MissingInNew or ShadowDifferenceKinds.MissingInLegacy or ShadowDifferenceKinds.Unsettled);
        }

        /// <summary>
        /// The dead-man's shadow cause (M49's <c>FailingAsync</c>): nothing to say on a quiet run; a reason for a recent difference no rule explains,
        /// and for a transition past every wait that the comparer never compared.
        /// </summary>
        [Fact]
        public async Task The_dead_mans_shadow_cause_names_unexplained_differences_and_a_comparer_behind()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = Options.Create(new ShadowOptions());
            await using var db = database.Context();
            var report = new ShadowReport(db, settings, TimeProvider.System);
            Assert.Null(await report.FailingAsync(Ct));

            var tenant = await Flow.TenantAsync(db);
            var now = DateTimeOffset.UtcNow;
            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);
            // The comparer ran from a day ago; this transition was planned 4 hours ago and never compared.
            db.ShadowComparisons.Add(new ShadowComparison { TenantId = tenant, TransitionId = Guid.CreateVersion7(), AlertId = alert, Event = NotificationPlan.Firing, ComparedAt = now.AddDays(-1) });
            await db.SaveChangesAsync(Ct);
            await db.AlertTransitions.Where(t => t.Id == transition).ExecuteUpdateAsync(s => s.SetProperty(t => t.NotificationsPlannedAt, now.AddHours(-4)), Ct);
            Assert.Contains("behind", await report.FailingAsync(Ct));

            db.ShadowComparisons.Add(new ShadowComparison { TenantId = tenant, TransitionId = transition, AlertId = alert, Event = NotificationPlan.Firing, ComparedAt = now, Differences = 1 });
            db.ShadowDifferences.Add(new ShadowDifference
            {
                TenantId = tenant, TransitionId = transition, AlertId = alert, Event = NotificationPlan.Firing, EndpointKey = "ops", Kind = ShadowDifferenceKinds.Body,
                Rule = ShadowRules.DigestBoundary, Detail = "explained", DetectedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            // Bodies of a type's default template are explained too (templates_not_ported): the dead-man's cause stays quiet on them.
            db.ShadowDifferences.Add(new ShadowDifference
            {
                TenantId = tenant, TransitionId = transition, AlertId = alert, Event = NotificationPlan.Firing, EndpointKey = "ops", Kind = ShadowDifferenceKinds.Body,
                Rule = ShadowRules.TemplatesNotPorted, Detail = "default template", DetectedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            Assert.Null(await report.FailingAsync(Ct));   // compared, and explained

            db.ShadowDifferences.Add(new ShadowDifference
            {
                TenantId = tenant, TransitionId = transition, AlertId = alert, Event = NotificationPlan.Firing, EndpointKey = "sre", Kind = ShadowDifferenceKinds.MissingInNew,
                Detail = "unexplained", DetectedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            Assert.StartsWith("1 shadow difference(s) no rule explains", await report.FailingAsync(Ct));
        }

        private static async Task<bool> ComparedAsync(ScratchDatabase database, Guid transition, int step)
        {
            await using var db = database.Context();
            return await db.ShadowComparisons.AnyAsync(c => c.TransitionId == transition && c.EscalationStep == step, Ct);
        }

        /// <summary>A unit still sending on one side waits, and isn't compared before both settled (or the wait runs out).</summary>
        [Fact]
        public async Task A_unit_still_sending_waits()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(503, 503, 503, 503);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            await Flow.EndpointAsync(database, receiver, "ops", secretVariable: LegacySecret);
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            await Flow.Dispatcher(database, o => o.MaxAttempts = 8).DispatchOnceAsync(Ct);   // a 503: retrying

            // Nothing recorded, and the unit waits for its next look: a second pass at once doesn't take it again (review of #12, no spin).
            var comparer = Comparer(factory, batch: 1);
            Assert.Equal(0, await comparer.CompareOnceAsync(Ct));
            Assert.Equal(0, await comparer.CompareOnceAsync(Ct));

            await using var db = database.Context();
            Assert.False(await db.ShadowComparisons.AnyAsync(c => c.TransitionId == transition, Ct));
        }

        private static async Task WouldSendAsync(WebApplicationFactory<Program> factory)
        {
            for (var i = 0; i < 200; i++)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                if (await pipeline.Deliveries.AsNoTracking().AnyAsync(d => d.Status == NewStatus.WouldSend, Ct))
                {
                    return;
                }
                await Task.Delay(100, Ct);
            }
            Assert.Fail("the new pipeline never planned the firing's delivery in dry run");
        }
    }
}
