using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Roadmap Phase 4 gate for the webhook: a firing alert reaches a real receiver signed; transient failures retry
    /// with the same id and body; permanent failures stop; blocked destinations are never contacted; alert state never
    /// depends on delivery. The receiver listens on this machine's private address (loopback is always blocked),
    /// which the tests allowlist.
    /// </summary>
    public class WebhookTests
    {
        private const string SecretVariable = "JUICE_TEST_WEBHOOK_SECRET";
        private const string Secret = "test-webhook-secret";
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public WebhookTests() => Environment.SetEnvironmentVariable(SecretVariable, Secret);

        [Fact]
        public async Task Firing_alert_from_real_telemetry_is_delivered_signed_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await ProvisionAsync(database, receiver);
            var service = $"hooks-{_run}-orders";
            var rule = await AddRuleAsync(database, service);
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "h1")],
                Otlp.Requests(20, 20, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 20);

            var evaluation = await Evaluator(database).EvaluateAsync(rule, Ct);   // ForDuration 0: Pending and Firing at once
            Assert.Equal(1, evaluation.Fired);
            Assert.Equal(1, await Dispatcher(database).DispatchOnceAsync(Ct));

            var request = Assert.Single(receiver.Requests);
            Assert.True(WebhookSigner.Verify(Secret, request.Id, request.Timestamp, request.Signature, request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
            Assert.Contains("\"type\":\"alert.firing\"", request.Body);
            Assert.Contains($"\"subject\":\"service:{service}@unspecified\"", request.Body);
            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.SingleAsync();
            Assert.Equal((DeliveryStatus.Delivered, 1, delivery.Id.ToString()), (delivery.Status, delivery.Attempts, request.Id));
            Assert.NotNull(delivery.FirstAttemptAt);
            Assert.Equal(200, (await db.WebhookAttempts.SingleAsync()).StatusCode);
        }

        [Fact]
        public async Task Transient_failures_retry_with_the_same_id_and_body_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(503, 500, 200);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await ProvisionAsync(database, receiver);
            var alert = await FiringAlertAsync(database);
            var dispatcher = Dispatcher(database);

            for (var i = 0; i < 50 && receiver.Requests.Count < 3; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(50, Ct);
            }

            Assert.Equal(3, receiver.Requests.Count);
            Assert.Single(receiver.Requests.Select(r => r.Id).Distinct());
            Assert.Single(receiver.Requests.Select(r => r.Body).Distinct());
            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.SingleAsync();
            Assert.Equal((DeliveryStatus.Delivered, 3), (delivery.Status, delivery.Attempts));
            Assert.Equal([503, 500, 200], await db.WebhookAttempts.OrderBy(a => a.Number).Select(a => a.StatusCode!.Value).ToListAsync());
            Assert.Equal(AlertState.Firing, (await db.Alerts.SingleAsync(a => a.Id == alert)).State);
        }

        [Fact]
        public async Task Permanent_failure_and_exhausted_retries_leave_the_alert_untouched_Async()
        {
            LocalStack.RequireAvailable();
            await using var rejecting = await Receiver.StartAsync(400);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await ProvisionAsync(database, rejecting);
            var alert = await FiringAlertAsync(database);
            var dispatcher = Dispatcher(database, o => o.MaxAttempts = 3);

            await dispatcher.DispatchOnceAsync(Ct);
            await using (var db = database.Context())
            {
                var rejected = await db.WebhookDeliveries.SingleAsync();
                Assert.Equal((DeliveryStatus.Failed, 1, "status_400"), (rejected.Status, rejected.Attempts, rejected.LastError));   // 4xx: not retried
            }

            // A receiver that is gone: network errors retry up to MaxAttempts, then fail.
            await rejecting.DisposeAsync();
            await using (var db = database.Context())
            {
                await db.WebhookAttempts.ExecuteDeleteAsync(Ct);
                await db.WebhookDeliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Pending).SetProperty(d => d.Attempts, 0)
                    .SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow), Ct);
            }
            for (var i = 0; i < 50; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await using var db = database.Context();
                if ((await db.WebhookDeliveries.SingleAsync()).Status == DeliveryStatus.Failed) break;
                await Task.Delay(50, Ct);
            }
            await using var check = database.Context();
            var gone = await check.WebhookDeliveries.SingleAsync();
            Assert.Equal((DeliveryStatus.Failed, 3, "network_error"), (gone.Status, gone.Attempts, gone.LastError));
            Assert.Equal(AlertState.Firing, (await check.Alerts.SingleAsync(a => a.Id == alert)).State);
        }

        [Theory]
        [InlineData("http://127.0.0.1:{port}/hook", "blocked_address:127.0.0.1")]
        [InlineData("http://localhost:{port}/hook", "blocked_address")]
        [InlineData("http://169.254.169.254/latest/meta-data", "blocked_address:169.254.169.254")]
        [InlineData("http://{lan}:{otherport}/hook", "private_address_not_allowlisted")]
        [InlineData("http://{lan}:{port}/redirect", "status_302")]
        public async Task Blocked_destinations_are_never_contacted_Async(string url, string error)
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var target = url.Replace("{port}", receiver.Port.ToString()).Replace("{otherport}", (receiver.Port + 1).ToString())
                .Replace("{lan}", receiver.Address.ToString());
            await ProvisionAsync(database, receiver, target);
            await FiringAlertAsync(database);

            await Dispatcher(database).DispatchOnceAsync(Ct);

            await using var db = database.Context();
            var delivery = await db.WebhookDeliveries.SingleAsync();
            Assert.Equal(DeliveryStatus.Failed, delivery.Status);
            Assert.StartsWith(error, delivery.LastError);
            if (error != "status_302")
            {
                Assert.Empty(receiver.Requests);
            }
            else
            {
                Assert.Single(receiver.Requests);   // the redirect itself, not followed
            }
        }

        [Fact]
        public async Task Configuration_is_validated_and_every_change_audited_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var options = new WebhookOptions
            {
                AllowHttpToAllowlisted = false,
                Endpoints = [new EndpointDefinition { Key = "ops", Url = "http://10.0.0.5/hook", SecretRef = $"env:{SecretVariable}" }],
                Allowlist = [new AllowlistDefinition { Destination = "127.0.0.0/8" }],
            };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provisioner(database, options).StartAsync(Ct));
            Assert.Contains("must be https", error.Message);
            Assert.Contains("never reachable", error.Message);

            options.Endpoints[0].Url = "https://hooks.example.com/juice";
            options.Allowlist = [new AllowlistDefinition { Destination = "10.20.0.0/16", Ports = [8443] }];
            await Provisioner(database, options).StartAsync(Ct);
            options.Allowlist = [];
            options.Endpoints = [];
            await Provisioner(database, options).StartAsync(Ct);

            await using var db = database.Context();
            var audit = await db.AuditRecords.Where(a => a.Operation.StartsWith("webhook")).OrderBy(a => a.OccurredAt).Select(a => a.Operation).ToListAsync();
            Assert.Equal(["webhook.create", "webhook.allowlist.add", "webhook.disable", "webhook.allowlist.remove"], audit);
            Assert.DoesNotContain(await db.AuditRecords.Select(a => a.Metadata).ToListAsync(), m => m.Values.Any(v => v.Contains(Secret)));
        }

        // ─── helpers ──────────────────────────────────────────────────────────

        private static ServiceProvider Services(ScratchDatabase database)
            => new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();

        private static WebhookProvisioner Provisioner(ScratchDatabase database, WebhookOptions options)
            => new(Services(database).GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<WebhookProvisioner>.Instance);

        /// <summary>One endpoint (all events) at the receiver, and an allowlist entry for the receiver's address and port.</summary>
        private static async Task ProvisionAsync(ScratchDatabase database, Receiver receiver, string? url = null)
            => await Provisioner(database, new WebhookOptions
            {
                AllowHttpToAllowlisted = true,
                Endpoints = [new EndpointDefinition { Key = "ops", Url = url ?? receiver.Url, SecretRef = $"env:{SecretVariable}" }],
                Allowlist = [new AllowlistDefinition { Destination = $"{receiver.Address}/32", Ports = [receiver.Port] }],
            }).StartAsync(Ct);

        private static WebhookDispatcher Dispatcher(ScratchDatabase database, Action<WebhookOptions>? configure = null)
        {
            var options = new WebhookOptions { RetryBase = TimeSpan.FromMilliseconds(20), RetryMax = TimeSpan.FromMilliseconds(100), AllowHttpToAllowlisted = true };
            configure?.Invoke(options);
            return new WebhookDispatcher(Services(database).GetRequiredService<IServiceScopeFactory>(), Options.Create(options), TimeProvider.System,
                NullLogger<WebhookDispatcher>.Instance);
        }

        private static RuleEvaluator Evaluator(ScratchDatabase database)
            => new(Services(database).GetRequiredService<IServiceScopeFactory>(),
                new RuleTemplates(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }))),
                Options.Create(new RulesOptions()), Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System,
                NullLogger<RuleEvaluator>.Instance, [new WebhookOutbox(Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System)]);

        private async Task<Rule> AddRuleAsync(ScratchDatabase database, string service)
        {
            var definition = new RuleDefinition
            {
                Key = $"hooks-{_run}", Name = "Errors", Template = RuleTemplate.ErrorRate, Service = service, Severity = AlertSeverity.Critical,
                Threshold = 0.05, MinimumSamples = 10, ForDuration = TimeSpan.Zero,
            };
            await using var db = database.Context();
            var rule = new Rule
            {
                TenantId = (await db.Tenants.SingleAsync()).Id, Key = definition.Key, Name = definition.Name, Template = definition.Template,
                Severity = definition.Severity, Definition = definition.ToJson(), DefinitionHash = definition.Hash(), LoadedAt = DateTimeOffset.UtcNow,
            };
            db.Rules.Add(rule);
            await db.SaveChangesAsync();
            return rule;
        }

        /// <summary>A firing alert whose Firing transition went through the outbox, as the evaluator does it.</summary>
        private async Task<Guid> FiringAlertAsync(ScratchDatabase database)
        {
            var rule = await AddRuleAsync(database, $"hooks-{_run}-seeded");
            await using var db = database.Context();
            var now = DateTimeOffset.UtcNow;
            var alert = new Alert
            {
                TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = $"{rule.Key}|service:x@production", SubjectKey = "service:x@production",
                SubjectType = HealthSubject.Service, Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now,
            };
            var transition = new AlertTransition { TenantId = rule.TenantId, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now, Value = 0.5, Reason = "for_duration_met" };
            db.Alerts.Add(alert);
            db.AlertTransitions.Add(transition);
            new WebhookOutbox(Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System).OnTransition(db, rule, alert, transition);
            await db.SaveChangesAsync();
            return alert.Id;
        }

        /// <summary>A webhook receiver on this machine's private address. Answers with the given status codes in turn, then 200.</summary>
        private sealed class Receiver : IAsyncDisposable
        {
            public sealed record Received(string Id, string Timestamp, string Signature, string Body);

            private readonly WebApplication _app;
            private readonly ConcurrentQueue<int> _statuses;
            public ConcurrentQueue<Received> Requests { get; } = new();
            public IPAddress Address { get; }
            public int Port { get; private set; }
            public string Url => $"http://{Address}:{Port}/hook";
            private bool _disposed;

            private Receiver(IPAddress address, int[] statuses)
            {
                Address = address;
                _statuses = new(statuses);
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls($"http://{address}:0");
                builder.Logging.ClearProviders();
                _app = builder.Build();
                _app.MapPost("/hook", async (HttpRequest request) =>
                {
                    using var reader = new StreamReader(request.Body);
                    Requests.Enqueue(new Received(request.Headers[WebhookSigner.IdHeader]!, request.Headers[WebhookSigner.TimestampHeader]!,
                        request.Headers[WebhookSigner.SignatureHeader]!, await reader.ReadToEndAsync()));
                    return Results.StatusCode(_statuses.TryDequeue(out var status) ? status : 200);
                });
                _app.MapPost("/redirect", () =>
                {
                    Requests.Enqueue(new Received("", "", "", ""));
                    return Results.Redirect("http://169.254.169.254/latest/meta-data");
                });
            }

            public static async Task<Receiver> StartAsync(params int[] statuses)
            {
                var address = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && DestinationPolicy.Classify(a) == DestinationClass.Private);
                Assert.SkipWhen(address is null, "no private IPv4 address on this machine for the webhook receiver");
                var receiver = new Receiver(address!, statuses);
                await receiver._app.StartAsync();
                receiver.Port = new Uri(receiver._app.Urls.First()).Port;
                return receiver;
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
