using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Messaging.Policies;
using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Types;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The notification pipeline embedded in the platform's host (phase 5, M45-0): what the host's container holds (proof point 8), and the
    /// platform's messaging beside the pipeline's: each transaction behavior wraps its own product's commands, each event is delivered once, and
    /// a request sent in a platform command commits or rolls back with it.
    /// </summary>
    public class EmbeddedCompositionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string TestType = "observability.composition.test";

        [Fact]
        public async Task The_host_has_no_notifications_authentication_hub_broker_or_audit_client_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;

            var schemes = await factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
            Assert.All(schemes, s => Assert.DoesNotContain("Juice.Notifications", s.HandlerType.Assembly.GetName().Name));
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToList();
            // Not the service's host (Juice.Notifications.Api); its contracts come with the admin endpoints (Juice.Notifications.Admin, M48).
            Assert.DoesNotContain(loaded, n => n == "Juice.Notifications.Api"
                || n.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal) || n.StartsWith("RabbitMQ", StringComparison.Ordinal)
                || n.StartsWith("Juice.EventBus.RabbitMQ", StringComparison.Ordinal) || n.StartsWith("Juice.Audit", StringComparison.Ordinal));
        }


        [Fact]
        public async Task A_platform_command_opens_no_pipeline_transaction_and_its_event_is_delivered_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            using var begun = new TransactionsBegun(database.Name);
            var tenant = await TenantAsync(database);

            var marker = Guid.NewGuid();
            await SendAsync(factory, new Record(tenant, marker));
            for (var i = 0; i < 50 && !Counted.Seen.ContainsKey(marker); i++) await Task.Delay(100, Ct);
            // The outbox's delivery pass (every 2 seconds) would hand a second delivery over.
            await Task.Delay(TimeSpan.FromSeconds(4), Ct);

            // One delivery: two would be handed over and handled each (the policies answered "local" twice). The post-commit dispatch and the
            // delivery pass may still both hand over the one, as before the pipeline (a consumer's own check keeps it from acting twice).
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM outbox_deliveries"));
            Assert.InRange(Counted.Seen.GetValueOrDefault(marker), 1, 2);
            Assert.Equal(1, begun.Of<PlatformDbContext>());
            Assert.Equal(0, begun.Of<Juice.Notifications.Data.NotificationsDbContext>());
            var routes = await factory.Services.GetRequiredService<IMessagePublishingPolicy>().ResolveAsync(new PolicyResolveContext { EventType = nameof(Recorded), Domain = "" });
            Assert.Single(routes);
        }

        [Fact]
        public void Every_platform_domain_event_runs_its_handlers_one_after_another()
        {
            // Juice's mediator runs a notification's handlers in parallel unless it is a sequence; they share the command's context.
            var events = new[] { typeof(PlatformDbContext), typeof(Juice.Observability.Rules.Notifications.NotificationPlan), typeof(ProducerAdapter) }
                .Select(t => t.Assembly).Distinct().SelectMany(a => a.GetTypes())
                .Where(t => typeof(INotification).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToList();
            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.True(typeof(ISequenceNotification).IsAssignableFrom(e), $"{e.Name} isn't an ISequenceNotification"));
        }

        [Fact]
        public async Task Only_the_pipelines_forwarder_handles_audit_events_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;

            // Audit events are dispatched on save and can't be sequences: a second handler would run beside the forwarder on the same
            // connection. The host's audit sink is called by the forwarder, never as a handler of its own.
            await using var scope = factory.Services.CreateAsyncScope();
            foreach (var entity in new[] { typeof(Juice.Notifications.Data.Integration), typeof(Rule) })
            {
                var handlers = scope.ServiceProvider.GetServices(typeof(INotificationHandler<>).MakeGenericType(
                    typeof(Juice.Domain.Events.AuditEvent<>).MakeGenericType(entity))).Select(h => h!.GetType().Name).ToList();
                Assert.Equal(["AuditForwarder`1"], handlers);
            }
        }

        [Fact]
        public async Task A_pipeline_command_opens_its_own_transaction_and_no_platform_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var application = await ScalarAsync<Guid>(database, $"SELECT id FROM notifications.applications WHERE key = '{NotificationsRegistration.ApplicationKey}'");
            using var begun = new TransactionsBegun(database.Name);

            const string type = "observability.composition.reverse";
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = PlatformMessageContext.Begin("test");
                var declared = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new DeclareType(application, NotificationsRegistration.ApplicationKey,
                    new TypeDeclaration(type, "Reverse", "tests", null, null, null, null, JsonSerializer.SerializeToElement(new { type = "object" }),
                        CorrelationRules.Optional, null, null, null)), Ct);
                Assert.True(declared.Value is { IsNew: true }, declared.Message);
            }

            Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM notifications.notification_types WHERE key = '{type}'"));
            Assert.Equal(1, begun.Of<Juice.Notifications.Data.NotificationsDbContext>());
            Assert.Equal(0, begun.Of<PlatformDbContext>());
        }

        [Fact]
        public async Task A_request_sent_in_a_platform_command_commits_with_it_in_its_transaction_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            using var begun = new TransactionsBegun(database.Name);
            var tenant = await TenantAsync(database);

            var marker = Guid.NewGuid();
            Assert.True(await SendAsync(factory, new SendInCommand(tenant, marker, Fail: false)));

            Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM notifications.requests WHERE idempotency_key = 'test:{marker}' AND tenant_id = '{LocalStack.TenantKey}'"));
            await using (var db = database.Context()) Assert.True(await db.AuditRecords.AnyAsync(a => a.TargetId == marker.ToString(), Ct));
            // The command's own transaction only: the client's scope opened neither a platform nor a pipeline transaction of its own.
            Assert.Equal(1, begun.Of<PlatformDbContext>());
            Assert.Equal(0, begun.Of<Juice.Notifications.Data.NotificationsDbContext>());
        }

        /// <summary>
        /// The flake of the full run: a host worker's pass (the action-request sweep begins a transaction at the host's start) landed inside the
        /// count of the command's transactions, 2 for 1. A worker names its caller, and the count is of the test's own flow.
        /// </summary>
        [Fact]
        public async Task A_worker_pass_in_the_same_database_is_not_counted_among_the_commands_transactions_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var begun = new TransactionsBegun(database.Name);

            await Task.Run(async () =>
            {
                PlatformCaller.Set("action-requests");
                await using var db = database.Context();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            }, Ct);
            Assert.Equal(0, begun.Of<PlatformDbContext>());

            await using (var db = database.Context())
            await using (await db.Database.BeginTransactionAsync(Ct))
            {
            }
            Assert.Equal(1, begun.Of<PlatformDbContext>());
        }

        [Fact]
        public async Task A_rolled_back_platform_command_takes_its_request_with_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var tenant = await TenantAsync(database);

            var marker = Guid.NewGuid();
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(async () => await SendAsync(factory, new SendInCommand(tenant, marker, Fail: true)));
            Assert.Equal(SendInCommandHandler.Failure, failed.Message);

            Assert.Equal(0, await CountAsync(database, $"SELECT count(*) FROM notifications.requests WHERE idempotency_key = 'test:{marker}'"));
            await using (var db = database.Context()) Assert.False(await db.AuditRecords.AnyAsync(a => a.TargetId == marker.ToString(), Ct));
        }

        [Fact]
        public async Task Outside_a_transaction_the_client_refuses_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;

            await using var scope = factory.Services.CreateAsyncScope();
            var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() =>
                scope.ServiceProvider.GetRequiredService<INotificationClient>().SendAsync(Request(Guid.NewGuid()), Ct));
            Assert.Equal(ErrorCodes.NoTransaction, refused.Code);
        }

        // ─── The host, with a command and a consumer of the test's own ────────

        public sealed record Recorded(Guid Marker) : IntegrationEvent;

        public sealed class Counted : IIntegrationEventHandler<Recorded>
        {
            public static readonly ConcurrentDictionary<Guid, int> Seen = new();

            public Task HandleAsync(Recorded @event)
            {
                Seen.AddOrUpdate(@event.Marker, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }
        }

        /// <summary>A platform command: an audit row, and an event through the platform's outbox.</summary>
        public sealed record Record(Guid Tenant, Guid Marker) : MessageBase, IRequest<bool>;

        public sealed class RecordHandler(PlatformDbContext db, IMessageService<PlatformDbContext> messages) : IRequestHandler<Record, bool>
        {
            public async ValueTask<bool> Handle(Record request, CancellationToken cancellationToken)
            {
                db.AuditRecords.Add(Audit(request.Tenant, request.Marker));
                await messages.PublishAsync(new Recorded(request.Marker), cancellationToken);
                return true;
            }
        }

        /// <summary>
        /// A platform command whose change sends a request, as the adapter will: from a domain event's handler, which the transaction behavior
        /// runs inside the command's transaction (a handler runs before it begins).
        /// </summary>
        public sealed record SendInCommand(Guid Tenant, Guid Marker, bool Fail) : MessageBase, IRequest<bool>;

        public sealed record Changed(Guid Marker, bool Fail) : MessageBase, INotification;

        public sealed class SendInCommandHandler(PlatformDbContext db) : IRequestHandler<SendInCommand, bool>
        {
            public const string Failure = "the command fails after its request was written";

            public ValueTask<bool> Handle(SendInCommand request, CancellationToken cancellationToken)
            {
                db.AuditRecords.Add(Audit(request.Tenant, request.Marker));
                // An alert is the platform's aggregate: the behavior dispatches its events, here the test's own (no transition, so no legacy plan).
                var now = DateTimeOffset.UtcNow;
                var rule = new Rule { TenantId = request.Tenant, Key = $"test-{request.Marker:N}", Name = "Test", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                    Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = Data.ManagedBy.Api };
                var alert = new Alert { TenantId = request.Tenant, RuleId = rule.Id, DedupKey = $"{rule.Key}|service:test", SubjectKey = "service:test", SubjectType = HealthSubject.Service,
                    Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now };
                alert.DomainEvents.Add(new Changed(request.Marker, request.Fail));
                db.Rules.Add(rule);
                db.Alerts.Add(alert);
                return ValueTask.FromResult(true);
            }
        }

        public sealed class SendOnChanged(INotificationClient client) : INotificationHandler<Changed>
        {
            public async ValueTask Handle(Changed notification, CancellationToken cancellationToken)
            {
                await client.SendAsync(Request(notification.Marker), cancellationToken);
                if (notification.Fail)
                {
                    throw new InvalidOperationException(SendInCommandHandler.Failure);
                }
            }
        }

        private static NotificationToSend Request(Guid marker) => new(TestType, $"test:{marker}")
        {
            CorrelationKey = $"test:{marker}",
            Recipients = [new NotificationRecipientSpec("address", "email", "ops@example.invalid")],
        };

        private static AuditRecord Audit(Guid tenant, Guid marker)
            => new() { TenantId = tenant, Actor = "test", Operation = "composition.test", TargetType = "test", TargetId = marker.ToString() };

        private static WebApplicationFactory<Program> Factory(ScratchDatabase database)
            // What these check is how the pipeline keeps out of the platform's commands and transactions: it runs no stage.
            => new ApiTests.ApiFactory(database, settings: ProducerAdapterTests.Quiet()).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddTransient<IRequestHandler<Record, bool>, RecordHandler>();
                services.AddTransient<IRequestHandler<SendInCommand, bool>, SendInCommandHandler>();
                services.AddTransient<INotificationHandler<Changed>, SendOnChanged>();
                services.AddMessaging().AddLocalConsumer(c => c.Subscribe<Recorded, Counted>());
                // A type of the test's own, declared at start with the platform's application.
                var embedded = (NotificationsEmbeddedOptions)services.Single(s => s.ServiceType == typeof(NotificationsEmbeddedOptions)).ImplementationInstance!;
                embedded.Types.Add(new TypeDeclaration(TestType, "Composition test", "tests", null, null, null, true,
                    JsonSerializer.SerializeToElement(new { type = "object" }), CorrelationRules.Optional, null, null,
                    JsonSerializer.SerializeToElement(new { email = "Subject: Test\n\nA test." })));
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

        private static async Task<T> ScalarAsync<T>(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync(Ct))!;
        }

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        /// <summary>
        /// Transactions begun on one database, per context type (EF's diagnostics), by the test's own flow: a host worker's pass (the action-request
        /// sweep and the SLO counter run one at the start, and a loaded machine puts it after the subscription) names its caller and is not counted.
        /// </summary>
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
                    && context.Database.GetDbConnection().Database == _database && PlatformCaller.Current == "other")
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
