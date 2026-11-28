using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>
    /// Core 2.0 M2 slice A against the stack: the evaluator reads thresholds only through <see cref="RuleThresholds"/>, evidence records what was used,
    /// a subject with no threshold is no data, and a provider that fails leaves the alerts as they were.
    /// </summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ThresholdEvaluationTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static RuleEvaluator Evaluator(ScratchDatabase database, ThresholdSeamTests.Provider provider, TimeProvider clock)
        {
            var clickHouse = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));
            var registry = new RuleTemplateRegistry(BuiltInTemplates.Create(null), null, [provider], [new ModuleRuleContribution("demo", typeof(ThresholdSeamTests.Provider))]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString));
            services.AddSingleton(clock);
            services.AddSingleton(Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }));
            services.AddSingleton(Options.Create(new RulesOptions()));
            services.AddSingleton(new RuleTemplates(clickHouse));
            services.AddSingleton(new RuleThresholds(registry));
            services.AddSingleton<RuleEvaluator>();
            services.AddPlatformMessaging();
            return services.BuildServiceProvider().GetRequiredService<RuleEvaluator>();
        }

        private RuleDefinition ErrorRate(string service, double threshold = 0.05, double recovery = 0.02)
        {
            var rule = ThresholdSeamTests.Rule(r =>
            {
                r.Key = $"split-{_run}";
                r.Service = service;
                r.Threshold = threshold;
                r.RecoveryThreshold = recovery;
                r.MinimumSamples = 10;
                r.ForDuration = TimeSpan.Zero;
                r.ThresholdSource = new RuleThresholdSource { Provider = "demo.p95" };
            });
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

        [Fact]
        public async Task The_provider_decides_whether_the_same_telemetry_breaches_and_the_evidence_says_which_numbers_were_used_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"split-{_run}-orders";
            var now = DateTimeOffset.UtcNow;
            // 50 % errors. The rule's own threshold (5 %) would breach; the provider's (90 %) does not.
            await SendAsync(service, total: 20, failed: 10, at: now.AddMinutes(-1));
            var rule = await AddRuleAsync(database, ErrorRate(service));
            var provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.9, 0.8)));

            var evaluation = await Evaluator(database, provider, TimeProvider.System).EvaluateAsync(rule, Ct);

            Assert.Equal((1, 0), (evaluation.Subjects, evaluation.Opened));
            Assert.Equal(1, provider.Calls);

            provider = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            Assert.Equal(1, (await Evaluator(database, provider, TimeProvider.System).EvaluateAsync(rule, Ct)).Opened);
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(Ct);
            Assert.Equal("0.4", alert.Evidence["threshold"]);
            Assert.Equal("0.3", alert.Evidence["recoveryThreshold"]);
            Assert.Equal("demo.p95", alert.Evidence["thresholdSource"]);
            Assert.False(alert.NoData);
        }

        [Fact]
        public async Task A_subject_without_a_usable_threshold_is_no_data_and_holds_an_open_alert_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"split-{_run}-nodata";
            await SendAsync(service, total: 20, failed: 20, at: DateTimeOffset.UtcNow.AddMinutes(-1));
            var rule = await AddRuleAsync(database, ErrorRate(service));
            var given = new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3)));
            await Evaluator(database, given, TimeProvider.System).EvaluateAsync(rule, Ct);          // opens (no ForDuration: fires at once)
            Assert.Equal(AlertState.Firing, (await TheAlertAsync(database)).State);

            foreach (var bad in new Dictionary<string, RuleThreshold>[] { new(), new() { ["x"] = new(0.4, 0.3) } })
            {
                var none = new ThresholdSeamTests.Provider(_ => bad);
                var evaluation = await Evaluator(database, none, TimeProvider.System).EvaluateAsync(rule, Ct);

                Assert.Equal(0, evaluation.Resolved);
                var alert = await TheAlertAsync(database);
                Assert.Equal((AlertState.Firing, true), (alert.State, alert.NoData));
                Assert.False(alert.Evidence.ContainsKey("threshold"));
                Assert.Equal("NoData", alert.Evidence["condition"]);
                Assert.Equal("demo.p95", alert.Evidence["thresholdSource"]);
            }
        }

        [Fact]
        public async Task A_provider_that_throws_fails_the_evaluation_and_leaves_the_alert_as_it_was_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"split-{_run}-throws";
            await SendAsync(service, total: 20, failed: 20, at: DateTimeOffset.UtcNow.AddMinutes(-1));
            var rule = await AddRuleAsync(database, ErrorRate(service));
            await Evaluator(database, new ThresholdSeamTests.Provider(keys => keys.ToDictionary(k => k, _ => new RuleThreshold(0.4, 0.3))), TimeProvider.System).EvaluateAsync(rule, Ct);
            var before = await TheAlertAsync(database);

            await Assert.ThrowsAsync<TimeoutException>(() => Evaluator(database, new ThresholdSeamTests.Provider(_ => throw new TimeoutException("down")), TimeProvider.System).EvaluateAsync(rule, Ct));

            var after = await TheAlertAsync(database);
            Assert.Equal((before.State, before.LastEvaluatedAt, before.NoData), (after.State, after.LastEvaluatedAt, after.NoData));
            Assert.Equal(1, await CountTransitionsAsync(database, AlertState.Firing));
        }

        [Fact]
        public async Task A_rule_without_a_source_records_its_own_numbers_and_no_source_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"split-{_run}-fixed";
            await SendAsync(service, total: 20, failed: 10, at: DateTimeOffset.UtcNow.AddMinutes(-1));
            var definition = ErrorRate(service);
            definition.ThresholdSource = null;
            var rule = await AddRuleAsync(database, definition);

            await Evaluator(database, new ThresholdSeamTests.Provider(_ => throw new InvalidOperationException("never asked")), TimeProvider.System).EvaluateAsync(rule, Ct);

            var alert = await TheAlertAsync(database);
            Assert.Equal("0.05", alert.Evidence["threshold"]);
            Assert.Equal("0.02", alert.Evidence["recoveryThreshold"]);
            Assert.False(alert.Evidence.ContainsKey("thresholdSource"));
        }

        private static async Task<Alert> TheAlertAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.Alerts.AsNoTracking().SingleAsync(Ct);
        }

        private static async Task<int> CountTransitionsAsync(ScratchDatabase database, AlertState to)
        {
            await using var db = database.Context();
            return await db.AlertTransitions.CountAsync(t => t.ToState == to, Ct);
        }
    }
}
