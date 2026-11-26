using System.Net;
using Juice.Observability.Notifications;
using Microsoft.EntityFrameworkCore;
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
        public async Task A_schema_never_migrated_fails_readiness_not_the_start_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:Database:Migrate"] = "false" });
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Ct)).StatusCode);
            // The provisioning file came first and failed on the missing schema; the application's provisioning, after it, didn't run.
            Assert.StartsWith("ConfigurationProvisioner", Start(factory).Failure);
            Assert.Equal(["EmbeddedApplicationProvisioner"], Start(factory).Skipped);
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
    }
}
