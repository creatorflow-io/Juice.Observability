using System.Net;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The pipeline's start in the platform's host: a failure fails readiness and leaves the host running (phase-5-plan risk table), and what
    /// follows the failed step is skipped.
    /// </summary>
    public class PipelineStartTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task SqlAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        private static PipelineStart Start(ApiTests.ApiFactory factory) => (PipelineStart)factory.Services.GetService(typeof(PipelineStart))!;

        [Fact]
        public async Task A_failed_migration_leaves_the_host_started_and_not_ready_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // A table in the way: the pipeline's first migration can't create it.
            await SqlAsync(database, "CREATE SCHEMA notifications; CREATE TABLE notifications.applications (unrelated int);");
            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
            Assert.StartsWith("DatabaseMigrator", Start(factory).Failure);
            // The platform's own start went on: its tenant is there.
            await using var db = database.Context();
            Assert.True(await db.Tenants.AnyAsync(Ct));
        }

        [Fact]
        public async Task A_failed_start_in_new_still_commits_alert_transitions_and_keeps_their_notifications_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SqlAsync(database, "CREATE SCHEMA notifications; CREATE TABLE notifications.applications (unrelated int);");
            var settings = new Dictionary<string, string> { ["Notifications:Pipeline"] = "new" };
            Guid alert, transition;
            await using (var factory = new ApiTests.ApiFactory(database, settings: settings))
            {
                _ = factory.Server;
                var start = Start(factory);
                Assert.StartsWith("DatabaseMigrator", start.Failure);
                // Nothing of the pipeline polls the tables that aren't there: its stages, its outbox, the release pass.
                Assert.Contains("CompositeDeliveryHostedService<NotificationsDbContext>", start.Skipped);
                Assert.Contains(nameof(NotificationsReleasePass), start.Skipped);
                Assert.Contains(start.Skipped, s => s.StartsWith("StageWorker", StringComparison.Ordinal));

                (alert, transition) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(factory, database, transition);
                await using var db = database.Context();
                // The plan committed, and the request waits for the pipeline.
                Assert.NotNull((await db.AlertTransitions.SingleAsync(t => t.Id == transition, Ct)).NotificationsPlannedAt);
                var hold = await db.NotificationHolds.SingleAsync(Ct);
                Assert.Equal((transition, ProducerAdapter.PipelineUnavailable, (DateTimeOffset?)null), (hold.TransitionId, hold.SilenceId, hold.DecidedAt));
            }

            // The cause fixed and the host restarted: the pipeline starts, and the release pass sends what was kept.
            await SqlAsync(database, "DROP TABLE notifications.applications;");
            await using (var factory = new ApiTests.ApiFactory(database, settings: settings))
            {
                _ = factory.Server;
                Assert.Null(Start(factory).Failure);
                var pass = new NotificationsReleasePass(factory.Services.GetRequiredService<IServiceScopeFactory>(),
                    Microsoft.Extensions.Options.Options.Create(new Webhooks.WebhookOptions()), TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationsReleasePass>.Instance);
                await pass.ReleaseOnceAsync(Ct);
                var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
                Assert.Equal(($"transition:{transition}", $"alert:{alert}"), (request.Key, request.Correlation));
            }
        }

        [Fact]
        public async Task A_schema_never_migrated_fails_readiness_not_the_start_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Database:Migrate"] = "false" });
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Ct)).StatusCode);
            // The provisioning file came first and failed on the missing schema; the application's provisioning, after it, didn't run.
            Assert.StartsWith("ConfigurationProvisioner", Start(factory).Failure);
            Assert.Equal("EmbeddedApplicationProvisioner", Start(factory).Skipped.First());
            Assert.Contains("CompositeDeliveryHostedService<NotificationsDbContext>", Start(factory).Skipped);
        }

        [Fact]
        public async Task A_good_start_migrates_after_the_platform_and_is_ready_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Ct)).StatusCode);
            Assert.Null(Start(factory).Failure);
        }

        [Fact]
        public async Task A_good_start_is_ready_and_its_setup_is_in_the_platforms_audit_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // The pipeline's provisioning file sets up one integration for the platform's tenant at start: a change to its setup.
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-provisioning-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": { "integrations": {
                  "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "alerts@example.invalid" },
                            "targets": { "ops": { "address": "ops@example.invalid" } } } } } } }
                """, Ct);
            try
            {
                await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
                {
                    ["Notifications:Provisioning:Path"] = file, ["Notifications:Delivery:SmtpServers:local:Host"] = "localhost", ["Notifications:Delivery:SmtpServers:local:Port"] = "1025",
                    ["Notifications:Delivery:SmtpServers:local:Security"] = "None",
                });
                using var client = factory.CreateClient();

                using var ready = await client.GetAsync("/health/ready", Ct);
                Assert.True(ready.StatusCode == HttpStatusCode.OK, (Start(factory)).Failure);
                await using var db = database.Context();
                var records = await db.AuditRecords.AsNoTracking().Where(a => a.Operation.StartsWith("notifications.")).ToListAsync(Ct);
                // Recorded as the provisioning's change, in the platform tenant, with the fields that changed and none of their values.
                var integration = Assert.Single(records, r => r.TargetType == "integrations");
                Assert.Equal(("notifications.integrations.inserted", "deployment-config", AuditOutcome.Succeeded),
                    (integration.Operation.ToLowerInvariant(), integration.Actor, integration.Outcome));
                Assert.Equal((await db.Tenants.SingleAsync(Ct)).Id, integration.TenantId);
                Assert.Contains("settings", integration.Metadata["fields"], StringComparison.OrdinalIgnoreCase);
                Assert.All(records, r => Assert.Equal("notifications", r.Metadata["source"]));
                Assert.All(records, r => Assert.DoesNotContain("alerts@example.invalid", string.Join(" ", r.Metadata.Values) + r.TargetId));
                Assert.Contains(records, r => r.TargetType == "integration_targets");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
