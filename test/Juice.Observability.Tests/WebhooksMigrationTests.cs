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
        /// The host gives the alias legacy's secret limits and leaves the notifications API's as they are (waves §5.3, M46-3): an alias row may
        /// name env:JUICE_WEBHOOK_…, an API one may not.
        /// </summary>
        [Fact]
        public async Task The_alias_has_legacys_secret_limits_and_the_api_keeps_its_own()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Webhooks:ApiSecretDirectory"] = "/run/secrets/juice",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            });
            _ = factory.CreateClient();

            var delivery = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Juice.Notifications.Providers.DeliveryOptions>>().Value;

            Assert.Equal(("JUICE_WEBHOOK_", "/run/secrets/juice"), (delivery.AliasSecretEnvPrefix, delivery.AliasSecretDirectory));
            Assert.Equal(("NOTIFICATIONS_SECRET_", (string?)null), (delivery.ApiSecretEnvPrefix, delivery.ApiSecretDirectory));
        }
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
            // A migration is of legacy's rows; in new the alias writes none (Q-A), so there is nothing to migrate.
            Assert.SkipWhen(string.Equals(LocalStack.NotificationsPipeline, "new", StringComparison.OrdinalIgnoreCase), "new keeps no legacy rows to migrate");
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
                await using var scope = factory.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var setup = await MigrationPlanner.ReadSetupAsync(notifications, Guid.Parse(LocalStack.TenantKey), "from-file", Ct);
                var mapped = EndpointMapping.Unmap(setup!).Value!;
                Assert.Equal(("https://receiver.example/hooks/file", "env:JUICE_WEBHOOK_FILE", AlertSeverity.Warning), (mapped.Url, mapped.SecretRef, mapped.MinimumSeverity));
                // Legacy's row too, but in new (Q-A), with the integration's id.
                var endpoint = await platform.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Key == "from-file", Ct);
                if (string.Equals(LocalStack.NotificationsPipeline, "new", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Null(endpoint);
                    continue;
                }
                Assert.Equal(ManagedBy.Configuration, endpoint!.ManagedBy);
                Assert.Equal(LegacyEndpoint.From(endpoint), mapped);
                Assert.Equal(endpoint.Id, (await notifications.Integrations.AsNoTracking().SingleAsync(i => i.Key == "from-file", Ct)).Id);
            }
        }


        /// <summary>
        /// What the deployment's file declares is the deployment's in notifications too (M46c, waves §6.4 B): the alias's provisioner writes it as
        /// <c>alias-config</c>, taking over the alias's own row of the same key; an admin's change through /webhooks gets 409 in every pipeline,
        /// as legacy answered; and the migration tool, which writes the alias's own, reports it as a conflict instead of writing over it.
        /// </summary>
        [Theory]
        [InlineData("legacy")]
        [InlineData("new")]
        public async Task Endpoints_and_allowlist_from_the_file_are_the_deployments_in_notifications(string pipeline)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var made = Guid.Empty;
            // First the endpoint is the admin's (through the API), then the file declares its key: the file takes it over.
            await using (var before = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>(Settings) { ["Notifications:Pipeline"] = pipeline }))
            {
                var client = before.CreateClient();
                var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
                made = (await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks",
                    new { key = "from-file", url = "https://receiver.example/hooks/api", secretRef = "env:JUICE_WEBHOOK_FILE" }))).GetProperty("id").GetGuid();
            }
            var settings = new Dictionary<string, string>(Settings)
            {
                ["Notifications:Pipeline"] = pipeline,
                ["Webhooks:Endpoints:0:Key"] = "from-file",
                ["Webhooks:Endpoints:0:Kind"] = "Webhook",
                ["Webhooks:Endpoints:0:Url"] = "https://receiver.example/hooks/file",
                ["Webhooks:Endpoints:0:SecretRef"] = "env:JUICE_WEBHOOK_FILE",
                ["Webhooks:Allowlist:0:Destination"] = "10.40.0.0/16",
            };
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);
            var http = factory.CreateClient();
            var (_, _, owner, tenantId) = await Caller.AllAsync(http, database);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var integration = await notifications.Integrations.AsNoTracking().SingleAsync(i => i.Key == "from-file", Ct);
                Assert.Equal((AliasVocabulary.AliasConfig, made), (integration.ManagedBy, integration.Id));
                Assert.Equal(AliasVocabulary.AliasConfig, (await notifications.Routes.AsNoTracking().SingleAsync(r => r.Key == "from-file", Ct)).ManagedBy);
                Assert.Equal(AliasVocabulary.AliasConfig, (await notifications.Allowlist.AsNoTracking().SingleAsync(a => a.Destination == "10.40.0.0/16", Ct)).ManagedBy);
                var setup = await MigrationPlanner.ReadSetupAsync(notifications, Guid.Parse(LocalStack.TenantKey), "from-file", Ct);
                Assert.Equal("https://receiver.example/hooks/file", EndpointMapping.Unmap(setup!).Value!.Url);
            }
            var webhooks = $"/api/v1/tenants/{tenantId}/webhooks";
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsync($"{webhooks}/{made}", new { secretRef = "env:JUICE_WEBHOOK_FILE", enabled = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.SendAsync(HttpMethod.Put, $"{webhooks}/allowlist", new { destination = "10.40.0.0/16", ports = new[] { 443 } })).StatusCode);
            var listed = await Caller.JsonAsync(await owner.GetAsync(webhooks));
            Assert.Equal(ManagedBy.Configuration, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("managedBy").GetString());
            if (pipeline == "legacy")
            {
                // The migration tool doesn't write over what the alias's configuration holds: it says so.
                var report = await MigrateAsync(owner, tenantId, apply: true);
                var item = Assert.Single(report.Items, i => i.Key == "from-file");
                Assert.Equal("conflict", item.Action);
            }
        }

        private static Dictionary<string, string> FileSettings(string pipeline, string? secret = "env:JUICE_WEBHOOK_FILE", bool allowlist = true)
        {
            var settings = new Dictionary<string, string>(Settings) { ["Notifications:Pipeline"] = pipeline };
            if (secret is not null)
            {
                settings["Webhooks:Endpoints:0:Key"] = "from-file";
                settings["Webhooks:Endpoints:0:Kind"] = "Webhook";
                settings["Webhooks:Endpoints:0:Url"] = "https://receiver.example/hooks/file";
                settings["Webhooks:Endpoints:0:SecretRef"] = secret;
            }
            if (allowlist)
            {
                settings["Webhooks:Allowlist:0:Destination"] = "10.40.0.0/16";
            }
            return settings;
        }

        private static async Task<(bool? RouteEnabled, bool Allowlisted)> AliasRowsAsync(ApiTests.ApiFactory factory)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var route = await notifications.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Key == "from-file", Ct);
            return (route?.Enabled, await notifications.Allowlist.AsNoTracking().AnyAsync(a => a.Destination == "10.40.0.0/16", Ct));
        }

        /// <summary>
        /// Drift (M46c, review of #10): the alias's copies follow what they mirror. An endpoint the file changes into one the alias can't write (a
        /// secret outside its limits) keeps its rows for their history but its route is disabled, so nothing stale sends at cutover; an endpoint
        /// gone from the file, the same; an allowlist entry gone from the file is removed, so the pipeline reaches no more than legacy does.
        /// </summary>
        [Theory]
        [InlineData("legacy")]
        [InlineData("new")]
        public async Task The_alias_follows_the_file_an_unwritable_or_gone_endpoint_stops_and_a_gone_entry_goes(string pipeline)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var first = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline)))
            {
                _ = first.CreateClient();
                Assert.Equal((true, true), await AliasRowsAsync(first));
            }
            // The file's endpoint now names a secret outside the alias's limits; its allowlist entry is gone.
            await using (var second = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline, secret: "env:OTHER_SECRET", allowlist: false)))
            {
                _ = second.CreateClient();
                Assert.Equal((false, false), await AliasRowsAsync(second));
            }
            // Mappable again: it's written, and sends, again. Then gone from the file: stopped.
            await using (var third = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline, allowlist: false)))
            {
                _ = third.CreateClient();
                Assert.Equal((true, false), await AliasRowsAsync(third));
            }
            await using var fourth = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline, secret: null, allowlist: false));
            _ = fourth.CreateClient();
            Assert.Equal((false, false), await AliasRowsAsync(fourth));
        }

        /// <summary>
        /// The migration tool reconciles the alias's own rows with legacy's while legacy sends: an allowlist entry removed in legacy only (the
        /// pipeline down when it was) is removed by the next migration, and reported as such.
        /// </summary>
        [Fact]
        public async Task The_migration_removes_an_allowlist_entry_legacy_no_longer_has()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>(Settings) { ["Notifications:Pipeline"] = "legacy" });
            var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            await Caller.JsonAsync(await admin.SendAsync(HttpMethod.Put, $"/api/v1/tenants/{tenant}/webhooks/allowlist", new { destination = "10.41.0.0/16", ports = new[] { 443 } }));
            await using (var db = database.Context())
            {
                // Removed in legacy behind the alias's back.
                await db.WebhookAllowlist.Where(a => a.Destination == "10.41.0.0/16").ExecuteDeleteAsync(Ct);
            }

            var report = await MigrateAsync(admin, tenant, apply: true);

            Assert.Equal("remove", Assert.Single(report.Items, i => i.Key == "10.41.0.0/16").Action);
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Allowlist.AsNoTracking().AnyAsync(a => a.Destination == "10.41.0.0/16", Ct));
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
