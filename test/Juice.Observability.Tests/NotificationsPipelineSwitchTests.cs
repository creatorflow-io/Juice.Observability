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

        [Theory]
        [InlineData("shadow")]
        [InlineData("new")]
        public void Shadow_and_new_are_refused_until_M45(string mode)
        {
            var configuration = Settings(("Notifications:Pipeline", mode), ("ConnectionStrings:Platform", "Host=x"), ("Platform:TenantKey", LocalStack.TenantKey));
            var refused = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPlatformNotifications(configuration));
            Assert.Contains("legacy", refused.Message);
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
