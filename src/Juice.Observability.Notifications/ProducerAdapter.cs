using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Observability.Agents;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The platform as a producer of the notification pipeline (phase-5-plan I64): an alert's transitions, its acknowledgement and an action
    /// request's events become requests, acknowledgements and resolves, written in the platform's transaction that made them happen. Never called
    /// outside one (the client refuses it with <c>no_transaction</c>).
    /// <list type="bullet">
    /// <item>firing: a request keyed <c>transition:{id}</c>, correlated <c>alert:{id}</c>, labelled for routing and with the policy to escalate by;</item>
    /// <item>resolved: <c>alert:{id}</c> resolved, then a request with no correlation (one would be cancelled by the resolve, or reopen it);</item>
    /// <item>a transition a silence covers is held (<see cref="NotificationHold"/>) instead of sent; the resolve is not held.</item>
    /// </list>
    /// A refusal (the request is invalid) is logged and counted, and never fails the platform's change: nothing was written, and the transaction
    /// is intact. A database error is not caught.
    /// </summary>
    public sealed class ProducerAdapter(INotificationClient client, PlatformDbContext db, IOptions<PlatformOptions> platform, IOptions<WebhookOptions> webhooks,
        TimeProvider clock, ILogger<ProducerAdapter> logger, PipelineStart? start = null)
    {
        /// <summary>
        /// The silence of a hold kept because the pipeline didn't start (<see cref="PipelineStart.Failure"/>): none. Once a restart brings the
        /// pipeline up, the release pass decides it as any other hold whose silence is over.
        /// </summary>
        public static readonly Guid PipelineUnavailable = Guid.Empty;

        /// <summary>Whether the pipeline failed to start: nothing is asked of it, so the platform's change still commits.</summary>
        private bool Unavailable => start?.Failure is not null;

        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Refused = Meter.CreateCounter<long>("juice.observability.notifications.refused", "{request}",
            "Requests the notification pipeline refused, by call and code");

        /// <summary>The longest label value the pipeline takes.</summary>
        internal const int MaxLabel = 500;

        public static string AlertCorrelation(Guid alertId) => $"alert:{alertId}";

        public static string TransitionKey(Guid transitionId) => $"transition:{transitionId}";

        public static string ActionCorrelation(Guid requestId) => $"action:{requestId}";

        private const string RequestPrefix = "action.request.";

        /// <summary>A transition whose notifications were just planned: its request, or its hold if a silence covers it.</summary>
        public async Task TransitionAsync(Guid tenantId, Guid alertId, Guid transitionId, CancellationToken cancellationToken)
        {
            var transition = await db.AlertTransitions.SingleAsync(t => t.TenantId == tenantId && t.Id == transitionId, cancellationToken);
            var alert = await db.Alerts.SingleAsync(a => a.TenantId == tenantId && a.Id == alertId, cancellationToken);
            var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == alert.RuleId, cancellationToken);
            var context = new AlertContext(rule.Key, alert.SubjectKey, alert.Severity);
            var now = clock.GetUtcNow();
            NotificationToSend request;
            if (transition.ToState == AlertState.Firing)
            {
                // Resolved before its firing was planned: a resolve would have cancelled the queued firing (Q2), so it isn't sent.
                if (alert.State != AlertState.Firing)
                {
                    logger.LogInformation("Alert {Alert} resolved before its firing was planned; no firing request", alert.Id);
                    return;
                }
                request = Firing(rule, alert, transition, context, Policy(alert, context));
            }
            else
            {
                // Parked while the pipeline is down: the release pass resolves it with the notice.
                if (!Unavailable)
                {
                    await ResolveAlertAsync(alert.Id, transition.Reason, cancellationToken);
                }
                request = Resolved(rule, alert, transition, context);
            }
            var silence = NotificationPlan.CoveringSilence(db, alert, context, now);
            if (silence is not null || Unavailable)
            {
                if (silence is null)
                {
                    logger.LogWarning("The notification pipeline didn't start ({Failure}): transition {Transition} is kept until it does", start!.Failure, transition.Id);
                }
                db.NotificationHolds.Add(new NotificationHold
                {
                    TenantId = tenantId, AlertId = alert.Id, TransitionId = transition.Id, SilenceId = silence?.Id ?? PipelineUnavailable, CreatedAt = now,
                    Event = transition.ToState == AlertState.Firing ? NotificationPlan.Firing : NotificationPlan.Resolved,
                    Request = JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
                });
                return;
            }
            await SendAsync(request, cancellationToken);
        }

        /// <summary>Someone acknowledged the alert: its correlation is acknowledged (escalations that stop on it stop).</summary>
        public async Task AcknowledgeAsync(Guid alertId, string by, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                logger.LogWarning("The notification pipeline didn't start ({Failure}): the acknowledgement of alert {Alert} isn't told to it", start!.Failure, alertId);
                return;
            }
            await TolerateAsync("acknowledge", () => client.AcknowledgeAsync(AlertCorrelation(alertId), Bounded(by, 200), cancellationToken));
        }

        /// <summary>The hold's reason for a silence: <c>silence:{id}</c>.</summary>
        public static string SilenceReason(Guid silenceId) => $"silence:{silenceId}";

        /// <summary>
        /// Holds the alert's escalation for a silence (p5-d3): a step that falls due waits for <see cref="ReleaseAlertAsync"/>. False when the
        /// pipeline has no correlation for it (its firing was never sent: held itself, or not planned yet) or refused, and when it didn't start.
        /// </summary>
        internal async Task<bool> HoldAlertAsync(Guid alertId, Guid silenceId, CancellationToken cancellationToken)
            => !Unavailable && await TolerateAsync("hold", () => client.HoldAsync(AlertCorrelation(alertId), SilenceReason(silenceId), "silence", cancellationToken));

        /// <summary>Releases the alert's hold: steps that fell due under it run now if the alert is still open. False as for a hold.</summary>
        internal async Task<bool> ReleaseAlertAsync(Guid alertId, CancellationToken cancellationToken)
            => !Unavailable && await TolerateAsync("release", () => client.ReleaseAsync(AlertCorrelation(alertId), "silence", cancellationToken));

        /// <summary>Resolves the alert's correlation (its escalation stops, what is still due is cancelled); none, or closed already, is no failure.</summary>
        internal Task ResolveAlertAsync(Guid alertId, string? reason, CancellationToken cancellationToken)
            => TolerateAsync("resolve", () => client.ResolveAsync(AlertCorrelation(alertId), Bounded(reason, 200), null, cancellationToken));

        /// <summary>
        /// What happened to an action request: <c>observability.action.request.{event}</c>, keyed <c>action:{id}:{event}</c>. While the request is
        /// open its notices are correlated <c>action:{id}</c>; the notice that ends it resolves the correlation first and has none.
        /// </summary>
        public async Task ActionRequestAsync(ActionRequest request, string hostname, string type, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                Refused.Add(1, new KeyValuePair<string, object?>("call", "send"), new KeyValuePair<string, object?>("code", "pipeline_unavailable"));
                logger.LogError("The notification pipeline didn't start ({Failure}): {Type} of action request {Request} isn't sent", start!.Failure, type, request.Id);
                return;
            }
            var @event = type.StartsWith(RequestPrefix, StringComparison.Ordinal) ? type[RequestPrefix.Length..] : type;
            var ends = Ends(request, type);
            if (ends)
            {
                await TolerateAsync("resolve", () => client.ResolveAsync(ActionCorrelation(request.Id), @event, null, cancellationToken));
            }
            var labels = new Dictionary<string, string>
            {
                ["event"] = type, ["action"] = Bounded(request.Action, MaxLabel)!, ["risk"] = Bounded(request.Risk, MaxLabel)!, ["policy"] = Bounded(request.Policy, MaxLabel)!,
                ["agent"] = request.AgentId.ToString(), ["request"] = request.Id.ToString(),
            };
            if (hostname.Length > 0) labels["hostname"] = Bounded(hostname, MaxLabel)!;
            if (request.AlertId is { } alert) labels["alert"] = alert.ToString();
            await SendAsync(new NotificationToSend(ObservabilityTypes.ForPlatformEvent(type), $"action:{request.Id}:{@event}")
            {
                CorrelationKey = ends ? null : ActionCorrelation(request.Id),
                Labels = labels,
                Data = WithoutId(ActionRequestNotices.Payload(Guid.Empty, type, platform.Value.TenantKey, request, hostname, now)),
                Links = Link("approvals"),
            }, cancellationToken);
        }

        /// <summary>Whether this event ends the request: decided against, lapsed, or done (verified, or issued with nothing to verify).</summary>
        internal static bool Ends(ActionRequest request, string type) => type switch
        {
            ActionRequestEvents.Created => false,
            ActionRequestEvents.Approved => request.Verification == ActionVerification.None,
            _ => true,
        };

        /// <summary>Sends a request kept by a hold, now that no silence covers it; false when the pipeline refused it.</summary>
        internal Task<bool> SendHeldAsync(NotificationHold hold, CancellationToken cancellationToken)
            => SendAsync(JsonSerializer.Deserialize<NotificationToSend>(hold.Request, JsonSerializerOptions.Web)!, cancellationToken);

        internal NotificationToSend Firing(Rule rule, Alert alert, AlertTransition transition, AlertContext context, string? policy)
        {
            var labels = Labels(rule, alert, context, NotificationPlan.Firing);
            if (policy is not null) labels["escalationPolicy"] = policy;
            return new NotificationToSend(ObservabilityTypes.AlertFiring, TransitionKey(transition.Id))
            {
                CorrelationKey = AlertCorrelation(alert.Id),
                Labels = labels,
                Data = WithoutId(NotificationPlan.Payload(Guid.Empty, NotificationPlan.Firing, platform.Value.TenantKey, rule, alert, transition, null)),
                Links = Link($"alerts/{alert.Id}"),
            };
        }

        internal NotificationToSend Resolved(Rule rule, Alert alert, AlertTransition transition, AlertContext context)
        {
            var labels = Labels(rule, alert, context, NotificationPlan.Resolved);
            labels["alert"] = alert.Id.ToString();
            return new NotificationToSend(ObservabilityTypes.AlertResolved, TransitionKey(transition.Id))
            {
                Labels = labels,
                Data = WithoutId(NotificationPlan.Payload(Guid.Empty, NotificationPlan.Resolved, platform.Value.TenantKey, rule, alert, transition, null)),
                Links = Link($"alerts/{alert.Id}"),
            };
        }

        /// <summary>
        /// The policy the pipeline escalates by: the pipeline runs one escalation per notification, so of the policies the platform's own match
        /// finds, the first by key (Q1: overlapping policies are refused by the migration report). More than one is logged.
        /// </summary>
        private string? Policy(Alert alert, AlertContext context)
        {
            var matching = NotificationPlan.MatchingPolicies(db, alert, context).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            if (matching.Count > 1)
            {
                logger.LogWarning("Alert {Alert} matches {Count} escalation policies ({Policies}); the notification pipeline escalates by {Policy} only",
                    alert.Id, matching.Count, string.Join(", ", matching.Select(p => p.Key)), matching[0].Key);
            }
            return matching.FirstOrDefault()?.Key;
        }

        private static Dictionary<string, string> Labels(Rule rule, Alert alert, AlertContext context, string @event)
        {
            var labels = new Dictionary<string, string>
            {
                ["event"] = @event, ["rule"] = rule.Key, ["severity"] = alert.Severity.ToString().ToLowerInvariant(), ["subject"] = Bounded(alert.SubjectKey, MaxLabel)!,
            };
            if (context.Environment is { Length: > 0 } environment) labels["environment"] = Bounded(environment, MaxLabel)!;
            return labels;
        }

        /// <summary>The Console's page as <c>self</c>, when the Console's address is set and absolute (<c>Webhooks:ConsoleUrl</c>).</summary>
        private Dictionary<string, string>? Link(string page)
            => Uri.TryCreate(webhooks.Value.ConsoleUrl, UriKind.Absolute, out var console) && console.Scheme is "https" or "http"
                ? new() { ["self"] = $"{console.AbsoluteUri.TrimEnd('/')}/{page}" }
                : null;

        /// <summary>Today's payload, less its id (each delivery's own in the webhooks; the pipeline gives its own).</summary>
        internal static JsonElement WithoutId(string payload)
        {
            var json = JsonNode.Parse(payload)!.AsObject();
            json.Remove("id");
            return JsonSerializer.SerializeToElement(json);
        }

        private static string? Bounded(string? value, int max) => value is { Length: > 0 } ? value.Length > max ? value[..max] : value : null;

        private Task<bool> SendAsync(NotificationToSend request, CancellationToken cancellationToken)
            => TolerateAsync("send", async () => await client.SendAsync(request, cancellationToken), request.IdempotencyKey);

        /// <summary>
        /// A refusal is the pipeline's answer, not a failure of the platform's change: a correlation it doesn't have (its firing was never sent) or
        /// one already closed is expected; anything else is logged as an error and counted. True when the pipeline took the call.
        /// </summary>
        private async Task<bool> TolerateAsync<T>(string call, Func<Task<T>> work, string? key = null)
        {
            try
            {
                await work();
                return true;
            }
            catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.NotFound or ErrorCodes.CorrelationClosed && call != "send")
            {
                logger.LogDebug("Notifications {Call}: {Code} ({Message})", call, refused.Code, refused.Message);
                return false;
            }
            catch (NotificationRefusedException refused)
            {
                Refused.Add(1, new KeyValuePair<string, object?>("call", call), new KeyValuePair<string, object?>("code", refused.Code));
                logger.LogError("The notification pipeline refused {Call} {Key}: {Code} {Message} {Errors}", call, key, refused.Code, refused.Message,
                    string.Join("; ", refused.Errors?.Select(e => $"{e.Pointer} {e.Message}") ?? []));
                return false;
            }
        }
    }
}
