namespace Juice.Observability.Extensibility;

/// <summary>What a contributor is asked about. It grows additively (the integrations milestone adds <c>Resource</c>).</summary>
public enum EvidenceTarget { Alert, Service }

/// <summary>
/// A read-only, tenant-scoped contributor of evidence to an alert or service page (core 2.0 M3, E9). Scoped. It runs inside a read-only transaction, in a
/// savepoint of its own, under a timeout and within the request's budget, and only for a caller its <see cref="Policy"/> allows. The context's tenant and subject
/// are the ones the core verified: the subject exists in that tenant. A module reads its own data through a context registered with <c>AddModuleData</c>,
/// which joins the transaction; a context on any other connection isn't covered by the read-only guarantee.
/// </summary>
public interface IEvidenceContributor
{
    /// <summary><c>&lt;module&gt;.&lt;name&gt;</c>; the core's are <c>core.&lt;name&gt;</c>. Checked when the host starts.</summary>
    string Key { get; }

    EvidenceTarget Target { get; }

    /// <summary>Null is <c>platform.read</c>; otherwise a <see cref="CorePolicies"/> value or one of the module's own policies. A caller it denies gets no section, and no hint of one.</summary>
    string? Policy => null;

    /// <summary>Null when there is nothing to show.</summary>
    Task<EvidenceSection?> ContributeAsync(EvidenceContext context, CancellationToken ct);
}

/// <summary>
/// <see cref="ServiceId"/> is the subject when it is a service, or the alert's service. <see cref="EnvironmentId"/> is the alert's environment or the requested
/// one. For an alert, the window runs from two hours before it fired to its resolution (or now); for a service, it is the requested one (default the last hour).
/// </summary>
public sealed record EvidenceContext(Guid TenantId, EvidenceTarget Target, Guid SubjectId, Guid? ServiceId, Guid? EnvironmentId, DateTimeOffset From, DateTimeOffset To);

/// <summary>Plain text, at most 50 items; a title or label over 64 characters, or a value or note over 512, is cut and the section flagged. Control and bidirectional characters make the section invalid.</summary>
public sealed record EvidenceSection(string Title, IReadOnlyList<EvidenceItem> Items, string? Note = null);

/// <summary><paramref name="Severity"/> is <c>info</c>, <c>warning</c> or <c>critical</c>.</summary>
public sealed record EvidenceItem(string Label, string? Value, DateTimeOffset? At = null, EvidenceLink? Link = null, string? Severity = null);

/// <summary>A route of the Console, relative, that matches a route pattern of a composed manifest (<c>slos/:id</c> matches <c>slos/abc</c>). Any other link is dropped and its item kept.</summary>
public sealed record EvidenceLink(string Route, IReadOnlyDictionary<string, string>? Query = null);

public static class EvidenceModuleExtensions
{
    public const int MaxContributorsPerModule = 5;

    /// <summary>
    /// Adds a scoped contributor, at most <see cref="MaxContributorsPerModule"/> per module. Its key, target and policy are checked when the host starts
    /// (a key outside <c>&lt;module&gt;.</c>, a key used twice or an unknown policy refuses the start).
    /// </summary>
    public static IModuleServicesBuilder AddEvidenceContributor<T>(this IModuleServicesBuilder b) where T : class, IEvidenceContributor
    {
        ArgumentNullException.ThrowIfNull(b);
        HostComposers.Find<IEvidenceComposer>(b).AddContributor<T>(b);
        return b;
    }
}

/// <summary>The host's side of <see cref="EvidenceModuleExtensions.AddEvidenceContributor{T}"/>: internal, implemented and registered by the host (see <c>IModuleDataComposer</c>).</summary>
internal interface IEvidenceComposer
{
    void AddContributor<T>(IModuleServicesBuilder b) where T : class, IEvidenceContributor;
}
