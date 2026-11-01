using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Juice.Observability.Data
{
    /// <summary>
    /// The platform's PostgreSQL database. Every tenant-owned table carries <c>tenant_id</c>, and every natural-key
    /// uniqueness constraint includes it, so records of two tenants never merge (requirements §6).
    /// </summary>
    public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
    {
        public DbSet<PlatformTenant> Tenants => Set<PlatformTenant>();
        public DbSet<Application> Applications => Set<Application>();
        public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
        public DbSet<Service> Services => Set<Service>();
        public DbSet<ServiceInstance> Instances => Set<ServiceInstance>();
        public DbSet<Resource> Resources => Set<Resource>();
        public DbSet<Dependency> Dependencies => Set<Dependency>();
        public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
        public DbSet<ReconciliationCheckpoint> Checkpoints => Set<ReconciliationCheckpoint>();

        /// <summary>Applies the snake_case naming used by every table. Call it when configuring the context.</summary>
        public static void Configure(DbContextOptionsBuilder options, string connectionString)
            => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<PlatformTenant>(e =>
            {
                e.ToTable("platform_tenants");
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.DisplayName).HasMaxLength(200);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => x.Key).IsUnique();
            });

            model.Entity<Application>(e =>
            {
                e.ToTable("applications");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(256);
                e.Property(x => x.DisplayName).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<DeploymentEnvironment>(e =>
            {
                e.ToTable("environments");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(256);
                e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<Service>(e =>
            {
                e.ToTable("services");
                Tenant(e);
                e.Property(x => x.Namespace).HasMaxLength(256);
                e.Property(x => x.Name).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.Namespace, x.Name }).IsUnique();
                e.HasOne<Application>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.SetNull);
            });

            model.Entity<ServiceInstance>(e =>
            {
                e.ToTable("service_instances");
                Tenant(e);
                e.Property(x => x.InstanceKey).HasMaxLength(256);
                e.Property(x => x.KeySource).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.ServiceVersion).HasMaxLength(256);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.ServiceId, x.EnvironmentId, x.InstanceKey }).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.State, x.LastSeen });
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.HostResourceId).OnDelete(DeleteBehavior.SetNull);
            });

            model.Entity<Resource>(e =>
            {
                e.ToTable("resources");
                Tenant(e);
                e.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
                e.Property(x => x.Key).HasMaxLength(1024);
                e.Property(x => x.DisplayName).HasMaxLength(256);
                Json(e.Property(x => x.Attributes));
                e.HasIndex(x => new { x.TenantId, x.Type, x.Key }).IsUnique();
            });

            model.Entity<Dependency>(e =>
            {
                e.ToTable("dependencies", t => t.HasCheckConstraint("ck_dependencies_one_target",
                    "(target_service_id IS NULL) <> (target_resource_id IS NULL)"));
                Tenant(e);
                e.Property(x => x.Relation).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.EvidenceSource).HasMaxLength(40);
                e.HasIndex(x => new { x.TenantId, x.EnvironmentId, x.SourceServiceId, x.Relation, x.TargetServiceId, x.TargetResourceId })
                    .IsUnique().AreNullsDistinct(false);
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.SourceServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.TargetServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.TargetResourceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<AuditRecord>(e =>
            {
                e.ToTable("audit_records");
                Tenant(e);
                e.Property(x => x.Actor).HasMaxLength(256);
                e.Property(x => x.Operation).HasMaxLength(100);
                e.Property(x => x.TargetType).HasMaxLength(100);
                e.Property(x => x.TargetId).HasMaxLength(256);
                e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CorrelationId).HasMaxLength(128);
                Json(e.Property(x => x.Metadata));
                e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            });

            model.Entity<ReconciliationCheckpoint>(e =>
            {
                e.ToTable("reconciliation_checkpoints");
                e.HasKey(x => new { x.TenantId, x.Source });
                e.Property(x => x.Source).HasMaxLength(100);
                e.HasOne<PlatformTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void Tenant<T>(EntityTypeBuilder<T> e) where T : class
            => e.HasOne<PlatformTenant>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);

        private static readonly JsonSerializerOptions JsonOptions = new();

        private static void Json(PropertyBuilder<Dictionary<string, string>> property)
            => property.HasColumnType("jsonb").HasConversion(new ValueConverter<Dictionary<string, string>, string>(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions) ?? new()),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
    }
}
