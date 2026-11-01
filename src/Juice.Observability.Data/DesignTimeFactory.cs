using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Juice.Observability.Data
{
    /// <summary>For <c>dotnet ef migrations</c>: the model only, no live database needed.</summary>
    internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, "Host=localhost;Database=design_time");
            return new PlatformDbContext(options.Options);
        }
    }
}
