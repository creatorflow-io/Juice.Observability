using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>Core 2.0 M2 slice A against the stack: the scheduler with the registry: a rule whose provider is gone holds, and the lease still decides who evaluates.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class SchedulerSeamTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        /// <summary>Counts how often the scheduler reads the time: it does so on a tick only while it holds the lease.</summary>
        private sealed class CountingClock : TimeProvider
        {
            public int Reads;

            public override DateTimeOffset GetUtcNow()
            {
                Interlocked.Increment(ref Reads);
                return DateTimeOffset.UtcNow;
            }
        }

        private static async Task<Rule> AddRuleAsync(ScratchDatabase database, RuleDefinition definition)
        {
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

        private static async Task SendAsync(string service, int total, int failed, DateTimeOffset at)
        {
            var before = int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_traces WHERE ServiceName = '{service}'"));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "i1")], Otlp.Requests(total, failed, at)));
            await LocalStack.WaitForSpansAsync(service, before + total);
        }

        private static RuleScheduler Scheduler(ScratchDatabase database, TimeProvider clock, ILogger<RuleScheduler> logger, RulesOptions? options = null)
        {
            options ??= new RulesOptions();
            var services = new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();
            return new RuleScheduler(services.GetRequiredService<IServiceScopeFactory>(), PlatformServices.Evaluator(database, clock, options), Options.Create(options),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), clock, logger);
        }

        [Fact]
        public async Task Provider_not_installed_rule_holds_logged_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"seam-{_run}-gone";
            await SendAsync(service, total: 20, failed: 20, at: DateTimeOffset.UtcNow.AddMinutes(-1));
            var definition = ThresholdSeamTests.Rule(r =>
            {
                r.Key = $"gone-{_run}";
                r.Service = service;
                r.ForDuration = TimeSpan.Zero;
                r.MinimumSamples = 10;
                r.ThresholdSource = new RuleThresholdSource { Provider = "demo.p95" };
            });
            var rule = await AddRuleAsync(database, definition);
            // An alert already open, made while the provider was installed.
            var installed = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            await ThresholdEvaluationTests.Evaluator(database, installed, TimeProvider.System).EvaluateAsync(rule, Ct);
            Alert Read() { using var db = database.Context(); return db.Alerts.AsNoTracking().Single(); }
            var before = Read();
            Assert.Equal(AlertState.Firing, before.State);

            var minute = TimeSpan.FromMinutes(1);
            var grid = RuleScheduler.Grid(DateTimeOffset.UtcNow, minute);
            var clock = new FixedClock(grid);
            var capture = new LogCapture();
            var skipped = new List<string?>();
            var evaluations = new List<string?>();
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Juice.Observability" && instrument.Name is "juice.observability.rules.skipped" or "juice.observability.rules.evaluations")
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((i, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (i.Name.EndsWith("skipped", StringComparison.Ordinal) && tag.Key == "reason") lock (skipped) skipped.Add(tag.Value as string);
                    if (i.Name.EndsWith("evaluations", StringComparison.Ordinal) && tag.Key == "outcome") lock (evaluations) evaluations.Add(tag.Value as string);
                }
            });
            listener.Start();
            var scheduler = Scheduler(database, clock, new LoggerFactory([capture]).CreateLogger<RuleScheduler>());

            foreach (var seconds in new[] { 0.5, 61, 122 })
            {
                clock.Now = grid + TimeSpan.FromSeconds(seconds);
                await scheduler.TickAsync(Ct);
                await scheduler.Running;
            }

            // Held: no evaluation was started, nothing failed, and the alert is exactly as it was.
            var after = Read();
            Assert.Equal((before.State, before.LastEvaluatedAt, before.LastValue, before.NoData), (after.State, after.LastEvaluatedAt, after.LastValue, after.NoData));
            Assert.DoesNotContain("failure", evaluations);
            Assert.DoesNotContain("success", evaluations);
            Assert.Equal(3, skipped.Count(r => r == "provider_missing"));
            Assert.Empty(capture.At(LogLevel.Error));
            var told = capture.Entries.Where(e => e.Message.Contains("demo.p95", StringComparison.Ordinal)).ToList();
            Assert.Single(told);
            Assert.Equal(LogLevel.Warning, told[0].Level);
            Assert.Contains(definition.Key, told[0].Message);
        }

        [Fact]
        public async Task Only_the_replica_holding_the_lease_evaluates_and_another_takes_over_when_it_stops_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var options = new RulesOptions { Tick = TimeSpan.FromMilliseconds(200) };
            var (clockA, clockB) = (new CountingClock(), new CountingClock());
            using var a = Scheduler(database, clockA, Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleScheduler>.Instance, options);
            using var b = Scheduler(database, clockB, Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleScheduler>.Instance, options);

            await a.StartAsync(Ct);
            await WaitAsync(() => clockA.Reads > 0);
            await b.StartAsync(Ct);
            var readsA = clockA.Reads;
            await WaitAsync(() => clockA.Reads > readsA + 3);
            Assert.Equal(0, clockB.Reads);                     // B waits: it never ticked while A held the lease

            await a.StopAsync(Ct);                             // A's lease is released
            await WaitAsync(() => clockB.Reads > 0);           // B takes it and ticks
            var frozen = clockA.Reads;
            await Task.Delay(600, Ct);
            Assert.Equal(frozen, clockA.Reads);                // A stopped for good
            await b.StopAsync(Ct);
        }

        /// <summary>The scheduler runs on the leased worker loop: a rule that waits for a worker gives up when the lease is lost, and the replica that takes it evaluates it.</summary>
        [Fact]
        public async Task Scheduler_on_leased_host_drops_queued_rules_on_lease_loss()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            // The first evaluation holds the only worker inside its provider; the second waits behind it.
            var provider = new ThresholdSeamTests.Provider(_ =>
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(60));
                return new Dictionary<string, RuleThreshold>();
            });
            foreach (var key in new[] { $"queue-{_run}-a", $"queue-{_run}-b" })
            {
                await AddRuleAsync(database, ThresholdSeamTests.Rule(r =>
                {
                    r.Key = key;
                    r.ThresholdSource = new RuleThresholdSource { Provider = "demo.p95" };
                }));
            }
            var options = new RulesOptions { Tick = TimeSpan.FromMilliseconds(200), MaxConcurrency = 1, EvaluationTimeout = TimeSpan.FromMinutes(2) };
            var registry = new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [provider], [new ModuleRuleContribution("demo", typeof(ThresholdSeamTests.Provider))]);
            var services = new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();
            var released = 0;
            var queued = 0;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Juice.Observability" && instrument.Name is "juice.observability.rules.skipped" or "juice.observability.rules.queued")
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>((i, _, tags, _) =>
            {
                if (i.Name.EndsWith("queued", StringComparison.Ordinal)) Interlocked.Increment(ref queued);
                foreach (var tag in tags)
                {
                    if (i.Name.EndsWith("skipped", StringComparison.Ordinal) && tag is { Key: "reason", Value: "released" }) Interlocked.Increment(ref released);
                }
            });
            listener.Start();
            using var scheduler = new RuleScheduler(services.GetRequiredService<IServiceScopeFactory>(),
                ThresholdEvaluationTests.Evaluator(database, provider, TimeProvider.System), Options.Create(options),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleScheduler>.Instance, registry);
            try
            {
                await scheduler.StartAsync(Ct);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                await WaitAsync(() => Volatile.Read(ref queued) > 0);

                // The lease's session ends: the scheduler takes it again as a new generation, and what it queued before gives up.
                await using (var admin = new Npgsql.NpgsqlConnection(database.ConnectionString))
                {
                    await admin.OpenAsync(Ct);
                    await using var kill = new Npgsql.NpgsqlCommand(
                        $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = '{database.Name}' AND application_name LIKE 'juice-worker:rules:%'", admin);
                    Assert.Equal(1L, await kill.ExecuteScalarAsync(Ct));
                }
                await WaitAsync(() => Volatile.Read(ref released) > 0);

                Assert.Equal(1, provider.Calls);   // the queued rule never ran under the lost lease
            }
            finally
            {
                release.Set();
                await scheduler.StopAsync(Ct);
            }
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(200, Ct);
            Assert.True(condition(), "the condition was not met in 30 s");
        }
    }
}
