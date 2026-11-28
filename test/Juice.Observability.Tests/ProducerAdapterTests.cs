using System.Text.Json;
using Juice.MediatR;
using Juice.Notifications;
using Juice.Observability.Agents;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Notifications;
using Juice.Observability.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The producer adapter (phase-5-plan I64, M45 QA plan): what a transition, an acknowledgement and an action request become in the pipeline,
    /// written in the platform transaction that made them. The pipeline's stages don't run here (its registration is <c>legacy</c>); the platform
    /// produces requests as in <c>new</c>.
    /// </summary>
    public class ProducerAdapterTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string Console = "https://console.example/";

        internal static WebApplicationFactory<Program> Factory(ScratchDatabase database, Action<IServiceCollection>? configure = null)
            // The pipeline registered as legacy (no stage, no pass of its own runs) and producing as new: each test drives what it checks.
            => new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:ConsoleUrl"] = Console, ["Notifications:Pipeline"] = "legacy" })
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.PostConfigure<PlatformNotificationsOptions>(o => o.Pipeline = NotificationsPipeline.New);
                    configure?.Invoke(services);
                }));

        [Fact]
        public async Task A_firing_becomes_one_request_with_its_key_correlation_labels_data_and_link_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, transition) = await FireAsync(database);

            Assert.Equal(0, await PlanAsync(factory, database, transition));

            var request = Assert.Single(await RequestsAsync(database));
            Assert.Equal((ObservabilityTypes.AlertFiring, $"transition:{transition}", $"alert:{alert}"), (request.Type, request.Key, request.Correlation));
            Assert.Equal("alert.firing", request.Labels["event"]);
            Assert.Equal("orders-errors", request.Labels["rule"]);
            Assert.Equal("critical", request.Labels["severity"]);
            Assert.Equal("service:shop/orders@production", request.Labels["subject"]);
            Assert.Equal("production", request.Labels["environment"]);
            Assert.False(request.Labels.ContainsKey(ObservabilityTypes.EscalationPolicyLabel));
            Assert.Equal($"https://console.example/alerts/{alert}", request.Links["self"]);
            // Today's payload, less its id.
            Assert.False(request.Data.TryGetProperty("id", out _));
            Assert.Equal("alert.firing", request.Data.GetProperty("type").GetString());
            Assert.Equal(alert.ToString(), request.Data.GetProperty("alert").GetProperty("id").GetString());
            Assert.Equal(LocalStack.TenantKey, request.Data.GetProperty("tenant").GetString());
            // Legacy planned nothing (new).
            await using var db = database.Context();
            Assert.Empty(await db.WebhookDeliveries.ToListAsync(Ct));
        }

        [Fact]
        public async Task The_outbox_handing_a_transition_over_twice_makes_one_request_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (_, transition) = await FireAsync(database);

            await PlanAsync(factory, database, transition);
            await PlanAsync(factory, database, transition);

            Assert.Single(await RequestsAsync(database));
        }

        [Fact]
        public async Task A_plan_rolled_back_leaves_no_request_and_the_transition_unplanned_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database, s => s.AddTransient<INotificationHandler<AlertNotificationsPlanned>, FailAfterAdapter>());
            _ = factory.Server;
            var (_, transition) = await FireAsync(database);

            await Assert.ThrowsAsync<InvalidOperationException>(() => PlanAsync(factory, database, transition));

            Assert.Empty(await RequestsAsync(database));
            await using var db = database.Context();
            Assert.Null((await db.AlertTransitions.SingleAsync(t => t.Id == transition, Ct)).NotificationsPlannedAt);
        }

        /// <summary>Registered after the adapter's handler, so it fails the plan once the request is written.</summary>
        private sealed class FailAfterAdapter : INotificationHandler<AlertNotificationsPlanned>
        {
            public ValueTask Handle(AlertNotificationsPlanned notification, CancellationToken cancellationToken) => throw new InvalidOperationException("after the adapter");
        }

        [Fact]
        public async Task A_resolve_resolves_the_correlation_then_sends_the_resolved_request_without_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            var resolved = await ResolveAsync(database, alert);
            await PlanAsync(factory, database, resolved);

            var requests = await RequestsAsync(database);
            Assert.Equal(2, requests.Count);
            var notice = requests.Single(r => r.Key == $"transition:{resolved}");
            Assert.Equal(ObservabilityTypes.AlertResolved, notice.Type);
            Assert.Null(notice.Correlation);
            Assert.Equal(alert.ToString(), notice.Labels["alert"]);
            Assert.Equal("alert.resolved", notice.Labels["event"]);
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
        }

        [Fact]
        public async Task Acknowledging_acknowledges_the_correlation_in_the_same_transaction_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            var tenant = await TenantAsync(database);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var _ = PlatformMessageContext.Begin("test");
                var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                    new AcknowledgeAlert(tenant, alert, "on it", "erin", null, "", "ack", "h"), Ct);
                Assert.Equal(200, outcome.Status);
            }

            Assert.Equal("Acknowledged", await CorrelationStateAsync(database, $"alert:{alert}"));
        }

        [Fact]
        public async Task A_silenced_firing_is_held_not_sent_and_its_resolve_still_resolves_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            await SilenceAsync(database);
            var (alert, firing) = await FireAsync(database);

            await PlanAsync(factory, database, firing);

            Assert.Empty(await RequestsAsync(database));
            await using (var db = database.Context())
            {
                var hold = await db.NotificationHolds.SingleAsync(Ct);
                Assert.Equal((alert, firing, NotificationPlan.Firing, (DateTimeOffset?)null), (hold.AlertId, hold.TransitionId, hold.Event, hold.DecidedAt));
                var kept = JsonSerializer.Deserialize<NotificationToSend>(hold.Request, JsonSerializerOptions.Web)!;
                Assert.Equal(($"transition:{firing}", $"alert:{alert}"), (kept.IdempotencyKey, kept.CorrelationKey));
            }
            // Its firing was never sent: the resolve finds no correlation, and that is no failure.
            var resolved = await ResolveAsync(database, alert);
            await PlanAsync(factory, database, resolved);
            await using (var db = database.Context())
            {
                Assert.Equal(2, await db.NotificationHolds.CountAsync(Ct));
            }
        }

        [Fact]
        public async Task A_firing_names_the_first_matching_policy_by_key_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            await using (var db = database.Context())
            {
                var tenant = await TenantAsync(database);
                foreach (var (key, minimum) in new[] { ("pager-b", AlertSeverity.Info), ("pager-a", AlertSeverity.Warning), ("pager-0", AlertSeverity.Critical) })
                {
                    db.EscalationPolicies.Add(new EscalationPolicy
                    {
                        TenantId = tenant, Key = key, Name = key, MinimumSeverity = minimum, Enabled = key != "pager-0",
                        Steps = [new EscalationStep { AfterMinutes = 5, EndpointKeys = ["ops"] }],
                    });
                }
                await db.SaveChangesAsync(Ct);
            }
            var (_, transition) = await FireAsync(database);

            await PlanAsync(factory, database, transition);

            // pager-0 is disabled; of the two that match, the first by key.
            Assert.Equal("pager-a", Assert.Single(await RequestsAsync(database)).Labels[ObservabilityTypes.EscalationPolicyLabel]);
        }

        [Fact]
        public async Task An_action_request_is_correlated_while_open_and_its_end_resolves_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var tenant = await TenantAsync(database);
            var request = new ActionRequest
            {
                TenantId = tenant, AgentId = Guid.NewGuid(), Action = "restart-service", Parameters = """{"service":"orders"}""", Risk = "medium", Policy = "admin",
                Reason = "stuck", RequestedBy = "erin", RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), State = ActionRequestState.Pending,
            };

            await InTransactionAsync(factory, (services, db) => ActionRequestNotices.AddAsync(db, services, new ActionApprovalOptions(), LocalStack.TenantKey, request, "web-1",
                ActionRequestEvents.Created, DateTimeOffset.UtcNow, Ct));
            request.State = ActionRequestState.Expired;
            await InTransactionAsync(factory, (services, db) => ActionRequestNotices.AddAsync(db, services, new ActionApprovalOptions(), LocalStack.TenantKey, request, "web-1",
                ActionRequestEvents.Expired, DateTimeOffset.UtcNow, Ct));

            var requests = await RequestsAsync(database);
            var created = requests.Single(r => r.Key == $"action:{request.Id}:created");
            Assert.Equal(("observability.action.request.created", $"action:{request.Id}"), (created.Type, created.Correlation));
            Assert.Equal(("restart-service", "web-1", "https://console.example/approvals"), (created.Labels["action"], created.Labels["hostname"], created.Links["self"]));
            Assert.Equal("restart-service", created.Data.GetProperty("request").GetProperty("action").GetString());
            var expired = requests.Single(r => r.Key == $"action:{request.Id}:expired");
            Assert.Equal(("observability.action.request.expired", (string?)null), (expired.Type, expired.Correlation));
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"action:{request.Id}"));
        }

        [Theory]
        [InlineData(ActionRequestEvents.Created, ActionVerification.None, false)]
        [InlineData(ActionRequestEvents.Approved, ActionVerification.Verifying, false)]
        [InlineData(ActionRequestEvents.Approved, ActionVerification.None, true)]
        [InlineData(ActionRequestEvents.Rejected, ActionVerification.None, true)]
        [InlineData(ActionRequestEvents.Cancelled, ActionVerification.None, true)]
        [InlineData(ActionRequestEvents.Expired, ActionVerification.None, true)]
        [InlineData(ActionRequestEvents.Verified, ActionVerification.Verified, true)]
        [InlineData(ActionRequestEvents.NotVerified, ActionVerification.NotVerified, true)]
        public void An_action_request_ends_when_decided_against_lapsed_or_done(string type, ActionVerification verification, bool ends)
            => Assert.Equal(ends, ProducerAdapter.Ends(new ActionRequest
            {
                Action = "a", Parameters = "{}", Risk = "low", Policy = "none", Reason = "r", RequestedBy = "u", Verification = verification,
            }, type));

        [Fact]
        public void Every_platform_event_has_a_declared_type()
        {
            var declared = ObservabilityTypes.All().Select(t => t.Key).ToList();
            Assert.Contains(ObservabilityTypes.AlertFiring, declared);
            Assert.Contains(ObservabilityTypes.AlertResolved, declared);
            foreach (var type in typeof(ActionRequestEvents).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!))
            {
                Assert.Contains(ObservabilityTypes.ForPlatformEvent(type), declared);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        internal sealed record Request(string Type, string Key, string? Correlation, Dictionary<string, string> Labels, Dictionary<string, string> Links, JsonElement Data);

        internal static async Task<List<Request>> RequestsAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT type_key, idempotency_key, correlation_key, labels::text, links::text, data::text FROM notifications.requests ORDER BY accepted_at, id", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var requests = new List<Request>();
            while (await reader.ReadAsync(Ct))
            {
                requests.Add(new Request(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3))!, JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4))!,
                    JsonDocument.Parse(reader.GetString(5)).RootElement.Clone()));
            }
            return requests;
        }

        internal static async Task<string?> CorrelationStateAsync(ScratchDatabase database, string key)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT state FROM notifications.correlations WHERE key = @key", connection);
            command.Parameters.AddWithValue("key", key);
            return (string?)await command.ExecuteScalarAsync(Ct);
        }

        internal static async Task<Guid> TenantAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return (await db.Tenants.SingleAsync(t => t.Key == LocalStack.TenantKey, Ct)).Id;
        }

        /// <summary>A committed firing, not planned yet: what the outbox hands the consumer.</summary>
        internal static async Task<(Guid Alert, Guid Transition)> FireAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(database);
            var now = DateTimeOffset.UtcNow;
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = Data.ManagedBy.Api };
            db.Rules.Add(rule);
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = "orders-errors|service:shop/orders@production", SubjectKey = "service:shop/orders@production",
                SubjectType = HealthSubject.Service, Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, LastValue = 0.4,
            };
            db.Alerts.Add(alert);
            var transition = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now, Value = 0.4, Reason = "for_duration_met" };
            db.AlertTransitions.Add(transition);
            await db.SaveChangesAsync(Ct);
            return (alert.Id, transition.Id);
        }

        internal static async Task<Guid> ResolveAsync(ScratchDatabase database, Guid alertId)
        {
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(a => a.Id == alertId, Ct);
            var now = DateTimeOffset.UtcNow;
            var transition = new AlertTransition { TenantId = alert.TenantId, AlertId = alert.Id, FromState = AlertState.Firing, ToState = AlertState.Resolved, At = now, Value = 0.01, Reason = "recovered" };
            db.AlertTransitions.Add(transition);
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = now;
            await db.SaveChangesAsync(Ct);
            return transition.Id;
        }

        internal static async Task<Guid> SilenceAsync(ScratchDatabase database, string service = "shop/orders")
        {
            await using var db = database.Context();
            var now = DateTimeOffset.UtcNow;
            var silence = new Silence
            {
                TenantId = await TenantAsync(database), Kind = SilenceKind.Silence, Comment = "test", CreatedBy = "erin", CreatedAt = now, StartsAt = now.AddMinutes(-1),
                EndsAt = now.AddHours(1), Service = service,
            };
            db.Silences.Add(silence);
            await db.SaveChangesAsync(Ct);
            return silence.Id;
        }

        /// <summary>Plans the transition as the outbox's consumer does.</summary>
        internal static async Task<int> PlanAsync(WebApplicationFactory<Program> factory, ScratchDatabase database, Guid transition)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var _ = PlatformMessageContext.Begin("test");
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PlanNotifications(await TenantAsync(database), transition, DateTimeOffset.UtcNow), Ct);
        }

        private static async Task InTransactionAsync(WebApplicationFactory<Program> factory, Func<IServiceProvider, PlatformDbContext, Task> work)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await work(scope.ServiceProvider, db);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }
    }
}
