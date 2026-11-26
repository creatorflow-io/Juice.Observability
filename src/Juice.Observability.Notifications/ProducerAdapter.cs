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
        TimeProvider clock, ILogger<ProducerAdapter> logger, IOptions<PlatformNotificationsOptions> pipeline, PipelineStart? start = null)
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
        private static readonly Counter<long> DryDuplicates = Meter.CreateCounter<long>("juice.observability.notifications.dry_duplicates", "{request}",
            "Sends in new mode that the pipeline took as a duplicate of a dry original (shadow-era), so nothing was sent: an invariant breach, to stay 0");

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
                    Reason = silence is null ? NotificationHoldReasons.PipelineUnavailable : NotificationHoldReasons.Silence, Pipeline = Mode,
                });
                return;
            }
            await SendAsync(request, cancellationToken, conflictThrows: true);
        }

        /// <summary>Someone acknowledged the alert: its correlation is acknowledged (escalations that stop on it stop).</summary>
        public async Task AcknowledgeAsync(Guid tenantId, Guid alertId, string by, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                // Kept, and replayed once the pipeline is up, before the transitions kept the same way: an escalation then stops as it should.
                Park(tenantId, NotificationParkedCalls.Acknowledge, AlertCorrelation(alertId), Bounded(by, 200), null);
                logger.LogWarning("The notification pipeline didn't start ({Failure}): the acknowledgement of alert {Alert} is kept until it does", start!.Failure, alertId);
                return;
            }
            // Still refused for a concurrent change: kept the same way, so the release pass replays it in order and the escalation stops.
            await TolerateAsync("acknowledge", () => client.AcknowledgeAsync(AlertCorrelation(alertId), Bounded(by, 200), cancellationToken),
                onConflict: () => Park(tenantId, NotificationParkedCalls.Acknowledge, AlertCorrelation(alertId), Bounded(by, 200), null));
        }

        /// <summary>A call kept while the pipeline is down (<see cref="NotificationParkedCall"/>), in the change's transaction.</summary>
        private void Park(Guid tenantId, string kind, string? correlationKey, string? by, NotificationToSend? request)
            => db.NotificationParkedCalls.Add(new NotificationParkedCall
            {
                TenantId = tenantId,
                Kind = kind, CorrelationKey = correlationKey, By = by, CreatedAt = clock.GetUtcNow(), Pipeline = Mode,
                Request = request is null ? null : JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
            });

        /// <summary>Replays a kept call once the pipeline is up: an acknowledgement, or a resolve and the request.</summary>
        internal async Task ReplayAsync(NotificationParkedCall call, CancellationToken cancellationToken)
        {
            if (call.Kind == NotificationParkedCalls.Acknowledge)
            {
                await TolerateAsync("acknowledge", () => client.AcknowledgeAsync(call.CorrelationKey!, call.By, cancellationToken), conflictThrows: true);
                return;
            }
            if (call.CorrelationKey is { } resolve)
            {
                await TolerateAsync("resolve", () => client.ResolveAsync(resolve, null, null, cancellationToken), conflictThrows: true);
            }
            var request = JsonSerializer.Deserialize<NotificationToSend>(call.Request!, JsonSerializerOptions.Web)!;
            // Kept while in shadow (architecture §6.4): legacy sent it then, so its own recipients are dry; its escalation's later steps are real.
            // Not dropped: without the request nobody would page for an alert still open across the switch.
            if (call.Pipeline == ShadowMode)
            {
                request = request with { DryRun = true };
            }
            await SendAsync(request, cancellationToken, conflictThrows: true);
        }

        /// <summary>The hold's reason for a silence: <c>silence:{id}</c>.</summary>
        public static string SilenceReason(Guid silenceId) => $"silence:{silenceId}";

        /// <summary>
        /// Holds the alert's escalation for a silence (p5-d3): a step that falls due waits for <see cref="ReleaseAsync"/>. False when the
        /// pipeline has no correlation for it (its firing was never sent: held itself, or not planned yet) or refused, and when it didn't start.
        /// </summary>
        internal async Task<bool> HoldAlertAsync(Guid alertId, Guid silenceId, CancellationToken cancellationToken)
            => !Unavailable && await TolerateAsync("hold", () => client.HoldAsync(AlertCorrelation(alertId), SilenceReason(silenceId), "silence", cancellationToken));

        /// <summary>Whether the pipeline failed to start: nothing is asked of it.</summary>
        internal bool IsUnavailable => Unavailable;

        /// <summary>
        /// Releases a hold (an alert's, <c>alert:{id}</c>): steps that fell due under it run now if the correlation is still open.
        /// <see cref="HoldRelease.Gone"/> when the pipeline has no such correlation (nothing left to release); <see cref="HoldRelease.Failed"/> when it
        /// refused or didn't start, so the hold still stands and the platform's record of it must stay.
        /// </summary>
        internal async Task<HoldRelease> ReleaseAsync(string correlationKey, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                return HoldRelease.Failed;
            }
            try
            {
                await client.ReleaseAsync(correlationKey, "silence", cancellationToken);
                return HoldRelease.Released;
            }
            catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.NotFound)
            {
                return HoldRelease.Gone;
            }
            catch (NotificationRefusedException refused)
            {
                Refused.Add(1, new KeyValuePair<string, object?>("call", "release"), new KeyValuePair<string, object?>("code", refused.Code));
                logger.LogError("The notification pipeline refused to release {Key}: {Code} {Message}; it is tried again", correlationKey, refused.Code, refused.Message);
                return HoldRelease.Failed;
            }
        }

        /// <summary>Resolves the alert's correlation (its escalation stops, what is still due is cancelled); none, or closed already, is no failure.</summary>
        internal Task ResolveAlertAsync(Guid alertId, string? reason, CancellationToken cancellationToken)
            => TolerateAsync("resolve", () => client.ResolveAsync(AlertCorrelation(alertId), Bounded(reason, 200), null, cancellationToken), conflictThrows: true);

        /// <summary>
        /// What happened to an action request: <c>observability.action.request.{event}</c>, keyed <c>action:{id}:{event}</c>. While the request is
        /// open its notices are correlated <c>action:{id}</c>; the notice that ends it resolves the correlation first and has none.
        /// </summary>
        public async Task ActionRequestAsync(ActionRequest request, string hostname, string type, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var @event = type.StartsWith(RequestPrefix, StringComparison.Ordinal) ? type[RequestPrefix.Length..] : type;
            var ends = Ends(request, type);
            var labels = new Dictionary<string, string>
            {
                ["event"] = type, ["action"] = Bounded(request.Action, MaxLabel)!, ["risk"] = Bounded(request.Risk, MaxLabel)!, ["policy"] = Bounded(request.Policy, MaxLabel)!,
                ["agent"] = request.AgentId.ToString(), ["request"] = request.Id.ToString(),
            };
            if (hostname.Length > 0) labels["hostname"] = Bounded(hostname, MaxLabel)!;
            if (request.AlertId is { } alert) labels["alert"] = alert.ToString();
            var notice = new NotificationToSend(ObservabilityTypes.ForPlatformEvent(type), $"action:{request.Id}:{@event}")
            {
                CorrelationKey = ends ? null : ActionCorrelation(request.Id),
                Labels = labels,
                Data = WithoutId(ActionRequestNotices.Payload(Guid.Empty, type, platform.Value.TenantKey, request, hostname, now)),
                Links = Link("approvals"),
            };
            if (Unavailable)
            {
                // Kept with the resolve it needs first, and replayed in order once the pipeline is up.
                Park(request.TenantId, NotificationParkedCalls.Send, ends ? ActionCorrelation(request.Id) : null, null, notice);
                logger.LogWarning("The notification pipeline didn't start ({Failure}): {Type} of action request {Request} is kept until it does", start!.Failure, type, request.Id);
                return;
            }
            // Still refused for a concurrent change: kept as when the pipeline is down (the resolve with its notice), and replayed in order.
            var parked = false;
            if (ends)
            {
                await TolerateAsync("resolve", () => client.ResolveAsync(ActionCorrelation(request.Id), @event, null, cancellationToken),
                    onConflict: () => { parked = true; Park(request.TenantId, NotificationParkedCalls.Send, ActionCorrelation(request.Id), null, notice); });
                if (parked)
                {
                    return;
                }
            }
            await SendAsync(notice, cancellationToken, onConflict: () => Park(request.TenantId, NotificationParkedCalls.Send, null, null, notice));
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
        {
            var request = JsonSerializer.Deserialize<NotificationToSend>(hold.Request, JsonSerializerOptions.Web)!;
            // Kept in shadow because the pipeline was down: legacy sent it then (architecture §6.4 A), so its own recipients are dry. A silence's
            // hold is real in any mode: legacy held it too.
            var dry = hold.Reason == NotificationHoldReasons.PipelineUnavailable && hold.Pipeline == ShadowMode;
            return SendAsync(dry ? request with { DryRun = true } : request, cancellationToken, conflictThrows: true);
        }

        internal NotificationToSend Firing(Rule rule, Alert alert, AlertTransition transition, AlertContext context, string? policy)
        {
            var labels = Labels(rule, alert, context, NotificationPlan.Firing);
            if (policy is not null) labels["escalationPolicy"] = policy;
            return new NotificationToSend(ObservabilityTypes.AlertFiring, TransitionKey(transition.Id))
            {
                CorrelationKey = AlertCorrelation(alert.Id),
                // When it fired: the escalation's first step is timed from it, so a firing sent late (held under a silence) still gets it on time.
                OccurredAt = transition.At,
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

        private Task<bool> SendAsync(NotificationToSend request, CancellationToken cancellationToken, bool conflictThrows = false, Action? onConflict = null)
            => TolerateAsync("send", async () => Checked(request, await client.SendAsync(request, cancellationToken)), conflictThrows, request.IdempotencyKey, onConflict);

        private const string ShadowMode = "shadow";

        /// <summary>The pipeline mode, as a kept call or hold records it.</summary>
        private string Mode => pipeline.Value.Pipeline.ToString().ToLowerInvariant();

        /// <summary>
        /// The adapter's guarantee (architecture, intake keeps its duplicate rule): in <c>new</c> it never sends again what shadow planned dry. A
        /// request that isn't dry itself, taken as a duplicate of a dry original, was sent to nobody: logged and counted
        /// (<c>juice.observability.notifications.dry_duplicates</c>, which stays 0; M50 checks it).
        /// </summary>
        private NotificationAccepted Checked(NotificationToSend request, NotificationAccepted accepted)
        {
            if (accepted is { Duplicate: true, DryRun: true } && !request.DryRun && pipeline.Value.Pipeline == NotificationsPipeline.New)
            {
                DryDuplicates.Add(1, new KeyValuePair<string, object?>("type", request.Type));
                logger.LogWarning("Notifications: {Key} ({Type}) is a duplicate of a request planned dry, so nothing was sent: the adapter sent again what shadow planned",
                    request.IdempotencyKey, request.Type);
            }
            return accepted;
        }

        /// <summary>
        /// A refusal is the pipeline's answer, not a failure of the platform's change: a correlation it doesn't have (its firing was never sent) or
        /// one already closed is expected; a concurrent change is transient (see the remarks); anything else is logged as an
        /// error and counted. True when the pipeline took the call.
        /// </summary>
        /// <remarks>
        /// A concurrent change (the correlation changed under the call: acknowledged or resolved elsewhere) is tried again here first, up to
        /// <see cref="ConflictRetries"/> times: the pipeline's lost update doesn't abort the host's transaction (m44 §6.2), so calling again is safe.
        /// Still refused, it is thrown on from the paths whose rollback is a retry (the outbox's planning, the release pass), and otherwise, on a
        /// person's or an agent's call (a silence, an acknowledgement, an action request), it is no failure of their change: false, and the
        /// hold pass reconciles a silence's hold, and an acknowledgement or an action request's notice is kept (<c>onConflict</c>, as when the pipeline is down) for the release pass to replay in order.
        /// </remarks>
        private async Task<bool> TolerateAsync<T>(string call, Func<Task<T>> work, bool conflictThrows = false, string? key = null, Action? onConflict = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await work();
                    return true;
                }
                catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.ConcurrentChange && attempt < ConflictRetries)
                {
                    logger.LogInformation("Notifications {Call} {Key}: {Code}, tried again ({Attempt})", call, key, refused.Code, attempt + 1);
                }
                catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.ConcurrentChange && conflictThrows)
                {
                    // Thrown on: the platform's change rolls back and is tried again against the new state (the outbox's retry, the release pass's next attempt).
                    logger.LogInformation("Notifications {Call} {Key}: {Code} after {Retries} retries, thrown on", call, key, refused.Code, ConflictRetries);
                    throw;
                }
                catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.ConcurrentChange)
                {
                    Refused.Add(1, new KeyValuePair<string, object?>("call", call), new KeyValuePair<string, object?>("code", refused.Code));
                    if (onConflict is not null)
                    {
                        onConflict();
                        logger.LogWarning("Notifications {Call} {Key}: {Code} after {Retries} retries; kept, and replayed by the release pass", call, key, refused.Code, ConflictRetries);
                    }
                    else
                    {
                        logger.LogWarning("Notifications {Call} {Key}: {Code} after {Retries} retries; left to heal", call, key, refused.Code, ConflictRetries);
                    }
                    return false;
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

        /// <summary>How many times a call refused with a concurrent change is tried again at once, before it is thrown on or left to heal.</summary>
        internal const int ConflictRetries = 2;

    }
}

namespace Juice.Observability.Notifications
{
    /// <summary>What releasing a hold did.</summary>
    internal enum HoldRelease
    {
        /// <summary>Released (or wasn't held).</summary>
        Released,

        /// <summary>The pipeline has no such correlation: nothing is held.</summary>
        Gone,

        /// <summary>Refused, or the pipeline didn't start: the hold stands.</summary>
        Failed,
    }
}
