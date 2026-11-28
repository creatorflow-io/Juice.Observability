using System.Data.Common;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Juice.Observability.Api.Modules.ModuleData
{
    /// <summary>
    /// The module contexts of one scope, over the scope's PlatformDbContext connection: they join its transaction (the ones already created when it
    /// begins, and any created after), and <see cref="IModuleMutation.SaveAsync"/> saves them with the core's records in it. A connection is shared, never
    /// opened by two at once: a request's contexts run one command at a time.
    /// </summary>
    internal sealed class ModuleDataSession(IServiceProvider services, PlatformDbContext core)
    {
        private readonly List<DbContext> _contexts = [];

        public ModuleDbContext Create(Type contextType)
        {
            var registration = services.GetServices<ModuleDataRegistration>().Single(r => r.ContextType == contextType);
            var context = registration.Create(services, core.Database.GetDbConnection());
            if (core.Database.CurrentTransaction is { } open)
            {
                context.Database.UseTransaction(open.GetDbTransaction());
            }
            lock (_contexts)
            {
                _contexts.Add(context);
            }
            return context;
        }

        private DbContext[] Snapshot()
        {
            lock (_contexts)
            {
                return [.. _contexts];
            }
        }

        /// <summary>Puts every context created so far in the core's open transaction.</summary>
        public async Task EnlistAsync(DbTransaction transaction, CancellationToken ct)
        {
            foreach (var context in Snapshot())
            {
                await context.Database.UseTransactionAsync(transaction, ct);
            }
        }

        /// <summary>Takes them out again once the transaction ended: a context holding a finished transaction can't run another command.</summary>
        public async Task ReleaseAsync(CancellationToken ct)
        {
            foreach (var context in Snapshot())
            {
                await context.Database.UseTransactionAsync(null, ct);
            }
        }

        public async Task SaveAsync(CancellationToken ct)
        {
            foreach (var context in Snapshot())
            {
                await context.SaveChangesAsync(ct);
            }
        }
    }
}
