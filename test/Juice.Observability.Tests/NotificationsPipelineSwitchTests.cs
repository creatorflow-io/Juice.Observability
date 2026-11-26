using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Observability.Tests
{
    /// <summary><c>Notifications:Pipeline</c> (phase-5-plan I67) as M45-0 has it: <c>legacy</c> only, and the pipeline's settings under it.</summary>
    public class NotificationsPipelineSwitchTests
    {
        private static IConfiguration Settings(params (string Key, string Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

        [Fact]
        public void Legacy_is_the_default_and_runs_no_stage()
        {
            var configuration = Settings();
            var section = configuration.GetSection(PlatformNotificationsOptions.Section);
            Assert.Equal(NotificationsPipeline.Legacy, new PlatformNotificationsOptions().Pipeline);
            var pipeline = NotificationsRegistration.PipelineSettings(section, configuration, NotificationsPipeline.Legacy);
            Assert.Equal("false", pipeline["Stages:Run"]);
        }

        [Theory]
        [InlineData("true", null, "true")]
        [InlineData("false", null, "false")]
        [InlineData("true", "false", "false")]
        [InlineData(null, null, null)]
        public void Http_to_allowlisted_receivers_carries_over_from_the_webhooks_unless_the_pipeline_says(string? webhooks, string? pipeline, string? expected)
        {
            var values = new List<(string, string)>();
            if (webhooks is not null) values.Add(("Webhooks:AllowHttpToAllowlisted", webhooks));
            if (pipeline is not null) values.Add(("Notifications:Delivery:AllowHttpToAllowlisted", pipeline));
            var configuration = Settings([.. values]);
            var settings = NotificationsRegistration.PipelineSettings(configuration.GetSection("Notifications"), configuration, NotificationsPipeline.New);
            Assert.Equal(expected, settings["Delivery:AllowHttpToAllowlisted"]);
        }

        [Fact]
        public void Legacy_runs_no_stage_even_when_the_section_says_so()
        {
            var configuration = Settings(("Notifications:Stages:Run", "true"), ("Notifications:Retention:Days", "30"));
            var pipeline = NotificationsRegistration.PipelineSettings(configuration.GetSection("Notifications"), configuration, NotificationsPipeline.Legacy);
            Assert.Equal("false", pipeline["Stages:Run"]);
            // The rest of the section is the pipeline's, as given.
            Assert.Equal("30", pipeline["Retention:Days"]);
        }

        [Theory]
        [InlineData(null, null, false)]
        [InlineData("true", null, true)]
        [InlineData("true", "false", false)]
        [InlineData(null, "true", true)]
        public void The_schema_migrates_when_the_platforms_does_unless_the_section_says_otherwise(string? platform, string? section, bool expected)
        {
            var values = new List<(string, string)>();
            if (platform is not null) values.Add(("Database:Migrate", platform));
            if (section is not null) values.Add(("Notifications:Database:Migrate", section));
            var configuration = Settings([.. values]);
            Assert.Equal(expected, NotificationsRegistration.MigratesSchema(configuration.GetSection("Notifications"), configuration));
            // The platform migrates it, never the pipeline's own migrator (it would stop the host on a failure).
            var pipeline = NotificationsRegistration.PipelineSettings(configuration.GetSection("Notifications"), configuration, NotificationsPipeline.Legacy);
            Assert.Equal("false", pipeline["Database:Migrate"]);
        }

        [Fact]
        public void Shadow_starts_with_the_pipelines_dry_run()
        {
            // The pinned alpha has a working dry run (M44-2): shadow may run beside legacy, every pipeline send a WouldSend.
            Assert.True(NotificationsRegistration.DryRunAvailable);
            var configuration = Settings(("Notifications:Pipeline", "shadow"), ("ConnectionStrings:Platform", "Host=x"), ("Platform:TenantKey", LocalStack.TenantKey));
            var services = new ServiceCollection();
            services.AddLogging();
            Api.PlatformExtensions.AddPlatformMessaging(services);
            // Registered, with the pipeline's dry run forced on (the settings test checks Delivery:DryRun).
            services.AddPlatformNotifications(configuration);
            Assert.Contains(services, s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && s.ImplementationFactory is not null);
        }

        /// <summary>
        /// Shadow on the local stack (M45's definition of done): legacy sends as before, the pipeline plans the same notification and records it
        /// as <c>WouldSend</c>, and its target hears nothing from it (review focus 5: shadow never really sends).
        /// </summary>
        [Fact]
        public async Task Shadow_plans_beside_legacy_and_records_what_it_would_send_without_sending_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var target = await WebhookTests.Receiver.StartAsync();
            var provisioning = Path.Combine(Path.GetTempPath(), $"shadow-{Guid.NewGuid():N}.json");
            Environment.SetEnvironmentVariable("JUICE_SHADOW_TEST", "shadow-secret");
            await File.WriteAllTextAsync(provisioning, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": {
                    "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "env:JUICE_SHADOW_TEST",
                                                 "targets": { "ops": { "address": "{{target.Url}}" } } } },
                    "routes": { "alerts": { "match": { "type": "observability.*" }, "targets": ["ops"] } },
                    "allowlist": [ { "destination": "{{target.Address}}", "ports": [{{target.Port}}] } ]
                } } }
                """, TestContext.Current.CancellationToken);
            try
            {
                await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
                {
                    ["Notifications:Pipeline"] = "shadow", ["Notifications:Provisioning:Path"] = provisioning, ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
                    ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                    ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2", ["Webhooks:Enabled"] = "true",
                });
                var workers = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Select(PipelineStart.NameOf).ToList();
                // Both pipelines run: legacy's dispatch and release, and the pipeline's stages.
                Assert.Contains(nameof(Webhooks.WebhookDispatcher), workers);
                Assert.Contains(workers, w => w.StartsWith("StageWorker", StringComparison.Ordinal));
                Assert.Null(((PipelineStart)factory.Services.GetService(typeof(PipelineStart))!).Failure);

                var (_, firing) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(factory, database, firing);

                string? status = null;
                for (var i = 0; i < 100 && status != "WouldSend"; i++)
                {
                    await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
                    await connection.OpenAsync(TestContext.Current.CancellationToken);
                    await using var read = new Npgsql.NpgsqlCommand("SELECT status FROM notifications.deliveries LIMIT 1", connection);
                    status = (string?)await read.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                    if (status != "WouldSend") await Task.Delay(200, TestContext.Current.CancellationToken);
                }
                Assert.Equal("WouldSend", status);
                // Legacy planned its own, as it does without the pipeline; the pipeline's target heard nothing.
                await using var db = database.Context();
                Assert.NotNull((await db.AlertTransitions.SingleAsync(t => t.Id == firing)).NotificationsPlannedAt);
                await Task.Delay(1000, TestContext.Current.CancellationToken);
                Assert.Empty(target.Requests);
            }
            finally
            {
                File.Delete(provisioning);
            }
        }

        private static class CapabilitiesWithDryRun { public static bool DryRun { get; } = true; }

        private static class CapabilitiesWithoutDryRun { public static bool DryRun { get; } = false; }

        private static class CapabilitiesWithAConst { public const bool DryRun = true; }

        [Fact]
        public void The_dry_run_is_the_pipelines_published_capability_and_absent_is_none()
        {
            Assert.True(NotificationsRegistration.DryRunIn(typeof(CapabilitiesWithDryRun)));
            Assert.False(NotificationsRegistration.DryRunIn(typeof(CapabilitiesWithoutDryRun)));
            // Only the property counts (a const would be inlined into the caller's build), and no type at all is no dry run.
            Assert.False(NotificationsRegistration.DryRunIn(typeof(CapabilitiesWithAConst)));
            Assert.False(NotificationsRegistration.DryRunIn(null));
        }

        [Fact]
        public void Shadow_runs_the_stages_in_dry_run_and_new_as_the_section_says()
        {
            var configuration = Settings(("Notifications:Stages:Run", "true"));
            var section = configuration.GetSection("Notifications");
            var shadow = NotificationsRegistration.PipelineSettings(section, configuration, NotificationsPipeline.Shadow);
            Assert.Equal(("true", "true"), (shadow["Delivery:DryRun"], shadow["Stages:Run"]));
            var @new = NotificationsRegistration.PipelineSettings(section, configuration, NotificationsPipeline.New);
            Assert.Equal(((string?)null, "true"), (@new["Delivery:DryRun"], @new["Stages:Run"]));
        }

        [Fact]
        public void What_each_mode_sends()
        {
            Assert.Equal((true, false), (new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.Legacy }.LegacySends,
                new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.Legacy }.ProducesRequests));
            Assert.Equal((true, true), (new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.Shadow }.LegacySends,
                new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.Shadow }.ProducesRequests));
            Assert.Equal((false, true), (new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.New }.LegacySends,
                new PlatformNotificationsOptions { Pipeline = NotificationsPipeline.New }.ProducesRequests));
        }

        private static List<string> Workers(ScratchDatabase database, string pipeline, out ApiTests.ApiFactory factory)
        {
            factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = pipeline, ["Webhooks:Enabled"] = "true",
            });
            return factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Select(PipelineStart.NameOf).ToList();
        }

        [Fact]
        public async Task New_has_no_legacy_dispatch_or_release_and_escalate_pass_and_runs_the_pipeline_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var workers = Workers(database, "new", out var factory);
            await using (factory)
            {
                Assert.DoesNotContain(nameof(Webhooks.WebhookDispatcher), workers);
                Assert.DoesNotContain(nameof(Webhooks.NotificationWorker), workers);
                Assert.Contains(nameof(NotificationsReleasePass), workers);
                Assert.Contains(workers, w => w.StartsWith("StageWorker", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Legacy_keeps_the_webhooks_and_has_no_release_pass_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var workers = Workers(database, "legacy", out var factory);
            await using (factory)
            {
                Assert.Contains(nameof(Webhooks.WebhookDispatcher), workers);
                Assert.Contains(nameof(Webhooks.NotificationWorker), workers);
                Assert.DoesNotContain(nameof(NotificationsReleasePass), workers);
                Assert.DoesNotContain(workers, w => w.StartsWith("StageWorker", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Legacy_plans_its_deliveries_and_writes_no_request_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Pipeline"] = "legacy" });
            _ = factory.Server;
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);

            await ProducerAdapterTests.PlanAsync(factory, database, transition);

            Assert.Empty(await ProducerAdapterTests.RequestsAsync(database));
            await using var db = database.Context();
            Assert.NotNull((await db.AlertTransitions.SingleAsync(t => t.Id == transition)).NotificationsPlannedAt);
        }
    }

    /// <summary>
    /// <see cref="PlatformNotificationTransaction.OnCommitted"/> (M45 review focus 2): an action runs when its own transaction commits, never on a
    /// rollback or another transaction's commit, and a context made without the interceptor says it can't tell.
    /// </summary>
    public class PlatformCommitActionsTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static PlatformDbContext Context(ScratchDatabase database, PlatformCommitActions? commits)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, database.ConnectionString);
            if (commits is not null) options.AddInterceptors(commits);
            return new PlatformDbContext(options.Options);
        }

        [Fact]
        public async Task An_action_runs_once_its_transaction_commits_and_never_on_a_rollback_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var commits = new PlatformCommitActions(NullLogger<PlatformCommitActions>.Instance);
            var ran = new List<string>();

            await using (var db = Context(database, commits))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                Assert.True(new PlatformNotificationTransaction(db, commits).OnCommitted(() => ran.Add("committed")));
                Assert.Empty(ran);
                await transaction.CommitAsync(Ct);
            }
            await using (var db = Context(database, commits))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                Assert.True(new PlatformNotificationTransaction(db, commits).OnCommitted(() => ran.Add("rolled back")));
                await transaction.RollbackAsync(Ct);
            }

            Assert.Equal(["committed"], ran);
        }

        [Fact]
        public async Task Another_transactions_commit_runs_none_of_its_actions_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var commits = new PlatformCommitActions(NullLogger<PlatformCommitActions>.Instance);
            var ran = new List<string>();

            await using var first = Context(database, commits);
            await using var second = Context(database, commits);
            await using var one = await first.Database.BeginTransactionAsync(Ct);
            await using var two = await second.Database.BeginTransactionAsync(Ct);
            new PlatformNotificationTransaction(first, commits).OnCommitted(() => ran.Add("first"));
            new PlatformNotificationTransaction(second, commits).OnCommitted(() => ran.Add("second"));
            await two.CommitAsync(Ct);
            Assert.Equal(["second"], ran);
            await one.CommitAsync(Ct);
            Assert.Equal(["second", "first"], ran);
        }

        [Fact]
        public async Task A_transaction_disposed_without_a_rollback_leaves_no_action_for_the_next_one_on_its_connection_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var commits = new PlatformCommitActions(NullLogger<PlatformCommitActions>.Instance);
            var ran = new List<string>();

            // A failed command: the transaction is disposed, never rolled back by name, and its connection goes back to the pool.
            await using (var db = Context(database, commits))
            {
                var transaction = await db.Database.BeginTransactionAsync(Ct);
                Assert.True(new PlatformNotificationTransaction(db, commits).OnCommitted(() => ran.Add("stale")));
                await transaction.DisposeAsync();
            }
            // The next transaction on the pooled connection (Npgsql reuses its transaction object) commits.
            await using (var db = Context(database, commits))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                new PlatformNotificationTransaction(db, commits).OnCommitted(() => ran.Add("own"));
                await transaction.CommitAsync(Ct);
            }

            Assert.Equal(["own"], ran);
        }

        [Fact]
        public async Task A_context_joining_a_live_transaction_keeps_its_actions_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var commits = new PlatformCommitActions(NullLogger<PlatformCommitActions>.Instance);
            var ran = new List<string>();

            await using var owner = Context(database, commits);
            await using var transaction = await owner.Database.BeginTransactionAsync(Ct);
            new PlatformNotificationTransaction(owner, commits).OnCommitted(() => ran.Add("registered"));
            // Another context on the same connection joins the transaction (as the embedded client's does on its own context).
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            options.UseNpgsql(owner.Database.GetDbConnection()).UseSnakeCaseNamingConvention();
            options.AddInterceptors(commits);
            await using (var joining = new PlatformDbContext(options.Options))
            {
                await joining.Database.UseTransactionAsync(transaction.GetDbTransaction(), Ct);
            }
            await transaction.CommitAsync(Ct);

            Assert.Equal(["registered"], ran);
        }

        [Fact]
        public async Task Without_a_transaction_or_the_interceptor_the_request_waits_for_the_poll_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var commits = new PlatformCommitActions(NullLogger<PlatformCommitActions>.Instance);

            await using (var db = Context(database, commits))
            {
                var transaction = new PlatformNotificationTransaction(db, commits);
                Assert.Null(transaction.Transaction);
                Assert.False(transaction.OnCommitted(() => { }));
            }
            await using (var db = Context(database, commits: null))
            {
                await using var open = await db.Database.BeginTransactionAsync(Ct);
                var transaction = new PlatformNotificationTransaction(db, commits);
                Assert.Same(open.GetDbTransaction(), transaction.Transaction);
                Assert.False(transaction.OnCommitted(() => { }));
            }
        }
    }
}
