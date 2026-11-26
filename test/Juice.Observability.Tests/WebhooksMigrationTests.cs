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
            All(report, "create");

            var applied = await MigrateAsync(admin, tenant, apply: true);
            Assert.True(applied.Applied);
            All(applied, "create");

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
    }
}
