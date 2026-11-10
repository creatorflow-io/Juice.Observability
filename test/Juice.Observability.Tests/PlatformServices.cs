using Juice.Messaging.Outbox;
using Juice.Messaging.Outbox.Delivery;
using Juice.Messaging.Outbox.Delivery.Processing;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The platform's commands as the host wires them (the mediator, its TransactionBehavior, the outbox and the consumer that plans notifications),
    /// without any background service: a test hands the outbox over itself with <see cref="DeliverAsync"/>, so it decides when notifications are planned.
    /// </summary>
    internal static class PlatformServices
    {
        public static ServiceProvider Build(ScratchDatabase database, TimeProvider? clock = null, RulesOptions? rules = null, ILoggerProvider? logging = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                if (logging is not null) builder.AddProvider(logging);
            });
            services.AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString));
            services.AddSingleton(clock ?? TimeProvider.System);
            services.AddSingleton(Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }));
            services.AddSingleton(Options.Create(rules ?? new RulesOptions()));
            services.AddSingleton(new RuleTemplates(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }))));
            services.AddSingleton<RuleEvaluator>();
            services.AddSingleton(Options.Create(new Slos.SloOptions()));
            services.AddPlatformMessaging();
            services.AddMessaging().AddLocalConsumer(consumer => consumer.Subscribe<AlertTransitionedIntegrationEvent, AlertTransitionedConsumer>());
            return services.BuildServiceProvider();
        }

        public static RuleEvaluator Evaluator(ScratchDatabase database, TimeProvider? clock = null, RulesOptions? rules = null)
            => Build(database, clock, rules).GetRequiredService<RuleEvaluator>();

        /// <summary>
        /// What the platform's outbox delivery does on its next pass: every message waiting goes to its consumer, here the one that plans notifications.
        /// Uses its own services, so any test's database works: the outbox is in it.
        /// </summary>
        public static async Task DeliverAsync(ScratchDatabase database, CancellationToken cancellationToken)
        {
            var errors = new ErrorLog();
            await using var services = Build(database, logging: errors);
            var worker = new DeliveryWorker<PlatformDbContext>(services, "local", "send-pending", services.GetRequiredService<IDeliveryPolicyResolver>(),
                NullLogger<DeliveryWorker<PlatformDbContext>>.Instance);
            await worker.InitializeAsync(cancellationToken);
            while (await worker.ExecuteAsync(cancellationToken))
            {
            }
            // A handler's exception is logged and the row marked for retry: say what it was, or a test sees only a row that failed.
            if (!errors.IsEmpty)
            {
                throw new InvalidOperationException("Delivering the outbox logged errors:\n" + string.Join("\n", errors));
            }
        }

        /// <summary>Errors logged while delivering, with their exceptions.</summary>
        private sealed class ErrorLog : System.Collections.Concurrent.ConcurrentQueue<string>, ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

            public void Dispose()
            {
            }

            private sealed class Logger(ErrorLog log, string category) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (IsEnabled(logLevel))
                    {
                        log.Enqueue($"{category}: {formatter(state, exception)} {exception}");
                    }
                }
            }
        }

        /// <summary>The outbox's rows for this database, to check what an action published. A failed row's state carries its error, so a failure says why.</summary>
        public static async Task<List<(string Type, string State)>> OutboxAsync(ScratchDatabase database, CancellationToken cancellationToken)
        {
            await using var db = database.Context();
            return (await db.OutboxDeliveries.AsNoTracking().Include(d => d.OutboxEvent).ToListAsync(cancellationToken))
                .Select(d => (d.OutboxEvent!.EventTypeName, d.State == DeliveryState.Failed ? $"Failed: {d.LastError}" : d.State.ToString())).ToList();
        }
    }

    internal static class OutboxQueries
    {
        /// <summary>The framework's outbox events in a platform database.</summary>
        public static IQueryable<OutboxEvent> OutboxEvents(this PlatformDbContext db) => db.Outbox.AsNoTracking();
    }
}
