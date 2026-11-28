using System.Collections.Concurrent;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.EvidenceContributors
{
    public sealed class EvidenceNote
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>The <c>evidence-sample</c> module's own data: schema <c>evidence_sample</c>.</summary>
    public sealed class EvidenceSampleContext(DbContextOptions<EvidenceSampleContext> options) : ModuleDbContext(options)
    {
        public override string Module => "evidence-sample";

        public DbSet<EvidenceNote> Notes => Set<EvidenceNote>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<EvidenceNote>().ToTable("notes");
        }
    }

    [DbContext(typeof(EvidenceSampleContext))]
    [Migration("20261129010000_CreateNotes")]
    public sealed class CreateEvidenceNotes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema("evidence_sample");
            migrationBuilder.CreateTable(name: "notes", schema: "evidence_sample", columns: t => new
            {
                Id = t.Column<Guid>(nullable: false), Text = t.Column<string>(nullable: false),
            }, constraints: t => t.PrimaryKey("pk_notes", x => x.Id));
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("notes", "evidence_sample");
    }

    /// <summary>What a test makes the sample contributors do, by key. Tests of this module run one after another, so the statics are the test's own.</summary>
    public static class EvidenceBehaviors
    {
        public delegate Task<EvidenceSection?> Behavior(EvidenceContext context, EvidenceServices services, CancellationToken ct);

        public static readonly ConcurrentDictionary<string, Behavior> ByKey = new();
        public static readonly ConcurrentQueue<(string Key, EvidenceContext Context)> Seen = new();

        public static void Reset()
        {
            ByKey.Clear();
            Seen.Clear();
        }
    }

    public sealed record EvidenceServices(EvidenceSampleContext Module, PlatformDbContext Core, IModuleMutations Mutations);

    public abstract class SampleContributor(string key, EvidenceTarget target, string? policy, EvidenceSampleContext module, PlatformDbContext core, IModuleMutations mutations) : IEvidenceContributor
    {
        public string Key { get; } = key;

        public EvidenceTarget Target { get; } = target;

        public string? Policy { get; } = policy;

        public Task<EvidenceSection?> ContributeAsync(EvidenceContext context, CancellationToken ct)
        {
            EvidenceBehaviors.Seen.Enqueue((Key, context));
            return EvidenceBehaviors.ByKey.TryGetValue(Key, out var behavior)
                ? behavior(context, new EvidenceServices(module, core, mutations), ct)
                : Task.FromResult<EvidenceSection?>(new EvidenceSection($"Sample {Key}", [new EvidenceItem("note", "value")]));
        }
    }

    public sealed class AlertOne(EvidenceSampleContext m, PlatformDbContext c, IModuleMutations u) : SampleContributor("evidence-sample.one", EvidenceTarget.Alert, null, m, c, u);

    public sealed class AlertTwo(EvidenceSampleContext m, PlatformDbContext c, IModuleMutations u) : SampleContributor("evidence-sample.two", EvidenceTarget.Alert, null, m, c, u);

    public sealed class AlertThree(EvidenceSampleContext m, PlatformDbContext c, IModuleMutations u) : SampleContributor("evidence-sample.three", EvidenceTarget.Alert, null, m, c, u);

    public sealed class AlertAdmin(EvidenceSampleContext m, PlatformDbContext c, IModuleMutations u) : SampleContributor("evidence-sample.admin", EvidenceTarget.Alert, "evidence-sample.admin", m, c, u);

    public sealed class ServiceOne(EvidenceSampleContext m, PlatformDbContext c, IModuleMutations u) : SampleContributor("evidence-sample.service", EvidenceTarget.Service, null, m, c, u);

    /// <summary>The module of the evidence tests: five contributors (the most), its own data, a policy and a Console route their links can point to.</summary>
    public sealed class EvidenceSampleModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("evidence-sample", "Evidence sample", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
        {
            module.AddModuleData<EvidenceSampleContext>().AddPolicy("evidence-sample.admin", CoreRoles.Admin)
                .SetConsoleManifest(new ConsoleManifest("@juice-js/observability-evidence-sample", [new ConsoleRoute("evidence-sample/:id", null)], []));
            module.AddEvidenceContributor<AlertOne>().AddEvidenceContributor<AlertTwo>().AddEvidenceContributor<AlertThree>()
                .AddEvidenceContributor<AlertAdmin>().AddEvidenceContributor<ServiceOne>();
        }
    }

    // ── Modules the host must refuse ───────────────────────────────────────

    public abstract class Refused(string key, string? policy = null) : IEvidenceContributor
    {
        public string Key { get; } = key;

        public EvidenceTarget Target => EvidenceTarget.Alert;

        public string? Policy { get; } = policy;

        public Task<EvidenceSection?> ContributeAsync(EvidenceContext context, CancellationToken ct) => Task.FromResult<EvidenceSection?>(null);
    }

    public sealed class OtherPrefix() : Refused("someone-else.x");

    public sealed class DuplicateA() : Refused("dup-module.a");

    public sealed class DuplicateB() : Refused("dup-module.a");

    public sealed class UnknownPolicy() : Refused("policy-module.x", "policy-module.nothing");

    public sealed class OtherModulesPolicy() : Refused("own-module.x", "evidence-sample.admin");

    public sealed class Cap1() : Refused("cap-module.a");

    public sealed class Cap2() : Refused("cap-module.b");

    public sealed class Cap3() : Refused("cap-module.c");

    public sealed class Cap4() : Refused("cap-module.d");

    public sealed class Cap5() : Refused("cap-module.e");

    public sealed class Cap6() : Refused("cap-module.f");

    public sealed class OtherPrefixModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("prefix-module", "Prefix", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEvidenceContributor<OtherPrefix>();
    }

    public sealed class DuplicateKeyModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("dup-module", "Duplicate", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEvidenceContributor<DuplicateA>().AddEvidenceContributor<DuplicateB>();
    }

    public sealed class UnknownPolicyModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("policy-module", "Policy", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEvidenceContributor<UnknownPolicy>();
    }

    /// <summary>Names a policy another module declared: it isn't the contributor's own module's.</summary>
    public sealed class ForeignPolicyModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("own-module", "Own", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEvidenceContributor<OtherModulesPolicy>();
    }

    public sealed class CapModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("cap-module", "Cap", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEvidenceContributor<Cap1>().AddEvidenceContributor<Cap2>().AddEvidenceContributor<Cap3>()
            .AddEvidenceContributor<Cap4>().AddEvidenceContributor<Cap5>().AddEvidenceContributor<Cap6>();
    }

    public static class HttpContexts
    {
        /// <summary>A request outside any module endpoint: what a contributor has.</summary>
        public static HttpContext Bare() => new DefaultHttpContext();
    }
}
