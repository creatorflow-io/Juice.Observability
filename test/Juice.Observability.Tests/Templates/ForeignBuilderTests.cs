using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Templates
{
    /// <summary>Core 2.0 M2: a rule template or threshold provider is registered in a module's name only through the host's own builder. No database.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ForeignBuilderTests
    {
        private sealed class FakeBuilder : IModuleServicesBuilder
        {
            public ModuleInfo Module { get; } = new("demo", "Demo", "1.0.0", "[2.0.0,3.0.0)");

            public IServiceCollection Services { get; } = new ServiceCollection();

            public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; } = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

            public IModuleServicesBuilder AddWorker<TWorker>() where TWorker : class, Microsoft.Extensions.Hosting.IHostedService => this;

            public IModuleServicesBuilder AddMigrations<TContext>() where TContext : DbContext => this;

            public IModuleServicesBuilder AddPolicy(string name, params string[] roles) => this;

            public IModuleServicesBuilder SetConsoleManifest(ConsoleManifest manifest) => this;
        }

        [Fact]
        public void A_builder_that_is_not_the_hosts_is_refused_for_templates_and_providers()
        {
            var fake = new FakeBuilder();

            Assert.Contains("not the host's module builder",
                Assert.Throws<ModuleCompositionException>(() => fake.AddRuleTemplate<TemplateRegistryTests.DemoTemplate>()).Message);
            Assert.Contains("not the host's module builder",
                Assert.Throws<ModuleCompositionException>(() => fake.AddThresholdProvider<TemplateRegistryTests.DemoProvider>()).Message);
            Assert.Empty(fake.Services);
        }
    }
}
