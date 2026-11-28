using System.Runtime.CompilerServices;
using Juice.Observability.Data;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>A site that applies migrations outside the app (<c>Database:Migrate</c> false) upgrading to a build with the integrations module.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class InstanceUpgradeTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Script([CallerFilePath] string here = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", "deploy", "migrations", "integrations.sql"));

        private static Dictionary<string, string?> NoMigrate => new() { ["Database:Migrate"] = "false" };

        [Fact]
        public async Task Refusal_names_the_module_the_script_and_the_setting()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var db = database.Context())
            {
                await db.Database.MigrateAsync(Ct);
            }

            var e = await Assert.ThrowsAnyAsync<Exception>(() => ModuleTestHost.StartAsync(database: database, hostedServices: true, settings: NoMigrate));

            Assert.Contains("integrations", e.ToString());
            Assert.Contains("deploy/migrations/integrations.sql", e.ToString());
            Assert.Contains("Database:Migrate=true", e.ToString());
        }

        [Fact]
        public async Task Shipped_script_applies_the_schema_and_a_host_with_migrate_false_starts()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var db = database.Context())
            {
                await db.Database.MigrateAsync(Ct);
            }
            Assert.True(File.Exists(Script()), Script());

            // Twice: the script is idempotent, so running it again at the next upgrade changes nothing.
            for (var i = 0; i < 2; i++)
            {
                await using var connection = new NpgsqlConnection(database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Script(), Ct), connection);
                await command.ExecuteNonQueryAsync(Ct);
            }

            await using var host = await ModuleTestHost.StartAsync(database: database, hostedServices: true, settings: NoMigrate);
            await using var check = new NpgsqlConnection(database.ConnectionString);
            await check.OpenAsync(Ct);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM integrations.__ef_migrations_history WHERE \"MigrationId\" LIKE '%IntegrationsInitial'", check);
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(Ct)));
        }
    }
}
