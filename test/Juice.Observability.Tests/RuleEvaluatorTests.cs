using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Roadmap Phase 3 gate, end to end against the stack: rule templates over real telemetry, the alert lifecycle in
    /// PostgreSQL, deduplication, tenant scope, no data, timeout, overload, lag and breach-to-alert time.
    /// </summary>
    public class RuleEvaluatorTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Error_rate_alert_goes_pending_firing_and_resolves_with_real_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"rules-{_run}-orders";
            var clock = new FixedClock(DateTimeOffset.UtcNow);
            var rule = await AddRuleAsync(database, ErrorRate(service));
            var evaluator = Evaluator(database, clock);

            await SendAsync(service, total: 20, failed: 10, at: clock.Now.AddMinutes(-1));
            Assert.Equal(1, (await evaluator.EvaluateAsync(rule, Ct)).Opened);
            Assert.Equal(AlertState.Pending, (await AlertAsync(database)).State);

            clock.Now += TimeSpan.FromMinutes(2);                                     // ForDuration later, still breaching
            Assert.Equal(1, (await evaluator.EvaluateAsync(rule, Ct)).Fired);
            clock.Now += TimeSpan.FromMinutes(1);
            Assert.Equal(0, (await evaluator.EvaluateAsync(rule, Ct)).Fired);         // fires once
            var firing = await AlertAsync(database);
            Assert.Equal((AlertState.Firing, 0.5), (firing.State, firing.LastValue));
            Assert.Equal("ErrorRate", firing.Evidence["template"]);

            clock.Now += TimeSpan.FromMinutes(10);                                    // telemetry ages out of the window
            await evaluator.EvaluateAsync(rule, Ct);
            var silent = await AlertAsync(database);
            Assert.Equal((AlertState.Firing, true), (silent.State, silent.NoData));    // no data never resolves

            await SendAsync(service, total: 50, failed: 0, at: clock.Now.AddMinutes(-1));
            await evaluator.EvaluateAsync(rule, Ct);                                  // recovering
            clock.Now += TimeSpan.FromMinutes(2);
            await SendAsync(service, total: 50, failed: 0, at: clock.Now.AddMinutes(-1));
            Assert.Equal(1, (await evaluator.EvaluateAsync(rule, Ct)).Resolved);

            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync();
            Assert.Equal(AlertState.Resolved, alert.State);
            Assert.NotNull(alert.FiredAt);
            Assert.Equal(["Pending", "Firing", "Resolved"],
                await db.AlertTransitions.Where(t => t.AlertId == alert.Id).OrderBy(t => t.At).ThenBy(t => t.Id).Select(t => t.ToState.ToString()).ToListAsync());
        }

        [Fact]
        public async Task Too_few_samples_is_no_data_not_an_alert_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"rules-{_run}-quiet";
            var rule = await AddRuleAsync(database, ErrorRate(service, r => r.MinimumSamples = 100));
            await SendAsync(service, total: 10, failed: 10, at: DateTimeOffset.UtcNow.AddMinutes(-1));   // 100 % of 10 requests

            Assert.Equal(0, (await Evaluator(database).EvaluateAsync(rule, Ct)).Opened);
        }

        [Fact]
        public async Task Concurrent_evaluations_keep_one_open_alert_per_subject_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"rules-{_run}-dedup";
            var rule = await AddRuleAsync(database, ErrorRate(service));
            await SendAsync(service, total: 20, failed: 20, at: DateTimeOffset.UtcNow.AddMinutes(-1));

            // Two replicas evaluating the same rule at once: the partial unique index lets only one open alert in.
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { await Evaluator(database).EvaluateAsync(rule, Ct); return true; }
                catch (DbUpdateException) { return false; }
            }));
            await Evaluator(database).EvaluateAsync(rule, Ct);

            await using var db = database.Context();
            Assert.Equal(1, await db.Alerts.CountAsync(a => a.State != AlertState.Resolved));
            Assert.Contains(true, results);
        }

        [Fact]
        public async Task Rules_see_only_their_tenants_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"rules-{_run}-foreign";
            var rule = await AddRuleAsync(database, ErrorRate(service));
            var values = string.Join(",", Enumerable.Range(0, 20).Select(_ =>
                $"(now64(9) - INTERVAL 1 MINUTE, '{Otlp.TraceId()}', '{Otlp.SpanId()}', '{service}', 'GET', 'Server', 'Error', map('juice.platform_tenant_id', 'other-tenant'))"));
            await LocalStack.ClickHouseAsync($"INSERT INTO otel.otel_traces (Timestamp, TraceId, SpanId, ServiceName, SpanName, SpanKind, StatusCode, ResourceAttributes) VALUES {values}");
            await LocalStack.WaitForSpansAsync(service, 20);

            Assert.Equal(0, (await Evaluator(database).EvaluateAsync(rule, Ct)).Subjects);
        }

        [Fact]
        public async Task Scheduler_bounds_concurrency_times_out_and_records_lag_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            for (var i = 0; i < 3; i++)
            {
                await AddRuleAsync(database, ErrorRate($"rules-{_run}-load{i}", r => r.Key = $"load-{_run}-{i}"));
            }
            var measurements = new List<(string Name, string? Reason, string? Outcome)>();
            using var listener = Listen(measurements);

            // One worker for three due rules: one starts, two wait (saturated) and start on later ticks.
            var scheduler = Scheduler(database, o => o.MaxConcurrency = 1);
            await scheduler.TickAsync(Ct);
            Assert.Equal(2, measurements.Count(m => m.Reason == "saturated"));
            for (var i = 0; i < 2; i++)
            {
                await scheduler.Running;
                await scheduler.TickAsync(Ct);
            }
            await scheduler.Running;
            Assert.Equal(3, measurements.Count(m => m.Name == "juice.observability.rules.evaluations" && m.Outcome == "success"));
            Assert.Equal(3, measurements.Count(m => m.Name == "juice.observability.rules.lag"));

            // An evaluation past its timeout is cancelled and counted; alerts are unchanged.
            var slow = Scheduler(database, o => o.EvaluationTimeout = TimeSpan.FromMilliseconds(1));
            await slow.TickAsync(Ct);
            await slow.Running;
            Assert.Contains(measurements, m => m.Outcome == "timeout");
            await using var db = database.Context();
            Assert.Equal(0, await db.Alerts.CountAsync());
        }

        /// <summary>
        /// Breach-to-alert (requirements §9: p95 ≤ 2 min after ForDuration for a 1-minute schedule). The scheduler runs
        /// a 5 s schedule here; the delay after ForDuration is at most lag + schedule, so for a 1-minute schedule it is
        /// at most 10 s + 60 s = 70 s.
        /// </summary>
        [Fact]
        public async Task Breach_fires_within_lag_plus_schedule_after_for_duration_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"rules-{_run}-timing";
            await AddRuleAsync(database, ErrorRate(service, r =>
            {
                r.Schedule = TimeSpan.FromSeconds(5);
                r.Window = TimeSpan.FromMinutes(1);
                r.ForDuration = TimeSpan.FromSeconds(10);
            }));
            var scheduler = Scheduler(database);
            var breach = Stopwatch.StartNew();
            await SendAsync(service, total: 20, failed: 20, at: DateTimeOffset.UtcNow);

            AlertState? state = null;
            while (state != AlertState.Firing && breach.Elapsed < TimeSpan.FromSeconds(90))
            {
                await scheduler.TickAsync(Ct);
                await scheduler.Running;
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
                await using var db = database.Context();
                state = (await db.Alerts.SingleOrDefaultAsync())?.State;
            }
            TestContext.Current.SendDiagnosticMessage($"breach to firing: {breach.Elapsed.TotalSeconds:F1} s");
            Assert.Equal(AlertState.Firing, state);
            // lag (10 s) + ForDuration (10 s) + schedule (5 s) + ingestion and tick slack
            Assert.True(breach.Elapsed < TimeSpan.FromSeconds(35), $"took {breach.Elapsed}");
        }

        [Fact]
        public async Task Every_template_measures_real_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            var templates = new RuleTemplates(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })));
            var now = DateTimeOffset.UtcNow;
            var window = new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 100);
            var (slow, gone, delivery) = ($"rules-{_run}-slow", $"rules-{_run}-gone", $"rules-{_run}-delivery");

            // Latency: 20 requests of 300 ms.
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", slow)],
                Otlp.Requests(20, 0, now.AddMinutes(-1)).Select(s => Otlp.Lasting(s, TimeSpan.FromMilliseconds(300))).ToArray()));
            // Throughput absent: traffic 30 minutes ago, none in the window.
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", gone)], Otlp.Requests(15, 0, now.AddMinutes(-30))));
            // Delivery: cumulative counters, +20 attempts and +6 failures since before the window, plus a new error type
            // whose first point already counts 2.
            (string, string)[] resource = [("service.name", delivery)];
            var start = now.AddMinutes(-10);
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "delivery_attempt_total", start, [("publisher", "outbox")],
                (now.AddMinutes(-7), 10), (now.AddMinutes(-1), 30)));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "delivery_failure_total", start, [("publisher", "outbox"), ("error_type", "Timeout")],
                (now.AddMinutes(-7), 0), (now.AddMinutes(-1), 6)));
            await LocalStack.SendAsync("metrics", Otlp.Sum(resource, "delivery_failure_total", start, [("publisher", "outbox"), ("error_type", "Refused")],
                (now.AddMinutes(-1), 2)));
            await LocalStack.WaitForSpansAsync(slow, 20);
            await LocalStack.WaitForSpansAsync(gone, 15);
            for (var i = 0; i < 50 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_sum WHERE ServiceName = '{delivery}'")) < 5; i++)
            {
                await Task.Delay(200, Ct);
            }

            async Task<RuleObservation> Observe(RuleTemplate template, string service)
                => Assert.Single(await templates.ObserveAsync(new RuleDefinition { Key = "t", Name = "t", Template = template, Service = service }, window, Ct));

            var latency = await Observe(RuleTemplate.LatencyP95, slow);
            Assert.InRange(latency.Value!.Value, 299, 301);
            Assert.Equal(20, latency.Samples);

            var absent = await Observe(RuleTemplate.ThroughputAbsent, gone);
            Assert.Equal((0d, 15L), (absent.Value, absent.Samples));        // no requests now, 15 in the baseline hour

            var failures = await Observe(RuleTemplate.DeliveryFailureRate, delivery);
            Assert.Equal((0.4, 20L), (failures.Value, failures.Samples));   // 6 + 2 of 20 attempts

            var hosts = await templates.ObserveAsync(new RuleDefinition { Key = "t", Name = "t", Template = RuleTemplate.HostMemory }, window, Ct);
            var host = Assert.Single(hosts, h => h.Host == "local-docker-host");   // the Collector's host metrics
            Assert.InRange(host.Value!.Value, 0.01, 0.99);
        }

        [Fact]
        public async Task Count_templates_measure_counter_increases_Async()
        {
            LocalStack.RequireAvailable();
            var templates = new RuleTemplates(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })));
            var now = DateTimeOffset.UtcNow;
            var window = new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 100);
            var (start, before, inside, last) = (now.AddMinutes(-20), now.AddMinutes(-7), now.AddMinutes(-3), now.AddMinutes(-1));
            var (mediator, quiet, stopped, consumer) = ($"rules-{_run}-mediator", $"rules-{_run}-quiet", $"rules-{_run}-stopped", $"rules-{_run}-consumer");

            Task Send(string service, string metric, (string, string)[] attributes, params (DateTimeOffset, long)[] points)
                => LocalStack.SendAsync("metrics", Otlp.Sum([("service.name", service)], metric, start, attributes, points));

            (string, string)[] create = [("juice.operation", "Create")], fail = [("juice.operation", "Create"), ("error.type", "Boom")];
            // 3 more errors than before the window; a new series whose first point already counts 2; 10 more requests.
            await Send(mediator, "juice.mediator.errors", create, (before, 5), (inside, 7), (last, 8));
            await Send(mediator, "juice.mediator.errors", fail, (last, 2));
            await Send(mediator, "juice.mediator.requests", create, (before, 100), (last, 110));
            // Requests, no errors: a zero, not "no data".
            await Send(quiet, "juice.mediator.requests", create, (before, 10), (last, 12));
            // Nothing in the window: no observation.
            await Send(stopped, "juice.mediator.errors", create, (before, 50));

            (string, string)[] ok = [("juice.dispatch.result", "success")], failed = [("juice.dispatch.result", "failure")];
            await Send(consumer, "juice.messaging.consumed", ok, (before, 40), (last, 46));
            await Send(consumer, "juice.messaging.consumed", failed, (before, 1), (last, 4));

            for (var i = 0; i < 50 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.otel_metrics_sum WHERE ServiceName LIKE 'rules-{_run}-%'")) < 13; i++)
            {
                await Task.Delay(200, Ct);
            }

            Task<IReadOnlyList<RuleObservation>> Observe(RuleTemplate template, string service)
                => templates.ObserveAsync(new RuleDefinition { Key = "t", Name = "t", Template = template, Service = service }, window, Ct);

            var errors = Assert.Single(await Observe(RuleTemplate.MediatorErrors, mediator));
            Assert.Equal((5d, 10L), (errors.Value, errors.Samples));
            var none = Assert.Single(await Observe(RuleTemplate.MediatorErrors, quiet));
            Assert.Equal((0d, 2L), (none.Value, none.Samples));
            Assert.Empty(await Observe(RuleTemplate.MediatorErrors, stopped));
            var failures = Assert.Single(await Observe(RuleTemplate.ConsumerFailures, consumer));
            Assert.Equal((3d, 9L), (failures.Value, failures.Samples));
            Assert.Empty(await Observe(RuleTemplate.ConsumerFailures, mediator));
        }

        [Fact]
        public void Count_templates_refuse_negative_thresholds()
        {
            foreach (var template in new[] { RuleTemplate.MediatorErrors, RuleTemplate.ConsumerFailures })
            {
                Assert.Empty(new RuleDefinition { Key = "ok-rule", Name = "n", Template = template, Threshold = 0 }.Validate());
                Assert.NotEmpty(new RuleDefinition { Key = "ok-rule", Name = "n", Template = template, Threshold = -1 }.Validate());
                Assert.NotEmpty(new RuleDefinition { Key = "ok-rule", Name = "n", Template = template, Host = "h" }.Validate());
            }
        }

        // ─── helpers ──────────────────────────────────────────────────────────

        private RuleDefinition ErrorRate(string service, Action<RuleDefinition>? configure = null)
        {
            var rule = new RuleDefinition
            {
                Key = $"errors-{_run}", Name = "Error rate", Template = RuleTemplate.ErrorRate, Service = service,
                Threshold = 0.05, RecoveryThreshold = 0.02, MinimumSamples = 10,
                ForDuration = TimeSpan.FromMinutes(2), RecoveryDuration = TimeSpan.FromMinutes(2),
            };
            configure?.Invoke(rule);
            Assert.Empty(rule.Validate());
            return rule;
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

        private static async Task<Alert> AlertAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.Alerts.SingleAsync();
        }

        private static ServiceProvider Services(ScratchDatabase database)
            => new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();

        private static RuleEvaluator Evaluator(ScratchDatabase database, TimeProvider? clock = null, RulesOptions? options = null)
            => PlatformServices.Evaluator(database, clock, options);

        private static RuleScheduler Scheduler(ScratchDatabase database, Action<RulesOptions>? configure = null)
        {
            var options = new RulesOptions();
            configure?.Invoke(options);
            return new RuleScheduler(Services(database).GetRequiredService<IServiceScopeFactory>(), Evaluator(database, options: options),
                Options.Create(options), Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System,
                NullLogger<RuleScheduler>.Instance);
        }

        private static MeterListener Listen(List<(string, string?, string?)> measurements)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "Juice.Observability" && instrument.Name.StartsWith("juice.observability.rules.", StringComparison.Ordinal))
                        l.EnableMeasurementEvents(instrument);
                },
            };
            void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                string? reason = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason") reason = tag.Value as string;
                    if (tag.Key == "outcome") outcome = tag.Value as string;
                }
                lock (measurements) measurements.Add((instrument.Name, reason, outcome));
            }
            listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Record(i, tags));
            listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Record(i, tags));
            listener.Start();
            return listener;
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
