using Juice.MediatR;
using Juice.Messaging;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;

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
    /// with the alert or not at all. The same goes for the modules' <see cref="AlertTransitionedEvent"/>, written when a module handles it.
    /// </summary>
    public sealed class PublishAlertTransitioned(IMessageService<PlatformDbContext> messages, IPlatformEventPublisher? events = null, PlatformDbContext? db = null)
        : INotificationHandler<AlertTransitioned>
    {
        public async ValueTask Handle(AlertTransitioned notification, CancellationToken cancellationToken = default)
        {
            await messages.PublishAsync(new AlertTransitionedIntegrationEvent(notification.PlatformTenantId, notification.AlertId, notification.TransitionId,
                notification.ToState), cancellationToken);
            if (events is null || db is null || !events.HasSubscribers<AlertTransitionedEvent>())
            {
                return;
            }
            var alert = await db.Alerts.AsNoTracking().Where(a => a.Id == notification.AlertId)
                .Select(a => new { a.DedupKey, a.SubjectKey, a.Severity, a.RuleId }).SingleAsync(cancellationToken);
            var rule = await db.Rules.AsNoTracking().Where(r => r.Id == alert.RuleId).Select(r => new { r.Key, r.Template }).SingleAsync(cancellationToken);
            var at = await db.AlertTransitions.AsNoTracking().Where(t => t.Id == notification.TransitionId).Select(t => t.At).SingleAsync(cancellationToken);
            await events.PublishAsync(new AlertTransitionedEvent(notification.AlertId, notification.TransitionId, rule.Key, rule.Template.ToString(), alert.DedupKey,
                alert.SubjectKey, notification.ToState.ToString(), alert.Severity.ToString())
            {
                EventId = Guid.CreateVersion7(), TenantId = notification.PlatformTenantId, At = at,
            }, cancellationToken);
        }
    }
}
