using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Mixed deployments: discovery marks the services that send Juice signals, and a Juice-template rule scoped to a service that never has is
    /// refused (through the API) or listed as quiet (from the rules file), since it would never observe it.
    /// </summary>
    public class SignalCoverageTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Discovery_marks_the_services_that_send_Juice_signals_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var run = Guid.NewGuid().ToString("N")[..8];
            var (outbox, mediator, plain) = ($"juice-outbox-{run}", $"juice-mediator-{run}", $"plain-{run}");
            var at = DateTimeOffset.UtcNow.AddMinutes(-2);

            // Juice's own span; a Juice metric; and a service with neither, which also exports an unprefixed delivery counter another library might.
            var spans = Otlp.Traces([("service.name", outbox)], Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "create OrderPlaced", Otlp.Producer, at));
            spans["resourceSpans"]![0]!["scopeSpans"]![0]!["scope"]!["name"] = "Juice";
            await LocalStack.SendAsync("traces", spans);
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", mediator)], Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "GET /", Otlp.Server, at)));
            await LocalStack.SendAsync("metrics", Otlp.Sum([("service.name", mediator)], "juice.mediator.requests", at.AddMinutes(-1), [], (at, 3)));
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", plain)], Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "GET /", Otlp.Server, at)));
            await LocalStack.SendAsync("metrics", Otlp.Sum([("service.name", plain)], "delivery_attempt_total", at.AddMinutes(-1), [], (at, 3)));
            foreach (var service in new[] { outbox, mediator, plain }) await LocalStack.WaitForSpansAsync(service, 1);
            for (var i = 0; i < 100 && await LocalStack.ClickHouseAsync(
                $"SELECT count() FROM otel.otel_metrics_sum WHERE ServiceName IN ('{mediator}', '{plain}')") != "2"; i++)
            {
                await Task.Delay(200, Ct);
            }

            var services = new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();
            await new DiscoveryRunner(services.GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })),
                Options.Create(new DiscoveryOptions { Lag = TimeSpan.Zero, InitialLookback = TimeSpan.FromMinutes(30) }),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<DiscoveryRunner>.Instance).RunOnceAsync(Ct);

            await using var db = database.Context();
            var packs = await db.Services.AsNoTracking().Where(s => s.Name.EndsWith(run)).ToDictionaryAsync(s => s.Name, s => s.SignalPacks, Ct);
            Assert.Equal(["juice"], packs[outbox]);
            Assert.Equal(["juice"], packs[mediator]);
            Assert.Empty(packs[plain]);
        }

        private static async Task SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var now = DateTimeOffset.UtcNow;
            db.Services.Add(new Service { TenantId = tenant, Namespace = "shop", Name = "orders", FirstSeen = now, LastSeen = now, SignalPacks = ["juice"] });
            db.Services.Add(new Service { TenantId = tenant, Namespace = "shop", Name = "checkout", FirstSeen = now, LastSeen = now });
            await db.SaveChangesAsync(Ct);
        }

        [Fact]
        public async Task A_Juice_rule_for_a_service_that_sends_no_Juice_signals_is_refused_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            await SeedAsync(database);
            var rules = $"/api/v1/tenants/{tenant}/rules";
            object Rule(string key, string service, string template = "MediatorErrors", bool enabled = true)
                => new { key, name = key, template, service, threshold = 1, enabled };

            var refused = await admin.PostAsync(rules, Rule("checkout-mediator", "shop/checkout"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("shop/checkout has sent no juice signals", await refused.Content.ReadAsStringAsync(Ct));

            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, Rule("orders-mediator", "shop/orders"))).StatusCode);
            // Not discovered yet: the rule may come first.
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, Rule("later-mediator", "shop/later"))).StatusCode);
            // Disabled, or a template every service has: nothing to refuse.
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, Rule("checkout-off", "shop/checkout", enabled: false))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(rules, Rule("checkout-errors", "shop/checkout", "ErrorRate"))).StatusCode);

            // Changing a rule to watch it is refused too.
            var update = await admin.PutAsync($"{rules}/orders-mediator", Rule("orders-mediator", "shop/checkout"));
            Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
            Assert.Contains("never observe it", await update.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task A_quiet_rule_from_the_file_is_listed_with_the_reason_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            var file = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "checkout-consumers", ["Rules:Definitions:0:Name"] = "Checkout consumers", ["Rules:Definitions:0:Template"] = "ConsumerFailures",
                ["Rules:Definitions:0:Service"] = "shop/checkout", ["Rules:Definitions:0:Threshold"] = "1",
                ["Rules:Definitions:1:Key"] = "orders-consumers", ["Rules:Definitions:1:Name"] = "Orders consumers", ["Rules:Definitions:1:Template"] = "ConsumerFailures",
                ["Rules:Definitions:1:Service"] = "shop/orders", ["Rules:Definitions:1:Threshold"] = "1",
            };
            await using var factory = new ApiFactory(database, settings: file);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);
            var listed = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/rules"))).GetProperty("items").EnumerateArray()
                .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("quiet"));
            Assert.Contains("shop/checkout has sent no juice signals", listed["checkout-consumers"].GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, listed["orders-consumers"].ValueKind);

            // And the service says which packs it sends.
            var resources = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/resources"))).GetProperty("items").EnumerateArray()
                .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("signalPacks").EnumerateArray().Select(p => p.GetString()).ToList());
            Assert.Equal(["juice"], resources["shop/orders"]);
            Assert.Empty(resources["shop/checkout"]);
        }
    }
}
