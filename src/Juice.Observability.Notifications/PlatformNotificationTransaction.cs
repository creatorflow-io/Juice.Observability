using System.Data.Common;
using System.Runtime.CompilerServices;
using Juice.Notifications;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The platform's transaction as the pipeline's (phase-5-plan I59): a notification request is written on <see cref="PlatformDbContext"/>'s
    /// connection, in the command's transaction, and commits or rolls back with the command's change. Scoped: the client takes the caller's.
    /// </summary>
    public sealed class PlatformNotificationTransaction(PlatformDbContext db, PlatformCommitActions commits) : INotificationTransaction
    {
        public DbConnection? Connection => db.Database.GetDbConnection();

        public DbTransaction? Transaction => db.Database.CurrentTransaction?.GetDbTransaction();

        /// <summary>
        /// Runs <paramref name="action"/> once this transaction commits. False when the context can't tell (it was made without
        /// <see cref="PlatformCommitActions"/>, as a test's or a tool's is): the request then waits for the pipeline's next poll.
        /// </summary>
        public bool OnCommitted(Action action)
            => Transaction is { } transaction && commits.IsOn(db) && commits.Add(transaction, action);
    }

    /// <summary>
    /// Actions to run when a given platform transaction commits, never when it rolls back or fails (M45 review focus 2). An EF interceptor on
    /// <see cref="PlatformDbContext"/>, keyed by the transaction itself, so one transaction's actions never run on another's commit.
    /// </summary>
    public sealed class PlatformCommitActions(ILogger<PlatformCommitActions> logger) : DbTransactionInterceptor
    {
        private readonly ConditionalWeakTable<DbTransaction, List<Action>> _pending = [];

        /// <summary>Whether <paramref name="db"/> was made with this interceptor (only then does a commit run the actions).</summary>
        internal bool IsOn(DbContext db)
            => db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(this) == true;

        internal bool Add(DbTransaction transaction, Action action)
        {
            lock (_pending)
            {
                _pending.GetOrCreateValue(transaction).Add(action);
            }
            return true;
        }

        // A transaction disposed without a rollback (a failed command's) raises neither RolledBack nor Failed, and Npgsql hands the same
        // transaction object to the next transaction on the pooled connection: what is still registered under it is dropped when it begins
        // again, so a stale action never runs on another transaction's commit. Only at begin: a context joining a live transaction
        // (UseTransaction) keeps its actions.
        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            Drop(result);
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Drop(result);
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Run(transaction);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Run(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Drop(transaction);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Drop(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Drop(transaction);

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Drop(transaction);
            return Task.CompletedTask;
        }

        private List<Action>? Take(DbTransaction transaction)
        {
            lock (_pending)
            {
                return _pending.TryGetValue(transaction, out var actions) && _pending.Remove(transaction) ? actions : null;
            }
        }

        private void Drop(DbTransaction transaction) => Take(transaction);

        private void Run(DbTransaction transaction)
        {
            foreach (var action in Take(transaction) ?? [])
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Committed: the request is durable, and the pipeline's poll takes it anyway.
                    logger.LogWarning(ex, "A post-commit action failed: {Message}", ex.Message);
                }
            }
        }
    }
}
