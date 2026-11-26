using Juice.MediatR;
using Juice.Messaging;

namespace Juice.Observability.Data
{
    // The platform on the Juice.Notifications pipeline, embedded (phase 5, M45): what the rest of the platform needs to know of it.

    /// <summary>Which pipeline sends the platform's notifications (<c>Notifications:Pipeline</c>, phase-5-plan I67).</summary>
    public enum NotificationsPipeline
    {
        /// <summary>The platform's own webhooks, as before. The embedded pipeline is registered and migrated, and runs nothing.</summary>
        Legacy,
        /// <summary>Legacy sends; the embedded pipeline plans the same notifications in dry run (with the M44-2 alpha).</summary>
        Shadow,
        /// <summary>The embedded pipeline only: legacy planning, dispatch and its release and escalate passes are off.</summary>
        New,
    }

    /// <summary>
    /// Configuration section <c>Notifications</c>: <see cref="Pipeline"/>, and the pipeline's own settings (<c>Stages</c>, <c>Delivery</c>,
    /// <c>Retention</c>, …), which the embedded pipeline reads from the same section.
    /// </summary>
    public sealed class PlatformNotificationsOptions
    {
        public const string Section = "Notifications";

        public NotificationsPipeline Pipeline { get; set; } = NotificationsPipeline.Legacy;

        /// <summary>How often silences that start or end by time become holds and releases in the pipeline (Q5: 15 s by default).</summary>
        public TimeSpan HoldPassInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Whether the legacy webhooks plan and send (<c>legacy</c> and <c>shadow</c>).</summary>
        public bool LegacySends => Pipeline != NotificationsPipeline.New;

        /// <summary>Whether the platform's changes become notification requests (<c>shadow</c> and <c>new</c>).</summary>
        public bool ProducesRequests => Pipeline != NotificationsPipeline.Legacy;
    }

    /// <summary>
    /// A transition's notifications were planned (<see cref="AlertTransition.NotificationsPlannedAt"/> set): raised on the alert in the plan's
    /// transaction, so the request it becomes is written in that transaction (phase-5-plan I64). Its handlers run one after another
    /// (<see cref="ISequenceNotification"/>): they share the command's context and its connection, which can't take two operations at once.
    /// </summary>
    public sealed record AlertNotificationsPlanned(Guid PlatformTenantId, Guid AlertId, Guid TransitionId) : MessageBase, ISequenceNotification;

    /// <summary>Someone acknowledged an open alert: raised on the alert in the acknowledgement's transaction; its handlers run one after another.</summary>
    public sealed record AlertAcknowledged(Guid PlatformTenantId, Guid AlertId, string By) : MessageBase, ISequenceNotification;

    /// <summary>
    /// What happened to an action request, told to the notification pipeline (<c>shadow</c> and <c>new</c>), in the transaction that made it
    /// happen. Registered by the pipeline's adapter; absent, nothing is told.
    /// </summary>
    public interface IActionRequestNotifications
    {
        Task NotifyAsync(ActionRequest request, string hostname, string type, DateTimeOffset now, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A notification a silence held (phase-5-plan I65): the request is kept here, not sent, and the release pass decides it once no silence
    /// covers the alert (<c>HeldNotification.Decide</c>): sent, or dropped with nothing to say after the silence.
    /// </summary>
    public sealed class NotificationHold
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        public Guid AlertId { get; init; }
        public Guid TransitionId { get; init; }
        /// <summary><c>alert.firing</c> or <c>alert.resolved</c>.</summary>
        public required string Event { get; init; }
        /// <summary>The silence that held it, and then the one that still does.</summary>
        public Guid SilenceId { get; set; }
        /// <summary>The request as it would have been sent (a <c>NotificationToSend</c>, as JSON).</summary>
        public required string Request { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? DecidedAt { get; set; }
        /// <summary><see cref="NotificationHoldOutcomes.Sent"/> or <see cref="NotificationHoldOutcomes.Dropped"/>, once decided.</summary>
        public string? Outcome { get; set; }
    }

    public static class NotificationHoldOutcomes
    {
        public const string Sent = "sent";
        public const string Dropped = "dropped";
    }

    /// <summary>
    /// An open alert whose correlation is held in the pipeline because a silence covers it (p5-d3): its due escalation steps wait. The row is the
    /// platform's record of the hold, so it is released once no silence covers the alert.
    /// </summary>
    public sealed class AlertHold
    {
        public Guid TenantId { get; init; }
        public Guid AlertId { get; init; }
        public Guid SilenceId { get; set; }
        public DateTimeOffset HeldAt { get; init; }
    }

    /// <summary>
    /// A call the platform couldn't make while the pipeline was down (its start failed): an acknowledgement, or an action request's notice. Kept
    /// in the change's transaction, and replayed in order once a restart brings the pipeline up, before the transitions kept the same way.
    /// </summary>
    public sealed class NotificationParkedCall
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid TenantId { get; init; }
        /// <summary><see cref="NotificationParkedCalls.Acknowledge"/> or <see cref="NotificationParkedCalls.Send"/>.</summary>
        public required string Kind { get; init; }
        /// <summary>The correlation to acknowledge, or to resolve before the request is sent (an action request's ending notice).</summary>
        public string? CorrelationKey { get; init; }
        /// <summary>Who acknowledged.</summary>
        public string? By { get; init; }
        /// <summary>The request to send (a <c>NotificationToSend</c>, as JSON).</summary>
        public string? Request { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ReplayedAt { get; set; }
    }

    public static class NotificationParkedCalls
    {
        public const string Acknowledge = "acknowledge";
        public const string Send = "send";
    }
}
