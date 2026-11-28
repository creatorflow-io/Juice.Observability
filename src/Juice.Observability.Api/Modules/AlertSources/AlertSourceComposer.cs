using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Slos;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Modules.AlertSources
{
    /// <summary>The host's <see cref="IAlertSourceComposer"/>, registered as an instance before any module composes.</summary>
    internal sealed class AlertSourceComposerService : IAlertSourceComposer
    {
        public void AddAlertSource(IModuleServicesBuilder b, string template) => AlertSourceComposer.Add(b, template);
    }

    /// <summary>
    /// <c>AddAlertSource</c> (reached through the contract assembly's extension): declares a template's alert source and registers it as a keyed scoped
    /// <see cref="IAlertSource"/>. A template is declared once per host, is never a registry template (the ten built-ins evaluate through the rule
    /// scheduler), and is either the core's <c>SloBurn</c>, declared by the slos module, or <c>&lt;module&gt;.&lt;name&gt;</c> of the declaring module.
    /// </summary>
    internal static partial class AlertSourceComposer
    {
        // The ten templates the registry evaluates: every RuleTemplate but SloBurn.
        private static readonly HashSet<string> Registry = Enum.GetNames<RuleTemplate>().Where(n => n != SloBurnTemplate).ToHashSet(StringComparer.Ordinal);

        private const string SloBurnTemplate = EvaluateSloBurnsHandler.Template;

        public static void Add(IModuleServicesBuilder b, string template)
        {
            CoreBuilders.Require(b);
            var module = b.Module.Name;
            if (string.IsNullOrEmpty(template) || template.Length > 40)
            {
                throw new ModuleCompositionException($"Module '{module}' declares an alert source with no template, or one over 40 characters.");
            }
            if (Registry.Contains(template))
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is a template of the rule registry, evaluated by the rule scheduler.");
            }
            // The core's own burn template belongs to the slos module (its casing is the built-in's); any other is the declaring module's, by the same key
            // rules as a module's rule template (RuleTemplateKeys).
            if (template == SloBurnTemplate)
            {
                if (module != "slos")
                {
                    throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is the slos module's.");
                }
            }
            else if (RuleTemplateKeys.Error(module, template) is { } invalid)
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': {invalid}.");
            }
            // A module's rule template is a registry template too: the key is the template's, not an alert source's.
            if (b.Services.Any(d => d.ServiceType == typeof(ModuleRuleContribution) && d.ImplementationInstance is ModuleRuleContribution c
                && typeof(IRuleTemplate).IsAssignableFrom(c.Type) && c.Type.GetConstructor(Type.EmptyTypes) is not null
                && ((IRuleTemplate)Activator.CreateInstance(c.Type)!).Info.Key == template))
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is a rule template of the registry.");
            }
            if (b.Services.Any(d => d.ServiceType == typeof(IAlertSource) && d.IsKeyedService && d.ServiceKey is string declared && declared.EndsWith(":" + template, StringComparison.Ordinal)))
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is already declared.");
            }
            // For the registry, which refuses a rule template with the same key whichever way the template is built.
            b.Services.AddSingleton(new ModuleAlertSourceDeclaration(module, template));
            // The command an alert source sends outside a transaction: internal, so the mediator's assembly scan doesn't find it.
            b.Services.TryAddTransient<Juice.MediatR.IRequestHandler<ApplyAlertSignals, AlertSourceResult>, ApplyAlertSignalsHandler>();
            b.Services.AddKeyedScoped<IAlertSource>(AlertSourceKeys.Key(module, template), (services, key) => new PlatformAlertSource(template, services.GetRequiredService<PlatformDbContext>(), services,
                services.GetRequiredService<IOptions<PlatformOptions>>(), services.GetRequiredService<TimeProvider>()));
        }
    }
}
