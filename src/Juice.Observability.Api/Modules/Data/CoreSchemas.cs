using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Juice.Observability.Api.Modules.ModuleData
{
    /// <summary>
    /// What belongs to the core and the database, never to a module: the schemas of the platform's model, of the embedded notification pipeline
    /// (<c>notifications</c>, <c>inbox</c>) and of PostgreSQL itself, and every table of the platform's model. Read once, from the model alone.
    /// </summary>
    internal static class CoreSchemas
    {
        private const string OfflineConnection = "Host=localhost;Database=model_check";

        // Schemas of the products the host embeds: their migrations and history tables are theirs.
        private static readonly string[] Embedded = ["public", "information_schema", "notifications", "inbox"];

        private static readonly Lazy<(HashSet<(string, string)> Tables, HashSet<string> Schemas)> Model = new(() =>
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, OfflineConnection);
            using var db = new PlatformDbContext(options.Options);
            var model = db.GetService<IDesignTimeModel>().Model;
            var tables = model.GetEntityTypes().Where(e => e.GetTableName() is not null)
                .Select(e => (e.GetSchema() ?? CoreMovedTables.Schema, e.GetTableName()!)).ToHashSet();
            return (tables, tables.Select(t => t.Item1).ToHashSet());
        });

        /// <summary>Every table of the platform's model, as (schema, name).</summary>
        public static HashSet<(string, string)> Tables => Model.Value.Tables;

        /// <summary>True for a schema a module can't own: the core's, an embedded product's, or PostgreSQL's (<c>pg_*</c>).</summary>
        public static bool IsCoreSchema(string schema)
            => Embedded.Contains(schema, StringComparer.OrdinalIgnoreCase) || schema.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) || Model.Value.Schemas.Contains(schema);
    }
}
