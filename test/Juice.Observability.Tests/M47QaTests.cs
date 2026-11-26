using Juice.Observability.Data;
using Juice.Observability.Notifications.Shadow;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M47, #12): shadow live as a deployment runs it. An endpoint made through the legacy API (the alias mirrors it), a firing and its
    /// resolve sent by legacy's own dispatcher and planned dry by the pipeline's stages; the comparer then matches each unit, the only differences
    /// are bodies explained by templates_not_ported, the report says 0 unexplained, and the dead-man's shadow cause stays quiet.
    /// </summary>
    public class M47QaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string SecretVariable = "JUICE_WEBHOOK_QA_M47";

        private static async Task WaitAsync(Func<Task<bool>> done, string what)
        {
            for (var i = 0; i < 150; i++)
            {
                if (await done()) return;
                await Task.Delay(200, Ct);
            }
            Assert.Fail($"timed out waiting for {what}");
        }

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        [Fact]
        public async Task A_firing_and_its_resolve_in_shadow_match_with_only_explained_body_differences_Async()
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable(SecretVariable, "qa-m47-secret");
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "shadow",
                ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2",
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.5",
                ["Notifications:Shadow:Interval"] = "01:00:00",
            });
            var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            using (var entry = await admin.SendAsync(HttpMethod.Put, $"/api/v1/tenants/{tenant}/webhooks/allowlist", new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } }))
            {
                Assert.True(entry.IsSuccessStatusCode, $"{entry.StatusCode}");
            }
            using (var created = await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks", new
            {
                key = "qa-ops", kind = "webhook", url = receiver.Url, secretRef = $"env:{SecretVariable}", events = new[] { "alert.firing", "alert.resolved" },
            }))
            {
                Assert.True(created.IsSuccessStatusCode, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync(Ct)}");
            }

            var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, firing);
            await WaitAsync(async () => receiver.Requests.Count >= 1 && await CountAsync(database, "SELECT count(*) FROM notifications.deliveries WHERE status = 'WouldSend'") >= 1, "the firing on both sides");
            var resolved = await ProducerAdapterTests.ResolveAsync(database, alert);
            await ProducerAdapterTests.PlanAsync(factory, database, resolved);
            await WaitAsync(async () => receiver.Requests.Count >= 2 && await CountAsync(database, "SELECT count(*) FROM notifications.deliveries WHERE status = 'WouldSend'") >= 2, "the resolve on both sides");

            var comparer = new ShadowComparer(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ShadowOptions { Grace = TimeSpan.Zero, Batch = 100 }),
                factory.Services.GetRequiredService<IOptions<WebhookOptions>>(), TimeProvider.System, NullLogger<ShadowComparer>.Instance);
            Assert.Equal(2, await comparer.CompareOnceAsync(Ct));
            Assert.Equal(0, await comparer.CompareOnceAsync(Ct));

            await using (var db = database.Context())
            {
                var comparisons = await db.ShadowComparisons.AsNoTracking().OrderBy(c => c.Event).ToListAsync(Ct);
                Assert.Equal([(NotificationPlan.Firing, 1, 1), (NotificationPlan.Resolved, 1, 1)], comparisons.Select(c => (c.Event, c.Legacy, c.New)).OrderBy(c => c.Event));
                Assert.All(comparisons, c => Assert.Equal(0, c.Unexplained));
                var differences = await db.ShadowDifferences.AsNoTracking().ToListAsync(Ct);
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m47.txt"),
                    $"{DateTime.UtcNow:O} differences: {string.Join(" | ", differences.Select(d => $"{d.EndpointKey} {d.Kind} {d.Rule ?? "UNEXPLAINED"}: {d.Detail}"))}{Environment.NewLine}");
                Assert.All(differences, d => Assert.Equal((ShadowDifferenceKinds.Body, ShadowRules.TemplatesNotPorted), d.Kind == ShadowDifferenceKinds.Shape ? (ShadowDifferenceKinds.Body, d.Rule) : (d.Kind, d.Rule)));
                Assert.DoesNotContain(differences, d => d.Kind is ShadowDifferenceKinds.MissingInNew or ShadowDifferenceKinds.MissingInLegacy or ShadowDifferenceKinds.Timing or ShadowDifferenceKinds.Unsettled);
            }

            var report = await Caller.JsonAsync(await @operator.GetAsync($"/api/v1/tenants/{tenant}/notifications/shadow-report"));
            Assert.Equal((2, 0), (report.GetProperty("compared").GetInt32(), report.GetProperty("unexplained").GetInt32()));
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Null(await scope.ServiceProvider.GetRequiredService<Juice.Observability.Health.DeadMan.IShadowReport>().FailingAsync(Ct));
        }
    }
}
