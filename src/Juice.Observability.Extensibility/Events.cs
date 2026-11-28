namespace Juice.Observability.Extensibility;

public abstract record ObservabilityEvent
{
    public required Guid EventId { get; init; }

    public required Guid TenantId { get; init; }

    public required DateTimeOffset At { get; init; }
}

public sealed record AlertTransitionedEvent(Guid AlertId, Guid TransitionId, string RuleKey, string Template, string DedupKey, string SubjectKey, string State, string Severity) : ObservabilityEvent;

/// <summary>Change: created, state, alerts or comment.</summary>
public sealed record IncidentChangedEvent(Guid IncidentId, string Change, string State) : ObservabilityEvent;

/// <summary>Kind: service, instance, resource or dependency. Change: created, retired, restored, deactivated or pruned.</summary>
public sealed record ResourceChangedEvent(string Kind, Guid Id, string Change) : ObservabilityEvent;

public sealed record ChangeRecordedEvent(Guid ChangeEventId, string Kind, Guid? ServiceId) : ObservabilityEvent;

/// <summary>Scoped. Handlers must be idempotent on <see cref="ObservabilityEvent.EventId"/>.</summary>
public interface IObservabilityEventHandler<in TEvent> where TEvent : ObservabilityEvent
{
    Task HandleAsync(TEvent e, CancellationToken ct);
}

public static class EventModuleExtensions
{
    public static IModuleServicesBuilder AddEventHandler<TEvent, THandler>(this IModuleServicesBuilder b)
        where TEvent : ObservabilityEvent where THandler : class, IObservabilityEventHandler<TEvent>
        => throw new NotImplementedException();
}
