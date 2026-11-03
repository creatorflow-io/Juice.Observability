using Microsoft.EntityFrameworkCore;
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
    /// </summary>
    public sealed class WorkerLease : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _name;
        private readonly long _key;
        private readonly ILogger _logger;
        private NpgsqlConnection? _connection;

        public WorkerLease(string connectionString, string name, ILogger logger)
        {
            // Unpooled: a pooled connection keeps its session, and so the lock, until the pool reuses or prunes it.
            _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            _name = name;
            _key = KeyOf(name);
            _logger = logger;
        }

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
                await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                take.Parameters.AddWithValue("key", _key);
                if (await take.ExecuteScalarAsync(cancellationToken) is true)
                {
                    _connection = connection;
                    _logger.LogInformation("This replica now runs {Worker}", _name);
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
