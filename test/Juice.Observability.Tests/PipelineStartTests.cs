using Juice.Observability.Tests.Legacy;
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
            var settings = new Dictionary<string, string>();
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
                    Microsoft.Extensions.Options.Options.Create(new PlatformNotificationsOptions()), TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationsReleasePass>.Instance);
                await pass.ReleaseOnceAsync(Ct);
                var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
                Assert.Equal(($"transition:{transition}", $"alert:{alert}"), (request.Key, request.Correlation));
            }
        }

        [Fact]
        public async Task An_acknowledgement_and_a_notice_made_while_down_are_replayed_once_the_pipeline_is_up_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var healthy = new Dictionary<string, string>();
            Guid alert;
            // Up: the firing is sent, and its correlation (and escalation) is open.
            await using (var factory = new ApiTests.ApiFactory(database, settings: healthy))
            {
                _ = factory.Server;
                Guid firing;
                (alert, firing) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(factory, database, firing);
                Assert.Equal("Open", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            }

            // Down (its provisioning file can't load): someone acknowledges, and an action request ends.
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-provisioning-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }
                """, Ct);
            var request = new ActionRequest
            {
                TenantId = await ProducerAdapterTests.TenantAsync(database), AgentId = Guid.NewGuid(), Action = "restart-service", Parameters = "{}", Risk = "medium",
                Policy = "admin", Reason = "stuck", RequestedBy = "erin", RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                State = ActionRequestState.Rejected,
            };
            try
            {
                await using var down = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>(healthy) { ["Notifications:Provisioning:Path"] = file });
                _ = down.Server;
                Assert.StartsWith("ConfigurationProvisioner", Start(down).Failure);
                await using (var scope = down.Services.CreateAsyncScope())
                {
                    using var context = PlatformMessageContext.Begin("test");
                    var outcome = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(
                        new Api.Endpoints.AcknowledgeAlert(request.TenantId, alert, null, "erin", null, "", "ack", "h"), Ct);
                    Assert.Equal(200, outcome.Status);
                }
                await using (var scope = down.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                    await Agents.ActionRequestNotices.AddAsync(scope.ServiceProvider, request, "web-1",
                        ActionRequestEvents.Rejected, DateTimeOffset.UtcNow, Ct);
                    await db.SaveChangesAsync(Ct);
                    await transaction.CommitAsync(Ct);
                }
                // The pipeline heard nothing; all are kept, in order. The one addition of M56: the tolerant resolve of the request's approval item
                // (a no-op on replay when there never was one), kept before the notice that ends the request.
                Assert.Equal("Open", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
                await using var check = database.Context();
                Assert.Equal([NotificationParkedCalls.Acknowledge, NotificationParkedCalls.Resolve, NotificationParkedCalls.Send],
                    await check.NotificationParkedCalls.OrderBy(c => c.Id).Select(c => c.Kind).ToListAsync(Ct));
            }
            finally
            {
                File.Delete(file);
            }

            // Up again: the release pass replays them, and the escalation that stops on an acknowledgement stops.
            await using (var factory = new ApiTests.ApiFactory(database, settings: healthy))
            {
                _ = factory.Server;
                Assert.Null(Start(factory).Failure);
                var pass = new NotificationsReleasePass(factory.Services.GetRequiredService<IServiceScopeFactory>(),
                    Microsoft.Extensions.Options.Options.Create(new PlatformNotificationsOptions()), TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationsReleasePass>.Instance);
                // The host's own pass (new) may have replayed them already; either way, once.
                await pass.ReleaseOnceAsync(Ct);
                Assert.Equal("Acknowledged", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
                Assert.Contains(await ProducerAdapterTests.RequestsAsync(database), r => r.Key == $"action:{request.Id}:rejected" && r.Correlation is null);
                await using var db = database.Context();
                Assert.All(await db.NotificationParkedCalls.ToListAsync(Ct), c => Assert.NotNull(c.ReplayedAt));
                // Replayed once: a later pass has nothing to do.
                Assert.Equal(0, await pass.ReleaseOnceAsync(Ct));
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
            // Nor did the rest of the pipeline: its stages would poll tables that aren't there.
            Assert.Contains("EmbeddedApplicationProvisioner", Start(factory).Skipped);
            Assert.Contains(Start(factory).Skipped, s => s.StartsWith("StageWorker", StringComparison.Ordinal));
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
