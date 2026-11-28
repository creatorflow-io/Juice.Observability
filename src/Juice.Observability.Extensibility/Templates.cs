using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Extensibility;

/// <summary>A rule template: the platform-owned query that observes one value per subject. Module templates use the key <c>&lt;module&gt;.&lt;name&gt;</c>.</summary>
public interface IRuleTemplate
{
    RuleTemplateInfo Info { get; }

    /// <summary>Template-specific errors only; the generic checks stay in the core's rule validation.</summary>
    IEnumerable<string> Validate(RuleSpec rule) => [];

    Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct);
}

public sealed record RuleTemplateInfo(string Key, string DisplayName, TemplateSubjectKind Subject, bool FiresAbove = true, bool UsesSamples = false, string? ThresholdUnit = null);

public enum TemplateSubjectKind { Service, Host, Either }

public sealed record RuleSpec(string Key, string Template, string Service, string Environment, string Host, TimeSpan Window,
    double Threshold, double RecoveryThreshold, int MinimumSamples, JsonElement Definition);

public sealed record TemplateWindow(DateTimeOffset From, DateTimeOffset To, int RowLimit);

public sealed record TemplateObservation(string SubjectKey, AlertSubjectKind SubjectKind, string ServiceNamespace, string ServiceName,
    string Environment, string Host, double? Value, long Samples, IReadOnlyDictionary<string, string>? Detail = null);

/// <summary>Grows additively.</summary>
public enum AlertSubjectKind { Service, Host }

/// <summary>The placeholders every template query must contain.</summary>
public static class TemplateSql
{
    public const string Tenant = "JuiceTenant = {tenant:String}";
    public const string From = "{from:DateTime64(9)}";
    public const string To = "{to:DateTime64(9)}";
    public const string Limit = "{limit:UInt32}";
}

/// <summary>Refuses SQL missing any <see cref="TemplateSql"/> placeholder, or a parameter named tenant, from, to or limit.</summary>
public interface ITemplateTelemetry
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map, CancellationToken ct);
}

public interface IRuleTemplateRegistry
{
    IReadOnlyList<RuleTemplateInfo> All { get; }

    bool TryGet(string key, [NotNullWhen(true)] out IRuleTemplate? template);
}

/// <summary>Supplies threshold numbers only: never the comparison, template or scope. A bad value reads as no data.</summary>
public interface IRuleThresholdProvider
{
    /// <summary><c>fixed</c> is the core's; a module's is <c>&lt;module&gt;.&lt;name&gt;</c>.</summary>
    string Key { get; }

    IEnumerable<string> Validate(RuleSpec rule, JsonElement? settings) => [];

    ValueTask<IReadOnlyDictionary<string, RuleThreshold>> ResolveAsync(RuleSpec rule, JsonElement? settings, IReadOnlyCollection<string> subjectKeys, DateTimeOffset at, CancellationToken ct);
}

public readonly record struct RuleThreshold(double Threshold, double Recovery);

/// <summary>Host plumbing: records which module registered a template or threshold provider, so the host can check that its key carries the module's prefix.</summary>
/// <remarks>Only the host's composition makes these (<c>AddRuleTemplate</c>, <c>AddThresholdProvider</c>): a module can't claim another module's key.</remarks>
public sealed class ModuleRuleContribution
{
    internal ModuleRuleContribution(string module, Type type) => (Module, Type) = (module, type);

    public string Module { get; }

    public Type Type { get; }
}

public static class RuleTemplateKeys
{
    /// <summary>The width of the <c>rules.template</c> column: a module template's key fits it, so a stored rule can name one without a migration.</summary>
    public const int MaxLength = 40;

    private static readonly System.Text.RegularExpressions.Regex Name = new(@"^[a-z0-9][a-z0-9-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Null when <paramref name="key"/> is <c>&lt;module&gt;.&lt;name&gt;</c> for this module; otherwise why not.</summary>
    public static string? Error(string module, string? key)
        => key is not null && key.Length <= MaxLength && key.StartsWith(module + ".", StringComparison.Ordinal) && Name.IsMatch(key[(module.Length + 1)..])
            ? null
            : $"the key '{key}' must be '{module}.<name>' (name: lower case letters, digits and '-'; at most {MaxLength} characters in all)";
}

public static class RuleTemplateModuleExtensions
{
    /// <summary>Key <c>&lt;module&gt;.&lt;name&gt;</c>, unique, not a built-in. The host creates the type once, as a singleton.</summary>
    public static IModuleServicesBuilder AddRuleTemplate<T>(this IModuleServicesBuilder b) where T : class, IRuleTemplate
    {
        ArgumentNullException.ThrowIfNull(b);
        CoreBuilders.Require(b);
        Contribute<T>(b, "rule template");
        b.Services.AddSingleton<IRuleTemplate, T>();
        if (typeof(T).GetConstructor(Type.EmptyTypes) is not null)
        {
            var key = Activator.CreateInstance<T>().Info.Key;
            if (RuleTemplateKeys.Error(b.Module.Name, key) is { } error)
            {
                throw new ModuleCompositionException($"module '{b.Module.Name}': rule template {typeof(T).Name}: {error}");
            }
            // A template key is not an alert source's, whichever was declared first.
            if (b.Services.Any(d => d.ServiceType == typeof(IAlertSource) && d.IsKeyedService && Equals(d.ServiceKey, AlertSourceKeys.Key(b.Module.Name, key))))
            {
                throw new ModuleCompositionException($"module '{b.Module.Name}': rule template {typeof(T).Name}: '{key}' is already declared as an alert source");
            }
        }
        return b;
    }

    /// <summary>Key <c>&lt;module&gt;.&lt;name&gt;</c>, unique. The host creates the type once, as a singleton.</summary>
    public static IModuleServicesBuilder AddThresholdProvider<T>(this IModuleServicesBuilder b) where T : class, IRuleThresholdProvider
    {
        ArgumentNullException.ThrowIfNull(b);
        CoreBuilders.Require(b);
        Contribute<T>(b, "threshold provider");
        b.Services.AddSingleton<IRuleThresholdProvider, T>();
        if (typeof(T).GetConstructor(Type.EmptyTypes) is not null && RuleTemplateKeys.Error(b.Module.Name, Activator.CreateInstance<T>().Key) is { } error)
        {
            throw new ModuleCompositionException($"module '{b.Module.Name}': threshold provider {typeof(T).Name}: {error}");
        }
        return b;
    }

    private static void Contribute<T>(IModuleServicesBuilder b, string what)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (typeof(T).IsAbstract)
        {
            throw new ModuleCompositionException($"module '{b.Module.Name}': {what} {typeof(T).Name} must be a concrete class");
        }
        if (b.Services.Any(d => d.ServiceType == typeof(ModuleRuleContribution) && d.ImplementationInstance is ModuleRuleContribution c && c.Type == typeof(T)))
        {
            throw new ModuleCompositionException($"module '{b.Module.Name}': {what} {typeof(T).Name} is registered more than once");
        }
        b.Services.AddSingleton(new ModuleRuleContribution(b.Module.Name, typeof(T)));
    }
}
