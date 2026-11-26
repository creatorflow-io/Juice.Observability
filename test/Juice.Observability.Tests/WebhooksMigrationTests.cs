using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Data;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Proof point 5 through the platform's API (phase-5-plan I70, M46b): endpoints and an allowlist entry made through the legacy webhooks API,
    /// migrated by <c>POST …/notifications/migrate</c> (a report, then an apply, then a report that finds everything unchanged), and every one
    /// read back from the notifications rows as the definition it was. Admin only.
    /// </summary>
    public sealed class WebhooksMigrationTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly Dictionary<string, string> Settings = new()
        {
            ["Webhooks:Smtp:Host"] = "smtp.example.com",
            ["Webhooks:Smtp:From"] = "alerts@example.com",
            ["Notifications:Delivery:ApiSecretEnvPrefix"] = "JUICE_WEBHOOK_",
            ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            ["Notifications:Migration:SmtpServer"] = "observability",
        };

        private static async Task<NotificationsMigrationReportDto> MigrateAsync(Caller admin, Guid tenant, bool apply)
        {
            using var response = await admin.PostAsync($"/api/v1/tenants/{tenant}/notifications/migrate", new { apply });
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<NotificationsMigrationReportDto>(Ct))!;
        }

        private static void All(NotificationsMigrationReportDto report, string action)
            => Assert.All(report.Items, i => Assert.True(i.Action == action, $"{i.Subject} {i.Key}: {i.Action} {string.Join("; ", i.Problems.Select(p => p.Message))}"));


        /// <summary>
        /// Applied by an admin over the API, the pipeline's audit entries name that admin, as the platform's own entry for the run does; the host
        /// command's name the migration (review of #6).
        /// </summary>
        [Fact]
        public async Task An_apply_over_the_api_audits_its_changes_as_the_admin()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var audit = new NotificationsMigrationTests.AuditRecorder();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings,
                services: s => s.AddSingleton<Juice.Notifications.INotificationsAuditSink>(audit));
            var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var key = $"seeded-{Guid.NewGuid().ToString("N")[..6]}";
            await using (var db = database.Context())
            {
                // Legacy's own row, as a host before the alias made it: nothing in notifications yet.
                db.WebhookEndpoints.Add(new WebhookEndpoint
                {
                    TenantId = tenant, Key = key, Kind = ChannelKind.Webhook, Url = "https://receiver.example/hooks/juice", SecretRef = "env:JUICE_WEBHOOK_HOOK",
                    Events = [Juice.Observability.Webhooks.NotificationPlan.Firing, Juice.Observability.Webhooks.NotificationPlan.Resolved], RuleKeys = [], Recipients = [], MinimumSeverity = AlertSeverity.Info, DefinitionHash = "h",
                });
                await db.SaveChangesAsync(Ct);
            }

            var report = await MigrateAsync(admin, tenant, apply: true);

            Assert.Contains(report.Items, i => i.Key == key && i.Action == "create");
            string actor;
            await using (var db = database.Context())
            {
                actor = (await db.AuditRecords.SingleAsync(r => r.Operation == "notifications.migrate.apply", Ct)).Actor;
            }
            var changes = audit.Changes.Where(c => c.Table == "integrations").ToList();
            Assert.NotEmpty(changes);
            Assert.All(changes, c => Assert.Equal(actor, c.User));
            Assert.NotEqual(MigrationPlanner.Source, actor);
        }
        [Fact]
        public async Task Endpoints_made_through_the_legacy_api_migrate_map_back_and_a_second_run_changes_nothing()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings);
            var client = factory.CreateClient();
            var (viewer, _, admin, tenant) = await Caller.AllAsync(client, database);
            var run = Guid.NewGuid().ToString("N")[..6];
            object[] endpoints =
            [
                new { key = $"hook-{run}", kind = "webhook", url = "https://receiver.example/hooks/juice", secretRef = "env:JUICE_WEBHOOK_HOOK",
                    events = new[] { "alert.firing", "alert.resolved" }, ruleKeys = new[] { "orders-errors" }, minimumSeverity = "warning" },
                new { key = $"slack-{run}", kind = "slack", secretRef = "env:JUICE_WEBHOOK_SLACK", events = new[] { "alert.firing" },
                    environments = new[] { "production", "staging" }, minimumSeverity = "critical" },
                new { key = $"teams-{run}", kind = "teams", secretRef = "env:JUICE_WEBHOOK_TEAMS", enabled = false },
                new { key = $"mail-{run}", kind = "email", recipients = new[] { "oncall@example.com", "lead@example.com" } },
            ];
            foreach (var endpoint in endpoints)
            {
                using var created = await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks", endpoint);
                Assert.True(created.IsSuccessStatusCode, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync(Ct)}");
            }
            using (var entry = await admin.SendAsync(HttpMethod.Put, $"/api/v1/tenants/{tenant}/webhooks/allowlist", new { destination = "10.20.0.0/16", ports = new[] { 8443 } }))
            {
                Assert.True(entry.IsSuccessStatusCode);
            }

            // Viewers can't run it, not even the report.
            using (var refused = await viewer.PostAsync($"/api/v1/tenants/{tenant}/notifications/migrate", new { apply = false }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            var report = await MigrateAsync(admin, tenant, apply: false);
            Assert.False(report.Applied);
            Assert.Equal(5, report.Items.Count);
            // The alias wrote each one's notifications rows as it was made (M46b): nothing is left to migrate.
            All(report, "unchanged");

            var applied = await MigrateAsync(admin, tenant, apply: true);
            Assert.True(applied.Applied);
            All(applied, "unchanged");

            All(await MigrateAsync(admin, tenant, apply: false), "unchanged");
            var again = await MigrateAsync(admin, tenant, apply: true);
            Assert.True(again.Applied);
            All(again, "unchanged");

            // Every endpoint reads back from the notifications rows as the definition it was; the legacy rows are as they were.
            await using var platform = database.Context();
            var legacy = await platform.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync(Ct);
            Assert.Equal(4, legacy.Count);
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var notificationsTenant = Guid.Parse(LocalStack.TenantKey); // the platform tenant's key, its Id
            foreach (var endpoint in legacy)
            {
                var setup = await MigrationPlanner.ReadSetupAsync(notifications, notificationsTenant, endpoint.Key, Ct);
                Assert.NotNull(setup);
                Assert.Equal(LegacyEndpoint.From(endpoint), EndpointMapping.Unmap(setup).Value);
            }
            var row = await notifications.Allowlist.AsNoTracking().SingleAsync(a => a.TenantId == notificationsTenant, Ct);
            Assert.Equal(LegacyAllowlistEntry.From(await platform.WebhookAllowlist.AsNoTracking().SingleAsync(a => a.TenantId == tenant, Ct)),
                AllowlistMapping.Unmap(new AllowlistDefinition(row.Destination, row.Ports)));

            // The apply is audited, with its counts.
            var audit = await platform.AuditRecords.AsNoTracking().Where(a => a.Operation == "notifications.migrate.apply").OrderBy(a => a.OccurredAt).ToListAsync(Ct);
            Assert.Equal([("5", AuditOutcome.Succeeded), ("5", AuditOutcome.Succeeded)],
                audit.Select(a => (a.Metadata.GetValueOrDefault("create") ?? a.Metadata.GetValueOrDefault("unchanged") ?? "", a.Outcome)));
        }

        [Theory]
        [InlineData(new string[0], null, null)]
        [InlineData(new[] { "--urls", "http://+:8080" }, null, null)]
        [InlineData(new[] { "notifications", "migrate" }, false, null)]
        [InlineData(new[] { "notifications", "migrate", "--apply" }, true, null)]
        [InlineData(new[] { "notifications", "migrate", "--tenant", "0193f0aa-0000-7000-8000-0000000000f1", "--apply" }, true, "0193f0aa-0000-7000-8000-0000000000f1")]
        public void The_host_command_is_read_from_the_arguments(string[] args, bool? apply, string? tenant)
        {
            var command = Api.NotificationsMigrationCommand.Parse(args);
            Assert.Equal(apply, command?.Apply);
            Assert.Equal(tenant is null ? null : Guid.Parse(tenant), command?.TenantId);
        }

        [Fact]
        public void An_option_the_command_doesnt_take_is_refused()
            => Assert.Throws<ArgumentException>(() => Api.NotificationsMigrationCommand.Parse(["notifications", "migrate", "--force"]));

        [Fact]
        public async Task The_host_command_reports_applies_and_exits_with_whether_anything_conflicts()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings);
            _ = factory.CreateClient(); // started: the schemas are migrated
            await using (var db = database.Context())
            {
                var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
                db.WebhookEndpoints.AddRange(
                    new WebhookEndpoint { TenantId = tenant, Key = "ops", Url = "https://receiver.example/hooks/ops", SecretRef = "env:JUICE_WEBHOOK_OPS",
                        Events = ["alert.firing"], DefinitionHash = "h" },
                    new WebhookEndpoint { TenantId = tenant, Key = "1-old", Url = "https://receiver.example/hooks/old", SecretRef = "env:JUICE_WEBHOOK_OLD",
                        Events = ["alert.firing"], DefinitionHash = "h" });
                await db.SaveChangesAsync(Ct);
            }
            var output = new StringWriter();

            var reported = await new Api.NotificationsMigrationCommand(false, null).RunAsync(factory.Services, output, Ct);
            var applied = await new Api.NotificationsMigrationCommand(true, null).RunAsync(factory.Services, output, Ct);

            // A digit-led key is a conflict: the exit says so, the other endpoint is applied.
            Assert.Equal((2, 2), (reported, applied));
            Assert.Contains("\"key_starts_with_digit\"", output.ToString());
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.NotNull(await MigrationPlanner.ReadSetupAsync(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(), Guid.Parse(LocalStack.TenantKey), "ops", Ct));
            Assert.Equal(1, await new Api.NotificationsMigrationCommand(false, Guid.NewGuid()).RunAsync(factory.Services, output, Ct));
        }

        /// <summary>
        /// <c>Webhooks:Endpoints</c> through the alias (I69, M46b): in <c>legacy</c>, the file is loaded into legacy's table as before, and the
        /// notifications rows follow it at start, the integration keeping the endpoint's id; a restart changes nothing.
        /// </summary>
        [Fact]
        public async Task Endpoints_from_configuration_are_loaded_through_the_alias()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = new Dictionary<string, string>(Settings)
            {
                ["Webhooks:Enabled"] = "true",
                ["Webhooks:Endpoints:0:Key"] = "from-file",
                ["Webhooks:Endpoints:0:Kind"] = "Webhook",
                ["Webhooks:Endpoints:0:Url"] = "https://receiver.example/hooks/file",
                ["Webhooks:Endpoints:0:SecretRef"] = "env:JUICE_WEBHOOK_FILE",
                ["Webhooks:Endpoints:0:MinimumSeverity"] = "Warning",
            };
            for (var start = 0; start < 2; start++)
            {
                await using var factory = new ApiTests.ApiFactory(database, settings: settings);
                _ = factory.CreateClient();
                await using var platform = database.Context();
                var endpoint = await platform.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Key == "from-file", Ct);
                Assert.Equal(ManagedBy.Configuration, endpoint.ManagedBy);
                await using var scope = factory.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var setup = await MigrationPlanner.ReadSetupAsync(notifications, Guid.Parse(LocalStack.TenantKey), "from-file", Ct);
                Assert.Equal(LegacyEndpoint.From(endpoint), EndpointMapping.Unmap(setup!).Value);
                Assert.Equal(endpoint.Id, (await notifications.Integrations.AsNoTracking().SingleAsync(i => i.Key == "from-file", Ct)).Id);
            }
        }

        /// <summary>
        /// The alias (I69): the routes keep their shapes, every answer says they are deprecated and names the successor, and an endpoint made
        /// through them is read back from notifications with the id the create answered.
        /// </summary>
        [Fact]
        public async Task The_webhooks_routes_answer_through_the_alias_with_deprecation_headers()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings);
            var (viewer, _, admin, tenant) = await Caller.AllAsync(factory.CreateClient(), database);

            using var created = await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks",
                new { key = "via-alias", kind = "webhook", url = "https://receiver.example/hooks/alias", secretRef = "env:JUICE_WEBHOOK_ALIAS" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<WebhookEndpointDto>(Ct))!.Id;
            using var listed = await viewer.SendAsync(HttpMethod.Get, $"/api/v1/tenants/{tenant}/webhooks");
            using var refused = await viewer.PostAsync($"/api/v1/tenants/{tenant}/webhooks", new { key = "nope" });

            foreach (var response in new[] { created, listed, refused })
            {
                Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
                Assert.Equal("</notifications>; rel=\"successor-version\"", response.Headers.GetValues("Link").Single());
            }
            var endpoint = Assert.Single((await listed.Content.ReadFromJsonAsync<Page<WebhookEndpointDto>>(Ct))!.Items);
            Assert.Equal((id, "via-alias", "https://receiver.example"), (endpoint.Id, endpoint.Key, endpoint.Destination));
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal(id, (await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Integrations.AsNoTracking().SingleAsync(i => i.Key == "via-alias", Ct)).Id);
        }
    }
}
