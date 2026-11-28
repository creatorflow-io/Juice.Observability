using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>For <c>dotnet ef migrations</c>: the model only, no live database needed (the history table is the module's, as at runtime).</summary>
    internal sealed class IntegrationsDesignTimeFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
    {
        public IntegrationsDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
                .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "integrations"));
            return new IntegrationsDbContext(options.Options);
        }
    }
}
