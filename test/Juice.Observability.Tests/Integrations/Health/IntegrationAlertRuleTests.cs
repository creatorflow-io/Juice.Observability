using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>The integration alert's template and rule key are the platform's, declared only by the integrations module (decision 8). No database.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class IntegrationAlertRuleTests
    {
        private sealed class IntegrationHealthByOther : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("integ-other", "Other", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module) => module.AddAlertSource("IntegrationHealth");
        }

        [Fact]
        public void IntegrationHealth_rule_refused_by_validate_and_reserved_key()
        {
            var rule = new RuleDefinition { Key = "mine", Name = "Mine", Template = RuleTemplate.IntegrationHealth, Threshold = 1 };
            Assert.Contains(rule.Validate(), e => e.Contains("platform's own", StringComparison.Ordinal));
            // Not looked up in the registry, so no "unknown Template" on top.
            Assert.DoesNotContain(rule.Validate(), e => e.Contains("unknown Template", StringComparison.Ordinal));

            Assert.True(RuleDefinition.IsReservedKey(IntegrationHealthRule.Key));
            Assert.Equal("integration-health", IntegrationHealthRule.Key);
            Assert.Contains("integration alerts", RuleDefinition.ReservedKeyError(IntegrationHealthRule.Key));
            Assert.Contains("SLO burn", RuleDefinition.ReservedKeyError(SloBurnRules.FastBurn));
            Assert.All(SloBurnRules.Keys, k => Assert.True(RuleDefinition.IsReservedKey(k)));
            Assert.False(RuleDefinition.IsReservedKey("integration-health-2"));
        }

        [Fact]
        public void Template_value_is_appended_and_keys_are_stable()
        {
            var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
            Assert.Equal("IntegrationHealth", Enum.GetNames<RuleTemplate>()[^1]);
            Assert.Equal("Integration", Enum.GetNames<HealthSubject>()[^1]);
            Assert.Equal("integration:0123456789abcdef0123456789abcdef", IntegrationHealthRule.SubjectKey(id));
            Assert.Equal("integration-health|integration:0123456789abcdef0123456789abcdef", IntegrationHealthRule.DedupKey(id));
        }

        [Fact]
        public void AddAlertSource_IntegrationHealth_only_by_integrations()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            var before = builder.Services.Count;
            var refused = Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<IntegrationHealthByOther>());
            Assert.Contains("IntegrationHealth", refused.Message);
            Assert.Equal(before, builder.Services.Count);
        }
    }
}
