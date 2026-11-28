using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests.ModuleData
{
    // Schemas, mappings and migrations a module may not have (review round 1 of the module data slice).

    public sealed class AuditRow
    {
        public Guid Id { get; set; }
    }

    /// <summary>Named "public", it would own the core's schema, and its history table would be the core's.</summary>
    public sealed class PublicSchemaModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("public", "Public", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<PublicSchemaContext>();
    }

    public sealed class PublicSchemaContext(DbContextOptions<PublicSchemaContext> options) : ModuleDbContext(options)
    {
        public override string Module => "public";

        public DbSet<AuditRow> Audit => Set<AuditRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<AuditRow>().ToTable("audit_records");
        }
    }

    /// <summary>Adopts a table of the embedded notification pipeline's schema.</summary>
    public sealed class AdoptNotificationsModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopt-notif", "Adopt notifications", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<AdoptNotifContext>(o => o.Adopts.Add(new AdoptedTable("notifications", "correlations")));
    }

    public sealed class AdoptNotifContext(DbContextOptions<AdoptNotifContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopt-notif";
    }

    /// <summary>Reads the core's table through a view mapping.</summary>
    public sealed class ViewModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-view", "View", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<ViewContext>();
    }

    public sealed class ViewContext(DbContextOptions<ViewContext> options) : ModuleDbContext(options)
    {
        public override string Module => "data-view";

        public DbSet<AuditRow> Audit => Set<AuditRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<AuditRow>().HasNoKey().ToView("audit_records", "public");
        }
    }

    /// <summary>Reads a table through a SQL query mapping.</summary>
    public sealed class QueryModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-query", "Query", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<QueryContext>();
    }

    public sealed class QueryContext(DbContextOptions<QueryContext> options) : ModuleDbContext(options)
    {
        public override string Module => "data-query";

        public DbSet<AuditRow> Audit => Set<AuditRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<AuditRow>().HasNoKey().ToSqlQuery("SELECT id FROM audit_records");
        }
    }

    /// <summary>Adopts a table in its first migration, and a later migration drops it.</summary>
    public sealed class DropAdoptedModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopt-drop", "Adopt drop", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<DropAdoptedContext>(o => o.Adopts.Add(new AdoptedTable("public", "adopt_drop_a")));
    }

    public sealed class DropAdoptedContext(DbContextOptions<DropAdoptedContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopt-drop";
    }

    [DbContext(typeof(DropAdoptedContext))]
    [Migration("20261129000030_CreateAdoptDrop")]
    public sealed class CreateAdoptDrop : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(name: "adopt_drop_a", schema: "public", columns: t => new { Id = t.Column<Guid>(nullable: false) }, constraints: t => t.PrimaryKey("pk_adopt_drop_a", x => x.Id));

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    [DbContext(typeof(DropAdoptedContext))]
    [Migration("20261129000031_DropAdoptDrop")]
    public sealed class DropAdoptDrop : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("adopt_drop_a", "public");

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    /// <summary>A migration that alters a table of the core.</summary>
    public sealed class CoreTouchModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("core-touch", "Core touch", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<CoreTouchContext>();
    }

    public sealed class CoreTouchContext(DbContextOptions<CoreTouchContext> options) : ModuleDbContext(options)
    {
        public override string Module => "core-touch";
    }

    [DbContext(typeof(CoreTouchContext))]
    [Migration("20261129000040_TouchCore")]
    public sealed class TouchCore : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(name: "injected", schema: "public", table: "audit_records", nullable: true);

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    /// <summary>A stand-in for the host's builder that reports another module's name.</summary>
    public sealed class FakeBuilder(IServiceCollection services, string name) : IModuleServicesBuilder
    {
        public ModuleInfo Module { get; } = new(name, name, "1.0.0", "[2.0.0,3.0.0)");

        public IServiceCollection Services { get; } = services;

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();

        public IModuleServicesBuilder AddWorker<TWorker>() where TWorker : class, IHostedService => this;

        public IModuleServicesBuilder AddMigrations<TContext>() where TContext : DbContext => this;

        public IModuleServicesBuilder AddPolicy(string policy, params string[] roles) => this;

        public IModuleServicesBuilder SetConsoleManifest(ConsoleManifest manifest) => this;
    }
}
