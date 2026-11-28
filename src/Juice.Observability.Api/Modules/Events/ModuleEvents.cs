using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Juice.Messaging;
using Juice.Messaging.Outbox;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Modules.Events
{
    /// <summary>
    /// One event for one module, as the outbox carries it: the module gets its own row, so a module whose handler fails is tried again alone.
    /// The payload is the event as JSON; <see cref="EventType"/> is its type's name.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed record ModuleEventEnvelope(string Module, string EventType, string Payload) : IntegrationEvent;

    internal static class ModuleEventJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }

    /// <summary>
    /// Writes events to the outbox, one envelope per module that handles them, in the transaction of the change that raised them: the row
    /// commits with the change, or never exists. With no handler anywhere nothing is written, so a core-only host's outbox load is what it was.
    /// </summary>
    internal sealed class PlatformEventPublisher(PlatformDbContext db, IOutboxService<PlatformDbContext> outbox, IMessageService<PlatformDbContext> messages,
        IEnumerable<ModuleEventSubscription> subscriptions) : IPlatformEventPublisher
    {
        public bool HasSubscribers<TEvent>() where TEvent : ObservabilityEvent => subscriptions.Any(s => s.EventType == typeof(TEvent));

        public async Task PublishAsync(IReadOnlyCollection<ObservabilityEvent> events, CancellationToken ct)
        {
            var envelopes = new List<ModuleEventEnvelope>();
            foreach (var e in events)
            {
                var type = e.GetType();
                var modules = subscriptions.Where(s => s.EventType == type).Select(s => s.Module).Distinct(StringComparer.Ordinal).ToList();
                if (modules.Count == 0)
                {
                    continue;
                }
                var payload = JsonSerializer.Serialize(e, type, ModuleEventJson.Options);
                envelopes.AddRange(modules.Select(module => new ModuleEventEnvelope(module, type.Name, payload)));
            }
            if (envelopes.Count == 0)
            {
                return;
            }
            if (db is IManagable { IsManaged: true })
            {
                // Inside a command: the transaction behavior saves the rows with the change, and the channel dispatches right after the commit.
                foreach (var envelope in envelopes)
                {
                    await messages.PublishAsync(envelope, ct);
                }
                return;
            }
            if (db.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("An event is published in the transaction of the change that raised it: none is open.");
            }
            using var context = PlatformMessageContext.Begin("events");
            foreach (var envelope in envelopes)
            {
                await outbox.AddEventAsync(envelope);
            }
            // In the open transaction, so delivery sees the rows once the change commits (and never if it rolls back).
            await outbox.SaveEventsAsync(null, ct);
        }
    }

    /// <summary>
    /// Hands an envelope to its module's handlers, in a scope of its own. A handler that throws fails the delivery for that module only: the
    /// outbox hands it over again, and the handlers that succeeded run again too, which is why they must be idempotent on the event's id.
    /// </summary>
    internal sealed class ModuleEventEnvelopeConsumer(IServiceScopeFactory scopes, IEnumerable<ModuleEventSubscription> subscriptions,
        ILogger<ModuleEventEnvelopeConsumer> logger) : IIntegrationEventHandler<ModuleEventEnvelope>
    {
        public async Task HandleAsync(ModuleEventEnvelope envelope)
        {
            var handlers = subscriptions.Where(s => s.Module == envelope.Module && s.EventType.Name == envelope.EventType).ToList();
            if (handlers.Count == 0)
            {
                logger.LogWarning("Module {Module} no longer handles {Event}: its delivery is dropped", envelope.Module, envelope.EventType);
                return;
            }
            var @event = (ObservabilityEvent)JsonSerializer.Deserialize(envelope.Payload, handlers[0].EventType, ModuleEventJson.Options)!;
            await using var scope = scopes.CreateAsyncScope();
            var failures = new List<Exception>();
            foreach (var handler in handlers)
            {
                try
                {
                    var instance = scope.ServiceProvider.GetRequiredService(handler.HandlerType);
                    var method = typeof(IObservabilityEventHandler<>).MakeGenericType(handler.EventType).GetMethod(nameof(IObservabilityEventHandler<ObservabilityEvent>.HandleAsync))!;
                    await (Task)method.Invoke(instance, [@event, CancellationToken.None])!;
                }
                catch (Exception ex)
                {
                    var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    logger.LogWarning(cause, "Module {Module} handler {Handler} failed on {Event} {EventId}; it is delivered again", envelope.Module, handler.HandlerType.Name, envelope.EventType, @event.EventId);
                    failures.Add(cause);
                }
            }
            if (failures.Count > 0)
            {
                throw new AggregateException($"Module {envelope.Module} failed to handle {envelope.EventType} {@event.EventId}", failures);
            }
        }
    }
}
