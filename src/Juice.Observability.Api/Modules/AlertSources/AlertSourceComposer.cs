using System.Text.RegularExpressions;
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
        [GeneratedRegex("^[a-z][a-z0-9-]*\\.[A-Za-z][A-Za-z0-9._-]{0,38}$", RegexOptions.CultureInvariant)]
        private static partial Regex ModuleTemplate();

        // The ten templates the registry evaluates: every RuleTemplate but SloBurn.
        private static readonly HashSet<string> Registry = Enum.GetNames<RuleTemplate>().Where(n => n != SloBurnTemplate).ToHashSet(StringComparer.Ordinal);

        private const string SloBurnTemplate = EvaluateSloBurnsHandler.Template;

        public static void Add(IModuleServicesBuilder b, string template)
        {
            if (b is not ModuleServicesBuilder)
            {
                throw new ModuleCompositionException("An alert source is declared only through the host's own module builder.");
            }
            var module = b.Module.Name;
            if (string.IsNullOrEmpty(template) || template.Length > 40)
            {
                throw new ModuleCompositionException($"Module '{module}' declares an alert source with no template, or one over 40 characters.");
            }
            if (Registry.Contains(template))
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is a template of the rule registry, evaluated by the rule scheduler.");
            }
            // The core's own burn template belongs to the slos module; any other is the declaring module's, by its prefix.
            var valid = template == SloBurnTemplate ? module == "slos" : ModuleTemplate().IsMatch(template) && template.StartsWith(module + ".", StringComparison.Ordinal);
            if (!valid)
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': a module's template is '{module}.<name>'.");
            }
            if (b.Services.Any(d => d.ServiceType == typeof(IAlertSource) && d.IsKeyedService && d.ServiceKey is string declared && declared.EndsWith(":" + template, StringComparison.Ordinal)))
            {
                throw new ModuleCompositionException($"Module '{module}' can't declare an alert source for '{template}': it is already declared.");
            }
            // The command an alert source sends outside a transaction: internal, so the mediator's assembly scan doesn't find it.
            b.Services.TryAddTransient<Juice.MediatR.IRequestHandler<ApplyAlertSignals, AlertSourceResult>, ApplyAlertSignalsHandler>();
            b.Services.AddKeyedScoped<IAlertSource>(AlertSourceKeys.Key(module, template), (services, key) => new PlatformAlertSource(template, services.GetRequiredService<PlatformDbContext>(), services,
                services.GetRequiredService<IOptions<PlatformOptions>>(), services.GetRequiredService<TimeProvider>()));
        }
    }
}
