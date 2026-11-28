using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

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

public static class RuleTemplateModuleExtensions
{
    /// <summary>Key <c>&lt;module&gt;.&lt;name&gt;</c>, unique, not a built-in.</summary>
    public static IModuleServicesBuilder AddRuleTemplate<T>(this IModuleServicesBuilder b) where T : class, IRuleTemplate
        => throw new NotImplementedException();

    public static IModuleServicesBuilder AddThresholdProvider<T>(this IModuleServicesBuilder b) where T : class, IRuleThresholdProvider
        => throw new NotImplementedException();
}
