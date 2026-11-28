using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Juice.MediatR;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules.Notifications;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationsDbContext = Juice.Notifications.Data.NotificationsDbContext;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// What 2.0 claims and nothing else covered, live: an email integration declared in a provisioning file reaching a real SMTP server
    /// (Mailpit on the notifications stack), a Slack webhook integration reaching a stub receiver, and an acknowledgement stopping an alert's
    /// escalation on the pipeline's path.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class LiveChannelDeliveryTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const int MailpitSmtpPort = 1026, MailpitApiPort = 8026;

        private static bool Listening(int port)
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ─── Email ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Email_ProvisionedSmtpIntegration_ReachesMailpit_WithTheTemplatesSubjectAndBody()
        {
            LocalStack.RequireAvailable();
            Assert.SkipUnless(Listening(MailpitSmtpPort) && Listening(MailpitApiPort), "Mailpit (the notifications stack's, SMTP 1026, API 8026) isn't running");
            await using var database = await LocalStack.CreateDatabaseAsync();
            var recipient = $"m50-{Guid.NewGuid():N}@example.invalid";
            await using var file = ProvisionedHost.WriteIntegration("mail", new
            {
                channel = "email", kind = "smtp", settings = new { server = "local", from = "alerts@example.invalid" },
                targets = new Dictionary<string, object> { ["ops"] = new { address = recipient } },
            }, "ops");
            var settings = ProvisionedHost.Settings(file.Path);
            settings["Notifications:Delivery:SmtpServers:local:Host"] = "localhost";
            settings["Notifications:Delivery:SmtpServers:local:Port"] = MailpitSmtpPort.ToString();
            settings["Notifications:Delivery:SmtpServers:local:Security"] = "None";
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);
            _ = factory.Server;
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);

            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            using var mailpit = new HttpClient { BaseAddress = new Uri($"http://localhost:{MailpitApiPort}") };
            JsonElement? message = null;
            await ProvisionedHost.WaitAsync(() =>
            {
                var found = JsonDocument.Parse(mailpit.GetStringAsync($"/api/v1/search?query={Uri.EscapeDataString($"to:{recipient}")}", Ct).GetAwaiter().GetResult()).RootElement;
                message = found.GetProperty("messages").GetArrayLength() > 0 ? found.GetProperty("messages")[0].Clone() : null;
                return message is not null;
            }, "the email to reach Mailpit");
            Assert.StartsWith("[Juice]", message!.Value.GetProperty("Subject").GetString());
            var full = JsonDocument.Parse(await mailpit.GetStringAsync($"/api/v1/message/{message.Value.GetProperty("ID").GetString()}", Ct)).RootElement;
            Assert.Equal("alerts@example.invalid", full.GetProperty("From").GetProperty("Address").GetString());
            var text = full.GetProperty("Text").GetString()!;
            Assert.Contains("Orders errors", text);
            Assert.Contains("orders-errors", text);
            await using var scope = factory.Services.CreateAsyncScope();
            var delivery = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Deliveries.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(Juice.Notifications.DeliveryStatus.Delivered, delivery.Status is Juice.Notifications.DeliveryStatus.Accepted ? Juice.Notifications.DeliveryStatus.Delivered : delivery.Status);
        }

        // ─── Slack ─────────────────────────────────────────────────────────────

        /// <summary>Slack's incoming-webhook host as the provider reaches it: every request goes on to the stub receiver instead.</summary>
        private sealed class ToReceiver(TestReceiver receiver) : HttpMessageHandler
        {
            private readonly HttpClient _client = new();
            public List<Uri> Asked { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Asked.Add(request.RequestUri!);
                using var forward = new HttpRequestMessage(HttpMethod.Post, receiver.Url) { Content = new StringContent(await request.Content!.ReadAsStringAsync(cancellationToken), System.Text.Encoding.UTF8, "application/json") };
                return await _client.SendAsync(forward, cancellationToken);
            }
        }

        [Fact]
        public async Task Slack_ProvisionedWebhookIntegration_PostsTheAlertToTheStubReceiver()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await TestReceiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            const string variable = "JUICE_M50_SLACK_URL";
            const string url = "https://hooks.slack.com/services/T0M50/B0M50/m50secret";
            Environment.SetEnvironmentVariable(variable, url);
            await using var file = ProvisionedHost.WriteIntegration("slack", new
            {
                channel = "slack", kind = "slack-webhook", settings = new { },
                targets = new Dictionary<string, object> { ["ops"] = new { addressRef = $"env:{variable}" } },
            }, "ops");
            var handler = new ToReceiver(receiver);
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path))
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(new ChatHttp(handler)))));
            _ = factory.Server;
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);

            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            await ProvisionedHost.WaitAsync(() => !receiver.Requests.IsEmpty, "the Slack message to reach the stub receiver");
            Assert.Equal(url, Assert.Single(handler.Asked).ToString());
            var body = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
            Assert.True(body.TryGetProperty("blocks", out var blocks) && blocks.GetArrayLength() > 0, "a Slack message with blocks");
            Assert.Contains("Orders errors", body.GetRawText());
            await ProvisionedHost.WaitAsync(() =>
            {
                using var scope = factory.Services.CreateScope();
                return scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Deliveries.AsNoTracking()
                    .Any(d => d.Channel == "slack" && (d.Status == Juice.Notifications.DeliveryStatus.Delivered || d.Status == Juice.Notifications.DeliveryStatus.Accepted));
            }, "the delivery to be marked delivered");
        }

        // ─── Acknowledgement stops the escalation ──────────────────────────────

        private static async Task<(Guid Alert, Guid Transition)> EscalatingFiringAsync(ScratchDatabase database, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, bool stopOnAcknowledge)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = PlatformMessageContext.Begin("test");
                var put = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Juice.Notifications.Configuration.PutEscalationPolicy(
                    Guid.Parse(LocalStack.TenantKey), "pager", JsonSerializer.SerializeToElement(new[]
                    {
                        // Far in the future: the step never runs in this test, the escalation row is what an acknowledgement stops.
                        new { afterMinutes = 60, recipients = new[] { new { kind = "route", key = "pager" } } },
                    }), stopOnAcknowledge), Ct);
                Assert.True(put.Kind is Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created, put.Message);
            }
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = await ProducerAdapterTests.TenantAsync(database), Key = "pager", Name = "pager", MinimumSeverity = AlertSeverity.Info, RuleKeys = [], Environments = [],
                });
                await db.SaveChangesAsync(Ct);
            }
            var fired = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, fired.Transition);
            return fired;
        }

        private static async Task<(Juice.Notifications.EscalationState State, string? Reason)[]> EscalationsAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var rows = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Escalations.AsNoTracking().ToListAsync(Ct);
            return rows.Select(r => (r.State, r.StopReason)).ToArray();
        }

        private static async Task AcknowledgeAsync(ScratchDatabase database, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid alert)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var _ = PlatformMessageContext.Begin("test");
            var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new AcknowledgeAlert(await ProducerAdapterTests.TenantAsync(database), alert, "on it", "erin", null, "", "ack", "h"), Ct);
            Assert.Equal(200, outcome.Status);
        }

        [Fact]
        public async Task Escalation_AcknowledgingTheAlert_StopsItsEscalation()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await TestReceiver.StartAsync();
            await using var file = ProvisionedHost.Write(receiver);
            // The pipeline's stages run (the escalation stage opens the escalation of a request that names a policy).
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path));
            _ = factory.Server;
            var (alert, _) = await EscalatingFiringAsync(database, factory, stopOnAcknowledge: true);
            await ProvisionedHost.WaitAsync(() => EscalationsAsync(factory).GetAwaiter().GetResult().Length == 1, "the escalation to open");
            Assert.Equal([(Juice.Notifications.EscalationState.Running, (string?)null)], await EscalationsAsync(factory));

            await AcknowledgeAsync(database, factory, alert);

            Assert.Equal("Acknowledged", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            await ProvisionedHost.WaitAsync(() => EscalationsAsync(factory).GetAwaiter().GetResult() is [(Juice.Notifications.EscalationState.Stopped, _)], "the escalation to stop");
            Assert.Equal([(Juice.Notifications.EscalationState.Stopped, "acknowledged")], await EscalationsAsync(factory));
        }

        [Fact]
        public async Task Escalation_AcknowledgingTheAlert_PolicyNotStoppingOnAcknowledge_KeepsEscalating()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await TestReceiver.StartAsync();
            await using var file = ProvisionedHost.Write(receiver);
            // The pipeline's stages run (the escalation stage opens the escalation of a request that names a policy).
            await using var factory = new ApiTests.ApiFactory(database, settings: ProvisionedHost.Settings(file.Path));
            _ = factory.Server;
            var (alert, _) = await EscalatingFiringAsync(database, factory, stopOnAcknowledge: false);
            await ProvisionedHost.WaitAsync(() => EscalationsAsync(factory).GetAwaiter().GetResult().Length == 1, "the escalation to open");

            await AcknowledgeAsync(database, factory, alert);

            // The control: the same acknowledgement, a policy that doesn't stop on it: still running, after the pipeline has had time to act on it.
            Assert.Equal("Acknowledged", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Equal([(Juice.Notifications.EscalationState.Running, (string?)null)], await EscalationsAsync(factory));
        }
    }
}
