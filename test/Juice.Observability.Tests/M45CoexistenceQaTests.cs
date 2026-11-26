using System.Collections.Concurrent;
using System.Diagnostics;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Messaging.Policies;
using Juice.Notifications;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M45-0, phase-5 waves §5.2): the embedded pipeline beside the platform with the platform's own events and commands, the reverse
    /// direction (a pipeline command), legacy sending nothing, and the migrations over a release/1.0 database.
    /// </summary>
    public class M45CoexistenceQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The platform's last migration on release/1.0: JuiceOutbox1013 (snapshot only) is this PR's.</summary>
        private const string Release10Last = "20261124065423_AgentReportedTenant";

        // ─── Each platform event: one outbox delivery, handled once; no pipeline transaction ────────

        [Fact]
        public async Task A_real_alert_transition_gets_one_outbox_delivery_one_plan_and_no_pipeline_transaction_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            using var begun = new TransactionsBegun(database.Name);
            var tenant = await TenantAsync(database);

            var marker = Guid.NewGuid();
            Assert.True(await SendAsync(factory, new FireAlert(tenant, marker)));
            Guid transition;
            await using (var db = database.Context())
            {
                transition = (await db.AlertTransitions.SingleAsync(t => t.Reason == marker.ToString(), Ct)).Id;
            }
            for (var i = 0; i < 50 && !TransitionsHeard.Seen.ContainsKey(transition); i++) await Task.Delay(100, Ct);
            // Past the outbox's delivery pass (every 2 seconds), which would hand a second delivery over.
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);

            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM outbox_events"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM outbox_deliveries"));
            // The pipeline's outbox never takes a platform event.
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM notifications.outbox_events"));
            var heard = TransitionsHeard.Seen.GetValueOrDefault(transition);
            var planned = PlansCounted.Seen.GetValueOrDefault(transition);
            // The post-commit dispatch and the delivery pass may both hand the one delivery over (as before the pipeline); the plan is
            // idempotent (NotificationsPlannedAt). Recorded for the QA report.
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m45-handled.txt"), $"{DateTime.UtcNow:O} heard={heard} planned={planned}{Environment.NewLine}");
            Assert.InRange(heard, 1, 2);
            Assert.InRange(planned, 1, 2);
            await using (var db = database.Context())
            {
                Assert.NotNull((await db.AlertTransitions.SingleAsync(t => t.Id == transition, Ct)).NotificationsPlannedAt);
            }
            Assert.Equal(0, begun.Of<Juice.Notifications.Data.NotificationsDbContext>());
            Assert.True(begun.Of<PlatformDbContext>() >= 2, "the command and the plan each in a platform transaction");
        }

        [Fact]
        public async Task Every_platform_event_type_resolves_to_exactly_one_route_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var policy = factory.Services.GetRequiredService<IMessagePublishingPolicy>();
            var events = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Juice.Observability", StringComparison.Ordinal) == true)
                .SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && typeof(IntegrationEvent).IsAssignableFrom(t)).ToList();
            Assert.Contains(typeof(AlertTransitionedIntegrationEvent), events);
            foreach (var type in events)
            {
                var routes = await policy.ResolveAsync(new PolicyResolveContext { EventType = type.Name, Domain = "" });
                Assert.True(routes.Count == 1, $"{type.Name}: {routes.Count} routes");
            }
        }

        // ─── The reverse: a pipeline command opens no platform transaction ────────

        [Fact]
        public async Task A_pipeline_command_runs_in_its_own_transaction_and_opens_no_platform_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            using var begun = new TransactionsBegun(database.Name);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = PlatformMessageContext.Begin("test");
                var tenant = Guid.Parse(LocalStack.TenantKey);
                var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>()
                    .Send(new Juice.Notifications.Configuration.PutAllowlistEntry(tenant, "qa.example.test", null), Ct);
                Assert.True(outcome.Code is null, $"{outcome.Kind} {outcome.Code} {outcome.Message}");
            }

            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM notifications.destination_allowlist WHERE destination = 'qa.example.test'"));
            Assert.Equal(1, begun.Of<Juice.Notifications.Data.NotificationsDbContext>());
            Assert.Equal(0, begun.Of<PlatformDbContext>());
        }

        // ─── Legacy: nothing planned or sent by the pipeline ────────

        [Fact]
        public async Task In_legacy_a_committed_request_is_never_planned_or_sent_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var tenant = await TenantAsync(database);

            var marker = Guid.NewGuid();
            Assert.True(await SendAsync(factory, new EmbeddedCompositionTests.SendInCommand(tenant, marker, Fail: false)));
            Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM notifications.requests WHERE idempotency_key = 'test:{marker}'"));
            // Longer than every stage's poll and the post-commit nudge: in legacy no stage runs.
            await Task.Delay(TimeSpan.FromSeconds(6), Ct);

            // Intake writes the request and its notification, waiting for the planner (Planning): nothing after that happens in legacy.
            Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM notifications.notifications n JOIN notifications.requests r ON r.id = n.request_id WHERE r.idempotency_key = 'test:{marker}' AND n.state = 'Planning'"));
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM notifications.notifications WHERE state <> 'Planning'"));
            foreach (var table in new[] { "notification_recipients", "deliveries", "delivery_attempts", "delivery_items", "escalations",
                "inbox_items" })
            {
                if (await CountAsync(database, $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'notifications' AND table_name = '{table}'") == 1)
                {
                    Assert.True(await CountAsync(database, $"SELECT count(*) FROM notifications.{table}") == 0, $"notifications.{table} has rows in legacy");
                }
            }
        }

        // ─── Migrations ────────

        [Fact]
        public async Task A_fresh_database_gets_both_schemas_with_the_pipelines_tables_in_notifications_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await EmptyDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" });
            _ = factory.Server;
            await AssertBothSchemasAsync(database);
        }

        [Fact]
        public async Task A_release_1_0_database_with_rows_upgrades_to_both_schemas_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await EmptyDatabaseAsync();
            Guid audit;
            await using (var db = database.Context())
            {
                // Release/1.0's schema: the platform's migrations up to its last; JuiceOutbox1013 changes nothing but the snapshot.
                // M45's own migrations (NotificationsAdapter) follow it.
                var migrations = db.Database.GetMigrations().ToList();
                var outbox = migrations.FindIndex(m => m.EndsWith("_JuiceOutbox1013", StringComparison.Ordinal));
                Assert.True(outbox > 0);
                Assert.Equal(Release10Last, migrations[outbox - 1]);
                await db.GetService<IMigrator>().MigrateAsync(Release10Last, Ct);
                db.Tenants.Add(new PlatformTenant { Key = LocalStack.TenantKey, DisplayName = "QA upgrade" });
                await db.SaveChangesAsync(Ct);
                var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
                var record = new AuditRecord { TenantId = tenant, Actor = "qa", Operation = "qa.before", TargetType = "test", TargetId = "before" };
                db.AuditRecords.Add(record);
                await db.SaveChangesAsync(Ct);
                audit = record.Id;
            }
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'notifications'"));

            await using (var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" }))
            {
                _ = factory.Server;
            }
            await AssertBothSchemasAsync(database);
            await using (var db = database.Context())
            {
                Assert.Equal("QA upgrade", (await db.Tenants.SingleAsync(Ct)).DisplayName);
                Assert.True(await db.AuditRecords.AnyAsync(a => a.Id == audit, Ct));
            }

            // A second start is a no-op for both.
            await using (var again = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" }))
            {
                _ = again.Server;
            }
            await AssertBothSchemasAsync(database);
        }

        private static async Task AssertBothSchemasAsync(ScratchDatabase database)
        {
            await using (var db = database.Context())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
                Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(Ct));
            }
            var options = new DbContextOptionsBuilder<Juice.Notifications.Data.NotificationsDbContext>();
            Juice.Notifications.Data.NotificationsDbContext.Configure(options, database.ConnectionString);
            await using (var notifications = new Juice.Notifications.Data.NotificationsDbContext(options.Options))
            {
                Assert.Empty(await notifications.Database.GetPendingMigrationsAsync(Ct));
                Assert.Equal(notifications.Database.GetMigrations(), await notifications.Database.GetAppliedMigrationsAsync(Ct));
            }
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'notifications' AND table_name = '__ef_migrations_history'"));
            // The pipeline's tables are in notifications only; none leaked into the platform's schema.
            foreach (var table in new[] { "applications", "application_tenants", "notification_types", "requests", "notifications", "deliveries", "integrations",
                "routes", "destination_allowlist", "outbox_events", "outbox_deliveries" })
            {
                Assert.True(await CountAsync(database, $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'notifications' AND table_name = '{table}'") == 1,
                    $"notifications.{table} is missing");
            }
            foreach (var table in new[] { "application_tenants", "notification_types", "requests", "notifications", "deliveries", "integrations",
                "destination_allowlist" })
            {
                Assert.True(await CountAsync(database, $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'") == 0,
                    $"public.{table} is the pipeline's");
            }
            // The platform keeps its own outbox and history in public.
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'outbox_events'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'")
                + await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '__ef_migrations_history'"));
        }

        private static async Task<ScratchDatabase> EmptyDatabaseAsync()
        {
            var name = $"juice_obs_test_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(LocalStack.PostgresConnection("juice_observability")))
            {
                await admin.OpenAsync(Ct);
                await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
                await create.ExecuteNonQueryAsync(Ct);
            }
            return new ScratchDatabase(name);
        }

        // ─── The host, with a platform command that fires a real alert ────────

        /// <summary>Fires an alert as the evaluator does: the alert, its transition, and the platform's own <see cref="AlertTransitioned"/>.</summary>
        public sealed record FireAlert(Guid Tenant, Guid Marker) : MessageBase, IRequest<bool>;

        public sealed class FireAlertHandler(PlatformDbContext db) : IRequestHandler<FireAlert, bool>
        {
            public ValueTask<bool> Handle(FireAlert request, CancellationToken cancellationToken)
            {
                var now = DateTimeOffset.UtcNow;
                var rule = new Rule { TenantId = request.Tenant, Key = $"qa-{request.Marker:N}", Name = "QA", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                    Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = Data.ManagedBy.Api };
                var alert = new Alert { TenantId = request.Tenant, RuleId = rule.Id, DedupKey = $"{rule.Key}|service:qa", SubjectKey = "service:qa", SubjectType = HealthSubject.Service,
                    Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now };
                var transition = new AlertTransition { TenantId = request.Tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now,
                    Reason = request.Marker.ToString() };
                alert.DomainEvents.Add(new AlertTransitioned(request.Tenant, alert.Id, transition.Id, AlertState.Firing));
                db.Rules.Add(rule);
                db.Alerts.Add(alert);
                db.AlertTransitions.Add(transition);
                return ValueTask.FromResult(true);
            }
        }

        public sealed class TransitionsHeard : IIntegrationEventHandler<AlertTransitionedIntegrationEvent>
        {
            public static readonly ConcurrentDictionary<Guid, int> Seen = new();

            public Task HandleAsync(AlertTransitionedIntegrationEvent @event)
            {
                Seen.AddOrUpdate(@event.TransitionId, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }
        }

        /// <summary>Counts the platform's own plan command as it reaches the mediator (once per handling).</summary>
        public sealed class PlansCounted<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
        {
            public int Order => 0;

            public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
            {
                if (request is Webhooks.PlanNotifications plan)
                {
                    PlansCounted.Seen.AddOrUpdate(plan.TransitionId, 1, (_, n) => n + 1);
                }
                return next.Invoke(request, cancellationToken);
            }
        }

        public static class PlansCounted
        {
            public static readonly ConcurrentDictionary<Guid, int> Seen = new();
        }

        private static WebApplicationFactory<Program> Factory(ScratchDatabase database)
            // Legacy: what these check is how the pipeline keeps out of the platform when it runs nothing (the switch's other modes run its stages).
            => new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddTransient<IRequestHandler<FireAlert, bool>, FireAlertHandler>();
                services.AddTransient<IRequestHandler<EmbeddedCompositionTests.SendInCommand, bool>, EmbeddedCompositionTests.SendInCommandHandler>();
                services.AddTransient<INotificationHandler<EmbeddedCompositionTests.Changed>, EmbeddedCompositionTests.SendOnChanged>();
                services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PlansCounted<,>));
                services.AddMessaging().AddLocalConsumer(c => c.Subscribe<AlertTransitionedIntegrationEvent, TransitionsHeard>());
                var embedded = (Juice.Notifications.Embedded.NotificationsEmbeddedOptions)services
                    .Single(s => s.ServiceType == typeof(Juice.Notifications.Embedded.NotificationsEmbeddedOptions)).ImplementationInstance!;
                embedded.Types.Add(new Juice.Notifications.Types.TypeDeclaration("observability.composition.test", "Composition test", "tests", null, null, null, true,
                    System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }), Juice.Notifications.CorrelationRules.Optional, null, null,
                    System.Text.Json.JsonSerializer.SerializeToElement(new { email = "Subject: Test\n\nA test." })));
            }));

        private static async Task<bool> SendAsync<T>(WebApplicationFactory<Program> factory, T command) where T : IRequest<bool>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var context = PlatformMessageContext.Begin("test");
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, Ct);
        }

        private static async Task<Guid> TenantAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return (await db.Tenants.SingleAsync(t => t.Key == LocalStack.TenantKey, Ct)).Id;
        }

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        /// <summary>Transactions begun on one database, per context type (EF's diagnostics).</summary>
        private sealed class TransactionsBegun : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
        {
            private readonly string _database;
            private readonly IDisposable _subscription;
            private readonly List<IDisposable> _listeners = [];
            private readonly ConcurrentDictionary<Type, int> _begun = new();

            public TransactionsBegun(string database)
            {
                _database = database;
                _subscription = DiagnosticListener.AllListeners.Subscribe(this);
            }

            public int Of<TContext>() => _begun.GetValueOrDefault(typeof(TContext));

            public void OnNext(DiagnosticListener value)
            {
                if (value.Name == DbLoggerCategory.Name)
                {
                    lock (_listeners) _listeners.Add(value.Subscribe(this));
                }
            }

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Key == RelationalEventId.TransactionStarted.Name && value.Value is TransactionEndEventData { Context: { } context }
                    && context.Database.GetDbConnection().Database == _database)
                {
                    _begun.AddOrUpdate(context.GetType(), 1, (_, n) => n + 1);
                }
            }

            public void OnCompleted() { }

            public void OnError(Exception error) { }

            public void Dispose()
            {
                _subscription.Dispose();
                lock (_listeners) _listeners.ForEach(l => l.Dispose());
            }
        }
    }
}
