using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Juice.Observability.Tests
{
    /// <summary>Roadmap Phase 1 gate: migrations pass clean-install and upgrade validation.</summary>
    public class MigrationTests
    {
        [Fact]
        public async Task Clean_install_applies_every_migration_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync(provisionTenant: false);
            await using var db = database.Context();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public void Model_has_no_changes_missing_a_migration()
        {
            using var db = new ScratchDatabase("model_check").Context();
            Assert.False(db.Database.HasPendingModelChanges(),
                "The model differs from the last migration: add one with dotnet ef migrations add");
        }

        [Fact]
        public async Task Upgrade_steps_through_each_migration_and_back_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync(provisionTenant: false);
            await using var db = database.Context();
            var migrator = db.GetService<IMigrator>();
            var migrations = db.Database.GetMigrations().ToList();
            var ct = TestContext.Current.CancellationToken;

            // Down to empty, then up one migration at a time: every upgrade path from an older install works.
            await migrator.MigrateAsync(Migration.InitialDatabase, ct);
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync(ct));
            foreach (var migration in migrations)
            {
                await migrator.MigrateAsync(migration, ct);
            }
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync(ct));

            // Re-running is a no-op.
            await db.Database.MigrateAsync(ct);
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync(ct));
        }
    }
}
