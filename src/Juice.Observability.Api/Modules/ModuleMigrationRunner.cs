using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// Migrates the contexts the modules declared, in module order, when <c>Database:Migrate</c> is true. Registered with the first external module, so it
    /// starts after the core's and the notifications' migrations and before any module's worker. A failure stops the host. EF's migration lock
    /// keeps two replicas starting together from migrating twice. (The module's own history table and adoption of an existing schema are M2.)
    /// </summary>
    internal sealed class ModuleMigrationRunner(IServiceScopeFactory scopes, ModuleRegistry registry, IConfiguration configuration, ILogger<ModuleMigrationRunner> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!configuration.GetValue("Database:Migrate", false))
            {
                return;
            }
            foreach (var entry in registry.Entries)
            {
                foreach (var type in entry.Migrations)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = (DbContext)scope.ServiceProvider.GetRequiredService(type);
                    logger.LogInformation("Migrating {Context} of module {Module}", type.Name, entry.Info.Name);
                    await db.Database.MigrateAsync(cancellationToken);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
