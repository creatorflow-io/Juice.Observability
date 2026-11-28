using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Juice.Observability.Tests.Modules;

namespace Juice.Observability.Tests.ModuleData
{
    // The modules of the module-data tests: each has its own context and migration, so a test composes the case it checks.

    public sealed record DataItemRequest(string Name);

    public sealed class DataItem
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>A module with module data: its own schema <c>data_sample</c>, a write endpoint in several flavours of failing.</summary>
    public sealed class DataSampleModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-sample", "Data sample", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<DataSampleContext>();

        public void MapEndpoints(IModuleEndpointsBuilder endpoints)
        {
            // Writes in the transaction, and only then finds the caller isn't an admin: the denial must not commit the write.
            endpoints.Tenant.MapPost("/items-late-deny", async (Guid tenantId, DataItemRequest request, HttpContext http, IModuleMutations mutations, DataSampleContext db, CancellationToken ct) =>
            {
                var mutation = mutations.Begin(http, tenantId, "data-sample.item.create", "data-sample-item");
                await mutation.BeginTransactionAsync(ct);
                db.Items.Add(new DataItem { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = request.Name });
                await db.SaveChangesAsync(ct);
                if (await mutation.RequireAsync(null, ct, CoreRoles.Admin) is { } denied)
                {
                    return denied;
                }
                await mutation.SaveAsync(ct);
                return Results.Ok();
            });
            endpoints.Tenant.MapPost("/items",async (Guid tenantId, string? mode, DataItemRequest request, HttpContext http, IModuleMutations mutations, DataSampleContext db, CancellationToken ct) =>
            {
                var mutation = mutations.Begin(http, tenantId, "data-sample.item.create", "data-sample-item");
                if (await mutation.RequireAsync(null, ct, CoreRoles.Operator, CoreRoles.Admin) is { } denied)
                {
                    return denied;
                }
                // The "no-tx" modes begin none: the save opens its own.
                if (mode is not ("no-tx" or "no-tx-bad-audit"))
                {
                    await mutation.BeginTransactionAsync(ct);
                }
                if (await mutation.ReplayAsync("POST /items", request, ct) is { } replay)
                {
                    return replay;
                }
                var item = new DataItem { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = request.Name };
                db.Items.Add(item);
                if (mode == "throw")
                {
                    // Written inside the transaction, then the handler dies before the audit is saved.
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException("the handler failed after the write");
                }
                mutation.Audit(ModuleAuditOutcome.Succeeded, mode is "bad-audit" or "no-tx-bad-audit" ? new string('x', 5000) : item.Id.ToString());
                var result = mutation.Remember(StatusCodes.Status201Created, new { item.Id, item.Name }, DateTimeOffset.UtcNow);
                await mutation.SaveAsync(ct);
                return result;
            });
        }
    }

    public sealed class DataSampleContext(DbContextOptions<DataSampleContext> options) : ModuleDbContext(options)
    {
        public override string Module => "data-sample";

        public DbSet<DataItem> Items => Set<DataItem>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<DataItem>().ToTable("items");
        }
    }

    [DbContext(typeof(DataSampleContext))]
    [Migration("20261129000000_CreateItems")]
    public sealed class CreateDataItems : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema("data_sample");
            migrationBuilder.CreateTable(name: "items", schema: "data_sample", columns: t => new
            {
                Id = t.Column<Guid>(nullable: false), TenantId = t.Column<Guid>(nullable: false), Name = t.Column<string>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_items", x => x.Id));
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("items", "data_sample");
    }

    // ── Composition refusals ───────────────────────────────────────────────

    /// <summary>The context says another module's name.</summary>
    public sealed class MismatchModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-mismatch", "Mismatch", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<MismatchContext>();
    }

    public sealed class MismatchContext(DbContextOptions<MismatchContext> options) : ModuleDbContext(options)
    {
        public override string Module => "somebody-else";
    }

    /// <summary>A table in the core's schema that the module doesn't adopt.</summary>
    public sealed class StrayModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-stray", "Stray", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<StrayContext>();
    }

    public sealed class StrayContext(DbContextOptions<StrayContext> options) : ModuleDbContext(options)
    {
        public override string Module => "data-stray";

        public DbSet<DataItem> Items => Set<DataItem>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<DataItem>().ToTable("stray", "public");
        }
    }

    /// <summary>Forgets the base: its tables fall into the default schema.</summary>
    public sealed class NoBaseModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-nobase", "No base", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<NoBaseContext>();
    }

    public sealed class NoBaseContext(DbContextOptions<NoBaseContext> options) : ModuleDbContext(options)
    {
        public override string Module => "data-nobase";

        public DbSet<DataItem> Items => Set<DataItem>();

        protected override void OnModelCreating(ModelBuilder model) => model.Entity<DataItem>().ToTable("items");
    }

    /// <summary>Adopts a table of the core that no feature moved out of.</summary>
    public sealed class AdoptCoreTableModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopt-core", "Adopt core", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<AdoptCoreContext>(o => o.Adopts.Add(new AdoptedTable("public", "audit_records")));
    }

    public sealed class AdoptCoreContext(DbContextOptions<AdoptCoreContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopt-core";
    }

    /// <summary>Takes the same context twice.</summary>
    public sealed class TwiceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("data-sample", "Twice", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddModuleData<DataSampleContext>().AddModuleData<DataSampleContext>();
    }

    // ── Alert sources ─────────────────────────────────────────────────────

    public sealed class SourceModule(string template) : IObservabilityModule
    {
        public SourceModule() : this("source-ok.burn")
        {
        }

        public ModuleInfo Info { get; } = new("source-ok", "Source", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource(template);
    }

    public sealed class BuiltinSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-builtin", "Builtin", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("ErrorRate");
    }

    public sealed class TwiceSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-twice", "Twice", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("source-twice.burn").AddAlertSource("source-twice.burn");
    }

    /// <summary>The core's own template is the slos module's.</summary>
    public sealed class SloBurnSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-slo", "Slo", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("SloBurn");
    }

    public sealed class ForeignPrefixSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-foreign", "Foreign", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("somebody-else.burn");
    }

    public sealed class ClashTemplate : IRuleTemplate
    {
        public RuleTemplateInfo Info => new("source-clash.split", "Clash", TemplateSubjectKind.Service);

        public Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TemplateObservation>>([]);
    }

    /// <summary>An alert source for a key the module's own rule template has.</summary>
    public sealed class TemplateThenSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-clash", "Clash", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddRuleTemplate<ClashTemplate>().AddAlertSource("source-clash.split");
    }

    /// <summary>The same, in the other order.</summary>
    public sealed class SourceThenTemplateModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-clash", "Clash", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("source-clash.split").AddRuleTemplate<ClashTemplate>();
    }

    /// <summary>Module keys are lower case, as a rule template's are.</summary>
    public sealed class UpperCaseSourceModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("source-upper", "Upper", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("source-upper.Burn");
    }

    // ── Adoption ──────────────────────────────────────────────────────────

    public sealed class AdoptedRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Adopts the SLO tables of the core's moved features: <c>slos</c> and <c>slo_versions</c>.</summary>
    public sealed class AdopterModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopter", "Adopter", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<AdopterContext>(o =>
            {
                o.Adopts.Add(new AdoptedTable("public", "slos"));
                o.Adopts.Add(new AdoptedTable("public", "slo_versions"));
            });
    }

    public sealed class AdopterContext(DbContextOptions<AdopterContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopter";

        public DbSet<AdoptedRow> Slos => Set<AdoptedRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<AdoptedRow>(e =>
            {
                e.ToTable("slos", "public");
                e.Property(x => x.Id).HasColumnName("id");
                e.Property(x => x.Name).HasColumnName("name");
            });
        }
    }

    [DbContext(typeof(AdopterContext))]
    [Migration("20261129000001_AdoptSlos")]
    public sealed class AdoptSlos : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "slos", schema: "public", columns: t => new
            {
                id = t.Column<Guid>(nullable: false), tenant_id = t.Column<Guid>(nullable: false), key = t.Column<string>(nullable: false), name = t.Column<string>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_slos", x => x.id));
            migrationBuilder.CreateTable(name: "slo_versions", schema: "public", columns: t => new
            {
                id = t.Column<Guid>(nullable: false), slo_id = t.Column<Guid>(nullable: false), version = t.Column<int>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_slo_versions", x => x.id));
        }

        // Adopted tables are never dropped by a module.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    /// <summary>A second migration after the adopted one: runs as usual, so the module's own tables come after adoption.</summary>
    [DbContext(typeof(AdopterContext))]
    [Migration("20261129000002_AdopterOwn")]
    public sealed class AdopterOwn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTable(name: "own", schema: "adopter", columns: t => new { id = t.Column<Guid>(nullable: false) }, constraints: t => t.PrimaryKey("pk_own", x => x.id));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("own", "adopter");
    }

    /// <summary>Adopts two tables outside the core's model: <c>public.adopt_new_a</c> and <c>public.adopt_new_b</c>.</summary>
    public sealed class AdoptNewModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopt-new", "Adopt new", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<AdoptNewContext>(o =>
            {
                o.Adopts.Add(new AdoptedTable("public", "adopt_new_a"));
                o.Adopts.Add(new AdoptedTable("public", "adopt_new_b"));
            });
    }

    public sealed class AdoptNewContext(DbContextOptions<AdoptNewContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopt-new";

        public DbSet<AdoptedRow> Rows => Set<AdoptedRow>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<AdoptedRow>().ToTable("adopt_new_a", "public");
        }
    }

    [DbContext(typeof(AdoptNewContext))]
    [Migration("20261129000010_CreateAdoptNew")]
    public sealed class CreateAdoptNew : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "adopt_new_a", schema: "public", columns: t => new
            {
                Id = t.Column<Guid>(nullable: false), Name = t.Column<string>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_adopt_new_a", x => x.Id));
            migrationBuilder.CreateTable(name: "adopt_new_b", schema: "public", columns: t => new { Id = t.Column<Guid>(nullable: false) },
                constraints: t => t.PrimaryKey("pk_adopt_new_b", x => x.Id));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    /// <summary>Adopts one table, and its first migration also creates one that isn't adopted.</summary>
    public sealed class AdoptOpsModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("adopt-ops", "Adopt ops", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddModuleData<AdoptOpsContext>(o => o.Adopts.Add(new AdoptedTable("public", "adopt_ops_a")));
    }

    public sealed class AdoptOpsContext(DbContextOptions<AdoptOpsContext> options) : ModuleDbContext(options)
    {
        public override string Module => "adopt-ops";
    }

    [DbContext(typeof(AdoptOpsContext))]
    [Migration("20261129000020_AdoptOps")]
    public sealed class AdoptOps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: "adopt_ops_a", schema: "public", columns: t => new { Id = t.Column<Guid>(nullable: false) }, constraints: t => t.PrimaryKey("pk_adopt_ops_a", x => x.Id));
            migrationBuilder.CreateTable(name: "extra", schema: "adopt_ops", columns: t => new { Id = t.Column<Guid>(nullable: false) }, constraints: t => t.PrimaryKey("pk_extra", x => x.Id));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
