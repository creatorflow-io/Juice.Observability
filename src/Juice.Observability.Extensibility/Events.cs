using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Extensibility;

/// <summary>
/// What a module can hear, delivered after the change commits, at least once, in no guaranteed order. An event carries the platform tenant only
/// (<see cref="TenantId"/>). Handlers must be idempotent on <see cref="EventId"/>: the same event may arrive twice.
/// </summary>
public abstract record ObservabilityEvent
{
    public required Guid EventId { get; init; }

    public required Guid TenantId { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>An alert fired, or resolved after firing (a Pending alert that resolves never fired, and raises nothing).</summary>
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

/// <summary>A module's handler of an event, as composed. Core side: modules don't create these.</summary>
internal sealed record ModuleEventSubscription(string Module, Type EventType, Type HandlerType);

public static class EventModuleExtensions
{
    /// <summary>
    /// The module's handler of <typeparamref name="TEvent"/>, one of the events above. Runs in a scope of its own, apart from other modules': when it
    /// throws, the delivery is tried again for this module only.
    /// </summary>
    public static IModuleServicesBuilder AddEventHandler<TEvent, THandler>(this IModuleServicesBuilder b)
        where TEvent : ObservabilityEvent where THandler : class, IObservabilityEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(b);
        CoreBuilders.Require(b);
        var module = b.Module.Name;
        if (!EventTypes.Core.Contains(typeof(TEvent)))
        {
            throw new ModuleCompositionException($"Module '{module}' handler for {typeof(TEvent).Name} is refused: it is not one of the core's events.");
        }
        var subscription = new ModuleEventSubscription(module, typeof(TEvent), typeof(THandler));
        if (b.Services.Any(d => d.ServiceType == typeof(ModuleEventSubscription) && subscription.Equals(d.ImplementationInstance)))
        {
            throw new ModuleCompositionException($"Module '{module}' handler {typeof(THandler).Name} for {typeof(TEvent).Name} is registered twice.");
        }
        b.Services.AddSingleton(subscription);
        b.Services.AddScoped<THandler>();
        return b;
    }
}

internal static class EventTypes
{
    internal static readonly IReadOnlySet<Type> Core = new HashSet<Type>
    {
        typeof(AlertTransitionedEvent), typeof(IncidentChangedEvent), typeof(ResourceChangedEvent), typeof(ChangeRecordedEvent),
    };
}
