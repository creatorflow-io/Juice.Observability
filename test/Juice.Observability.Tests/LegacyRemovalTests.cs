using System.Net;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// What 2.0 removed (M50): the legacy webhooks' configuration refuses the start rather than being ignored, the removed routes and host command
    /// say so, and the migration that drops the legacy tables upgrades a database that still has their rows.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class LegacyRemovalTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IConfiguration Config(params (string Key, string? Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

        // ─── The configuration ─────────────────────────────────────────────────

        [Theory]
        [InlineData("Webhooks:File", "webhooks.json", "Notifications:Provisioning:Path")]
        [InlineData("Webhooks:File", "", "Notifications:Provisioning:Path")]
        [InlineData("webhooks:consoleurl", "https://console.example", "Notifications:ConsoleUrl")]
        [InlineData("WEBHOOKS:CONSOLEURL", "", "Notifications:ConsoleUrl")]
        [InlineData("Webhooks:Endpoints:0:Key", "ops", "Notifications:Provisioning:Path")]
        [InlineData("Webhooks:Allowlist:0:Destination", "10.0.0.0/8", "Notifications:Provisioning:Path")]
        [InlineData("Webhooks:Smtp:Host", "smtp.example.com", "Notifications:Delivery:SmtpServers")]
        [InlineData("Webhooks:AllowHttpToAllowlisted", "true", "Notifications:Delivery:AllowHttpToAllowlisted")]
        [InlineData("Webhooks:ApiSecretEnvPrefix", "JUICE_WEBHOOK_", "Notifications:Delivery:ApiSecretEnvPrefix")]
        [InlineData("Webhooks:ApiSecretDirectory", "/run/secrets", "Notifications:Delivery:ApiSecretDirectory")]
        [InlineData("Webhooks:AdditionalChatHosts:0", "chat.example.com", "allowlist")]
        [InlineData("Webhooks:Enabled", "false", "Notifications:Stages")]
        [InlineData("Webhooks:Interval", "00:00:02", "Notifications:ReleasePassInterval")]
        [InlineData("Webhooks:MaxAttempts", "3", "Notifications:Delivery:Maxattempts")]
        [InlineData("Actions:NotifyEndpoints:0", "approvers", "Notifications:Provisioning:Path")]
        public void Start_AnyWebhooksKey_Refused_NamesReplacement(string key, string value, string replacement)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LegacyConfiguration.Check(Config((key, value))));

            Assert.Contains(key, ex.Message, StringComparison.OrdinalIgnoreCase);
            // One line for the key, not another for the section above it.
            Assert.DoesNotContain("Webhooks was removed", ex.Message, StringComparison.Ordinal);
            Assert.Contains(replacement, ex.Message, StringComparison.OrdinalIgnoreCase);
            // The value (a path, an address, a secret's prefix) is never echoed.
            if (value.Length > 6) Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Start_EveryRefusedKeyIsNamed_NotJustTheFirst()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LegacyConfiguration.Check(Config(("Webhooks:File", "a"), ("Webhooks:Smtp:Host", "h"), ("Notifications:Pipeline", "shadow"))));

            Assert.Contains("Webhooks:File", ex.Message);
            Assert.Contains("Webhooks:Smtp:Host", ex.Message);
            Assert.Contains("Notifications:Pipeline", ex.Message);
        }

        [Fact]
        public void Start_NoLegacyKey_Passes()
            => LegacyConfiguration.Check(Config(("Notifications:ConsoleUrl", "https://console.example"), ("Notifications:Provisioning:Path", "p.json"), ("Actions:Overrides:x", "y")));

        [Theory]
        [InlineData("legacy")]
        [InlineData("shadow")]
        [InlineData("Shadow")]
        [InlineData("LEGACY")]
        [InlineData("")]
        [InlineData("newish")]
        public void Start_PipelineLegacyOrShadow_Refused(string value)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LegacyConfiguration.Check(Config(("Notifications:Pipeline", value))));

            Assert.Contains("Notifications:Pipeline", ex.Message);
            Assert.Contains("Notifications:Provisioning:Path", ex.Message);
        }

        [Fact]
        public async Task Start_AnyWebhooksKey_Refused_TheHostDoesNotStart()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:File"] = "/config/webhooks.json" });

            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("Webhooks:File", ex.ToString());
            Assert.Contains("Notifications:Provisioning:Path", ex.ToString());
        }

        [Fact]
        public async Task Start_PipelineLegacyOrShadow_Refused_TheHostDoesNotStart()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "shadow" });

            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("Notifications:Pipeline", ex.ToString());
        }

        [Fact]
        public async Task Start_PipelineNewMixedCase_WarnsOnly()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var logs = new LogCapture();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>(ProducerAdapterTests.Quiet()) { ["Notifications:Pipeline"] = "NeW" },
                services: s => s.AddSingleton<ILoggerProvider>(logs));

            _ = factory.Server;

            var warning = Assert.Single(logs.At(LogLevel.Warning), e => e.Message.Contains("Notifications:Pipeline", StringComparison.Ordinal));
            Assert.Contains("obsolete", warning.Message);
        }

        // ─── What is gone ──────────────────────────────────────────────────────

        [Fact]
        public async Task RemovedRoutes_Webhooks_Migrate_ShadowReport_404()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var api = $"/api/v1/tenants/{tenant}";

            var wrong = new List<string>();
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, "/webhooks"), (HttpMethod.Post, "/webhooks"), (HttpMethod.Put, $"/webhooks/{Guid.NewGuid()}"), (HttpMethod.Get, "/webhooks/allowlist"),
                (HttpMethod.Put, "/webhooks/allowlist"), (HttpMethod.Get, $"/webhooks/{Guid.NewGuid()}/deliveries"), (HttpMethod.Post, $"/webhooks/{Guid.NewGuid()}/test"),
                (HttpMethod.Post, "/notifications/migrate"), (HttpMethod.Get, "/notifications/shadow-report"),
            })
            {
                var response = await admin.SendAsync(method, api + path, method == HttpMethod.Get ? null : new { });
                // Nothing is routed there: a GET is 404; any other method is 405 where the host's Console fallback (GET only) is the one route left to say it.
                var gone = method == HttpMethod.Get ? response.StatusCode == HttpStatusCode.NotFound : response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
                if (!gone) wrong.Add($"{method} {path}: {(int)response.StatusCode}");
            }
            Assert.Empty(wrong);
        }

        [Theory]
        [InlineData("notifications", "migrate")]
        [InlineData("notifications", "migrate", "--apply")]
        [InlineData("notifications", "migrate", "--tenant", "00000000-0000-0000-0000-000000000001")]
        public async Task HostCommand_NotificationsMigrate_Exit2(params string[] args)
        {
            Assert.True(RemovedCommands.IsRemoved(args));
            var output = new StringWriter();

            var exit = await RemovedCommands.RunAsync(args, output);

            Assert.Equal(2, exit);
            Assert.Contains("removed in 2.0", output.ToString());
            Assert.False(RemovedCommands.IsRemoved(["notifications"]));
            Assert.False(RemovedCommands.IsRemoved([]));
        }

        [Fact]
        public async Task AlertNotifications_ReturnsCorrelationKey_SilencesIncidents()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, _) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.SilenceAsync(database);
            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            var body = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/alerts/{alert}/notifications"));

            Assert.Equal(["correlationKey", "incidents", "silences"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal($"alert:{alert}", body.GetProperty("correlationKey").GetString());
            Assert.Equal("shop/orders", Assert.Single(body.GetProperty("silences").EnumerateArray()).GetProperty("service").GetString());
            Assert.Empty(body.GetProperty("incidents").EnumerateArray());
        }

        // ─── The migration ─────────────────────────────────────────────────────

        private static readonly string[] LegacyTables =
            ["webhook_endpoints", "webhook_allowlist", "webhook_deliveries", "webhook_attempts", "alert_escalations", "shadow_comparisons", "shadow_differences"];

        private const string Previous = "20261126132759_PipelineModeKept";

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        private static async Task ExecuteAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        private static async Task AssertLegacyGoneAsync(ScratchDatabase database)
        {
            foreach (var table in LegacyTables)
            {
                Assert.Equal(0, await CountAsync(database, $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
            }
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'escalation_policies' AND column_name IN ('steps', 'stop_on_acknowledge')"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'escalation_policies'"));
        }

        [Fact]
        public async Task Migration_FreshDatabase_Passes()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync(provisionTenant: false);

            await using (var db = database.Context())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
                Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(Ct));
                Assert.EndsWith("_RemoveLegacyNotifications", db.Database.GetMigrations().Last(), StringComparison.Ordinal);
            }
            await AssertLegacyGoneAsync(database);
        }

        /// <summary>Down restores the schema, not the data: a policy kept through it has empty steps and 1.x's default of stopping on acknowledge.</summary>
        [Fact]
        public async Task Migration_Down_RestoresSchema_StopOnAcknowledgeDefaultsToTrue()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync(provisionTenant: true);
            Guid tenant;
            await using (var db = database.Context()) tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            await ExecuteAsync(database, $$"""
                INSERT INTO escalation_policies (id, tenant_id, key, name, enabled, rule_keys, environments, minimum_severity, updated_at)
                VALUES ('{{Guid.CreateVersion7()}}', '{{tenant}}', 'pager', 'Pager', true, '{}', '{}', 0, now());
                """);

            await using (var db = database.Context())
            {
                await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
            }

            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM escalation_policies WHERE stop_on_acknowledge AND steps = '[]'::jsonb"));
        }

        private static async Task<(ScratchDatabase Database, Guid Tenant, Guid Alert)> LegacyDatabaseAsync()
        {
            var database = await LocalStack.CreateDatabaseAsync(provisionTenant: false);
            await using var db = database.Context();
            // CreateDatabaseAsync migrated it to the latest: go back to what 1.x had.
            await db.GetService<IMigrator>().MigrateAsync(Previous, Ct);
            var tenant = new PlatformTenant { Key = LocalStack.TenantKey, DisplayName = "upgrade" };
            db.Tenants.Add(tenant);
            var now = DateTimeOffset.UtcNow;
            var rule = new Rule { TenantId = tenant.Id, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = ManagedBy.Api };
            var alert = new Alert
            {
                TenantId = tenant.Id, RuleId = rule.Id, DedupKey = "orders-errors|service:shop/orders@production", SubjectKey = "service:shop/orders@production",
                SubjectType = HealthSubject.Service, Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now,
            };
            db.AddRange(rule, alert);
            await db.SaveChangesAsync(Ct);
            return (database, tenant.Id, alert.Id);
        }

        private static NotificationHold Hold(Guid tenant, Guid alert, string reason, string? pipeline, Guid? silence = null)
            => new()
            {
                TenantId = tenant, AlertId = alert, TransitionId = Guid.CreateVersion7(), Event = "alert.firing", SilenceId = silence ?? Guid.Empty, Request = "{}",
                CreatedAt = DateTimeOffset.UtcNow, Reason = reason, Pipeline = pipeline,
            };

        [Fact]
        public async Task Migration_UpgradeFromLegacyRows_DropsTables_SelectionRowsAndHoldsKept()
        {
            LocalStack.RequireAvailable();
            var (database, tenant, _) = await LegacyDatabaseAsync();
            await using var _ = database;
            // The legacy tables' rows, and a policy with steps: written as 1.x did, which the new model can't.
            var policy = Guid.CreateVersion7();
            await ExecuteAsync(database, $$"""
                INSERT INTO escalation_policies (id, tenant_id, key, name, enabled, rule_keys, environments, minimum_severity, steps, stop_on_acknowledge, updated_at)
                VALUES ('{{policy}}', '{{tenant}}', 'pager', 'Pager', true, ARRAY['orders-errors'], ARRAY['production'], 'Warning', '[{"afterMinutes":5,"endpointKeys":["ops"]}]', true, now());
                INSERT INTO webhook_endpoints (id, tenant_id, key, url, secret_ref, events, rule_keys, environments, recipients, group_window_seconds, minimum_severity, enabled, definition_hash, loaded_at)
                VALUES ('{{Guid.CreateVersion7()}}', '{{tenant}}', 'ops', 'https://hooks.example.com/x', 'env:X', ARRAY['alert.firing'], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 0, 'Info', true, 'h', now());
                INSERT INTO shadow_comparisons (tenant_id, transition_id, escalation_step, alert_id, compared_at, differences, event, legacy, new, unexplained)
                VALUES ('{{tenant}}', '{{Guid.CreateVersion7()}}', 0, '{{Guid.CreateVersion7()}}', now(), 0, 'alert.firing', 1, 1, 0);
                """);

            await using (var db = database.Context())
            {
                await db.Database.MigrateAsync(Ct);
            }

            await AssertLegacyGoneAsync(database);
            await using var check = database.Context();
            // The selection stays as it was: which alerts select the policy, not what it does.
            var kept = await check.EscalationPolicies.AsNoTracking().SingleAsync(p => p.Id == policy, Ct);
            Assert.Equal(("pager", "Pager", true, "Warning"), (kept.Key, kept.Name, kept.Enabled, kept.MinimumSeverity.ToString()));
            Assert.Equal(["orders-errors"], kept.RuleKeys);
            Assert.Equal(["production"], kept.Environments);
        }

        [Fact]
        public async Task Migration_ShadowKeptHoldAndParkedSend_DecidedKeptInShadow_SilenceHoldUntouched()
        {
            LocalStack.RequireAvailable();
            var (database, tenant, alert) = await LegacyDatabaseAsync();
            await using var _ = database;
            var silence = Guid.CreateVersion7();
            var shadowKept = Hold(tenant, alert, NotificationHoldReasons.PipelineUnavailable, "shadow");
            var shadowSilenced = Hold(tenant, alert, NotificationHoldReasons.Silence, "shadow", silence);
            var newKept = Hold(tenant, alert, NotificationHoldReasons.PipelineUnavailable, "new");
            var decidedShadow = Hold(tenant, alert, NotificationHoldReasons.PipelineUnavailable, "shadow");
            decidedShadow.DecidedAt = DateTimeOffset.UtcNow.AddHours(-1);
            decidedShadow.Outcome = NotificationHoldOutcomes.Sent;
            var parkedShadowSend = new NotificationParkedCall { TenantId = tenant, Kind = NotificationParkedCalls.Send, CorrelationKey = null, Request = "{}", Pipeline = "shadow", CreatedAt = DateTimeOffset.UtcNow };
            var parkedShadowAck = new NotificationParkedCall { TenantId = tenant, Kind = NotificationParkedCalls.Acknowledge, CorrelationKey = $"alert:{alert}", By = "erin", Pipeline = "shadow", CreatedAt = DateTimeOffset.UtcNow };
            var parkedNewSend = new NotificationParkedCall { TenantId = tenant, Kind = NotificationParkedCalls.Send, Request = "{}", Pipeline = "new", CreatedAt = DateTimeOffset.UtcNow };
            await using (var db = database.Context())
            {
                db.AddRange(shadowKept, shadowSilenced, newKept, decidedShadow, parkedShadowSend, parkedShadowAck, parkedNewSend);
                await db.SaveChangesAsync(Ct);
                await db.Database.MigrateAsync(Ct);
            }

            await using var check = database.Context();
            var holds = await check.NotificationHolds.AsNoTracking().ToDictionaryAsync(h => h.Id, Ct);
            // Legacy had sent it: nothing to send now, and it says why.
            Assert.Equal((NotificationHoldOutcomes.Dropped, "kept_in_shadow", true), (holds[shadowKept.Id].Outcome, holds[shadowKept.Id].LastError, holds[shadowKept.Id].DecidedAt is not null));
            // A silence's hold is real in any mode; a hold kept in the only mode, and one decided already, are as they were.
            Assert.Equal((null, null, false), (holds[shadowSilenced.Id].Outcome, holds[shadowSilenced.Id].LastError, holds[shadowSilenced.Id].DecidedAt is not null));
            Assert.Equal((null, null, false), (holds[newKept.Id].Outcome, holds[newKept.Id].LastError, holds[newKept.Id].DecidedAt is not null));
            Assert.Equal((NotificationHoldOutcomes.Sent, null), (holds[decidedShadow.Id].Outcome, holds[decidedShadow.Id].LastError));
            var calls = await check.NotificationParkedCalls.AsNoTracking().ToDictionaryAsync(c => c.Id, Ct);
            Assert.Equal(("kept_in_shadow", true), (calls[parkedShadowSend.Id].LastError, calls[parkedShadowSend.Id].ReplayedAt is not null));
            // An acknowledgement is real in any mode, and a send kept in the only mode is still to be replayed.
            Assert.Equal((null, false), (calls[parkedShadowAck.Id].LastError, calls[parkedShadowAck.Id].ReplayedAt is not null));
            Assert.Equal((null, false), (calls[parkedNewSend.Id].LastError, calls[parkedNewSend.Id].ReplayedAt is not null));
        }
    }
}
