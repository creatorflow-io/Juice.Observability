using System.Net.Http.Json;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M46b, waves §5.3): an endpoint made through the legacy API is mirrored by the alias and works in shadow beside legacy (the receiver
    /// hears legacy only, the pipeline would send); the host refuses to start when the API's and the alias's secret limits overlap, in every mode;
    /// and the migration tool, over the HTTP endpoint, migrates the local stack's real legacy rows (written before M46b, so not mirrored yet)
    /// with the AliasSecret* defaults taken from Webhooks.
    /// </summary>
    public class M46bQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string SecretVariable = "JUICE_WEBHOOK_QA_M46B";
        private const string Secret = "qa-m46b-secret";

        private static async Task<List<(string Status, string Integration)>> PipelineDeliveriesAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var read = new NpgsqlCommand(
                "SELECT d.status, i.key FROM notifications.deliveries d JOIN notifications.integrations i ON i.id = d.integration_id ORDER BY d.created_at", connection);
            await using var reader = await read.ExecuteReaderAsync(Ct);
            var rows = new List<(string, string)>();
            while (await reader.ReadAsync(Ct)) rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        }

        private static async Task WaitAsync(Func<Task<bool>> done, string what, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!await done())
            {
                Assert.True(DateTime.UtcNow < until, $"timed out waiting for {what}");
                await Task.Delay(200, Ct);
            }
        }

        // ─── Shadow: an endpoint made through the legacy API, beside legacy ────────

        [Fact]
        public async Task In_shadow_an_endpoint_made_through_the_legacy_api_is_sent_by_legacy_and_would_be_by_the_pipeline_Async()
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable(SecretVariable, Secret);
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "shadow",
                ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2",
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.5",
            });
            var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            using (var entry = await admin.SendAsync(HttpMethod.Put, $"/api/v1/tenants/{tenant}/webhooks/allowlist",
                new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } }))
            {
                Assert.True(entry.IsSuccessStatusCode, $"{entry.StatusCode}: {await entry.Content.ReadAsStringAsync(Ct)}");
            }
            using (var created = await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks", new
            {
                key = "qa-ops", kind = "webhook", url = receiver.Url, secretRef = $"env:{SecretVariable}", events = new[] { "alert.firing", "alert.resolved" },
            }))
            {
                Assert.True(created.IsSuccessStatusCode, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync(Ct)}");
            }
            // Both sides hold it: the legacy row, and the alias's integration, target and route.
            await using (var check = new NpgsqlConnection(database.ConnectionString))
            {
                await check.OpenAsync(Ct);
                await using var read = new NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM webhook_endpoints WHERE key = 'qa-ops') * 100 + (SELECT count(*) FROM notifications.integrations WHERE key = 'qa-ops' AND managed_by = 'webhooks-alias') * 10 + (SELECT count(*) FROM notifications.routes WHERE key = 'qa-ops' AND managed_by = 'webhooks-alias')", check);
                Assert.Equal(111L, Convert.ToInt64(await read.ExecuteScalarAsync(Ct)));
            }

            var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, firing);
            await WaitAsync(async () => (await PipelineDeliveriesAsync(database)) is [("WouldSend", "qa-ops")], "the pipeline's WouldSend through the alias's integration");
            await WaitAsync(() => Task.FromResult(receiver.Requests.Count >= 1), "legacy's firing at the receiver");
            var resolved = await ProducerAdapterTests.ResolveAsync(database, alert);
            await ProducerAdapterTests.PlanAsync(factory, database, resolved);
            await WaitAsync(async () => (await PipelineDeliveriesAsync(database)).Count(d => d.Status == "WouldSend") == 2, "the second WouldSend");
            await WaitAsync(() => Task.FromResult(receiver.Requests.Count >= 2), "legacy's resolved at the receiver");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);

            var heard = receiver.Requests.ToArray();
            Assert.Equal(2, heard.Length);
            Assert.All(heard, r => Assert.True(WebhookSigner.Verify(Secret, r.Id, r.Timestamp, r.Signature, r.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5))));
            await using var db = database.Context();
            // Legacy sent them, as before: its delivery ids are what the receiver heard.
            var legacy = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Status == Data.DeliveryStatus.Delivered).Select(d => d.Id.ToString()).ToListAsync(Ct);
            Assert.Equal(heard.Select(r => r.Id).Order(), legacy.Order());
            Assert.Equal([("WouldSend", "qa-ops"), ("WouldSend", "qa-ops")], await PipelineDeliveriesAsync(database));
        }

        // ─── The start check: overlapping secret limits ────────

        [Theory]
        [InlineData("legacy", "Notifications:Delivery:ApiSecretEnvPrefix", "JUICE_WEBHOOK_QA_", "overlap")]
        [InlineData("shadow", "Notifications:Delivery:ApiSecretEnvPrefix", "juice_webhook_", "overlap")]
        [InlineData("legacy", "Notifications:Delivery:AliasSecretEnvPrefix", "NOTIFICATIONS_SECRET_ALIAS_", "overlap")]
        [InlineData("shadow", "Notifications:Delivery:ApiSecretDirectory", "/run/secrets/juice", "same folder")]
        public async Task Overlapping_secret_limits_refuse_the_host_at_start(string mode, string setting, string value, string expected)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = mode, ["Webhooks:ApiSecretDirectory"] = "/run/secrets/juice", [setting] = value,
            });
            var refused = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains(expected, refused.ToString());
        }

        [Fact]
        public async Task Distinct_secret_limits_start()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "shadow", ["Webhooks:ApiSecretDirectory"] = "/run/secrets/juice",
                ["Notifications:Delivery:ApiSecretEnvPrefix"] = "NOTIFICATIONS_SECRET_", ["Notifications:Delivery:ApiSecretDirectory"] = "/run/secrets/notifications",
            });
            using var client = factory.CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.True(ready.IsSuccessStatusCode, $"{ready.StatusCode}");
        }

        // ─── An upgrade: endpoints written before M46b ────────

        /// <summary>
        /// A deployment upgraded to M46b keeps its endpoints in legacy's table only (made through the API before the alias existed) until someone
        /// runs the migration. In legacy, which still sends from those rows, /webhooks should still list them.
        /// </summary>
        [Fact(Skip = "QA defect D5: /webhooks reads only the alias's rows, so endpoints made before M46b vanish from it until the migration is applied.")]
        public async Task After_the_upgrade_webhooks_still_lists_the_endpoints_made_before_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" });
            var client = factory.CreateClient();
            var (viewer, _, admin, tenant) = await Caller.AllAsync(client, database);
            await using (var db = database.Context())
            {
                db.WebhookEndpoints.Add(new WebhookEndpoint
                {
                    TenantId = tenant, Key = "made-before", Url = "https://hooks.example.com/before", SecretRef = "env:JUICE_WEBHOOK_BEFORE",
                    Events = ["alert.firing"], DefinitionHash = "x", LoadedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }
            using var listed = await viewer.GetAsync($"/api/v1/tenants/{tenant}/webhooks");
            var body = await listed.Content.ReadAsStringAsync(Ct);
            Assert.True(listed.IsSuccessStatusCode, body);
            Assert.Contains("\"key\":\"made-before\"", body);
        }

        // ─── The migration tool over HTTP, on the real legacy rows ────────

        [Fact]
        public async Task The_migration_endpoint_migrates_the_real_legacy_rows_with_the_alias_secret_defaults_Async()
        {
            LocalStack.RequireAvailable();
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, LocalStack.PostgresConnection("juice_observability"));
            List<WebhookEndpoint> real;
            await using (var live = new PlatformDbContext(options.Options))
            {
                var liveTenant = await live.Tenants.AsNoTracking().Select(t => t.Id).FirstOrDefaultAsync(Ct);
                real = await live.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == liveTenant).OrderBy(e => e.Key).ToListAsync(Ct);
            }
            Assert.SkipWhen(real.Count == 0, "the local platform has no legacy endpoints");
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Webhooks:Smtp:Host"] = "smtp.example.com", ["Webhooks:Smtp:From"] = "alerts@example.com",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com", ["Notifications:Migration:SmtpServer"] = "observability",
            });
            var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            // Written as a deployment before M46b wrote them: legacy rows only, nothing mirrored.
            await using (var db = database.Context())
            {
                foreach (var e in real)
                {
                    db.WebhookEndpoints.Add(new WebhookEndpoint
                    {
                        TenantId = tenant, Key = e.Key, Kind = e.Kind, Url = e.Url, SecretRef = e.SecretRef, Events = e.Events, RuleKeys = e.RuleKeys,
                        Environments = e.Environments, Recipients = e.Recipients, GroupWindowSeconds = e.GroupWindowSeconds, MinimumSeverity = e.MinimumSeverity,
                        Enabled = e.Enabled, DefinitionHash = e.DefinitionHash, ManagedBy = e.ManagedBy,
                    });
                }
                await db.SaveChangesAsync(Ct);
            }
            async Task<NotificationsMigrationReportDto> MigrateAsync(bool apply)
            {
                using var response = await admin.PostAsync($"/api/v1/tenants/{tenant}/notifications/migrate", new { apply });
                Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
                return (await response.Content.ReadFromJsonAsync<NotificationsMigrationReportDto>(Ct))!;
            }

            var report = await MigrateAsync(apply: false);
            Assert.Equal(real.Count, report.Items.Count);
            Assert.All(report.Items, i => Assert.True(i.Action == "create", $"{i.Key}: {i.Action} {string.Join("; ", i.Problems.Select(p => $"{p.Code} {p.Message}"))}"));
            var applied = await MigrateAsync(apply: true);
            Assert.True(applied.Applied);
            Assert.All(applied.Items, i => Assert.Equal("create", i.Action));
            var again = await MigrateAsync(apply: true);
            Assert.All(again.Items, i => Assert.Equal("unchanged", i.Action));

            // The mirrored rows name the legacy secrets (a webhook's signing secret on its integration, a chat URL on its target), which only the alias's limits allow.
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var read = new NpgsqlCommand("SELECT (SELECT count(*) FROM notifications.integrations WHERE managed_by = 'webhooks-alias' AND secret_ref LIKE 'env:JUICE_WEBHOOK_%') + (SELECT count(*) FROM notifications.integration_targets WHERE managed_by = 'webhooks-alias' AND address_ref LIKE 'env:JUICE_WEBHOOK_%')", connection);
            Assert.Equal(real.Count(e => e.SecretRef.StartsWith("env:JUICE_WEBHOOK_", StringComparison.Ordinal)), Convert.ToInt32(await read.ExecuteScalarAsync(Ct)));
        }
    }
}
