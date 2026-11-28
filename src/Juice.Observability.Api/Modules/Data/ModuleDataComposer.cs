using System.Data.Common;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Observability.Api.Modules.ModuleData
{
    /// <summary>What a module declared with <c>AddModuleData</c>: read by the migration runner and by the scoped context's factory.</summary>
    internal sealed record ModuleDataRegistration(string Module, Type ContextType, string Schema, IReadOnlyList<AdoptedTable> Adopts,
        Func<IServiceProvider, DbConnection, ModuleDbContext> Create)
    {
        public const string HistoryTable = "__ef_migrations_history";
    }

    /// <summary>
    /// <c>AddModuleData</c> (reached through <see cref="ModuleDbContext"/>'s extension in the contract assembly): checks the declaration and the context's
    /// model at composition, and registers the module's context scoped over the request's PlatformDbContext connection. Every refusal is a
    /// <see cref="ModuleCompositionException"/>, so a refused module leaves nothing behind.
    /// </summary>
    internal static partial class ModuleDataComposer
    {
        private const string OfflineConnection = "Host=localhost;Database=model_check";

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
        private static partial Regex Identifier();

        // The tables of the core's model: read once, from the model alone (no database).
        private static readonly Lazy<HashSet<(string, string)>> CoreTables = new(() =>
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, OfflineConnection);
            using var db = new PlatformDbContext(options.Options);
            return db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
                .Where(e => e.GetTableName() is not null)
                .Select(e => (e.GetSchema() ?? CoreMovedTables.Schema, e.GetTableName()!)).ToHashSet();
        });

        public static void Add<TContext>(IModuleServicesBuilder b, Action<ModuleDataOptions>? configure) where TContext : ModuleDbContext
        {
            var module = b.Module.Name;
            var options = new ModuleDataOptions();
            configure?.Invoke(options);
            if (b.Services.Any(d => d.ServiceType == typeof(ModuleDataRegistration) && ((ModuleDataRegistration?)d.ImplementationInstance)?.ContextType == typeof(TContext)))
            {
                throw new ModuleCompositionException($"Module '{module}' declares module data for {typeof(TContext).Name} twice.");
            }

            var adopts = Adopts(module, options);
            var schema = Check<TContext>(module, adopts);

            b.Services.AddSingleton(new ModuleDataRegistration(module, typeof(TContext), schema, adopts, Factory<TContext>(schema)));
            b.Services.TryAddScoped<ModuleDataSession>();
            b.Services.AddScoped(sp => (TContext)sp.GetRequiredService<ModuleDataSession>().Create(typeof(TContext)));
        }

        /// <summary>The module's options, copied and checked: names that are identifiers, no duplicates, and nothing of the core's but its moved features' tables.</summary>
        private static List<AdoptedTable> Adopts(string module, ModuleDataOptions options)
        {
            var list = new List<AdoptedTable>();
            foreach (var table in options.Adopts)
            {
                if (table is null || !Identifier().IsMatch(table.Schema ?? "") || !Identifier().IsMatch(table.Name ?? ""))
                {
                    throw new ModuleCompositionException($"Module '{module}' adopts a table whose schema and name must be identifiers (letters, digits, underscore).");
                }
                if (list.Contains(table))
                {
                    throw new ModuleCompositionException($"Module '{module}' adopts {table.Schema}.{table.Name} twice.");
                }
                // The core's own tables are the core's; only what moved out of it is adoptable. Anything else is outside the core's model.
                if (CoreTables.Value.Contains((table.Schema!, table.Name!)) && !CoreMovedTables.All.Contains(table))
                {
                    throw new ModuleCompositionException($"Module '{module}' can't adopt {table.Schema}.{table.Name}: it is a table of the core, not one of a feature that moved to a module.");
                }
                list.Add(table);
            }
            return list;
        }

        /// <summary>Builds the context's model with no database, and refuses a wrong module name or a table outside the module's schema that isn't adopted.</summary>
        private static string Check<TContext>(string module, IReadOnlyList<AdoptedTable> adopts) where TContext : ModuleDbContext
        {
            var builder = new DbContextOptionsBuilder<TContext>().UseNpgsql(OfflineConnection);
            TContext context;
            try
            {
                context = (TContext)(Activator.CreateInstance(typeof(TContext), builder.Options)
                    ?? throw new InvalidOperationException("no instance"));
            }
            catch (Exception e) when (e is MissingMethodException or InvalidOperationException)
            {
                throw new ModuleCompositionException($"Module '{module}': {typeof(TContext).Name} needs a public constructor taking DbContextOptions<{typeof(TContext).Name}> and nothing else.");
            }
            using (context)
            {
                if (!string.Equals(context.Module, module, StringComparison.Ordinal))
                {
                    throw new ModuleCompositionException($"Module '{module}': {typeof(TContext).Name}.Module is '{context.Module}', and must be the name of the module that registers it.");
                }
                var schema = context.Schema;
                foreach (var entity in context.GetService<IDesignTimeModel>().Model.GetEntityTypes())
                {
                    if (entity.GetTableName() is not { } table)
                    {
                        continue;
                    }
                    var own = entity.GetSchema() ?? entity.Model.GetDefaultSchema();
                    if (own != schema && !adopts.Contains(new AdoptedTable(own ?? CoreMovedTables.Schema, table)))
                    {
                        throw new ModuleCompositionException(
                            $"Module '{module}': {entity.DisplayName()} maps to {own ?? CoreMovedTables.Schema}.{table}, outside the module's schema '{schema}' and not one of its adopted tables.");
                    }
                }
                return schema;
            }
        }

        /// <summary>Makes a context of the type over a given connection, with its history table in the module's schema.</summary>
        private static Func<IServiceProvider, DbConnection, ModuleDbContext> Factory<TContext>(string schema) where TContext : ModuleDbContext
            => (services, connection) =>
            {
                var options = new DbContextOptionsBuilder<TContext>()
                    .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(ModuleDataRegistration.HistoryTable, schema)).Options;
                return ActivatorUtilities.CreateInstance<TContext>(services, options);
            };
    }
}
