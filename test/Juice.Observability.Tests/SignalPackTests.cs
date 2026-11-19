using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Juice.Observability.Signals;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The Juice signal pack (docs/signal-packs.md): on by default, and switched off with <c>Signals:Juice:Enabled</c>, its rule templates can't be
    /// used by an enabled rule and observe nothing, and SLOs and the capacity view don't read its signals.
    /// </summary>
    public class SignalPackTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly Dictionary<string, string> JuiceOff = new() { ["Signals:Juice:Enabled"] = "false" };

        private static SignalPacks Packs(bool juice) => new(Options.Create(new SignalOptions { Juice = { Enabled = juice } }));

        private static RuleDefinition Rule(RuleTemplate template, bool enabled = true)
            => new() { Key = "a-rule", Name = "A rule", Template = template, Threshold = 0.1, Enabled = enabled };

        [Fact]
        public void Off_the_pack_s_templates_are_refused_for_an_enabled_rule_only()
        {
            var off = Packs(juice: false);
            foreach (var template in JuiceSignals.RuleTemplates)
            {
                Assert.False(off.Provides(template));
                Assert.Contains(Rule(template).Validate(off), e => e.Contains("juice signal pack, which is off"));
                // Disabling one made before the pack was switched off stays possible.
                Assert.DoesNotContain(Rule(template, enabled: false).Validate(off), e => e.Contains("signal pack"));
                // On, or without packs (a definition read back), it's valid.
                Assert.Empty(Rule(template).Validate(Packs(juice: true)));
                Assert.Empty(Rule(template).Validate());
            }
            foreach (var template in new[] { RuleTemplate.ErrorRate, RuleTemplate.LatencyP95, RuleTemplate.ThroughputAbsent })
            {
                Assert.True(off.Provides(template));
                Assert.Empty(Rule(template).Validate(off));
            }
        }

        [Fact]
        public async Task Off_a_pack_s_template_observes_nothing_without_reading_ClickHouse_Async()
        {
            // An unreachable ClickHouse: a query would throw.
            var clickHouse = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = "Host=127.0.0.1;Port=1;Username=x" }));
            var templates = new RuleTemplates(clickHouse, Packs(juice: false));
            var now = DateTimeOffset.UtcNow;
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.MediatorErrors), new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 100), Ct));
        }

        [Fact]
        public void Slos_count_a_failed_dispatch_as_bad_only_with_the_pack_on()
        {
            Assert.Contains(JuiceSignals.FailedDispatchSpan, SloCounter.CountQuery);
            Assert.DoesNotContain(JuiceSignals.DispatchResult, SloCounter.CountQueryWithoutJuice);
        }

        [Fact]
        public void Every_Juice_name_the_platform_queries_is_in_the_pack()
        {
            // The pack is the one place: no Juice-specific name is written anywhere else in the platform's sources.
            var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
            Assert.True(Directory.Exists(src), src);
            var names = new[] { JuiceSignals.MediatorRequests, JuiceSignals.MediatorErrors, JuiceSignals.MessagesConsumed, JuiceSignals.DeliveryAttempts,
                JuiceSignals.DeliveryFailures, JuiceSignals.DispatchResult, JuiceSignals.DeliveryOutcome, $"ScopeName = '{JuiceSignals.Scope}'" };
            var outside = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains("Juice.Observability.Signals"))
                .SelectMany(f => names.Where(n => File.ReadAllText(f).Contains($"\"{n}") || File.ReadAllText(f).Contains($"'{n}'")).Select(n => $"{Path.GetFileName(f)}: {n}"))
                .ToList();
            Assert.Empty(outside);
        }

        [Fact]
        public async Task The_api_describes_the_packs_and_refuses_an_off_pack_s_rule_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var on = new ApiFactory(database))
            {
                using var client = on.CreateClient();
                var (viewer, _, _, _) = await Caller.AllAsync(client, database);
                var packs = await Caller.JsonAsync(await viewer.GetAsync("/api/v1/signal-packs"));
                var juice = Assert.Single(packs.EnumerateArray());
                Assert.Equal(("juice", 1, true), (juice.GetProperty("name").GetString(), juice.GetProperty("version").GetInt32(), juice.GetProperty("enabled").GetBoolean()));
                Assert.Equal(["ConsumerFailures", "DeliveryFailureRate", "MediatorErrors"], juice.GetProperty("ruleTemplates").EnumerateArray().Select(t => t.GetString()));
                Assert.Contains("juice.mediator.errors", juice.GetProperty("metrics").EnumerateArray().Select(m => m.GetString()));
            }

            await using var off = new ApiFactory(database, settings: JuiceOff);
            using var offClient = off.CreateClient();
            var (offViewer, _, admin, tenant) = await Caller.AllAsync(offClient, database);
            Assert.False(Assert.Single((await Caller.JsonAsync(await offViewer.GetAsync("/api/v1/signal-packs"))).EnumerateArray()).GetProperty("enabled").GetBoolean());

            var rules = $"/api/v1/tenants/{tenant}/rules";
            var refused = await admin.PostAsync(rules, new { key = "mediator", name = "Mediator", template = "MediatorErrors", threshold = 5 });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Signals:Juice:Enabled", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, new { key = "mediator", name = "Mediator", template = "MediatorErrors", threshold = 5, enabled = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, new { key = "errors", name = "Errors", template = "ErrorRate", threshold = 0.1 })).StatusCode);
            await using var db = database.Context();
            Assert.Equal(["errors", "mediator"], await db.Rules.Select(r => r.Key).OrderBy(k => k).ToListAsync(Ct));
        }

        [Fact]
        public async Task A_rules_file_with_an_off_pack_s_rule_stops_the_host_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = new Dictionary<string, string>(JuiceOff)
            {
                ["Rules:Definitions:0:Key"] = "consumers", ["Rules:Definitions:0:Name"] = "Consumers", ["Rules:Definitions:0:Template"] = "ConsumerFailures",
                ["Rules:Definitions:0:Threshold"] = "1",
            };
            await using var factory = new ApiFactory(database, settings: settings);
            var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains("ConsumerFailures reads the juice signal pack", error.ToString());
        }
    }
}
