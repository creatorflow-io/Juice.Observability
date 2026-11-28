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
    /// <summary>Not a registry template; once per host.</summary>
    public static IModuleServicesBuilder AddAlertSource(this IModuleServicesBuilder b, string template)
        => throw new NotImplementedException();
}
