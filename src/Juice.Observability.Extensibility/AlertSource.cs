namespace Juice.Observability.Extensibility;

/// <summary>A keyed scoped service: <c>GetRequiredKeyedService&lt;IAlertSource&gt;(template)</c>.</summary>
public interface IAlertSource
{
    string Template { get; }

    Task<IReadOnlyList<SourceAlert>> OpenAlertsAsync(CancellationToken ct);

    /// <summary>Fired, then resolved for <paramref name="reason"/>.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> LastResolvedAsync(IReadOnlyCollection<string> dedupKeys, string reason, CancellationToken ct);

    Task<AlertSourceResult> ApplyAsync(IReadOnlyList<AlertSignal> signals, CancellationToken ct);
}

public sealed record SourceAlert(Guid Id, string RuleKey, string DedupKey, string SubjectKey, string State, DateTimeOffset? FiredAt, double? LastValue);

public sealed record AlertSubject(AlertSubjectKind Kind, string Key, Guid? SubjectId = null, Guid? EnvironmentId = null);

public enum AlertSignalKind { Fire, Resolve, Observe }

/// <summary>A null Value keeps the last.</summary>
public sealed record AlertSignal(string RuleKey, string DedupKey, AlertSignalKind Kind, DateTimeOffset At, string Reason, AlertSubject? Subject = null,
    double? Value = null, long? Samples = null, bool NoData = false, IReadOnlyDictionary<string, string>? Evidence = null);

public sealed record AlertSourceResult(int Fired, int Resolved, int Observed);

public static class AlertSourceModuleExtensions
{
    /// <summary>
    /// Declares an alert source: the module's alerts of <paramref name="template"/>, applied through <see cref="IAlertSource"/> (a keyed scoped service). The
    /// template is not a registry template and is declared once per host: refused when it is one of the built-ins or already declared. Outside the
    /// core's own <c>SloBurn</c>, the template is <c>&lt;module&gt;.&lt;name&gt;</c>.
    /// </summary>
    public static IModuleServicesBuilder AddAlertSource(this IModuleServicesBuilder b, string template)
    {
        ArgumentNullException.ThrowIfNull(b);
        HostComposers.Find<IAlertSourceComposer>(b).AddAlertSource(b, template);
        return b;
    }
}

/// <summary>The host's side of <see cref="AlertSourceModuleExtensions.AddAlertSource"/>: internal, implemented and registered by the host (see <c>IModuleDataComposer</c>).</summary>
internal interface IAlertSourceComposer
{
    void AddAlertSource(IModuleServicesBuilder b, string template);
}
