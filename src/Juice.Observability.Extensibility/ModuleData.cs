using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Extensibility;

public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Must equal the registering module's name.</summary>
    public abstract string Module { get; }

    public string Schema => Module.Replace('-', '_');

    protected override void OnModelCreating(ModelBuilder model) => model.HasDefaultSchema(Schema);
}

public sealed class ModuleDataOptions
{
    public IList<AdoptedTable> Adopts { get; } = [];
}

public sealed record AdoptedTable(string Schema, string Name);

public static class ModuleDataExtensions
{
    public static IModuleServicesBuilder AddModuleData<TContext>(this IModuleServicesBuilder b, Action<ModuleDataOptions>? configure = null) where TContext : ModuleDbContext
        => throw new NotImplementedException();
}
