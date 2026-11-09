using System.Text.Json;
using Juice.Observability.Agents;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <param name="Action">The action to run.</param>
    /// <param name="Parameters">Its parameters.</param>
    /// <param name="Reason">Why, for whoever approves it.</param>
    /// <param name="AlertId">The alert that prompted it: it must still be firing, by fresh evidence, when the request is approved.</param>
    /// <param name="Target">What it should make healthy; the alert's subject when an alert is given and this isn't.</param>
    public sealed record ActionRequestBody(string? Action, JsonElement? Parameters, string? Reason, Guid? AlertId = null, ActionTargetBody? Target = null);

    /// <summary>A health subject: <c>Service</c> (with its environment), <c>Instance</c>, <c>Application</c> (with its environment) or <c>Host</c>.</summary>
    public sealed record ActionTargetBody(string? Type, Guid? Id, Guid? EnvironmentId);

    public sealed record ActionDecision(string? Comment);

    /// <summary>Requests waiting for a decision: all of them, and those the caller can decide.</summary>
    public sealed record ActionRequestsWaiting(int Total, int ForYou);

    /// <summary>
    /// A request and where it is. <c>State</c> is as it is now: a pending request past its expiry reads <c>Expired</c>. <c>Command</c> is what it
    /// became once issued, with the agent's answer.
    /// </summary>
    public sealed record ActionRequestDto(Guid Id, Guid AgentId, string Hostname, string Action, JsonElement Parameters, string Risk, string Policy, string Reason,
        string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, string State, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment,
        AgentCommandDto? Command, Guid? AlertId = null, ActionTargetBody? Target = null, string Verification = nameof(ActionVerification.None),
        string? VerificationNote = null, DateTimeOffset? VerifiedAt = null);

    /// <summary>
    /// Requests to run actions under the approval policy for their risk (roadmap Phase 8). Operators and admins request; what the policy needs decides
    /// what happens next: <c>None</c> issues at once, <c>Admin</c> at once for an admin and after an admin's approval for an operator, <c>SecondPerson</c>
    /// after an admin other than the requester approves. A request waits at most <c>Actions:ApprovalLifetime</c>. One request per agent, action and
    /// parameters is open at a time. Every step is audited; the command carries the request as its approval reference.
    /// </summary>
    public static class ActionRequestEndpoints
    {
        public static RouteGroupBuilder MapActionRequestEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapPost("/agents/{agentId:guid}/action-requests", CreateAsync);
            tenant.MapGet("/action-requests", ListAsync);
            tenant.MapGet("/action-requests/waiting", WaitingAsync);
            tenant.MapGet("/action-requests/{requestId:guid}", GetAsync);
            tenant.MapPost("/action-requests/{requestId:guid}/approval", ApproveAsync);
            tenant.MapPost("/action-requests/{requestId:guid}/rejection", RejectAsync);
            tenant.MapPost("/action-requests/{requestId:guid}/cancellation", CancelAsync);
            return tenant;
        }

        /// <summary>The policy in words, for refusals and the Console.</summary>
        public static string Describe(string policy) => policy switch
        {
            ApprovalPolicies.None => "no approval",
            ApprovalPolicies.Admin => "an admin's approval",
            _ => "the approval of an admin other than the requester",
        };

        /// <summary>What the signed command carries: the request, and who approved it.</summary>
        internal static string Reference(ActionRequest request) => $"action-request:{request.Id};approved-by:{request.DecidedBy ?? "policy"}";

        /// <summary>Tells the configured endpoints what happened to the request, in the transaction that made it happen.</summary>
        private static async Task NotifyAsync(PlatformDbContext db, IServiceProvider services, ActionRequest request, string type, DateTimeOffset now, CancellationToken ct)
        {
            var options = services.GetRequiredService<IOptions<ActionApprovalOptions>>().Value;
            if (options.NotifyEndpoints.Count == 0) return;
            var hostname = await db.Agents.AsNoTracking().Where(a => a.Id == request.AgentId).Select(a => a.Hostname).SingleOrDefaultAsync(ct) ?? "";
            await ActionRequestNotices.AddAsync(db, options, services.GetRequiredService<IOptions<PlatformOptions>>().Value.TenantKey, request, hostname, type, now, ct);
        }

        private static IResult NotFound() => Mutation.Problem(StatusCodes.Status404NotFound, "Request not found", "No such action request.");

        private static ActionRequestState StateOf(ActionRequest r, DateTimeOffset now) => r.State == ActionRequestState.Pending && r.ExpiresAt <= now ? ActionRequestState.Expired : r.State;

        private static ActionRequestDto ToDto(ActionRequest r, string hostname, AgentCommand? command, DateTimeOffset now) => new(r.Id, r.AgentId, hostname, r.Action,
            JsonDocument.Parse(r.Parameters).RootElement.Clone(), r.Risk, r.Policy, r.Reason, r.RequestedBy, r.RequestedAt, r.ExpiresAt, StateOf(r, now).ToString(), r.DecidedBy,
            r.DecidedAt, r.Comment, command is null ? null : AgentEndpoints.ToDto(command, now), r.AlertId,
            r.TargetId is { } target ? new ActionTargetBody(r.TargetType?.ToString(), target, r.TargetEnvironmentId) : null, r.Verification.ToString(), r.VerificationNote, r.VerifiedAt);

        /// <summary>The refusal for preconditions that don't hold: 409, titled by its code.</summary>
        private static IResult Refused(PreconditionProblem problem) => Mutation.Problem(StatusCodes.Status409Conflict,
            problem.Code == PreconditionProblem.Stale ? "Stale evidence" : "Precondition failed", $"{problem.Code}: {problem.Detail}.");

        /// <summary>The request's alert and target: as given, the target defaulting to the alert's subject. Null, with the errors, when they don't hold.</summary>
        private static async Task<(Guid? AlertId, HealthSubject? Type, Guid? Id, Guid? EnvironmentId)?> TargetAsync(PlatformDbContext db, Guid tenantId, ActionRequestBody body,
            List<string> errors, CancellationToken ct)
        {
            HealthSubject? type = null;
            Guid? id = null, environment = null;
            if (body.Target is { } target)
            {
                if (!Enum.TryParse<HealthSubject>(target.Type, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)) errors.Add($"target.type is one of {string.Join(", ", Enum.GetNames<HealthSubject>())}.");
                if (target.Id is null) errors.Add("target.id is required.");
                (type, id, environment) = (parsed, target.Id, parsed == HealthSubject.Host ? null : target.EnvironmentId);
            }
            if (body.AlertId is { } alertId)
            {
                var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
                if (alert is null) errors.Add("alertId: no such alert.");
                else if (body.Target is null && alert.SubjectId is { } subject) (type, id, environment) = (alert.SubjectType, subject, alert.EnvironmentId);
            }
            return errors.Count > 0 ? null : (body.AlertId, type, id, environment);
        }

        /// <summary>
        /// The open request for the same agent, action and parameters, if any: pending and not expired, or issued with its command not answered yet.
        /// Called under <see cref="LockAsync"/>, so two at once can't both find none.
        /// </summary>
        internal static async Task<ActionRequest?> OpenDuplicateAsync(PlatformDbContext db, Guid agentId, string action, string parameters, DateTimeOffset now, CancellationToken ct)
        {
            var same = await db.ActionRequests.AsNoTracking()
                .Where(r => r.AgentId == agentId && r.Action == action && r.Parameters == parameters
                    && ((r.State == ActionRequestState.Pending && r.ExpiresAt > now) || r.State == ActionRequestState.Issued))
                .ToListAsync(ct);
            foreach (var request in same)
            {
                if (request.State == ActionRequestState.Pending) return request;
                var running = await db.AgentCommands.AsNoTracking().AnyAsync(c => c.Id == request.CommandId
                    && (c.State == AgentCommandState.Pending || c.State == AgentCommandState.Delivered) && c.ExpiresAt > now, ct);
                if (running) return request;
            }
            return null;
        }

        /// <summary>Holds the lock for one agent, action and parameters until the transaction ends.</summary>
        internal static Task LockAsync(PlatformDbContext db, Guid agentId, string action, string parameters, CancellationToken ct)
            => db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WorkerLease.KeyOf($"action-request:{agentId}:{action}:{parameters}")})", ct);

        internal static IResult Duplicate(ActionRequest open) => Mutation.Problem(StatusCodes.Status409Conflict, "Duplicate request",
            $"Request {open.Id} for the same action is still {(open.State == ActionRequestState.Pending ? "waiting for approval" : "running")}: wait for it, or cancel it.");

        private static async Task<IResult> CreateAsync(Guid tenantId, Guid agentId, ActionRequestBody? body, HttpContext http, PlatformDbContext db, IServiceProvider services,
            IOptions<PlatformOptions> platform, IOptions<ActionApprovalOptions> approval, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "action.request.create", "action_request");
            if (await mutation.RequireAsync(agentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (services.GetService<AgentAuthority>() is not { } authority) return Mutation.Problem(StatusCodes.Status409Conflict, "Agents are not enabled", "This deployment has no agent CA.");
            if (body is null) return Mutation.Invalid(["A body is required."]);
            var reason = ChangeSanitizer.Text(body.Reason, 500);
            if (reason.Length == 0) return Mutation.Invalid(["reason is required: say why this should run."]);
            if (await mutation.ReplayAsync($"POST agents/{agentId}/action-requests", body, ct) is { } replay) return replay;
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
            if (agent is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Agent not found", "No such agent.");
            if (agent.Status != AgentStatus.Active) return Mutation.Problem(StatusCodes.Status409Conflict, "Agent revoked", "A revoked agent takes no commands.");
            if (AgentEndpoints.Resolve(agent, body.Action, body.Parameters, services, approval.Value, out var problem) is not { } action) return problem!;
            var errors = new List<string>();
            if (await TargetAsync(db, tenantId, body, errors, ct) is not { } aim) return Mutation.Invalid(errors);

            var now = clock.GetUtcNow();
            var parameters = action.Parameters.GetRawText();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, agentId, action.Name, parameters, ct);
            if (await OpenDuplicateAsync(db, agentId, action.Name, parameters, now, ct) is { } open)
            {
                mutation.Audit(AuditOutcome.Failed, agentId.ToString(), new() { ["action"] = action.Name, ["reason"] = "duplicate", ["open"] = open.Id.ToString() });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Duplicate(open);
            }
            // Nothing is asked for on evidence that's stale or no longer says there's anything to fix.
            if (await ActionEvidence.CheckAsync(db, tenantId, aim.AlertId, aim.Type, aim.Id, aim.EnvironmentId, now, approval.Value.StaleEvidence, ct) is { } unmet)
            {
                mutation.Audit(AuditOutcome.Failed, agentId.ToString(), new() { ["action"] = action.Name, ["reason"] = unmet.Code, ["detail"] = unmet.Detail });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Refused(unmet);
            }
            var request = new ActionRequest
            {
                TenantId = tenantId, AgentId = agentId, Action = action.Name, Parameters = parameters, Risk = action.Risk, Policy = action.Policy, Reason = reason,
                RequestedBy = mutation.Actor, RequestedAt = now, ExpiresAt = now + approval.Value.ApprovalLifetime, State = ActionRequestState.Pending,
                AlertId = aim.AlertId, TargetType = aim.Type, TargetId = aim.Id, TargetEnvironmentId = aim.EnvironmentId,
            };
            db.ActionRequests.Add(request);
            AgentCommand? command = null;
            // What needs no one else: at once, approved by the policy (None) or by the admin who asked (Admin).
            var self = action.Policy == ApprovalPolicies.Admin && http.User.IsInRole(Roles.Admin);
            if (action.Policy == ApprovalPolicies.None || self)
            {
                request.State = ActionRequestState.Issued;
                request.DecidedBy = self ? mutation.Actor : null;
                request.DecidedAt = now;
                command = AgentEndpoints.Sign(authority, platform.Value.TenantKey, tenantId, agentId, action.Name, action.Parameters, mutation.Actor, now,
                    AgentEndpoints.DefaultLifetime(action), Reference(request));
                request.CommandId = command.Id;
                request.Verification = request.TargetId is null ? ActionVerification.None : ActionVerification.Verifying;
                db.AgentCommands.Add(command);
            }
            else
            {
                await ActionRequestNotices.AddAsync(db, approval.Value, platform.Value.TenantKey, request, agent.Hostname, ActionRequestEvents.Created, now, ct);
            }
            mutation.Audit(AuditOutcome.Succeeded, request.Id.ToString(), new()
            {
                ["agent"] = agentId.ToString(), ["action"] = action.Name, ["risk"] = action.Risk, ["policy"] = action.Policy, ["state"] = request.State.ToString(),
            });
            var result = mutation.Remember(StatusCodes.Status201Created, ToDto(request, agent.Hostname, command, now), now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (command is not null) services.GetService<AgentWakeups>()?.Wake(agentId);
            return result;
        }

        /// <summary>Newest first; <c>state</c> narrows to one (Pending means still waiting and not expired), <c>agentId</c> to one agent.</summary>
        private static async Task<IResult> ListAsync(Guid tenantId, string? state, Guid? agentId, int? limit, string? cursor, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid) return invalid;
            var now = clock.GetUtcNow();
            var query = db.ActionRequests.AsNoTracking().Where(r => r.TenantId == tenantId);
            if (agentId is { } agent) query = query.Where(r => r.AgentId == agent);
            if (state is { Length: > 0 })
            {
                if (!Enum.TryParse<ActionRequestState>(state, ignoreCase: true, out var wanted)) return Mutation.Invalid([$"state is one of {string.Join(", ", Enum.GetNames<ActionRequestState>())}."]);
                query = wanted switch
                {
                    ActionRequestState.Pending => query.Where(r => r.State == ActionRequestState.Pending && r.ExpiresAt > now),
                    ActionRequestState.Expired => query.Where(r => r.State == ActionRequestState.Expired || (r.State == ActionRequestState.Pending && r.ExpiresAt <= now)),
                    _ => query.Where(r => r.State == wanted),
                };
            }
            if (after is not null) query = query.Where(r => r.Id < after.After);
            var rows = await query.OrderByDescending(r => r.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var hostnames = await db.Agents.AsNoTracking().Where(a => page.Select(r => r.AgentId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Hostname, ct);
            var commandIds = page.Where(r => r.CommandId is not null).Select(r => r.CommandId!.Value).ToList();
            var commands = await db.AgentCommands.AsNoTracking().Where(c => commandIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            return Results.Ok(new Page<ActionRequestDto>([.. page.Select(r => ToDto(r, hostnames.GetValueOrDefault(r.AgentId, ""), r.CommandId is { } c ? commands.GetValueOrDefault(c) : null, now))],
                rows.Count > take ? new Cursor("ar", page[^1].Id).Encode() : null));
        }

        /// <summary>
        /// How many requests wait, and how many of them the caller could decide: none for a non-admin, and an admin's own second-person requests
        /// don't count. For the Console's menu count, polled from every page, so it reads two counts and nothing else.
        /// </summary>
        private static async Task<IResult> WaitingAsync(Guid tenantId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var waiting = db.ActionRequests.AsNoTracking().Where(r => r.TenantId == tenantId && r.State == ActionRequestState.Pending && r.ExpiresAt > now);
            var total = await waiting.CountAsync(ct);
            var actor = http.User.Actor();
            var forYou = total == 0 || !http.User.IsInRole(Roles.Admin) ? 0
                : await waiting.CountAsync(r => !(r.Policy == ApprovalPolicies.SecondPerson && r.RequestedBy == actor), ct);
            return Results.Ok(new ActionRequestsWaiting(total, forYou));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid requestId, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var request = await db.ActionRequests.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == requestId, ct);
            if (request is null) return NotFound();
            var hostname = await db.Agents.AsNoTracking().Where(a => a.Id == request.AgentId).Select(a => a.Hostname).SingleAsync(ct);
            var command = request.CommandId is { } id ? await db.AgentCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) : null;
            return Results.Ok(ToDto(request, hostname, command, clock.GetUtcNow()));
        }

        /// <summary>The request, locked for this transaction, so two decisions on it happen one after the other.</summary>
        private static async Task<ActionRequest?> LockedAsync(PlatformDbContext db, Guid tenantId, Guid requestId, CancellationToken ct)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM action_requests WHERE id = {requestId} FOR UPDATE", ct);
            return await db.ActionRequests.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == requestId, ct);
        }

        /// <summary>
        /// An admin approves a pending request, and its command is signed with the request as its approval reference. Under <c>SecondPerson</c> the
        /// requester can't approve their own. The action is checked again against the agent as it is now: a request whose action no longer holds isn't signed.
        /// </summary>
        private static async Task<IResult> ApproveAsync(Guid tenantId, Guid requestId, ActionDecision? decision, HttpContext http, PlatformDbContext db, IServiceProvider services,
            IOptions<PlatformOptions> platform, IOptions<ActionApprovalOptions> approval, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "action.request.approve", "action_request");
            if (await mutation.RequireAsync(requestId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            if (services.GetService<AgentAuthority>() is not { } authority) return Mutation.Problem(StatusCodes.Status409Conflict, "Agents are not enabled", "This deployment has no agent CA.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var request = await LockedAsync(db, tenantId, requestId, ct);
            if (request is null) return NotFound();
            var now = clock.GetUtcNow();
            if (await Undecidable(request, now, mutation, db, services, ct) is { } closed)
            {
                await transaction.CommitAsync(ct);
                return closed;
            }
            if (request.Policy == ApprovalPolicies.SecondPerson && request.RequestedBy == mutation.Actor)
            {
                mutation.Audit(AuditOutcome.Denied, request.Id.ToString(), new() { ["reason"] = "own_request" });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Mutation.Problem(StatusCodes.Status403Forbidden, "A second person must approve", "This request needs an admin other than the one who asked.");
            }
            var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == request.AgentId, ct);
            if (agent.Status != AgentStatus.Active) return Mutation.Problem(StatusCodes.Status409Conflict, "Agent revoked", "A revoked agent takes no commands.");
            var parameters = JsonDocument.Parse(request.Parameters).RootElement.Clone();
            if (AgentEndpoints.Resolve(agent, request.Action, parameters, services, approval.Value, out var problem) is not { } action) return problem!;
            // Checked again now: what was broken when it was asked for may have recovered, or its evidence gone stale, since.
            if (await ActionEvidence.CheckAsync(db, tenantId, request.AlertId, request.TargetType, request.TargetId, request.TargetEnvironmentId, now,
                approval.Value.StaleEvidence, ct) is { } unmet)
            {
                mutation.Audit(AuditOutcome.Failed, request.Id.ToString(), new() { ["reason"] = unmet.Code, ["detail"] = unmet.Detail });
                if (unmet.IsFinal)
                {
                    // Nothing left to fix: closed, so it doesn't wait to be approved by someone who didn't see this.
                    request.State = ActionRequestState.Cancelled;
                    request.DecidedBy = ActionRequestSweeper.Actor;
                    request.DecidedAt = now;
                    request.Comment = unmet.Detail.Length > 500 ? unmet.Detail[..500] : unmet.Detail;
                    await NotifyAsync(db, services, request, ActionRequestEvents.Cancelled, now, ct);
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Refused(unmet);
            }

            request.State = ActionRequestState.Issued;
            request.DecidedBy = mutation.Actor;
            request.DecidedAt = now;
            request.Comment = ChangeSanitizer.Text(decision?.Comment, 500) is { Length: > 0 } comment ? comment : null;
            var command = AgentEndpoints.Sign(authority, platform.Value.TenantKey, tenantId, request.AgentId, action.Name, action.Parameters, mutation.Actor, now,
                AgentEndpoints.DefaultLifetime(action), Reference(request));
            request.CommandId = command.Id;
            request.Verification = request.TargetId is null ? ActionVerification.None : ActionVerification.Verifying;
            db.AgentCommands.Add(command);
            mutation.Audit(AuditOutcome.Succeeded, request.Id.ToString(), new() { ["action"] = request.Action, ["requestedBy"] = request.RequestedBy, ["command"] = command.Id.ToString() });
            await ActionRequestNotices.AddAsync(db, approval.Value, platform.Value.TenantKey, request, agent.Hostname, ActionRequestEvents.Approved, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            services.GetService<AgentWakeups>()?.Wake(request.AgentId);
            return Results.Ok(ToDto(request, agent.Hostname, command, now));
        }

        /// <summary>The refusal for a request that's no longer waiting, closing it first if it just expired; null when it can still be decided.</summary>
        private static async Task<IResult?> Undecidable(ActionRequest request, DateTimeOffset now, Mutation mutation, PlatformDbContext db, IServiceProvider services,
            CancellationToken ct)
        {
            if (request.State == ActionRequestState.Pending && request.ExpiresAt <= now)
            {
                request.State = ActionRequestState.Expired;
                mutation.Audit(AuditOutcome.Failed, request.Id.ToString(), new() { ["reason"] = "expired" });
                await NotifyAsync(db, services, request, ActionRequestEvents.Expired, now, ct);
                await db.SaveChangesAsync(ct);
            }
            return request.State == ActionRequestState.Pending ? null
                : Mutation.Problem(StatusCodes.Status409Conflict, "Not waiting", $"This request is {request.State.ToString().ToLowerInvariant()}: only a pending one can be decided.");
        }

        private static async Task<IResult> RejectAsync(Guid tenantId, Guid requestId, ActionDecision? decision, HttpContext http, PlatformDbContext db, IServiceProvider services,
            TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "action.request.reject", "action_request");
            if (await mutation.RequireAsync(requestId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            var comment = ChangeSanitizer.Text(decision?.Comment, 500);
            if (comment.Length == 0) return Mutation.Invalid(["comment is required: say why it shouldn't run."]);
            return await CloseAsync(tenantId, requestId, ActionRequestState.Rejected, comment, mutation, db, services, clock, ct);
        }

        /// <summary>The requester withdraws their own pending request; an admin can withdraw anyone's.</summary>
        private static async Task<IResult> CancelAsync(Guid tenantId, Guid requestId, ActionDecision? decision, HttpContext http, PlatformDbContext db, IServiceProvider services,
            TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "action.request.cancel", "action_request");
            if (await mutation.RequireAsync(requestId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            var owner = await db.ActionRequests.AsNoTracking().Where(r => r.TenantId == tenantId && r.Id == requestId).Select(r => r.RequestedBy).SingleOrDefaultAsync(ct);
            if (owner is not null && owner != mutation.Actor && !http.User.IsInRole(Roles.Admin))
            {
                mutation.Audit(AuditOutcome.Denied, requestId.ToString(), new() { ["reason"] = "not_requester" });
                await db.SaveChangesAsync(ct);
                return Mutation.Problem(StatusCodes.Status403Forbidden, "Forbidden", "Only the requester or an admin can cancel a request.");
            }
            return await CloseAsync(tenantId, requestId, ActionRequestState.Cancelled, ChangeSanitizer.Text(decision?.Comment, 500), mutation, db, services, clock, ct);
        }

        private static async Task<IResult> CloseAsync(Guid tenantId, Guid requestId, ActionRequestState to, string comment, Mutation mutation, PlatformDbContext db,
            IServiceProvider services, TimeProvider clock, CancellationToken ct)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var request = await LockedAsync(db, tenantId, requestId, ct);
            if (request is null) return NotFound();
            var now = clock.GetUtcNow();
            if (await Undecidable(request, now, mutation, db, services, ct) is { } closed)
            {
                await transaction.CommitAsync(ct);
                return closed;
            }
            request.State = to;
            request.DecidedBy = mutation.Actor;
            request.DecidedAt = now;
            request.Comment = comment.Length > 0 ? comment : null;
            mutation.Audit(AuditOutcome.Succeeded, request.Id.ToString(), new() { ["action"] = request.Action, ["requestedBy"] = request.RequestedBy });
            await NotifyAsync(db, services, request, to == ActionRequestState.Rejected ? ActionRequestEvents.Rejected : ActionRequestEvents.Cancelled, now, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            var hostname = await db.Agents.AsNoTracking().Where(a => a.Id == request.AgentId).Select(a => a.Hostname).SingleAsync(ct);
            return Results.Ok(ToDto(request, hostname, null, now));
        }
    }
}
