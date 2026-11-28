using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>Core 2.0 M2 slice A against the stack: the registry path gives what the queries give, fixed rules evaluate as before, and the API refuses an unknown provider.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class RegistryPathTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static ClickHouseTelemetrySource ClickHouse() => new(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));

        private static async Task WaitForRowsAsync(string table, string where, int expected)
        {
            for (var i = 0; i < 100 && int.Parse(await LocalStack.ClickHouseAsync($"SELECT count() FROM otel.{table} WHERE {where}")) < expected; i++)
                await Task.Delay(200, Ct);
        }

        private static object Project(IReadOnlyList<RuleObservation> rows)
            => rows.OrderBy(o => o.SubjectKey, StringComparer.Ordinal).Select(o => (o.SubjectKey, o.SubjectType, o.ServiceNamespace, o.ServiceName, o.Environment, o.Host,
                o.Value, o.Samples, Detail: string.Join(";", (o.Detail ?? new Dictionary<string, string>()).OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value}")))).ToList();

        [Fact]
        public async Task Builtin_observe_through_registry_equals_direct_Async()
        {
            LocalStack.RequireAvailable();
            var templates = new RuleTemplates(ClickHouse());
            var now = DateTimeOffset.UtcNow;
            var window = new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 100);
            var busy = $"reg-{_run}-busy";
            var gone = $"reg-{_run}-gone";
            var gauge = $"reg.{_run}.gauge";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", busy), ("service.instance.id", "i1")],
                Otlp.Requests(20, 5, now.AddMinutes(-1)).Select(s => Otlp.Lasting(s, TimeSpan.FromMilliseconds(300))).ToArray()));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", gone)], Otlp.Requests(15, 0, now.AddMinutes(-30))));
            await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge([("service.name", busy)], gauge, [], (now.AddMinutes(-2), 7), (now.AddMinutes(-1), 9)));
            await WaitForRowsAsync("otel_traces", $"ServiceName = '{busy}'", 20);
            await WaitForRowsAsync("otel_traces", $"ServiceName = '{gone}'", 15);
            await WaitForRowsAsync("otel_metrics_gauge", $"MetricName = '{gauge}'", 2);
            var nobody = $"reg-{_run}-nobody";

            var metric = new MetricSpec { Name = gauge, Kind = MetricKind.Gauge, Aggregation = MetricAggregation.Max };
            var rules = new Dictionary<RuleTemplate, RuleDefinition>
            {
                [RuleTemplate.ErrorRate] = new() { Service = busy },
                [RuleTemplate.LatencyP95] = new() { Service = busy },
                [RuleTemplate.ThroughputAbsent] = new() { Service = gone },
                [RuleTemplate.DeliveryFailureRate] = new() { Service = nobody },
                [RuleTemplate.HostMemory] = new() { Host = nobody },
                [RuleTemplate.MediatorErrors] = new() { Service = nobody },
                [RuleTemplate.ConsumerFailures] = new() { Service = nobody },
                [RuleTemplate.MetricThreshold] = new() { Service = busy, Metric = metric },
                [RuleTemplate.HostCpu] = new() { Host = nobody },
                [RuleTemplate.HostDisk] = new() { Host = nobody },
            };
            Assert.Equal(10, rules.Count);
            var direct = new Dictionary<RuleTemplate, Func<RuleDefinition, Task<IReadOnlyList<RuleObservation>>>>
            {
                [RuleTemplate.ErrorRate] = r => templates.ObserveErrorRateAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.LatencyP95] = r => templates.ObserveLatencyP95Async(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.ThroughputAbsent] = r => templates.ObserveThroughputAbsentAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.DeliveryFailureRate] = r => templates.ObserveDeliveryFailureRateAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.HostMemory] = r => templates.ObserveHostMemoryAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.MediatorErrors] = r => templates.ObserveMediatorErrorsAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.ConsumerFailures] = r => templates.ObserveConsumerFailuresAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.MetricThreshold] = r => templates.ObserveMetricThresholdAsync(r, window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.HostCpu] = r => templates.ObserveCpuAsync(window, RuleTemplates.Scope(r), Ct),
                [RuleTemplate.HostDisk] = r => templates.ObserveDiskAsync(window, RuleTemplates.Scope(r), Ct),
            };

            var nonEmpty = new List<RuleTemplate>();
            foreach (var (template, rule) in rules)
            {
                rule.Key = $"reg-{template}".ToLowerInvariant();
                rule.Name = "n";
                rule.Template = template;
                var viaRegistry = await templates.ObserveAsync(rule, window, Ct);
                var viaQuery = await direct[template](rule);
                Assert.Equal(Project(viaQuery), Project(viaRegistry));
                if (viaRegistry.Count > 0) nonEmpty.Add(template);
            }
            // The equality is not vacuous: the templates given data observed it.
            Assert.Equal([RuleTemplate.ErrorRate, RuleTemplate.LatencyP95, RuleTemplate.ThroughputAbsent, RuleTemplate.MetricThreshold], nonEmpty.Order());
        }

        [Fact]
        public async Task Fixed_threshold_evaluation_identical_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"reg-{_run}-fixed";
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.instance.id", "i1")], Otlp.Requests(20, 10, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await LocalStack.WaitForSpansAsync(service, 20);
            RuleDefinition Make(string key, RuleThresholdSource? source) => ThresholdSeamTests.Rule(r =>
            {
                (r.Key, r.Service, r.ForDuration, r.MinimumSamples, r.ThresholdSource) = (key, service, TimeSpan.Zero, 10, source);
            });
            async Task<Rule> AddAsync(RuleDefinition d)
            {
                await using var db = database.Context();
                var rule = new Rule
                {
                    TenantId = (await db.Tenants.SingleAsync(Ct)).Id, Key = d.Key, Name = d.Name, Template = d.Template, Severity = d.Severity,
                    Definition = d.ToJson(), DefinitionHash = d.Hash(), LoadedAt = DateTimeOffset.UtcNow,
                };
                db.Rules.Add(rule);
                await db.SaveChangesAsync(Ct);
                return rule;
            }
            var plain = await AddAsync(Make($"plain-{_run}", null));
            var named = await AddAsync(Make($"named-{_run}", new RuleThresholdSource { Provider = "fixed" }));
            var evaluator = ThresholdEvaluationTests.Evaluator(database, new ThresholdSeamTests.Provider(_ => throw new InvalidOperationException("never asked")), TimeProvider.System);

            Assert.Equal((1, 1, 1), ((await evaluator.EvaluateAsync(plain, Ct)) is var p ? (p.Subjects, p.Opened, p.Fired) : default));
            Assert.Equal((1, 1, 1), ((await evaluator.EvaluateAsync(named, Ct)) is var n ? (n.Subjects, n.Opened, n.Fired) : default));

            await using var read = database.Context();
            var alerts = await read.Alerts.AsNoTracking().ToListAsync(Ct);
            var (a, b) = (alerts.Single(x => x.DedupKey.StartsWith("plain", StringComparison.Ordinal)), alerts.Single(x => x.DedupKey.StartsWith("named", StringComparison.Ordinal)));
            Assert.Equal((a.State, a.LastValue, a.LastSamples, a.NoData, a.SubjectKey, a.Severity), (b.State, b.LastValue, b.LastSamples, b.NoData, b.SubjectKey, b.Severity));
            Assert.Equal(AlertState.Firing, a.State);
            Assert.False(a.Evidence.ContainsKey("thresholdSource"));
            Assert.False(b.Evidence.ContainsKey("thresholdSource"));
            string Same(Alert x) => string.Join(";", x.Evidence.Where(e => e.Key is not ("from" or "to")).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"));
            Assert.Equal(Same(a), Same(b));
            Assert.Equal("0.05", a.Evidence["threshold"]);
            Assert.Equal("0.02", a.Evidence["recoveryThreshold"]);
        }

        [Fact]
        public async Task Api_create_with_unknown_provider_400_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            object Body(string provider) => new
            {
                key = "split-rule", name = "Split", template = "ErrorRate", threshold = 0.1, service = $"reg-{_run}",
                thresholdSource = new { provider, settings = new { percentile = 95 } },
            };

            var unknown = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body("pro.percentile"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("'pro.percentile' is not a known provider", await unknown.Content.ReadAsStringAsync(Ct));
            var malformed = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body("no spaces"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            await using (var db = database.Context())
            {
                Assert.False(await db.Rules.AnyAsync(Ct));
            }

            // The core's own provider is known.
            var fixedSource = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", Body("fixed"));
            Assert.Equal(HttpStatusCode.Created, fixedSource.StatusCode);
        }

        public sealed class TenantProbeTemplate : IRuleTemplate
        {
            // No table: the tenant, window and limit only as the host's parameters.
            public static readonly string Sql = $"SELECT {TemplateSql.Tenant.Replace("JuiceTenant = ", "")} AS seen, {TemplateSql.Limit} AS cap FROM (SELECT {TemplateSql.Tenant.Replace("JuiceTenant = ", "")} AS JuiceTenant, {TemplateSql.From} AS f, {TemplateSql.To} AS t) WHERE {TemplateSql.Tenant}";

            public RuleTemplateInfo Info => new("demo.probe", "Probe", TemplateSubjectKind.Service);

            public async Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct)
            {
                var rows = await telemetry.QueryAsync(Sql, new Dictionary<string, object>(), r => (Tenant: r.GetString(0), Limit: Convert.ToInt64(r.GetValue(1))), ct);
                return [.. rows.Select(r => new TemplateObservation($"service:{r.Tenant}", AlertSubjectKind.Service, "", r.Tenant, "", "", r.Limit, 1))];
            }
        }

        [Fact]
        public async Task Tenant_parameter_always_platform_tenant_Async()
        {
            LocalStack.RequireAvailable();
            var window = new TelemetryWindow(LocalStack.TenantKey, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 77);
            var telemetry = new TemplateTelemetry(ClickHouse(), window);

            var seen = await new TenantProbeTemplate().ObserveAsync(new RuleDefinition { Key = "probe", Name = "n" }.ToSpec(), telemetry,
                new TemplateWindow(window.From, window.To, window.Limit), Ct);

            var row = Assert.Single(seen);
            Assert.Equal(LocalStack.TenantKey, row.ServiceName);            // the platform's tenant, as the host set it
            Assert.Equal(77, row.Value);                                    // and its limit
            // A template that sets its own tenant, or one that tries another through the rule, is refused before ClickHouse hears of it.
            await Assert.ThrowsAsync<ArgumentException>(() => telemetry.QueryAsync(TenantProbeTemplate.Sql, new Dictionary<string, object> { ["tenant"] = "someone-else" }, r => 0, Ct));
        }
    }
}
