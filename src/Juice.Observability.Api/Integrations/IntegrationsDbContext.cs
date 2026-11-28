using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>The <c>integrations</c> module's tables (schema <c>integrations</c>, history <c>integrations.__ef_migrations_history</c>), snake_case by name.</summary>
    internal sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : ModuleDbContext(options)
    {
        public override string Module => "integrations";

        public DbSet<IntegrationInstance> Instances => Set<IntegrationInstance>();

        public DbSet<IntegrationCheckpointRow> Checkpoints => Set<IntegrationCheckpointRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<IntegrationInstance>(e =>
            {
                e.ToTable("integration_instances");
                e.HasKey(i => i.Id);
                e.Property(i => i.PluginKey).HasMaxLength(64);
                e.Property(i => i.Name).HasMaxLength(64);
                // Case-insensitive uniqueness the database itself keeps: a stored generated column and a unique index over it.
                e.Property(i => i.NameKey).HasMaxLength(64).HasComputedColumnSql("lower(name)", stored: true);
                e.Property(i => i.Config).HasColumnType("jsonb");
                e.Property(i => i.SecretRefs).HasColumnType("jsonb");
                e.Property(i => i.Scope).HasColumnType("jsonb");
                e.Property(i => i.LastValidation).HasColumnType("jsonb");
                e.Property(i => i.State).HasConversion<string>().HasMaxLength(20);
                e.Property(i => i.StateReason).HasMaxLength(500);
                e.Property(i => i.ValidatedHash).HasMaxLength(64);
                e.Property(i => i.LastOutcome).HasMaxLength(20);
                e.Property(i => i.LastFailure).HasMaxLength(20);
                e.Property(i => i.LastFailureDetail).HasMaxLength(500);
                e.Property(i => i.Version).IsConcurrencyToken();
                e.Property(i => i.CreatedBy).HasMaxLength(256);
                e.Property(i => i.UpdatedBy).HasMaxLength(256);
                e.HasIndex(i => new { i.TenantId, i.NameKey }).IsUnique();
                e.HasIndex(i => new { i.State, i.NextRunAt });
                e.HasMany(i => i.Checkpoints).WithOne().HasForeignKey(c => c.InstanceId).OnDelete(DeleteBehavior.Cascade);
            });
            model.Entity<IntegrationCheckpointRow>(e =>
            {
                e.ToTable("integration_checkpoints");
                e.HasKey(c => new { c.InstanceId, c.Stream });
                e.Property(c => c.Stream).HasMaxLength(64);
                e.Property(c => c.Cursor).HasMaxLength(1024);
            });
            SnakeCase(model);
        }

        /// <summary>The platform's naming convention, applied by hand: the module's context is built over the request's connection with no naming plugin.</summary>
        private static void SnakeCase(ModelBuilder model)
        {
            foreach (var entity in model.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(Snake(property.Name));
                }
                foreach (var key in entity.GetKeys())
                {
                    key.SetName("pk_" + entity.GetTableName());
                }
                foreach (var index in entity.GetIndexes())
                {
                    index.SetDatabaseName($"ix_{entity.GetTableName()}_{string.Join('_', index.Properties.Select(p => Snake(p.Name)))}");
                }
                foreach (var fk in entity.GetForeignKeys())
                {
                    fk.SetConstraintName($"fk_{entity.GetTableName()}_{fk.PrincipalEntityType.GetTableName()}_{string.Join('_', fk.Properties.Select(p => Snake(p.Name)))}");
                }
            }
        }

        private static string Snake(string name)
        {
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && i > 0)
                {
                    text.Append('_');
                }
                text.Append(char.ToLowerInvariant(name[i]));
            }
            return text.ToString();
        }
    }
}
