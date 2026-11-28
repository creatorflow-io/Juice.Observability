using Juice.Observability.Extensibility;

namespace Juice.Observability.Data
{
    /// <summary>
    /// Core side of the module events (<see cref="ObservabilityEvent"/>): writes them to the outbox in the transaction of the change that raised
    /// them, one envelope per module with a handler for them, and nothing when no module has one. Modules don't publish, and aren't given this: it
    /// isn't in the module contract. The event's <see cref="ObservabilityEvent.TenantId"/> is always the platform tenant's, whatever the caller set.
    /// </summary>
    internal interface IPlatformEventPublisher
    {
        /// <summary>Whether any module handles <typeparamref name="TEvent"/>: false means publishing it writes nothing, so don't build it.</summary>
        bool HasSubscribers<TEvent>() where TEvent : ObservabilityEvent;

        /// <summary>
        /// Inside a command, the envelopes join its transaction (and are dispatched locally right after it commits). Elsewhere, the caller has a
        /// transaction open on the platform's context, and they commit with it, and are delivered from the outbox.
        /// </summary>
        Task PublishAsync(IReadOnlyCollection<ObservabilityEvent> events, CancellationToken ct);

        Task PublishAsync(ObservabilityEvent e, CancellationToken ct) => PublishAsync([e], ct);
    }
}
