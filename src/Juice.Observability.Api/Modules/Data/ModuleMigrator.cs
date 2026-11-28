using System.Data.Common;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Juice.Observability.Api.Modules.ModuleData
{
    /// <summary>A module's migrations refused at start: the host stops, and the message names the module and why.</summary>
    internal sealed class ModuleMigrationException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Brings one module context's schema up to date at start, on its own connection and under a session advisory lock
    /// (<c>module-migrations:&lt;module&gt;</c>), so replicas starting together migrate and adopt once. A module that adopts tables
    /// (<see cref="ModuleDataOptions.Adopts"/>) is adopted only while its history is empty:
    /// <list type="bullet">
    /// <item>every adopted table exists, with every column the first migration's <c>CreateTable</c> names: the first migration is recorded as applied, not run;</item>
    /// <item>none exists: migrated as usual, which creates them;</item>
    /// <item>otherwise (some exist, or a column is missing): refused. Nothing is dropped or altered, and no history is written.</item>
    /// </list>
    /// The first migration of an adopting module may hold only operations on adopted tables.
    /// </summary>
    internal static class ModuleMigrator
    {
        public static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
        {
            var connection = new NpgsqlConnection(PlatformDbContext.WithoutGssProbe(connectionString));
            try
            {
                await connection.OpenAsync(ct);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        /// <summary>The first pending migration of a module whose migrations aren't all applied, or null: with <c>Database:Migrate</c> false that stops the host.</summary>
        public static async Task<string?> FirstPendingAsync(ModuleDataRegistration registration, IServiceProvider services, string connectionString, CancellationToken ct)
        {
            await using var connection = await OpenAsync(connectionString, ct);
            await using var db = registration.Create(services, connection);
            return (await db.Database.GetPendingMigrationsAsync(ct)).FirstOrDefault();
        }

        public static async Task MigrateAsync(ModuleDataRegistration registration, IServiceProvider services, string connectionString, ILogger logger, CancellationToken ct)
        {
            await using var connection = await OpenAsync(connectionString, ct);
            await using var locked = new Lock(connection, WorkerLease.KeyOf($"module-migrations:{registration.Module}"));
            await locked.AcquireAsync(ct);
            await using var db = registration.Create(services, connection);
            var module = registration.Module;
            if (registration.Adopts.Count > 0 && !(await db.Database.GetAppliedMigrationsAsync(ct)).Any())
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                {
                    await AdoptAsync(registration, db, connection, pending[0], logger, ct);
                }
            }
            await db.Database.MigrateAsync(ct);
        }

        /// <summary>Records the first migration as applied when the tables are all there, leaves it to run when none is, and throws when refused.</summary>
        private static async Task AdoptAsync(ModuleDataRegistration registration, DbContext db, NpgsqlConnection connection, string first, ILogger logger, CancellationToken ct)
        {
            var module = registration.Module;
            var migration = FirstMigration(db, first);
            var adopted = registration.Adopts.ToHashSet();

            foreach (var operation in migration.UpOperations)
            {
                if (operation is EnsureSchemaOperation)
                {
                    continue;
                }
                var (schema, table) = operation switch
                {
                    CreateTableOperation o => (o.Schema, o.Name),
                    CreateIndexOperation o => (o.Schema, o.Table),
                    AddForeignKeyOperation o => (o.Schema, o.Table),
                    AddPrimaryKeyOperation o => (o.Schema, o.Table),
                    AddUniqueConstraintOperation o => (o.Schema, o.Table),
                    _ => ((string?)null, (string?)null),
                };
                // A table with no schema is the default's: the module's own, which isn't adopted. Drops, alters and raw SQL are never adoption's.
                if (table is null || !adopted.Contains(new AdoptedTable(schema ?? registration.Schema, table)))
                {
                    throw new ModuleMigrationException(
                        $"Module '{module}' adopts tables, so its first migration {first} may hold only operations that create them ({operation.GetType().Name}"
                        + (table is null ? "" : $" on {schema ?? registration.Schema}.{table}") + " is not one). Move the module's other tables to a later migration.");
                }
            }

            var existing = new List<AdoptedTable>();
            foreach (var table in registration.Adopts)
            {
                if (await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_name = @n", table, null, ct) > 0)
                {
                    existing.Add(table);
                }
            }
            if (existing.Count == 0)
            {
                logger.LogInformation("Module {Module}: none of its adopted tables exist, migrating {Migration} to create them", module, first);
                return;
            }
            if (existing.Count !=registration.Adopts.Count)
            {
                var missing = registration.Adopts.Except(existing).Select(t => $"{t.Schema}.{t.Name}");
                throw new ModuleMigrationException(
                    $"Module '{module}' can't adopt its tables: {string.Join(", ", existing.Select(t => $"{t.Schema}.{t.Name}"))} exist, but {string.Join(", ", missing)} do not. "
                    + "Adoption takes all of them or none, and nothing was changed.");
            }
            foreach (var create in migration.UpOperations.OfType<CreateTableOperation>())
            {
                var table = new AdoptedTable(create.Schema ?? registration.Schema, create.Name);
                foreach (var column in create.Columns)
                {
                    if (await ScalarAsync(connection, "SELECT count(*) FROM information_schema.columns WHERE table_schema = @s AND table_name = @n AND column_name = @c", table, column.Name, ct) == 0)
                    {
                        throw new ModuleMigrationException(
                            $"Module '{module}' can't adopt {table.Schema}.{table.Name}: it has no column '{column.Name}', which the first migration {first} names. Nothing was changed.");
                    }
                }
            }

            // Verified. The schema, the history table and the row are one transaction, so a failure leaves no history behind.
            var history = db.GetService<IHistoryRepository>();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await db.Database.UseTransactionAsync(transaction, ct);
            var createSchema = $"CREATE SCHEMA IF NOT EXISTS \"{registration.Schema}\""; // a name derived from the module name, validated at composition
            await db.Database.ExecuteSqlRawAsync(createSchema, ct);
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(first, ProductVersion)), ct);
            await transaction.CommitAsync(ct);
            await db.Database.UseTransactionAsync(null, ct);
            logger.LogInformation("Module {Module} adopted {Count} existing tables: {Migration} is recorded as applied, not run", module, existing.Count, first);
        }

        private static string ProductVersion => typeof(DbContext).Assembly.GetName().Version!.ToString(3);

        private static Migration FirstMigration(DbContext db, string id)
        {
            var assembly = db.GetService<IMigrationsAssembly>();
            return assembly.Migrations.TryGetValue(id, out var type)
                ? assembly.CreateMigration(type, db.GetService<IDatabaseProvider>().Name)
                : throw new ModuleMigrationException($"Migration {id} was not found in the assembly of the module's context.");
        }

        private static async Task<long> ScalarAsync(DbConnection connection, string sql, AdoptedTable table, string? column, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in new[] { ("s", table.Schema), ("n", table.Name), ("c", column) })
            {
                if (value is not null)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
            }
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }

        /// <summary>A session advisory lock on the connection: released by <see cref="DisposeAsync"/>, or when the connection closes.</summary>
        private sealed class Lock(NpgsqlConnection connection, long key) : IAsyncDisposable
        {
            private bool _held;

            public async Task AcquireAsync(CancellationToken ct)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", connection);
                command.Parameters.AddWithValue("k", key);
                await command.ExecuteNonQueryAsync(ct);
                _held = true;
            }

            public async ValueTask DisposeAsync()
            {
                if (_held && connection.State == System.Data.ConnectionState.Open)
                {
                    _held = false;
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", connection);
                    command.Parameters.AddWithValue("k", key);
                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
    }
}
