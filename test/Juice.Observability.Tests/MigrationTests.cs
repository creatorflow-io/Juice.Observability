using Juice.Observability.Data;
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
            // Compares the model with the last migration snapshot only: no database (or local stack) is needed, so
            // this runs on CI too.
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, "Host=localhost;Database=model_check");
            using var db = new PlatformDbContext(options.Options);
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

            // Data written before HealthModel is carried over: instances give their services an environment.
            await migrator.MigrateAsync(migrations[0], ct);
            var (tenant, env, svc) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO platform_tenants (id, key, display_name, status, created_at) VALUES ({tenant}, 'upgrade', 'upgrade', 'Active', now());
                INSERT INTO environments (id, tenant_id, key, type, first_seen, last_seen) VALUES ({env}, {tenant}, 'production', 'Production', now(), now());
                INSERT INTO services (id, tenant_id, namespace, name, first_seen, last_seen) VALUES ({svc}, {tenant}, '', 'orders', now(), now());
                INSERT INTO service_instances (id, tenant_id, service_id, environment_id, instance_key, key_source, state, first_seen, last_seen)
                VALUES ({Guid.CreateVersion7()}, {tenant}, {svc}, {env}, 'i1', 'Observed', 'Active', now() - interval '1 hour', now());
                """, ct);
            await db.Database.MigrateAsync(ct);
            var link = await db.ServiceEnvironments.SingleAsync(ct);
            Assert.Equal((svc, env), (link.ServiceId, link.EnvironmentId));

            // Re-running is a no-op.
            await db.Database.MigrateAsync(ct);
            Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync(ct));
        }
    }
}
