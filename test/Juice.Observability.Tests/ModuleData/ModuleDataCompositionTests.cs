using Juice.Observability.Api;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.ModuleData
{
    /// <summary>What <c>AddModuleData</c> and <c>AddAlertSource</c> refuse at composition, and that a refusal leaves the host as it was. No database.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ModuleDataCompositionTests
    {
        private static WebApplicationBuilder Core()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        private static ModuleCompositionException Refused<T>() where T : IObservabilityModule, new()
        {
            var builder = Core();
            var before = builder.Services.Count;
            var refused = Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<T>());
            Assert.Equal(before, builder.Services.Count);
            return refused;
        }

        [Fact]
        public void Context_module_mismatch_refused()
        {
            var e = Refused<MismatchModule>();
            Assert.Contains("somebody-else", e.Message);
            Assert.Contains("data-mismatch", e.Message);
        }

        [Fact]
        public void Model_table_outside_schema_or_adopts_refused()
        {
            Assert.Contains("public.stray", Refused<StrayModule>().Message);
            // Forgetting the base call puts the table in the default schema, which is the core's.
            Assert.Contains("public.items", Refused<NoBaseModule>().Message);
        }

        [Fact]
        public void Adopting_core_table_not_moved_refused() => Assert.Contains("audit_records", Refused<AdoptCoreTableModule>().Message);

        [Theory]
        [InlineData("Juice.Observability.Extensibility.IModuleDataComposer")]
        [InlineData("Juice.Observability.Extensibility.IAlertSourceComposer")]
        [InlineData("Juice.Observability.Extensibility.HostComposers")]
        public void Host_composition_seams_are_not_visible_to_a_module_assembly(string name)
        {
            // Internal to the contract assembly (visible to the host and the tests by InternalsVisibleTo only): a module can't name them to compose around the checks.
            var type = typeof(IObservabilityModule).Assembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type.IsVisible);
            Assert.DoesNotContain(typeof(IObservabilityModule).Assembly.GetExportedTypes(), t => t.Name.EndsWith("Composer", StringComparison.Ordinal));
            Assert.DoesNotContain(typeof(ModuleCompositionException).Assembly.GetExportedTypes(), t => t.Name == "ModuleHostBridge");
        }

        [Fact]
        public void Module_data_twice_refused() => Assert.Contains("twice", Refused<TwiceModule>().Message);

        [Fact]
        public void Valid_module_data_and_adopting_moved_tables_compose()
        {
            var builder = Core();
            builder.AddObservabilityModule<DataSampleModule>();
            builder.AddObservabilityModule<AdopterModule>();
        }

        [Fact]
        public void AddAlertSource_builtin_or_twice_refused()
        {
            Assert.Contains("registry", Refused<BuiltinSourceModule>().Message);
            Assert.Contains("already declared", Refused<TwiceSourceModule>().Message);
            // SloBurn is declared by the slos module, once per host.
            Assert.Contains("SloBurn", Refused<SloBurnSourceModule>().Message);
            // Another module's prefix is not its own.
            Assert.Contains("source-foreign.<name>", Refused<ForeignPrefixSourceModule>().Message);
            Core().AddObservabilityModule<SourceModule>();
        }
    }
}
