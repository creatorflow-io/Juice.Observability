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
    public class ShadowComparerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string PipelineSecret = "NOTIFICATIONS_SECRET_SHADOW";

        static ShadowComparerTests()
        {
            Environment.SetEnvironmentVariable(Flow.SecretVariable, "flow-secret");
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

        private static ShadowComparer Comparer(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ShadowOptions { Grace = TimeSpan.Zero }),
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
            await Flow.EndpointAsync(database, receiver, "ops");
            await Flow.EndpointAsync(database, receiver, "sre");
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
            Assert.True(report.GetProperty("unexplained").GetInt32() >= 1);
            Assert.Contains(report.GetProperty("latest").EnumerateArray(), d => d.GetProperty("endpointKey").GetString() == "sre");
            Assert.Contains(report.GetProperty("byRule").EnumerateArray(),
                r => r.GetProperty("rule").GetString() == ShadowComparer.Unexplained && r.GetProperty("kind").GetString() == ShadowDifferenceKinds.MissingInNew);

            await using var status = factory.Services.CreateAsyncScope();
            var shadow = await status.ServiceProvider.GetRequiredService<IShadowReport>().StatusAsync(tenant, TimeSpan.FromHours(1), Ct);
            Assert.Equal(1, shadow.Compared);
            Assert.True(shadow.Unexplained >= 1);
            Assert.NotNull(shadow.LastComparedAt);
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
            await Flow.EndpointAsync(database, receiver, "ops");
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            await Flow.Dispatcher(database, o => o.MaxAttempts = 8).DispatchOnceAsync(Ct);   // a 503: retrying

            Assert.Equal(1, await Comparer(factory).CompareOnceAsync(Ct));

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
