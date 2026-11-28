using Juice.MediatR;
using Juice.Messaging;

namespace Juice.Observability.Data
{
    // The platform on the Juice.Notifications pipeline, embedded (phase 5, M45): what the rest of the platform needs to know of it.

    /// <summary>
    /// Configuration section <c>Notifications</c>: the platform's own settings for the embedded pipeline (<see cref="ConsoleUrl"/>,
    /// <see cref="HoldPassInterval"/>, <see cref="ReleasePassInterval"/>); the pipeline reads its own (<c>Stages</c>, <c>Delivery</c>, <c>Retention</c>,
    /// <c>Provisioning</c>, …) from the same section. The pipeline is the only one since 2.0: <c>Notifications:Pipeline</c> is obsolete.
    /// </summary>
    public sealed class PlatformNotificationsOptions
    {
        public const string Section = "Notifications";

        /// <summary>How often silences that start or end by time become holds and releases in the pipeline (Q5: 15 s by default).</summary>
        public TimeSpan HoldPassInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>How often what a silence held is decided once no silence covers it.</summary>
        public TimeSpan ReleasePassInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The Console's address (<c>Notifications:ConsoleUrl</c>): an absolute http or https URL, checked at start
        /// (<see cref="PlatformNotificationsOptionsValidation"/>). The link in a notification is this text, then <c>/</c> and the page: never normalised.
        /// </summary>
        public string? ConsoleUrl { get; set; }

        /// <summary>The Console's page as a link, <c>{ConsoleUrl}/{page}</c> with the address's trailing slashes dropped; null when no address is set.</summary>
        public string? Link(string page)
            => ConsoleUrl is { Length: > 0 } console ? $"{console.TrimEnd('/')}/{page}" : null;
    }

    /// <summary>Checks <see cref="PlatformNotificationsOptions.ConsoleUrl"/> at start (<c>ValidateOnStart</c>). A message never echoes the value.</summary>
    public sealed class PlatformNotificationsOptionsValidation : Microsoft.Extensions.Options.IValidateOptions<PlatformNotificationsOptions>
    {
        /// <summary>The longest address it takes.</summary>
        public const int MaxConsoleUrlLength = 2048;

        private const string Key = "Notifications:ConsoleUrl";

        public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, PlatformNotificationsOptions options)
            => Problem(options.ConsoleUrl) is { } problem
                ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail($"{Key} {problem}")
                : Microsoft.Extensions.Options.ValidateOptionsResult.Success;

        /// <summary>Why the address is refused, without quoting it; null when it is valid or not set.</summary>
        internal static string? Problem(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            if (value.Length > MaxConsoleUrlLength)
            {
                return $"must be at most {MaxConsoleUrlLength} characters.";
            }
            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return "must not contain whitespace or control characters.";
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                return "must be an absolute http or https URL (such as https://host.example.com).";
            }
            var authority = value[(uri.Scheme.Length + 3)..];
            var end = authority.IndexOfAny(['/', '?', '#']);
            if (uri.UserInfo.Length > 0 || (end < 0 ? authority : authority[..end]).Contains('@'))
            {
                return "must not contain user information.";
            }
            if (value.Contains('?') || value.Contains('#'))
            {
                return "must not contain a query or a fragment.";
            }
            return null;
        }
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
    /// What happened to an action request, told to the notification pipeline in the transaction that made it
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
        /// <summary>
        /// Why it was kept (<see cref="NotificationHoldReasons"/>): a silence, or the pipeline not started. A silence wins when both apply. Kept as it was,
        /// whichever silence holds it later.
        /// </summary>
        public required string Reason { get; init; }
        /// <summary>The pipeline mode when it was kept (rows kept by 1.x say <c>shadow</c> or <c>legacy</c>; since 2.0 always <c>new</c>); null for a hold kept before the mode was.</summary>
        public string? Pipeline { get; init; }
        /// <summary>
        /// A held resolve whose correlation was resolved when it was planned (the pipeline was up): the release pass doesn't resolve it again,
        /// which would close an escalation the alert reopened since (review #13).
        /// </summary>
        public bool ResolveAsked { get; init; }
        /// <summary><see cref="NotificationHoldOutcomes.Sent"/>, <see cref="NotificationHoldOutcomes.Dropped"/> or <see cref="NotificationHoldOutcomes.Failed"/>, once decided.</summary>
        public string? Outcome { get; set; }
        /// <summary>Tries that failed (an error, not the pipeline's refusal); at <see cref="NotificationHoldOutcomes.MaxAttempts"/> it is decided failed.</summary>
        public int Attempts { get; set; }
        public string? LastError { get; set; }
    }

    public static class NotificationHoldReasons
    {
        /// <summary>A silence covered the alert: sent for real once released, in any mode.</summary>
        public const string Silence = "silence";
        /// <summary>The pipeline didn't start: kept until it does, then decided as any other hold.</summary>
        public const string PipelineUnavailable = "pipeline_unavailable";
    }

    public static class NotificationHoldOutcomes
    {
        public const string Sent = "sent";
        public const string Dropped = "dropped";
        /// <summary>A held resolve dropped because the alert fired again since (it would have been sent otherwise).</summary>
        public const string DroppedStale = "dropped_stale";
        /// <summary>Its send kept failing (<see cref="MaxAttempts"/> tries): given up, so it no longer blocks the others.</summary>
        public const string Failed = "failed";
        public const int MaxAttempts = 5;
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
        /// <summary>
        /// The pipeline mode when it was parked: always <c>new</c> since 2.0 (the migration settled what 1.x kept in another mode). Null for a call
        /// parked before the mode was kept, replayed as it was.
        /// </summary>
        public string? Pipeline { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ReplayedAt { get; set; }
        /// <summary>Replays that failed; at <see cref="NotificationHoldOutcomes.MaxAttempts"/> it is given up (replayed, with its error).</summary>
        public int Attempts { get; set; }
        public string? LastError { get; set; }
    }

    public static class NotificationParkedCalls
    {
        public const string Acknowledge = "acknowledge";
        public const string Send = "send";
    }
}
