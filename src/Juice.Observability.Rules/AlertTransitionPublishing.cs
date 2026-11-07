using Juice.MediatR;
using Juice.Messaging;
using Juice.Observability.Data;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// An alert fired, or resolved after firing, and its change is committed. What the rules module tells the rest of the platform: who is notified,
    /// and how, isn't its business. Ids only: a consumer reads the alert as it is when it handles this.
    /// </summary>
    public sealed record AlertTransitionedIntegrationEvent(Guid PlatformTenantId, Guid AlertId, Guid TransitionId, AlertState ToState) : IntegrationEvent;

    /// <summary>
    /// Turns the alert's domain event into the integration event. Runs inside the command's transaction (TransactionBehavior dispatches domain events
    /// before it saves the outbox), and <see cref="IMessageService{TContext}"/> defers the write to the TransactionBehavior, so the outbox row commits
    /// with the alert or not at all.
    /// </summary>
    public sealed class PublishAlertTransitioned(IMessageService<PlatformDbContext> messages) : INotificationHandler<AlertTransitioned>
    {
        public async ValueTask Handle(AlertTransitioned notification, CancellationToken cancellationToken = default)
            => await messages.PublishAsync(new AlertTransitionedIntegrationEvent(notification.PlatformTenantId, notification.AlertId, notification.TransitionId,
                notification.ToState), cancellationToken);
    }
}
