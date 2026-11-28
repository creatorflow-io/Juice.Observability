using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Api = Juice.Observability.Api;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>The module of the tests: <c>sample</c>, with its own context and migration, a worker, a policy and three routes.</summary>
    public class TestModule : IObservabilityModule
    {
        /// <summary>Counts handler calls: a refused request never reaches it.</summary>
        public static int Handled;

        public static readonly string Schema = "sample";

        public virtual ModuleInfo Info { get; } = new("sample", "Sample", "1.0.0", "[2.0.0,3.0.0)");

        public virtual void ConfigureServices(IModuleServicesBuilder module)
        {
            var connection = module.Configuration.GetConnectionString("Platform");
            module.Services.AddDbContext<SampleDbContext>(o => o.UseNpgsql(connection, n => n.MigrationsHistoryTable("history", Schema)));
            module.AddMigrations<SampleDbContext>().AddPolicy("sample.write", CoreRoles.Operator, CoreRoles.Admin).AddWorker<SampleWorker>();
        }

        public virtual void MapEndpoints(IModuleEndpointsBuilder endpoints)
        {
            endpoints.Tenant.MapGet("/items", (Guid tenantId) =>
            {
                Interlocked.Increment(ref Handled);
                return Results.Ok(new { tenantId });
            });
            endpoints.Tenant.MapGet("/write-only", () =>
            {
                Interlocked.Increment(ref Handled);
                return Results.Ok();
            }).RequireAuthorization("sample.write");
            endpoints.Tenant.MapPost("/items", async (Guid tenantId, ItemRequest request, HttpContext http, IModuleMutations mutations, SampleDbContext db, CancellationToken ct) =>
            {
                Interlocked.Increment(ref Handled);
                var mutation = mutations.Begin(http, tenantId, "sample.item.create", "sample-item");
                if (await mutation.RequireAsync(null, ct, CoreRoles.Operator, CoreRoles.Admin) is { } denied)
                {
                    return denied;
                }
                await mutation.BeginTransactionAsync(ct);
                if (await mutation.ReplayAsync("POST /items", request, ct) is { } replay)
                {
                    return replay;
                }
                var item = new SampleItem { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = request.Name };
                db.Items.Add(item);
                await db.SaveChangesAsync(ct);
                mutation.Audit(ModuleAuditOutcome.Succeeded, item.Id.ToString(), new Dictionary<string, string> { ["name"] = request.Name });
                var result = mutation.Remember(StatusCodes.Status201Created, new { item.Id, item.Name }, DateTimeOffset.UtcNow);
                await mutation.SaveAsync(ct);
                return result;
            });
            // The two ways a module gets the tenant wrong: another tenant's id, and an operation outside its own prefix.
            endpoints.Tenant.MapPost("/wrong-tenant", (HttpContext http, IModuleMutations mutations) => mutations.Begin(http, Guid.NewGuid(), "sample.x", "t"));
            endpoints.Tenant.MapPost("/unprefixed", (Guid tenantId, HttpContext http, IModuleMutations mutations) => mutations.Begin(http, tenantId, "other.x", "t"));
        }
    }

    public sealed record ItemRequest(string Name);

    public sealed class SampleItem
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
    {
        public DbSet<SampleItem> Items => Set<SampleItem>();

        protected override void OnModelCreating(ModelBuilder model)
            => model.HasDefaultSchema(TestModule.Schema).Entity<SampleItem>().ToTable("items");
    }

    [DbContext(typeof(SampleDbContext))]
    [Migration("20261128000000_Create")]
    public sealed class CreateSample : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(TestModule.Schema);
            migrationBuilder.CreateTable(name: "items", schema: TestModule.Schema, columns: t => new
            {
                Id = t.Column<Guid>(nullable: false), TenantId = t.Column<Guid>(nullable: false), Name = t.Column<string>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_items", x => x.Id));
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("items", TestModule.Schema);
    }

    /// <summary>Records that it started, and whether the module's table existed then.</summary>
    public sealed class SampleWorker(IServiceScopeFactory scopes) : IHostedService
    {
        public static volatile string? Started;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Items.CountAsync(cancellationToken);
                Started = "table-present";
            }
            catch (Exception e)
            {
                Started = "table-missing: " + e.Message;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A module whose migration throws.</summary>
    public sealed class FailingModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("failing", "Failing", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            module.Services.AddDbContext<FailingDbContext>(o => o.UseNpgsql(module.Configuration.GetConnectionString("Platform")));
            module.AddMigrations<FailingDbContext>();
        }

        public override void MapEndpoints(IModuleEndpointsBuilder endpoints)
        {
        }
    }

    public sealed class FailingDbContext(DbContextOptions<FailingDbContext> options) : DbContext(options);

    [DbContext(typeof(FailingDbContext))]
    [Migration("20261128000001_Fail")]
    public sealed class FailMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1/0");
    }

    /// <summary>Takes whatever name a test sets first (tests of one class don't run in parallel).</summary>
    public sealed class NamedModule : TestModule
    {
        public static string Name = "named";

        public override ModuleInfo Info { get; } = new(Name, "Named", "1.0.0", "[2.0.0,3.0.0)");
    }

    public sealed class OutOfRangeModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("future", "Future", "1.0.0", "[9.0.0,10.0.0)");
    }

    public sealed class UnparseableRangeModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("odd", "Odd", "1.0.0", "two to three");
    }

    public sealed class AlertsModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("alerts", "Alerts", "1.0.0", "[2.0.0,3.0.0)");
    }

    public sealed class TelemetryModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("telemetry", "Telemetry", "1.0.0", "[2.0.0,3.0.0)");
    }

    public sealed class SlosNamedModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("slos", "Slos", "1.0.0", "[2.0.0,3.0.0)");
    }

    /// <summary>Named <c>platform</c> in the hope of reaching <c>platform.read</c> through the prefix rule.</summary>
    public sealed class PlatformPolicyModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("platform", "Platform", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module) => module.AddPolicy("platform.read", CoreRoles.Viewer);
    }

    /// <summary>A policy named for the module, but one the core already has under another module's name.</summary>
    public sealed class PolicyCollisionModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("collide", "Collide", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module) => module.AddPolicy("sample.write", CoreRoles.Viewer);
    }

    /// <summary>Fails after it has registered a service and a policy.</summary>
    public sealed class HalfwayModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("halfway", "Halfway", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            module.Services.AddSingleton<HalfwayMarker>();
            module.AddWorker<SampleWorker>().AddMigrations<SampleDbContext>();
            module.AddPolicy("other.x", CoreRoles.Viewer);
        }
    }

    public sealed class HalfwayMarker;

    /// <summary>Replaces and removes core registrations, then fails: a refusal must put every one back.</summary>
    public sealed class ReplacingModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("replacing", "Replacing", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            module.Services.Replace(ServiceDescriptor.Singleton<Api.HeartbeatWriter>(_ => throw new InvalidOperationException("replaced")));
            module.Services.RemoveAll<Api.PlatformStatusService>();
            module.AddPolicy("other.x", CoreRoles.Viewer);
        }
    }

    public sealed class AnonymousModule : TestModule
    {
        public override ModuleInfo Info { get; } = new("anon", "Anon", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module)
        {
        }

        public override void MapEndpoints(IModuleEndpointsBuilder endpoints) => endpoints.Tenant.MapGet("/open", () => Results.Ok()).AllowAnonymous();
    }
}
