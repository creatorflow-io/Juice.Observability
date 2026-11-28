using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Juice.Observability.Tests.InboxHostTests;
using static Juice.Observability.Tests.ProducerAdapterTests;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The inbox, live (M54, slice B): an alert fires on a host whose pipeline runs its stages, and the operator who signed in has the item in their
    /// inbox and hears of it on the hub; acknowledging and resolving the alert update it. Needs the local stack.
    /// </summary>
    [Trait("Milestone", "M54")]
    public sealed class InboxLiveTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The inbox on, and the pipeline's stages running as they do in a deployment.</summary>
        private static Dictionary<string, string> Live() => new()
        {
            ["Notifications:Inbox:Enabled"] = "true", ["Notifications:ConsoleUrl"] = "https://console.example.com",
            ["Notifications:ReleasePassInterval"] = "00:00:01", ["Notifications:HoldPassInterval"] = "01:00:00",
        };

        /// <summary>A session on the hub, as the Console's bell opens one: WebSockets, its token as <c>access_token</c> in the query.</summary>
        private static HubConnection Hub(ApiFactory factory, string token, ConcurrentQueue<string> heard)
        {
            var server = factory.Server;
            var hub = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, $"notifications/hubs/inbox?tenant={Tenant}"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                // A browser can't set a header on a WebSocket: its token is a query value, as the Console's bell sends it (the .NET client would use a header).
                options.WebSocketFactory = async (context, ct) =>
                    await server.CreateWebSocketClient().ConnectAsync(new Uri($"{context.Uri}&access_token={Uri.EscapeDataString(token)}"), ct);
            }).Build();
            hub.On<JsonElement>("inboxChanged", message => heard.Enqueue(message.GetProperty("kind").GetString()!));
            return hub;
        }

        private static async Task<JsonElement[]> ItemsAsync(Caller caller)
            => [.. (await Caller.JsonAsync(await caller.GetAsync(Me()))).EnumerateArray()];

        private static async Task<JsonElement[]> WaitForItemsAsync(Caller caller, int count, string what)
        {
            JsonElement[] items = [];
            await ProvisionedHost.WaitAsync(() =>
            {
                items = ItemsAsync(caller).GetAwaiter().GetResult();
                return items.Length >= count;
            }, what);
            return items;
        }

        private static async Task<JsonElement> WaitForCorrelationAsync(Caller caller, Guid item, string state, string what)
        {
            JsonElement current = default;
            await ProvisionedHost.WaitAsync(() =>
            {
                current = Caller.JsonAsync(caller.GetAsync(Me($"/{item}")).GetAwaiter().GetResult()).GetAwaiter().GetResult();
                return current.GetProperty("correlation").GetProperty("state").GetString()!.Equals(state, StringComparison.OrdinalIgnoreCase);
            }, what);
            return current;
        }

        [Fact]
        public async Task Inbox_AlertFires_SignedInOperatorGetsItem_AcknowledgeAndResolveUpdateIt()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var capture = new LogCapture();
            await using var factory = new ApiFactory(database, settings: Live(), services: services => services.AddSingleton<ILoggerProvider>(capture));
            using var client = factory.CreateClient();
            var (operatorToken, viewerToken) = (await LocalStack.TokenAsync("erin"), await LocalStack.TokenAsync("victor"));
            var (@operator, viewer, admin) = (new Caller(client, operatorToken), new Caller(client, viewerToken), new Caller(client, await LocalStack.TokenAsync("alice")));

            // The operator signs in (a call to their inbox, and the hub), and so does a viewer. The admin doesn't, yet.
            Assert.Equal(0, (await Caller.JsonAsync(await @operator.GetAsync(Me("/counts")))).GetProperty("unread").GetInt32());
            Assert.Empty(await ItemsAsync(viewer));
            var heard = new ConcurrentQueue<string>();
            await using var hub = Hub(factory, operatorToken, heard);
            await hub.StartAsync(Ct);
            Assert.Equal("operator", await ScalarAsync(database,
                "SELECT string_agg(r.role, ',') FROM notifications.subscriber_roles r JOIN notifications.subscribers s ON s.id = r.subscriber_id AND s.tenant_id = r.tenant_id WHERE s.name = 'erin'"));

            // The rule fires.
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            var item = Assert.Single(await WaitForItemsAsync(@operator, 1, "the alert's item in the operator's inbox"));
            Assert.Equal("[Critical] Orders errors: service:shop/orders@production", item.GetProperty("title").GetString());
            Assert.Equal($"https://console.example.com/alerts/{alert}", item.GetProperty("url").GetString());
            Assert.Contains("Value: 0.4", item.GetProperty("body").GetString());
            Assert.Equal("observability.alert.firing", item.GetProperty("type").GetString());
            await ProvisionedHost.WaitAsync(() => !heard.IsEmpty, "the hub to tell the session");
            Assert.Equal("open", item.GetProperty("correlation").GetProperty("state").GetString(), ignoreCase: true);
            var id = item.GetProperty("id").GetGuid();

            // The viewer's role isn't one the alerts name, and the admin never signed in: no item now, and none after signing in later.
            Assert.Empty(await ItemsAsync(viewer));
            Assert.Empty(await ItemsAsync(admin));
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Assert.Empty(await ItemsAsync(admin));
            Assert.Single(await ItemsAsync(@operator));

            // The Console acknowledges the alert: the operator's item says who.
            using (var ack = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenants/{await TenantAsync(database)}/alerts/{alert}/acknowledgement") { Content = JsonContent.Create(new { comment = "on it" }) })
            {
                ack.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
                Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ack, Ct)).StatusCode);
            }
            var acknowledged = await WaitForCorrelationAsync(@operator, id, "acknowledged", "the item to be acknowledged");
            Assert.Equal("erin", acknowledged.GetProperty("correlation").GetProperty("acknowledgedBy").GetString());

            // The alert resolves.
            var heardBefore = heard.Count;
            await PlanAsync(factory, database, await ResolveAsync(database, alert));
            var resolved = await WaitForCorrelationAsync(@operator, id, "resolved", "the item to be resolved");
            Assert.NotNull(resolved.GetProperty("correlation").GetProperty("resolvedAt").GetString());
            await ProvisionedHost.WaitAsync(() => heard.Count > heardBefore, "the hub to tell the session of the change");
            // Still the one item (a resolve is no new item), and the viewer still has none.
            Assert.Single(await ItemsAsync(@operator));
            Assert.Empty(await ItemsAsync(viewer));

            // The admin signs in later (above) and is still without: the item is not backfilled.
            Assert.Empty(await ItemsAsync(admin));
            // The hub's token was only ever in the query string, and no log line holds it.
            Assert.DoesNotContain(capture.Entries, e => e.Message.Contains(operatorToken, StringComparison.Ordinal) || e.Message.Contains(operatorToken.Split('.')[2], StringComparison.Ordinal));
            await hub.StopAsync(Ct);
        }

        /// <summary>A second alert, as <see cref="FireAsync"/> makes the first: its own rule, subject and firing.</summary>
        private static async Task<(Guid Alert, Guid Transition)> FireAnotherAsync(ScratchDatabase database, string key, string subject)
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(database);
            var now = DateTimeOffset.UtcNow;
            var rule = new Rule { TenantId = tenant, Key = key, Name = key, Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Warning,
                Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = ManagedBy.Api };
            db.Rules.Add(rule);
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = $"{key}|{subject}", SubjectKey = subject, SubjectType = HealthSubject.Service, Severity = AlertSeverity.Warning,
                State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, LastValue = 0.2,
            };
            db.Alerts.Add(alert);
            var transition = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now, Value = 0.2, Reason = "for_duration_met" };
            db.AlertTransitions.Add(transition);
            await db.SaveChangesAsync(Ct);
            return (alert.Id, transition.Id);
        }

        [Fact]
        public async Task Inbox_TwoAlertsSameMoment_EachOneItem_NoDuplicateOnPlannerRetry()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: Live());
            using var client = factory.CreateClient();
            var @operator = new Caller(client, await LocalStack.TokenAsync("erin"));
            Assert.Empty(await ItemsAsync(@operator));
            var (_, first) = await FireAsync(database);
            var (_, second) = await FireAnotherAsync(database, "payments-latency", "service:shop/payments@production");

            // At the same moment, and the first handed over twice (the outbox's retry): the pipeline's keys make each one request.
            // The outbox tries a message again when its handler's transaction lost a race (the other handing over of the same transition won).
            async Task PlanLikeTheOutboxAsync(Guid transition)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await PlanAsync(factory, database, transition);
                        return;
                    }
                    catch (DbUpdateConcurrencyException) when (attempt < 5)
                    {
                    }
                }
            }
            await Task.WhenAll(PlanLikeTheOutboxAsync(first), PlanLikeTheOutboxAsync(second), PlanLikeTheOutboxAsync(first));
            await PlanLikeTheOutboxAsync(first);

            var items = await WaitForItemsAsync(@operator, 2, "an item for each alert");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            items = await ItemsAsync(@operator);
            Assert.Equal(2, items.Length);
            Assert.Equal(["[Critical] Orders errors: service:shop/orders@production", "[Warning] payments-latency: service:shop/payments@production"],
                items.Select(i => i.GetProperty("title").GetString()!).Order(StringComparer.Ordinal));
            Assert.Equal(2L, await ScalarAsync(database, "SELECT count(*) FROM notifications.requests"));
            Assert.Equal(2L, await ScalarAsync(database, "SELECT count(*) FROM notifications.inbox_items"));
        }
    }
}
