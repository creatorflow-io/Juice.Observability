using Juice.MediatR;
using Juice.Messaging;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Notifications
{
    /// <summary>Plan the notifications of one alert transition: it becomes a request to the notification pipeline, in the command's transaction.</summary>
    public sealed record PlanNotifications(Guid PlatformTenantId, Guid TransitionId, DateTimeOffset CommittedAt) : MessageBase, IRequest<int>;

    /// <summary>
    /// Hears that an alert transition was committed, from the outbox (the <c>local</c> route: right after the commit, and from delivery if that failed), and
    /// plans its notifications as a command of its own.
    /// </summary>
    public sealed class AlertTransitionedConsumer(IMediator mediator) : IIntegrationEventHandler<AlertTransitionedIntegrationEvent>
    {
        // In UTC: the message serializer reads a time back in the host's offset, and PostgreSQL stores only UTC offsets.
        public async Task HandleAsync(AlertTransitionedIntegrationEvent @event)
            => await mediator.Send(new PlanNotifications(@event.PlatformTenantId, @event.TransitionId, @event.CreatedAt.ToUniversalTime()));
    }

    /// <summary>
    /// Plans a transition's notifications once. The outbox may hand a transition over twice, so a transition planned already is left alone, and two plans
    /// racing on different replicas meet at <see cref="AlertTransition.NotificationsPlannedAt"/>, a concurrency token: the second rolls back and, retried, finds it planned.
    /// The alert raises <see cref="AlertNotificationsPlanned"/> once the token is set, and its handler writes the request in this transaction (phase-5-plan I64).
    /// </summary>
    public sealed class PlanNotificationsHandler(PlatformDbContext db, TimeProvider clock) : IRequestHandler<PlanNotifications, int>
    {
        public async ValueTask<int> Handle(PlanNotifications request, CancellationToken cancellationToken)
        {
            var transition = await db.AlertTransitions.SingleOrDefaultAsync(t => t.TenantId == request.PlatformTenantId && t.Id == request.TransitionId, cancellationToken);
            if (transition is null || transition.NotificationsPlannedAt is not null)
            {
                return 0;
            }
            // Tracked only to carry the event: nothing of it changes here.
            var alert = await db.Alerts.SingleAsync(a => a.Id == transition.AlertId, cancellationToken);
            transition.NotificationsPlannedAt = clock.GetUtcNow();
            alert.DomainEvents.Add(new AlertNotificationsPlanned(alert.TenantId, alert.Id, transition.Id));
            return 0;
        }
    }
}
