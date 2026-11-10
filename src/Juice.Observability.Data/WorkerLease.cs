using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Juice.Observability.Data
{
    /// <summary>
    /// Lets one replica at a time run a background worker (discovery, health, rules): the holder keeps a PostgreSQL
    /// advisory lock on a connection of its own, and the other replicas skip their passes. When the holder stops or
    /// loses its connection, PostgreSQL releases the lock and another replica takes it on its next pass.
    /// <para>Check <see cref="TryHoldAsync"/> before every pass: it also confirms the connection, and so the lock, is
    /// still there.</para>
    /// <para>A newer version takes the lease over: the holder's connection names its version, and a replica that finds an older
    /// holder ends that session, so a host left on an older version can't keep a worker from doing what a newer one does. An
    /// older replica still takes a lease nobody holds, so going back to an older version works.</para>
    /// </summary>
    public sealed class WorkerLease : IAsyncDisposable
    {
        /// <summary>What a lease connection is called in <c>pg_stat_activity</c>: the worker, then the holder's version.</summary>
        internal const string ApplicationPrefix = "juice-worker:";

        /// <summary>This build's version, as releases name it: the build metadata after <c>+</c> left out.</summary>
        public static readonly string CurrentVersion = (typeof(WorkerLease).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

        private readonly string _connectionString;
        private readonly string _name;
        private readonly string _version;
        private readonly long _key;
        private readonly ILogger _logger;
        private NpgsqlConnection? _connection;

        /// <summary>A lease named <paramref name="name"/>; <paramref name="version"/> is this replica's, <see cref="CurrentVersion"/> unless a test says otherwise.</summary>
        /// <param name="connectionString">The platform database.</param>
        /// <param name="name">The worker.</param>
        /// <param name="logger">Where taking and losing it is logged.</param>
        /// <param name="version">This replica's version.</param>
        public WorkerLease(string connectionString, string name, ILogger logger, string? version = null)
        {
            _name = name;
            _version = version ?? CurrentVersion;
            _key = KeyOf(name);
            _logger = logger;
            // Unpooled: a pooled connection keeps its session, and so the lock, until the pool reuses or prunes it. PostgreSQL keeps 63
            // bytes of an application name; one that doesn't fit has no version anyone can read, and is never taken over.
            _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = ApplicationName(name, _version) }.ConnectionString;
        }

        private static string ApplicationName(string name, string version)
            => $"{ApplicationPrefix}{name}:{version}" is { Length: <= 63 } full ? full : $"{ApplicationPrefix}{name}"[..Math.Min(63, ApplicationPrefix.Length + name.Length)];

        /// <summary>A lease on the platform database, for a worker resolved from the host's services.</summary>
        public static WorkerLease For(IServiceScopeFactory scopes, string name, ILogger logger)
        {
            using var scope = scopes.CreateScope();
            var connectionString = scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.GetConnectionString()
                ?? throw new InvalidOperationException("The platform database has no connection string");
            return new WorkerLease(connectionString, name, logger);
        }

        /// <summary>Whether this replica holds the lease now, taking it if it is free.</summary>
        public async Task<bool> TryHoldAsync(CancellationToken cancellationToken)
        {
            if (_connection is not null)
            {
                try
                {
                    await using var check = new NpgsqlCommand("SELECT 1", _connection);
                    await check.ExecuteScalarAsync(cancellationToken);
                    return true;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Lost the {Worker} lease; another replica may take it", _name);
                    await CloseAsync();
                }
            }

            var connection = new NpgsqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                if (await TakeAsync(connection, cancellationToken))
                {
                    _connection = connection;
                    _logger.LogInformation("This replica now runs {Worker}, version {Version}", _name, _version);
                    return true;
                }
                if (await EndOlderHolderAsync(connection, cancellationToken) && await TakeAsync(connection, cancellationToken))
                {
                    _connection = connection;
                    _logger.LogWarning("This replica took {Worker} over from an older version; that replica should be upgraded or stopped", _name);
                    return true;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not take the {Worker} lease", _name);
            }
            await connection.DisposeAsync();
            return false;
        }

        private async Task<bool> TakeAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            take.Parameters.AddWithValue("key", _key);
            return await take.ExecuteScalarAsync(cancellationToken) is true;
        }

        /// <summary>
        /// Ends the session holding this lease when its version is older than this replica's: its lock goes with it. The holder is found by the
        /// lock itself (an advisory lock on this key, split into its two 32-bit halves), and its version by its application name.
        /// </summary>
        private async Task<bool> EndOlderHolderAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using var find = new NpgsqlCommand("""
                SELECT a.pid, a.application_name FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE l.locktype = 'advisory' AND l.granted AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND l.classid::bigint = @high AND l.objid::bigint = @low AND l.objsubid = 1
                """, connection);
            find.Parameters.AddWithValue("high", (long)((ulong)_key >> 32));
            find.Parameters.AddWithValue("low", (long)((ulong)_key & 0xFFFFFFFF));
            int? pid = null;
            string? holder = null;
            await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    pid = reader.GetInt32(0);
                    holder = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
            var prefix = $"{ApplicationPrefix}{_name}:";
            if (pid is null || holder is null || !holder.StartsWith(prefix, StringComparison.Ordinal)
                || PlatformVersion.Compare(holder[prefix.Length..], _version) is not < 0)
            {
                return false;
            }
            _logger.LogWarning("{Worker} is held by version {Holder}, older than this replica's {Version}: ending that session to take it over",
                _name, holder[prefix.Length..], _version);
            await using var end = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            end.Parameters.AddWithValue("pid", pid.Value);
            if (await end.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
            // The lock goes when the session ends, a moment after it's told to.
            for (var i = 0; i < 50; i++)
            {
                await using var gone = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = @pid", connection);
                gone.Parameters.AddWithValue("pid", pid.Value);
                if (await gone.ExecuteScalarAsync(cancellationToken) is 0L)
                {
                    break;
                }
                await Task.Delay(20, cancellationToken);
            }
            return true;
        }

        /// <summary>
        /// Whether any replica holds the lease named <paramref name="name"/> now: its advisory lock is granted to some session. Runs on the context's
        /// connection, in its transaction if it has one.
        /// </summary>
        public static async Task<bool> IsHeldAsync(PlatformDbContext db, string name, CancellationToken cancellationToken)
        {
            var key = KeyOf(name);
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    SELECT EXISTS (SELECT 1 FROM pg_locks l
                    WHERE l.locktype = 'advisory' AND l.granted AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                      AND l.classid::bigint = @high AND l.objid::bigint = @low AND l.objsubid = 1)
                    """;
                command.Parameters.Add(new NpgsqlParameter("high", (long)((ulong)key >> 32)));
                command.Parameters.Add(new NpgsqlParameter("low", (long)((ulong)key & 0xFFFFFFFF)));
                return await command.ExecuteScalarAsync(cancellationToken) is true;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        public ValueTask DisposeAsync() => CloseAsync();

        private async ValueTask CloseAsync()
        {
            if (_connection is { } connection)
            {
                _connection = null;
                // Closing the session releases its advisory locks.
                await connection.DisposeAsync();
            }
        }

        /// <summary>The advisory lock key for a worker name: a stable 64-bit hash (FNV-1a), the same in every replica.</summary>
        public static long KeyOf(string name)
        {
            var hash = 14695981039346656037UL;
            foreach (var c in "juice.observability.worker:" + name)
            {
                hash = (hash ^ c) * 1099511628211UL;
            }
            return unchecked((long)hash);
        }
    }
}
