using Juice.Observability.Api.Modules.ModuleData;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// Migrates the contexts the modules declared, in module order, when <c>Database:Migrate</c> is true. Registered with the first external module, so it
    /// starts after the core's and the notifications' migrations and before any module's worker. A failure stops the host.
    /// <list type="bullet">
    /// <item><c>AddMigrations</c> contexts (M1): <c>Database.MigrateAsync</c>, EF's migration lock keeping two replicas from migrating twice.</item>
    /// <item><c>AddModuleData</c> contexts (M2): on their own connection under the session advisory lock <c>module-migrations:&lt;module&gt;</c>, adopting
    /// existing tables when the module declares any (<see cref="ModuleMigrator"/>). With <c>Database:Migrate</c> false, a pending migration of one stops the
    /// host, naming the module and the migration: the module's code is ahead of its schema.</item>
    /// </list>
    /// </summary>
    internal sealed class ModuleMigrationRunner(IServiceScopeFactory scopes, ModuleRegistry registry, IConfiguration configuration, ILogger<ModuleMigrationRunner> logger,
        IEnumerable<ModuleDataRegistration> data) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var migrate = configuration.GetValue("Database:Migrate", false);
            var registrations = data.ToList();
            if (!migrate && registrations.Count == 0)
            {
                return;
            }
            var connectionString = configuration.GetConnectionString("Platform") ?? throw new InvalidOperationException("ConnectionStrings:Platform is required");
            foreach (var entry in registry.Entries)
            {
                if (migrate)
                {
                    foreach (var type in entry.Migrations)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = (DbContext)scope.ServiceProvider.GetRequiredService(type);
                        logger.LogInformation("Migrating {Context} of module {Module}", type.Name, entry.Info.Name);
                        await db.Database.MigrateAsync(cancellationToken);
                    }
                }
                foreach (var registration in registrations.Where(r => r.Module == entry.Info.Name))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (migrate)
                    {
                        logger.LogInformation("Migrating {Context} of module {Module}", registration.ContextType.Name, registration.Module);
                        await ModuleMigrator.MigrateAsync(registration, scope.ServiceProvider, connectionString, logger, cancellationToken);
                    }
                    else if (await ModuleMigrator.FirstPendingAsync(registration, scope.ServiceProvider, connectionString, cancellationToken) is { } pending)
                    {
                        throw new ModuleMigrationException(
                            $"Module '{registration.Module}' has a migration that is not applied ({pending}) and Database:Migrate is false: apply the module's migrations, or set Database:Migrate.");
                    }
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
