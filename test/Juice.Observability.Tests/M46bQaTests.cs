using System.Net.Http.Json;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Health.Delivery;
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
    [Trait("Milestone", "M46c")]
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
        [Fact]
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

        // ─── Endpoints the alias can't map, through the legacy API and the file; the pipeline down ────────

        [Theory]
        [InlineData("legacy")]
        [InlineData("shadow")]
        public async Task Endpoints_the_alias_cant_map_from_the_file_and_the_api_are_listed_and_the_apis_editable_Async(string pipeline)
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable("QA_OTHER_SECRET", "qa-other");
            Environment.SetEnvironmentVariable(SecretVariable, Secret);
            await using var database = await LocalStack.CreateDatabaseAsync();
            // The file: a key with a digit first, and one whose secret is outside the alias's limits (any variable, as the file may name).
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = pipeline,
                ["Webhooks:Endpoints:0:Key"] = "9cfg", ["Webhooks:Endpoints:0:Url"] = "https://hooks.example.com/digit", ["Webhooks:Endpoints:0:SecretRef"] = $"env:{SecretVariable}",
                ["Webhooks:Endpoints:1:Key"] = "cfg-other", ["Webhooks:Endpoints:1:Url"] = "https://hooks.example.com/other", ["Webhooks:Endpoints:1:SecretRef"] = "env:QA_OTHER_SECRET",
            });
            var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
            // A new key with a digit first is refused (decision G4, Q14: notifications keys start with a letter)...
            using (var created = await admin.PostAsync(webhooks, new { key = "7new", kind = "webhook", url = "https://hooks.example.com/api", secretRef = $"env:{SecretVariable}" }))
            {
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, created.StatusCode);
                Assert.Contains("starts with a digit", await created.Content.ReadAsStringAsync(Ct));
            }
            // ...but one the API made before M46b stays: listed and editable while legacy sends.
            await using (var db = database.Context())
            {
                db.WebhookEndpoints.Add(new WebhookEndpoint
                {
                    TenantId = tenant, Key = "7api", Url = "https://hooks.example.com/api", SecretRef = $"env:{SecretVariable}", Events = ["alert.firing"],
                    DefinitionHash = "x", LoadedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }

            var listed = await Caller.JsonAsync(await admin.GetAsync(webhooks));
            var items = listed.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("id").GetGuid());
            Assert.Equal(["7api", "9cfg", "cfg-other"], items.Keys.Order());
            // The API's is editable; the file's are the file's (409, as before the alias).
            using (var changed = await admin.PutAsync($"{webhooks}/{items["7api"]}", new { secretRef = $"env:{SecretVariable}", minimumSeverity = "critical" }))
            {
                Assert.True(changed.IsSuccessStatusCode, $"{(int)changed.StatusCode}: {await changed.Content.ReadAsStringAsync(Ct)}");
            }
            var after = (await Caller.JsonAsync(await admin.GetAsync(webhooks))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("key").GetString() == "7api");
            Assert.Equal("critical", after.GetProperty("minimumSeverity").GetString()!.ToLowerInvariant());
            foreach (var key in new[] { "9cfg", "cfg-other" })
            {
                using var refused = await admin.PutAsync($"{webhooks}/{items[key]}", new { enabled = false });
                Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
            }
        }

        /// <summary>
        /// With the pipeline down (its schema never migrated), the legacy API still creates, lists and changes endpoints and the allowlist (legacy
        /// sends); a restart with the schema then catches the alias up: the endpoint as last changed, and the allowlist entry.
        /// </summary>
        [Fact]
        public async Task With_the_pipeline_down_the_legacy_api_lists_creates_and_changes_and_a_restart_catches_the_alias_up_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid id;
            Guid tenant;
            await using (var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Database:Migrate"] = "false", ["Notifications:Pipeline"] = "legacy",
            }))
            {
                var client = factory.CreateClient();
                var (_, _, admin, t) = await Caller.AllAsync(client, database);
                tenant = t;
                var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
                using var created = await admin.PostAsync(webhooks, new { key = "down-ops", kind = "webhook", url = "https://hooks.example.com/down", secretRef = $"env:{SecretVariable}" });
                var createdBody = await created.Content.ReadAsStringAsync(Ct);
                Assert.True(created.IsSuccessStatusCode, $"POST {(int)created.StatusCode}: {createdBody}");
                id = System.Text.Json.JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetGuid();
                using var listed = await admin.GetAsync(webhooks);
                var listedBody = await listed.Content.ReadAsStringAsync(Ct);
                Assert.True(listed.IsSuccessStatusCode && listedBody.Contains("\"key\":\"down-ops\""), $"GET {(int)listed.StatusCode}: {listedBody}");
                using var changed = await admin.PutAsync($"{webhooks}/{id}", new { secretRef = $"env:{SecretVariable}", enabled = false, minimumSeverity = "warning" });
                Assert.True(changed.IsSuccessStatusCode, $"PUT {(int)changed.StatusCode}: {await changed.Content.ReadAsStringAsync(Ct)}");
                using var allowed = await admin.SendAsync(HttpMethod.Put, $"{webhooks}/allowlist", new { destination = "10.50.0.0/16", ports = new[] { 8443 } });
                Assert.True(allowed.IsSuccessStatusCode, $"allowlist PUT {(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync(Ct)}");
                await using var db = database.Context();
                var row = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
                Assert.Equal((false, AlertSeverity.Warning), (row.Enabled, row.MinimumSeverity));
            }

            // Restarted with the schema: the alias catches up from legacy's rows.
            await using (var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" }))
            {
                _ = factory.CreateClient();
                await using var scope = factory.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<Juice.Notifications.Data.NotificationsDbContext>();
                var pipelineTenant = Guid.Parse(LocalStack.TenantKey);
                Juice.Observability.Notifications.Migration.EndpointSetup? setup = null;
                for (var i = 0; i < 50 && setup is null; i++)
                {
                    setup = await Juice.Observability.Notifications.Migration.MigrationPlanner.ReadSetupAsync(notifications, pipelineTenant, "down-ops", Ct);
                    if (setup is null) await Task.Delay(200, Ct);
                }
                Assert.NotNull(setup);
                await using var db = database.Context();
                var legacy = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == id, Ct);
                Assert.Equal(Juice.Observability.Notifications.Migration.LegacyEndpoint.From(legacy), Juice.Observability.Notifications.Migration.EndpointMapping.Unmap(setup).Value);
                Assert.True(await notifications.Allowlist.AsNoTracking().AnyAsync(a => a.TenantId == pipelineTenant && a.Destination == "10.50.0.0/16", Ct),
                    "the allowlist entry made while the pipeline was down");
            }
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
