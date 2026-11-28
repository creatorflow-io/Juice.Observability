using Juice.MediatR;
using Juice.Observability.Data;

namespace Juice.Observability.Notifications
{
    // The platform's domain events, to the adapter: each handled inside the transaction of the command that raised it (the transaction behavior
    // dispatches them before it commits).

    internal sealed class OnNotificationsPlanned(ProducerAdapter adapter) : INotificationHandler<AlertNotificationsPlanned>
    {
        public async ValueTask Handle(AlertNotificationsPlanned notification, CancellationToken cancellationToken)
            => await adapter.TransitionAsync(notification.PlatformTenantId, notification.AlertId, notification.TransitionId, cancellationToken);
    }

    internal sealed class OnAlertAcknowledged(ProducerAdapter adapter) : INotificationHandler<AlertAcknowledged>
    {
        public async ValueTask Handle(AlertAcknowledged notification, CancellationToken cancellationToken)
            => await adapter.AcknowledgeAsync(notification.PlatformTenantId, notification.AlertId, notification.By, cancellationToken);
    }

    /// <summary>An action request's events, told by <c>ActionRequestNotices</c> in the transaction that made them.</summary>
    internal sealed class ActionRequestNotifications(ProducerAdapter adapter) : IActionRequestNotifications
    {
        public Task NotifyAsync(ActionRequest request, string hostname, string type, DateTimeOffset now, CancellationToken cancellationToken)
            => adapter.ActionRequestAsync(request, hostname, type, now, cancellationToken);
    }
}
