using Juice.Observability.Api;
using Juice.Observability.Api.Modules.ModuleData;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Juice.Observability.Tests.ModuleData
{
    /// <summary>
    /// A module taking over tables that already exist (the §4 adoption rule of core 2.0): recorded as applied when all are there with the columns the
    /// first migration names, created when none is, refused otherwise, and never dropped or altered. Local stack.
    /// </summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ModuleAdoptionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The last migration of 1.4.0: SLOs, their versions and the rest of the model of that release.</summary>
        private const string Release14 = "20261119074928_ServiceSignalPacks";

        private static Task<ModuleTestHost> HostAsync<T>(ScratchDatabase database, Dictionary<string, string?>? settings = null) where T : Juice.Observability.Extensibility.IObservabilityModule, new()
            => ModuleTestHost.StartAsync(b => b.AddObservabilityModule<T>(), database, hostedServices: true, settings: settings);

        private static Task<long> CountAsync(ScratchDatabase database, string sql) => ModuleDataTests.CountAsync(database, sql);

        private static async Task<string> TextAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToString(await command.ExecuteScalarAsync(Ct)) ?? "";
        }

        private static async Task ExecuteAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        /// <summary>What a table holds, byte for byte: a hash of its rows as text, and of its columns.</summary>
        private static Task<string> FingerprintAsync(ScratchDatabase database, string table)
            => TextAsync(database, $"""
                SELECT md5(coalesce((SELECT string_agg(t::text, '|' ORDER BY t.id) FROM {table} t), '') || '#'
                    || (SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'))
                """);

        /// <summary>
        /// A 1.4.0 database as an operator would copy it: migrated to the 1.4.0 release, SLO rows written, and then taken to this core's head (the 2.0 upgrade).
        /// </summary>
        private static async Task<ScratchDatabase> Copy14Async()
        {
            var database = new ScratchDatabase($"juice_obs_test_{Guid.NewGuid():N}");
            await using (var admin = new NpgsqlConnection(LocalStack.PostgresConnection("juice_observability")))
            {
                await admin.OpenAsync(Ct);
                await using var create = new NpgsqlCommand($"CREATE DATABASE {database.Name}", admin);
                await create.ExecuteNonQueryAsync(Ct);
            }
            await using var db = database.Context();
            await db.GetService<IMigrator>().MigrateAsync(Release14, Ct);
            var (tenant, slo) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await ExecuteAsync(database, $"""
                INSERT INTO platform_tenants (id, key, display_name, status, created_at) VALUES ('{tenant}', '{LocalStack.TenantKey}', 'tenant', 'Active', now());
                INSERT INTO slos (id, tenant_id, key, name, category, sli, service, environment, current_version, retired, created_at)
                VALUES ('{slo}', '{tenant}', 'orders-availability', 'Orders availability', 'Technical', 'Availability', 'shop/orders', 'production', 1, false, now());
                INSERT INTO slo_versions (id, tenant_id, slo_id, version, definition, hash, target, effective_from, created_at, created_by)
                VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{slo}', 1, 'orders', 'h1', 99.5, now(), now(), 'file');
                """);
            await db.Database.MigrateAsync(Ct);
            return database;
        }

        [Fact]
        public async Task Adopts_on_14_copy()
        {
            LocalStack.RequireAvailable();
            await using var database = await Copy14Async();
            // The core's own start first: it retires an SLO its file doesn't list, which is the core's doing and not adoption's.
            await using (var core = await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }
            var before = (await FingerprintAsync(database, "slos"), await FingerprintAsync(database, "slo_versions"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM slos"));

            await using (var host = await HostAsync<AdopterModule>(database))
            {
            }

            // The rows, and the tables as they were, byte for byte: nothing dropped, altered or rewritten.
            Assert.Equal(before, (await FingerprintAsync(database, "slos"), await FingerprintAsync(database, "slo_versions")));
            // The first migration is recorded, not run (the tables it creates already existed, with more columns than it names); the second ran.
            Assert.Equal("20261129000001_AdoptSlos,20261129000002_AdopterOwn", await TextAsync(database, "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM adopter.__ef_migrations_history"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'adopter' AND table_name = 'own'"));

            // A second start is a no-op.
            await using (var again = await HostAsync<AdopterModule>(database))
            {
            }
            Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM adopter.__ef_migrations_history"));
            Assert.Equal(before, (await FingerprintAsync(database, "slos"), await FingerprintAsync(database, "slo_versions")));
        }

        [Fact]
        public async Task Two_replicas_adopt_once()
        {
            LocalStack.RequireAvailable();
            await using var database = await Copy14Async();
            await using (var core = await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }
            var before = await FingerprintAsync(database, "slos");

            var hosts = await Task.WhenAll(HostAsync<AdopterModule>(database), HostAsync<AdopterModule>(database));
            try
            {
                Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM adopter.__ef_migrations_history"));
                Assert.Equal(before, await FingerprintAsync(database, "slos"));
            }
            finally
            {
                foreach (var host in hosts)
                {
                    await host.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task Adopt_creates_when_absent()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();

            await using (var host = await HostAsync<AdoptNewModule>(database))
            {
            }

            Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('adopt_new_a', 'adopt_new_b')"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM adopt_new.__ef_migrations_history WHERE \"MigrationId\" = '20261129000010_CreateAdoptNew'"));
        }

        [Fact]
        public async Task Adopt_partial_refused_host_stops()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await ExecuteAsync(database, "CREATE TABLE public.adopt_new_a (\"Id\" uuid PRIMARY KEY, \"Name\" text NOT NULL)");

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<AdoptNewModule>(database));

            Assert.Contains("public.adopt_new_b", e.ToString());
            // Nothing was changed: the table that was there is as it was, the other was not made, and no history was written.
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'adopt_new_b'"));
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'adopt_new' AND table_name = '__ef_migrations_history'"));
        }

        [Fact]
        public async Task Adopt_missing_column_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // Both tables are there, but the first has no "Name", which the first migration names.
            await ExecuteAsync(database, "CREATE TABLE public.adopt_new_a (\"Id\" uuid PRIMARY KEY); CREATE TABLE public.adopt_new_b (\"Id\" uuid PRIMARY KEY);");

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<AdoptNewModule>(database));

            Assert.Contains("'Name'", e.ToString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'adopt_new' AND table_name = '__ef_migrations_history'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'adopt_new_a'"));
        }

        [Fact]
        public async Task Adopt_first_migration_with_other_ops_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<AdoptOpsModule>(database));

            Assert.Contains("may hold only operations", e.ToString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('adopt_ops_a', 'extra')"));
        }

        [Fact]
        public async Task Later_migration_dropping_an_adopted_table_refused_before_any_runs()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<DropAdoptedModule>(database));

            Assert.Contains("DropTableOperation", e.ToString());
            // The first migration, which was fine, was not run either: every pending migration is judged before the first applies.
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'adopt_drop_a'"));
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'adopt_drop' AND table_name = '__ef_migrations_history'"));
        }

        [Fact]
        public async Task Migration_touching_a_core_table_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<CoreTouchModule>(database));

            Assert.Contains("AddColumnOperation on public.audit_records", e.ToString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'audit_records' AND column_name = 'injected'"));
        }

        [Fact]
        public async Task Migrate_false_pending_module_migration_stops_host()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();

            var e = await Assert.ThrowsAnyAsync<Exception>(async () => await HostAsync<DataSampleModule>(database, new() { ["Database:Migrate"] = "false" }));

            Assert.Contains("data-sample", e.ToString());
            Assert.Contains("20261129000000_CreateItems", e.ToString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'data_sample'"));

            // Once its migrations are applied (by a host that migrates), the same host starts with Database:Migrate false.
            await using (var migrating = await HostAsync<DataSampleModule>(database))
            {
            }
            await using var host = await HostAsync<DataSampleModule>(database, new() { ["Database:Migrate"] = "false" });
        }

        [Fact]
        public async Task Database_down_at_start_stops_host_no_history_row()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // The host composed (not started) with its connection pointing nowhere, as CoreHost does: what a module's migration finds when the database is down.
            await using var app = CoreHost.Build(b => b.AddObservabilityModule<DataSampleModule>());
            var registration = app.Services.GetServices<ModuleDataRegistration>().Single();
            await using var scope = app.Services.CreateAsyncScope();

            await Assert.ThrowsAnyAsync<Exception>(() => ModuleMigrator.MigrateAsync(registration, scope.ServiceProvider,
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1", NullLogger.Instance, Ct));

            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'data_sample'"));
        }
    }
}
