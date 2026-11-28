using Juice.Observability.Data;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>The module's schema arriving on an existing 2.0 database, and the host refusing to start ahead of its schema.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class InstanceDataTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        /// <summary>A database as a 2.0 install leaves it: the core migrated, a tenant, a rule, and no integrations schema.</summary>
        private static async Task<Guid> SeedExistingAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            await db.Database.MigrateAsync(Ct);
            var tenant = await db.Tenants.FirstOrDefaultAsync(Ct);
            if (tenant is null)
            {
                tenant = new PlatformTenant { Key = LocalStack.TenantKey, DisplayName = "tests" };
                db.Tenants.Add(tenant);
            }
            db.Rules.Add(new Rule { TenantId = tenant.Id, Key = "existing-rule", Name = "Existing", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Warning, Definition = "{}", DefinitionHash = "h", LoadedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            return tenant.Id;
        }

        [Fact]
        public async Task Module_migrates_schema_integrations_on_existing_20_database()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedExistingAsync(database);
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'integrations'"));

            await using var host = await ModuleTestHost.StartAsync(database: database, hostedServices: true);

            Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'integrations' AND table_name IN ('integration_instances', 'integration_checkpoints')"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM integrations.__ef_migrations_history WHERE \"MigrationId\" LIKE '%IntegrationsInitial'"));
            // the core's history is the core's, and what was there is as it was
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM public.__ef_migrations_history WHERE migration_id LIKE '%Integrations%'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM rules WHERE key = 'existing-rule' AND name = 'Existing' AND enabled"));
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM alerts"));
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM integrations.integration_instances"));
        }

        [Fact]
        public async Task Migrate_false_pending_integrations_migration_stops_host()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedExistingAsync(database);

            var e = await Assert.ThrowsAnyAsync<Exception>(() => ModuleTestHost.StartAsync(database: database, hostedServices: true,
                settings: new Dictionary<string, string?> { ["Database:Migrate"] = "false" }));

            Assert.Contains("integrations", e.ToString());
            Assert.Contains("IntegrationsInitial", e.ToString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'integrations'"));
        }
    }
}
