using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Observability.Agents;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Discovery;
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
    public sealed class ProducerAdapter(INotificationClient client, PlatformDbContext db, IOptions<PlatformOptions> platform, PipelinePolicies policies,
        TimeProvider clock, ILogger<ProducerAdapter> logger, IOptions<PlatformNotificationsOptions> console, PipelineStart? start = null)
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

        /// <summary>
        /// The key of the service-managed resolver of the people who signed in. Role recipients name it: with a second resolver of the kind (an admin may add
        /// one), a recipient naming none is refused as ambiguous.
        /// </summary>
        internal const string SignedInResolver = "signed-in";

        /// <summary>The only role an approval item is for: the Console's <c>admin</c>, as <see cref="InboxNotificationsOptions.KnownRoles"/> names it.</summary>
        private const string AdminRole = "admin";

        public static string AlertCorrelation(Guid alertId) => $"alert:{alertId}";

        public static string TransitionKey(Guid transitionId) => $"transition:{transitionId}";

        public static string ActionCorrelation(Guid requestId) => $"action:{requestId}";

        /// <summary>The correlation (and the key) of an action request's approval item: <c>approval:{id}</c>, a lowercase <c>D</c>-format guid.</summary>
        public static string ApprovalCorrelation(Guid requestId) => $"approval:{requestId:D}";

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
                request = Firing(rule, alert, transition, context, await PolicyAsync(alert, context, cancellationToken));
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
                    // Resolved above unless the pipeline was down (whatever the reason it is kept).
                    ResolveAsked = transition.ToState != AlertState.Firing && !Unavailable,
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

        private static readonly object ParkedIdLock = new();
        private static long _lastParkedMillisecond;

        /// <summary>
        /// The id of the next kept call: a version 7 id, whose order is the replay's. Two calls kept in the same millisecond (an approval's resolve
        /// and its notice) would otherwise get ids in no order, so each is at least a millisecond after the one before.
        /// Ordering is guaranteed within one process (and so within one transaction) only; above a thousand kept calls a second the ids run
        /// ahead of the clock, by the backlog, until the burst ends.
        /// </summary>
        private static Guid NextParkedId()
        {
            lock (ParkedIdLock)
            {
                _lastParkedMillisecond = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastParkedMillisecond + 1);
                return Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(_lastParkedMillisecond));
            }
        }

        /// <summary>A call kept while the pipeline is down (<see cref="NotificationParkedCall"/>), in the change's transaction.</summary>
        private void Park(Guid tenantId, string kind, string? correlationKey, string? by, NotificationToSend? request)
            => db.NotificationParkedCalls.Add(new NotificationParkedCall
            {
                Id = NextParkedId(), TenantId = tenantId,
                Kind = kind, CorrelationKey = correlationKey, By = by, CreatedAt = clock.GetUtcNow(), Pipeline = Mode,
                Request = request is null ? null : JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
            });

        /// <summary>
        /// Replays a kept call once the pipeline is up: an acknowledgement, or a resolve and the request. False when it must stay kept for the next
        /// pass (not a failed attempt): an approval whose request a decision holds right now.
        /// </summary>
        internal async Task<bool> ReplayAsync(NotificationParkedCall call, CancellationToken cancellationToken)
        {
            if (call.Kind == NotificationParkedCalls.Acknowledge)
            {
                await TolerateAsync("acknowledge", () => client.AcknowledgeAsync(call.CorrelationKey!, call.By, cancellationToken), conflictThrows: true);
                return true;
            }
            if (call.Kind == NotificationParkedCalls.Resolve)
            {
                // A resolve alone (an approval's): By is its outcome. None to resolve (the inbox was never on) is no failure.
                await TolerateAsync("resolve", () => client.ResolveAsync(call.CorrelationKey!, call.By, null, cancellationToken), conflictThrows: true);
                return true;
            }
            if (call.CorrelationKey is { } resolve)
            {
                await TolerateAsync("resolve", () => client.ResolveAsync(resolve, null, null, cancellationToken), conflictThrows: true);
            }
            if (WithoutRolesIfInboxOff(JsonSerializer.Deserialize<NotificationToSend>(call.Request!, JsonSerializerOptions.Web)!) is { } request)
            {
                switch (await StillWaitingAsync(request, cancellationToken))
                {
                    case null:
                        // A decision holds the request's row: it is not waited for (that could deadlock with the notice this pass replays before it).
                        // Kept, and looked at again by the next pass.
                        return false;
                    case true:
                        await SendAsync(request, cancellationToken, conflictThrows: true);
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// False for an approval item whose request is not waiting any more: one decided after the call was kept (its resolve ran before the item
        /// existed, a no-op) would otherwise be sent and stay open for good. The request's row is locked shared while this reads it, so a decision
        /// racing the send finds the item to resolve. Null when a decision holds the row now (<c>SKIP LOCKED</c>: it is not waited for), so the call
        /// stays kept for the next pass. Anything but an approval is as it was.
        /// </summary>
        private async Task<bool?> StillWaitingAsync(NotificationToSend request, CancellationToken cancellationToken)
        {
            if (request.Type != ObservabilityTypes.ActionApproval)
            {
                return true;
            }
            var key = request.CorrelationKey;
            if (key is null || !key.StartsWith("approval:", StringComparison.Ordinal) || !Guid.TryParse(key["approval:".Length..], out var id))
            {
                return false;
            }
            var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM action_requests WHERE id = {id} FOR SHARE SKIP LOCKED").ToListAsync(cancellationToken);
            // No row locked: either none (the request is gone), or a decision holds it.
            if (locked.Count == 0 && await db.ActionRequests.AsNoTracking().AnyAsync(r => r.Id == id, cancellationToken))
            {
                return null;
            }
            var now = clock.GetUtcNow();
            var waiting = await db.ActionRequests.AsNoTracking().AnyAsync(r => r.Id == id && r.State == ActionRequestState.Pending && r.ExpiresAt > now, cancellationToken);
            if (!waiting)
            {
                logger.LogInformation("Approval {Key} is dropped: its request is not waiting any more", request.IdempotencyKey);
            }
            return waiting;
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
            // The approval item (M56): an admin's inbox gets one while the request waits, and it is resolved when the request ends, whatever the
            // inbox's state now (it may have been turned off since the item was made). Never a route's: see Approval().
            var approval = type == ActionRequestEvents.Created && console.Value.Inbox.Enabled ? Approval(request, notice) : null;
            var outcome = ApprovalOutcome(request, type);
            if (Unavailable)
            {
                // Kept with the resolve it needs first, and replayed in order once the pipeline is up.
                if (outcome is not null)
                {
                    Park(request.TenantId, NotificationParkedCalls.Resolve, ApprovalCorrelation(request.Id), outcome, null);
                }
                Park(request.TenantId, NotificationParkedCalls.Send, ends ? ActionCorrelation(request.Id) : null, null, notice);
                if (approval is not null)
                {
                    Park(request.TenantId, NotificationParkedCalls.Send, null, null, approval);
                }
                logger.LogWarning("The notification pipeline didn't start ({Failure}): {Type} of action request {Request} is kept until it does", start!.Failure, type, request.Id);
                return;
            }
            if (outcome is not null)
            {
                // Before the notice: the item is closed for everyone whether or not the request's own correlation ends. A concurrent change is kept and replayed in order.
                await TolerateAsync("resolve", () => client.ResolveAsync(ApprovalCorrelation(request.Id), outcome, null, cancellationToken),
                    onConflict: () => Park(request.TenantId, NotificationParkedCalls.Resolve, ApprovalCorrelation(request.Id), outcome, null));
            }
            if (approval is not null)
            {
                await SendAsync(approval, cancellationToken, onConflict: () => Park(request.TenantId, NotificationParkedCalls.Send, null, null, approval));
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

        /// <summary>
        /// The approval item for a request that was just made: <c>observability.action.approval</c>, keyed and correlated <c>approval:{id}</c>, with
        /// the admins who signed in as its <b>only</b> recipients and the notice's own data. It names no route, and is never sent without its roles
        /// (<see cref="WithoutRolesIfInboxOff"/>): a route's default delivery would send an approval request to a channel.
        /// </summary>
        private NotificationToSend Approval(ActionRequest request, NotificationToSend notice) => new(ObservabilityTypes.ActionApproval, ApprovalCorrelation(request.Id))
        {
            CorrelationKey = ApprovalCorrelation(request.Id),
            Recipients = [new NotificationRecipientSpec(RecipientKinds.Role, Id: AdminRole, Resolver: SignedInResolver)],
            Labels = notice.Labels,
            Data = notice.Data,
            Links = notice.Links,
        };

        /// <summary>How the request's approval item ends: <c>Approved by erin</c>; null for an event that doesn't end the wait (<c>created</c>, and the verification's).</summary>
        private static string? ApprovalOutcome(ActionRequest request, string type)
        {
            var word = type switch
            {
                ActionRequestEvents.Approved => "Approved",
                ActionRequestEvents.Rejected => "Rejected",
                ActionRequestEvents.Cancelled => "Cancelled",
                ActionRequestEvents.Expired => "Expired",
                _ => null,
            };
            return word is null ? null : Bounded($"{word} by {request.DecidedBy ?? ActionRequestSweeper.Actor}", 200);
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
            => WithoutRolesIfInboxOff(JsonSerializer.Deserialize<NotificationToSend>(hold.Request, JsonSerializerOptions.Web)!) is { } request
                ? SendAsync(request, cancellationToken, conflictThrows: true)
                : Task.FromResult(false);

        /// <summary>
        /// A request kept while the inbox was on (a silence's hold, a parked send) and sent after it was turned off: the pipeline refuses a
        /// <c>role</c> recipient with no inbox, which would lose the notice for its routes too. Sent without them, its routes are as before.
        /// </summary>
        internal NotificationToSend? WithoutRolesIfInboxOff(NotificationToSend request)
        {
            // An approval has no route to fall back to: with the inbox off, or with no role left, it is dropped (null), logged and counted
            // (refused{call=send,code=inbox_off}), never sent with its default recipients (the routes it matches).
            if (request.Type == ObservabilityTypes.ActionApproval)
            {
                if (console.Value.Inbox.Enabled && request.Recipients is { Count: > 0 } named && named.All(r => r.Kind == RecipientKinds.Role))
                {
                    return request;
                }
                DropApproval(request);
                return null;
            }
            if (console.Value.Inbox.Enabled || request.Recipients is not { } recipients || !recipients.Any(r => r.Kind == RecipientKinds.Role))
            {
                return request;
            }
            logger.LogWarning("The inbox is off: request {Key} is sent without its role recipients", request.IdempotencyKey);
            var rest = recipients.Where(r => r.Kind != RecipientKinds.Role).ToList();
            return request with { Recipients = rest.Count == 0 ? null : rest };
        }

        internal NotificationToSend Firing(Rule rule, Alert alert, AlertTransition transition, AlertContext context, string? policy)
        {
            var labels = Labels(rule, alert, context, NotificationPlan.Firing);
            if (policy is not null) labels["escalationPolicy"] = policy;
            return new NotificationToSend(ObservabilityTypes.AlertFiring, TransitionKey(transition.Id))
            {
                CorrelationKey = AlertCorrelation(alert.Id),
                // The people of the configured Console roles, when the inbox is on (M54): who signed in with one gets an item. Naming recipients
                // replaces the default (the routes the request matches), so the routes are named too: delivery to them is as it was.
                Recipients = console.Value.Inbox.Enabled
                    ? [new NotificationRecipientSpec(RecipientKinds.Route), .. console.Value.Inbox.EffectiveRoles.Select(role => new NotificationRecipientSpec(RecipientKinds.Role, Id: role, Resolver: SignedInResolver))]
                    : null,
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
        /// The policy the pipeline escalates by: the pipeline runs one escalation per notification, so of the policies the platform's own selection
        /// matches (<see cref="EscalationSelection"/>), the first by key (ordinal). More than one is logged. A selected policy the pipeline no longer
        /// has (deleted since) is logged too, and the notification goes out without escalating.
        /// </summary>
        private async Task<string?> PolicyAsync(Alert alert, AlertContext context, CancellationToken cancellationToken)
        {
            var enabled = await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == alert.TenantId && p.Enabled).ToListAsync(cancellationToken);
            var matching = EscalationSelection.Match(enabled, context, alert.Severity);
            if (matching.Count == 0)
            {
                return null;
            }
            if (matching.Count > 1)
            {
                logger.LogWarning("Alert {Alert} matches {Count} escalation policies ({Policies}); the notification pipeline escalates by {Policy} only",
                    alert.Id, matching.Count, string.Join(", ", matching.Select(p => p.Key)), matching[0].Key);
            }
            var key = matching[0].Key;
            if (!await policies.ExistsAsync(key, cancellationToken))
            {
                logger.LogWarning("Alert {Alert} is selected for escalation policy {Policy}, which the notification pipeline doesn't have; it is sent without escalating", alert.Id, key);
                return null;
            }
            return key;
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

        /// <summary>The Console's page as <c>self</c>, when the Console's address is set (<c>Notifications:ConsoleUrl</c>, checked at start).</summary>
        private Dictionary<string, string>? Link(string page)
            => console.Value.Link(page) is { } link ? new() { ["self"] = link } : null;

        /// <summary>Today's payload, less its id (the pipeline gives each delivery its own).</summary>
        internal static JsonElement WithoutId(string payload)
        {
            var json = JsonNode.Parse(payload)!.AsObject();
            json.Remove("id");
            return JsonSerializer.SerializeToElement(json);
        }

        private static string? Bounded(string? value, int max) => value is { Length: > 0 } ? value.Length > max ? value[..max] : value : null;

        /// <summary>Sends it; false when the pipeline refused it, or when it was dropped (an approval that can't go to its roles).</summary>
        private async Task<bool> SendAsync(NotificationToSend request, CancellationToken cancellationToken, bool conflictThrows = false, Action? onConflict = null)
        {
            var sent = false;
            var taken = await TolerateAsync("send", async () => (sent = await SendOrWithoutRolesAsync(request, cancellationToken) is not null), conflictThrows,
                request.IdempotencyKey, onConflict);
            return taken && sent;
        }

        /// <summary>An approval that can't go to its roles is dropped, not sent on: logged, and counted as <c>refused{call=send,code=inbox_off}</c>.</summary>
        private void DropApproval(NotificationToSend request)
        {
            Refused.Add(1, new KeyValuePair<string, object?>("call", "send"), new KeyValuePair<string, object?>("code", "inbox_off"));
            logger.LogWarning("Approval {Key} is dropped: it goes to the admins' inbox only, and the inbox can't take it", request.IdempotencyKey);
        }

        /// <summary>
        /// Sends the request; when the pipeline refuses only its role recipients (<see cref="ErrorCodes.RecipientNotAllowed"/>: the shared
        /// <c>signed-in</c> resolver was disabled by a replica that restarted with the inbox off), sends it once more without them. Delivery to the
        /// routes never depends on the inbox, and the correlation must exist for the acknowledgement and the resolve.
        /// </summary>
        private async Task<NotificationAccepted?> SendOrWithoutRolesAsync(NotificationToSend request, CancellationToken cancellationToken)
        {
            try
            {
                return await client.SendAsync(request, cancellationToken);
            }
            catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.RecipientNotAllowed && OnlyRoles(request, refused)
                && request.Type == ObservabilityTypes.ActionApproval)
            {
                // Its roles are all it has: sent without them it would go to the routes the request matches.
                DropApproval(request);
                return null;
            }
            catch (NotificationRefusedException refused) when (refused.Code is ErrorCodes.RecipientNotAllowed && OnlyRoles(request, refused))
            {
                logger.LogWarning("The notification pipeline refused the role recipients of {Key} (the inbox is off on another replica?): it is sent without its role recipients", request.IdempotencyKey);
                var rest = request.Recipients!.Where(r => r.Kind != RecipientKinds.Role).ToList();
                return await client.SendAsync(request with { Recipients = rest.Count == 0 ? null : rest }, cancellationToken);
            }
        }

        /// <summary>Whether every field the refusal names is a role recipient of the request.</summary>
        private static bool OnlyRoles(NotificationToSend request, NotificationRefusedException refused)
        {
            if (request.Recipients is not { } recipients || refused.Errors is not { Count: > 0 } errors)
            {
                return false;
            }
            return errors.All(e => e.Pointer.StartsWith("/recipients/", StringComparison.Ordinal)
                && int.TryParse(e.Pointer.AsSpan("/recipients/".Length).ToString().Split('/')[0], out var at)
                && at >= 0 && at < recipients.Count && recipients[at].Kind == RecipientKinds.Role);
        }

        /// <summary>The pipeline mode, as a kept call or hold records it: the pipeline is the only one since 2.0.</summary>
        private const string Mode = "new";

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
