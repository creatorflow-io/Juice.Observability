using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Extensibility;

/// <summary>
/// A module's own DbContext (core 2.0 M2). The module's tables are in its own schema (<see cref="Schema"/>), its migrations history is
/// <c>&lt;schema&gt;.__ef_migrations_history</c>, and at runtime the context shares the request's connection and transaction with the core's
/// (<see cref="IModuleMutation"/> saves it in the same transaction as the audit and idempotency records). Override <c>OnModelCreating</c> and call the base.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Must equal the registering module's name.</summary>
    public abstract string Module { get; }

    public string Schema => Module.Replace('-', '_');

    protected override void OnModelCreating(ModelBuilder model) => model.HasDefaultSchema(Schema);
}

public sealed class ModuleDataOptions
{
    /// <summary>
    /// Tables the module takes over from where they are: only tables of features that moved out of the core (the SLO, load-run, correlation, escalation and
    /// agent tables), or outside the core's model. The module's first migration may hold only operations on these (it is recorded, not run, when they all
    /// exist), and the module never drops or alters them.
    /// </summary>
    public IList<AdoptedTable> Adopts { get; } = [];
}

public sealed record AdoptedTable(string Schema, string Name);

public static class ModuleDataExtensions
{
    /// <summary>
    /// Gives the module a context of its own: schema, history table and migrations at start (adopting <see cref="ModuleDataOptions.Adopts"/> when the
    /// history is empty), and a scoped <typeparamref name="TContext"/> over the request's connection. Refused at composition when
    /// <see cref="ModuleDbContext.Module"/> isn't the module's name, a table is outside the schema and not adopted, or an adopted table is the core's.
    /// </summary>
    public static IModuleServicesBuilder AddModuleData<TContext>(this IModuleServicesBuilder b, Action<ModuleDataOptions>? configure = null) where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(b);
        ModuleHostBridge.Invoke("Juice.Observability.Api.Modules.ModuleData.ModuleDataComposer", "Add", [typeof(TContext)], b, configure);
        return b;
    }
}

/// <summary>
/// How an extension method reaches the host's implementation: the contract assembly sits below the host (the core's DbContext, mediator and
/// module registry are the host's), so a composition call goes through a static method of the host assembly, found by name. Refused when this
/// isn't running in a Juice Observability host.
/// </summary>
internal static class ModuleHostBridge
{
    private const string HostAssembly = "Juice.Observability.Api";

    public static void Invoke(string type, string method, Type[] typeArguments, params object?[] arguments)
    {
        var host = Type.GetType($"{type}, {HostAssembly}", throwOnError: false)
            ?? throw new ModuleCompositionException($"{method} needs the Juice Observability host ({HostAssembly}) to compose a module.");
        var target = host.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new ModuleCompositionException($"{type}.{method} was not found in {HostAssembly}.");
        try
        {
            (typeArguments.Length == 0 ? target : target.MakeGenericMethod(typeArguments)).Invoke(null, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }
}
