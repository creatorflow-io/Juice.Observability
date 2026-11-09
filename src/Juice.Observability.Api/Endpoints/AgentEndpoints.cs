using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agents;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record AgentDto(Guid Id, string Hostname, string OperatingSystem, string Version, string Status, Guid? ResourceId, bool Online,
        DateTimeOffset? LastSeenAt, DateTimeOffset CertificateNotAfter, string EnrolledBy, DateTimeOffset EnrolledAt, DateTimeOffset? RevokedAt, string? RevokedBy, string? RevokeReason);

    public sealed record EnrollmentTokenRequest(string? Purpose, int? ExpiresInMinutes);

    /// <summary>The token is in this response only: the platform keeps its hash.</summary>
    public sealed record EnrollmentTokenDto(Guid Id, string Token, string? Purpose, DateTimeOffset ExpiresAt);

    public sealed record RevocationRequest(string? Reason);

    public sealed record AgentCommandRequest(string? Action, JsonElement? Parameters, int? LifetimeSeconds);

    /// <summary>A package an admin can update agents to. <c>SignedBy</c> is the id of the release key whose signature checked out.</summary>
    public sealed record AgentPackageDto(string Version, string Sha256, long Size, string SignedBy, DateTimeOffset AddedAt);

    /// <summary>
    /// One action an agent can be asked for. <c>Permitted</c> is what its host's allowlist says, or null when the agent doesn't report it (an older
    /// agent); <c>Parameters</c> are narrowed to the host's bounds where the agent reported them, and <c>Limits</c> says the rest in words.
    /// </summary>
    /// <param name="Name">The action's name.</param>
    /// <param name="Description">What it does.</param>
    /// <param name="Changing">True when it changes the host.</param>
    /// <param name="Custom">The agent build's own action.</param>
    /// <param name="Parameters">What it takes, narrowed to the host's bounds when the agent reported them.</param>
    /// <param name="Permitted">What the host's allowlist says; null when the agent doesn't report it.</param>
    /// <param name="Limits">The host's other bounds, in words.</param>
    /// <param name="Risk">One of <c>ActionRisks</c>.</param>
    /// <param name="Approval">What the platform's policy needs before it's signed: one of <c>ApprovalPolicies</c>.</param>
    public sealed record AgentActionDto(string Name, string Description, bool Changing, bool Custom, IReadOnlyList<ActionParameter> Parameters, bool? Permitted,
        IReadOnlyList<string> Limits, string Risk, string Approval);

    /// <summary>An agent's actions, and when it last reported them; <c>ReportedAt</c> null means the built-in actions, as an older agent has.</summary>
    public sealed record AgentActionsDto(DateTimeOffset? ReportedAt, IReadOnlyList<AgentActionDto> Items);

    public sealed record AgentCommandDto(Guid Id, Guid AgentId, string Action, JsonElement Parameters, string State, string IssuedBy, DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt, DateTimeOffset? DeliveredAt, int DeliveryCount, DateTimeOffset? CompletedAt, string? Reason, JsonElement? Result, int? DurationMs);

    /// <summary>
    /// Juice Agents from the people's side (roadmap Phase 7): admins create enrollment tokens and revoke agents; operators and admins issue read-only
    /// diagnostics; everyone with access reads agents and what they reported. Every change is audited. Commands are signed here and checked again by the
    /// agent, whose own allowlist has the last word.
    /// </summary>
    public static class AgentEndpoints
    {
        public static readonly TimeSpan Online = TimeSpan.FromMinutes(2);
        public const int MaxTokenMinutes = 24 * 60;
        public const int MinLifetimeSeconds = 30;

        public static RouteGroupBuilder MapAgentEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapPost("/agents/enrollment-tokens", CreateTokenAsync);
            tenant.MapGet("/agents", ListAsync);
            tenant.MapGet("/agents/packages", ListPackages);
            tenant.MapGet("/agents/actions", Catalog);
            tenant.MapGet("/agents/{agentId:guid}/actions", ActionsAsync);
            tenant.MapGet("/agents/{agentId:guid}", GetAsync);
            tenant.MapPost("/agents/{agentId:guid}/revocation", RevokeAsync);
            tenant.MapPost("/agents/{agentId:guid}/commands", IssueAsync);
            tenant.MapGet("/agents/{agentId:guid}/commands", ListCommandsAsync);
            tenant.MapGet("/agents/{agentId:guid}/commands/{commandId:guid}", GetCommandAsync);
            return tenant;
        }

        private static IResult NotEnabled() => Mutation.Problem(StatusCodes.Status409Conflict, "Agents are not enabled", "This deployment has no agent CA: set Agents:Enabled and its keys.");

        private static IResult NotFound() => Mutation.Problem(StatusCodes.Status404NotFound, "Agent not found", "No such agent.");

        internal static AgentDto ToDto(EnrolledAgent a, DateTimeOffset now) => new(a.Id, a.Hostname, a.OperatingSystem, a.Version, a.Status.ToString(), a.ResourceId,
            a.Status == AgentStatus.Active && a.LastSeenAt is { } seen && now - seen < Online, a.LastSeenAt, a.CertificateNotAfter, a.EnrolledBy, a.EnrolledAt,
            a.RevokedAt, a.RevokedBy, a.RevokeReason);

        /// <summary>A command's state as it is now: one still waiting past its expiry is expired, even before the agent's next poll closes it.</summary>
        internal static AgentCommandDto ToDto(AgentCommand c, DateTimeOffset now) => new(c.Id, c.AgentId, c.Action, AgentAuthority.Parameters(c.Parameters),
            (c.State is AgentCommandState.Pending or AgentCommandState.Delivered && c.ExpiresAt <= now ? AgentCommandState.Expired : c.State).ToString(),
            c.IssuedBy, c.IssuedAt, c.ExpiresAt, c.DeliveredAt, c.DeliveryCount, c.CompletedAt, c.Reason,
            c.Result is { } result ? AgentAuthority.Parameters(result) : null, c.DurationMs);

        /// <summary>The agent's last action report, or null when it never sent one or it doesn't read.</summary>
        internal static ActionsReport? ReportOf(EnrolledAgent agent)
        {
            if (agent.Actions is null) return null;
            try
            {
                return JsonSerializer.Deserialize<ActionsReport>(agent.Actions, CommandSignature.Tolerant);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The built-in actions every agent knows. An agent's own page lists what it reported instead, custom actions included.</summary>
        private static IResult Catalog() => Results.Ok(new Page<ActionDescriptor>([.. AgentActions.Descriptors], null));

        private static async Task<IResult> ActionsAsync(Guid tenantId, Guid agentId, PlatformDbContext db, IOptions<ActionApprovalOptions> approval, CancellationToken ct)
        {
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
            if (agent is null) return NotFound();
            // A built-in's risk is the platform's own, whatever a report says.
            string RiskOf(ActionDescriptor d) => (AgentActions.Descriptors.FirstOrDefault(b => b.Name == d.Name) ?? d).RiskOf();
            AgentActionDto Dto(ActionDescriptor d, bool? permitted, IReadOnlyList<string> limits)
                => new(d.Name, d.Description, d.Changing, d.Custom, d.Parameters, permitted, limits, RiskOf(d), approval.Value.PolicyFor(d.Name, RiskOf(d)));
            if (ReportOf(agent) is { } report)
            {
                return Results.Ok(new AgentActionsDto(agent.ActionsReportedAt, [.. report.Actions.Select(r => Dto(r.Action, r.Permitted, r.Limits))]));
            }
            return Results.Ok(new AgentActionsDto(null, [.. AgentActions.Descriptors.Select(d => Dto(d, null, []))]));
        }

        /// <summary>A one-time token for enrolling one agent, valid 5 minutes to 24 hours (an hour by default). Shown once.</summary>
        private static async Task<IResult> CreateTokenAsync(Guid tenantId, EnrollmentTokenRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock,
            IServiceProvider services, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "agent.enrollment_token.create", "agent_enrollment_token");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            if (services.GetService<AgentAuthority>() is null) return NotEnabled();
            var minutes = request?.ExpiresInMinutes ?? 60;
            if (minutes is < 5 or > MaxTokenMinutes) return Mutation.Invalid([$"expiresInMinutes is 5 to {MaxTokenMinutes}."]);
            var purpose = ChangeSanitizer.Text(request?.Purpose, 200);
            var now = clock.GetUtcNow();
            var token = AgentAuthority.Token(32);
            var row = new AgentEnrollmentToken
            {
                TenantId = tenantId, TokenHash = AgentAuthority.Hash(token), Purpose = purpose.Length > 0 ? purpose : null, CreatedBy = mutation.Actor, CreatedAt = now,
                ExpiresAt = now.AddMinutes(minutes),
            };
            db.AgentEnrollmentTokens.Add(row);
            mutation.Audit(AuditOutcome.Succeeded, row.Id.ToString(), new() { ["expiresAt"] = row.ExpiresAt.ToString("O") });
            await db.SaveChangesAsync(ct);
            return Results.Created($"{http.Request.Path}/{row.Id}", new EnrollmentTokenDto(row.Id, token, row.Purpose, row.ExpiresAt));
        }

        private static async Task<IResult> ListAsync(Guid tenantId, bool? all, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var query = db.Agents.AsNoTracking().Where(a => a.TenantId == tenantId);
            if (all != true) query = query.Where(a => a.Status == AgentStatus.Active);
            var agents = await query.OrderBy(a => a.Hostname).Take(500).ToListAsync(ct);
            return Results.Ok(new Page<AgentDto>([.. agents.Select(a => ToDto(a, now))], null));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid agentId, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
            => await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct) is { } agent
                ? Results.Ok(ToDto(agent, clock.GetUtcNow())) : NotFound();

        /// <summary>Stops the agent at its next call: the platform refuses its certificate from now on. Commands still waiting are cancelled. Needs a reason.</summary>
        private static async Task<IResult> RevokeAsync(Guid tenantId, Guid agentId, RevocationRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "agent.revoke", "agent");
            if (await mutation.RequireAsync(agentId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            var reason = ChangeSanitizer.Text(request?.Reason, 500);
            if (reason.Length == 0) return Mutation.Invalid(["reason is required: say why this agent is revoked."]);
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
            if (agent is null) return NotFound();
            var now = clock.GetUtcNow();
            if (agent.Status == AgentStatus.Active)
            {
                agent.Status = AgentStatus.Revoked;
                agent.RevokedAt = now;
                agent.RevokedBy = mutation.Actor;
                agent.RevokeReason = reason;
                // Tracked, not updated in place: the revocation, its audit and the cancelled commands are one save, so none happens without the others.
                foreach (var waiting in await db.AgentCommands.Where(c => c.AgentId == agentId && (c.State == AgentCommandState.Pending || c.State == AgentCommandState.Delivered)).ToListAsync(ct))
                {
                    waiting.State = AgentCommandState.Cancelled;
                    waiting.CompletedAt = now;
                }
                mutation.Audit(AuditOutcome.Succeeded, agentId.ToString(), new() { ["hostname"] = agent.Hostname });
                mutation.RecordChange($"Agent {agent.Hostname}: revoked", now);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(agent, now));
        }

        /// <summary>An action checked against one agent: what it is (as built in, or as the agent reported it), its parameters canonical, and what it needs.</summary>
        internal sealed record ResolvedAction(ActionDescriptor Descriptor, JsonElement Parameters, string Policy, string Risk)
        {
            public string Name => Descriptor.Name;
        }

        /// <summary>
        /// Checks an action and its parameters for one agent as the agent will check them: a built-in by its own rules, a custom action against the
        /// schema the agent reported (narrowed to its host's bounds). Fills in <c>agent.update</c>'s package hash. Null with the problem when it doesn't hold.
        /// </summary>
        internal static ResolvedAction? Resolve(EnrolledAgent agent, string? action, JsonElement? requested, IServiceProvider services, ActionApprovalOptions approval,
            out IResult? problem)
        {
            problem = null;
            var name = action ?? "";
            var reported = ReportOf(agent)?.Actions.Where(a => a.Action.Custom).Select(a => a.Action).ToList() ?? [];
            // A built-in is described here, never by the agent: its risk can't be lowered by a report.
            var descriptor = AgentActions.Descriptors.FirstOrDefault(d => d.Name == name) ?? reported.FirstOrDefault(a => a.Name == name);
            if (descriptor is null)
            {
                problem = Mutation.Invalid([$"action is one of: {string.Join(", ", AgentActions.All.Concat(reported.Select(a => a.Name)))}."]);
                return null;
            }
            var parameters = requested ?? JsonSerializer.SerializeToElement(new { });
            if (name == AgentActions.AgentUpdate)
            {
                if (UpdateParameters(parameters, services.GetService<AgentPackageCatalog>(), out problem) is not { } filled) return null;
                parameters = filled;
            }
            var error = descriptor.Custom ? ActionSchema.Validate(descriptor.Parameters, parameters) : AgentActions.Parse(name, parameters, out var parseError) is null ? parseError : null;
            if (error is not null)
            {
                problem = Mutation.Invalid([error]);
                return null;
            }
            // For these parameters: a cleanup's dry run is Low, whatever the action's own risk.
            var risk = AgentActions.RiskOf(descriptor, parameters);
            return new ResolvedAction(descriptor, Canonical(parameters), approval.PolicyFor(name, risk), risk);
        }

        /// <summary>Parameters with their members in name order, so the same request reads the same however it was written.</summary>
        internal static JsonElement Canonical(JsonElement parameters) => parameters.ValueKind == JsonValueKind.Object
            ? JsonSerializer.SerializeToElement(new SortedDictionary<string, JsonElement>(parameters.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), StringComparer.Ordinal))
            : parameters;

        /// <summary>A command for one agent, signed: what the agent will receive, and verify, as it is.</summary>
        internal static AgentCommand Sign(AgentAuthority authority, string tenantKey, Guid tenantId, Guid agentId, string action, JsonElement parameters, string issuedBy,
            DateTimeOffset now, TimeSpan lifetime, string? approvalReference)
        {
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), tenantKey, agentId, action, parameters,
                now, now + lifetime, AgentAuthority.Token(), AgentAuthority.Token(), approvalReference);
            var signed = authority.Sign(envelope);
            return new AgentCommand
            {
                Id = envelope.CommandId, TenantId = tenantId, AgentId = agentId, Action = action, Parameters = parameters.GetRawText(), IssuedBy = issuedBy,
                IssuedAt = now, ExpiresAt = envelope.ExpiresAt, Nonce = envelope.Nonce, IdempotencyKey = envelope.IdempotencyKey, Payload = signed.Payload,
                Signature = signed.Signature, KeyId = signed.KeyId, State = AgentCommandState.Pending,
            };
        }

        /// <summary>A changing action needs the time to do its work (an update: download, restart and prove the new version) before the agent answers.</summary>
        internal static TimeSpan DefaultLifetime(ResolvedAction action) => action.Descriptor.Changing ? CommandLimits.MaxLifetime : CommandLimits.DefaultLifetime;

        /// <summary>
        /// Signs a command for one active agent, valid for 30 seconds to 10 minutes. Only for what needs no one else's approval: an action whose policy
        /// is <c>None</c>, or <c>Admin</c> asked by an admin, which is recorded as a request the admin approved. Anything else is refused with
        /// <c>approval_required</c>: request it at <c>agents/{id}/action-requests</c>. The action and its parameters are checked here as the agent will
        /// check them; the agent's own allowlist may still refuse it, and that refusal is what the command will show.
        /// </summary>
        private static async Task<IResult> IssueAsync(Guid tenantId, Guid agentId, AgentCommandRequest? request, HttpContext http, PlatformDbContext db,
            IServiceProvider services, IOptions<PlatformOptions> platform, IOptions<ActionApprovalOptions> approval, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "agent.command.issue", "agent_command");
            if (await mutation.RequireAsync(agentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (services.GetService<AgentAuthority>() is not { } authority) return NotEnabled();
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync($"POST agents/{agentId}/commands", request, ct) is { } replay) return replay;
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
            if (agent is null) return NotFound();
            if (Resolve(agent, request.Action, request.Parameters, services, approval.Value, out var problem) is not { } action) return problem!;
            var lifetime = request.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultLifetime(action);
            if (lifetime < TimeSpan.FromSeconds(MinLifetimeSeconds) || lifetime > CommandLimits.MaxLifetime)
            {
                return Mutation.Invalid([$"lifetimeSeconds is {MinLifetimeSeconds} to {(int)CommandLimits.MaxLifetime.TotalSeconds}."]);
            }
            if (agent.Status != AgentStatus.Active) return Mutation.Problem(StatusCodes.Status409Conflict, "Agent revoked", "A revoked agent takes no commands.");

            var now = clock.GetUtcNow();
            var admin = http.User.IsInRole(Roles.Admin);
            ActionRequest? approved = null;
            if (action.Policy == ApprovalPolicies.Admin && admin)
            {
                // An admin's own request under an Admin policy: approved by asking. Recorded as one, so it has an approval reference and is in the history.
                approved = new ActionRequest
                {
                    TenantId = tenantId, AgentId = agentId, Action = action.Name, Parameters = action.Parameters.GetRawText(), Risk = action.Risk, Policy = action.Policy,
                    Reason = "issued directly by an admin", RequestedBy = mutation.Actor, RequestedAt = now, ExpiresAt = now, State = ActionRequestState.Issued,
                    DecidedBy = mutation.Actor, DecidedAt = now,
                };
            }
            else if (action.Policy != ApprovalPolicies.None)
            {
                mutation.Audit(AuditOutcome.Denied, agentId.ToString(), new() { ["action"] = action.Name, ["reason"] = "approval_required", ["policy"] = action.Policy });
                await db.SaveChangesAsync(ct);
                return Mutation.Problem(StatusCodes.Status409Conflict, "Approval required",
                    $"{action.Name} is {action.Risk} risk and needs {ActionRequestEndpoints.Describe(action.Policy)}: request it at agents/{agentId}/action-requests, and it runs once approved.");
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (approved is not null)
            {
                // Recorded as a request, so held to the same rule: one open per agent, action and parameters.
                await ActionRequestEndpoints.LockAsync(db, agentId, action.Name, approved.Parameters, ct);
                if (await ActionRequestEndpoints.OpenDuplicateAsync(db, agentId, action.Name, approved.Parameters, now, ct) is { } open) return ActionRequestEndpoints.Duplicate(open);
            }
            var command = Sign(authority, platform.Value.TenantKey, tenantId, agentId, action.Name, action.Parameters, mutation.Actor, now, lifetime,
                approved is null ? null : ActionRequestEndpoints.Reference(approved));
            db.AgentCommands.Add(command);
            if (approved is not null)
            {
                approved.CommandId = command.Id;
                db.ActionRequests.Add(approved);
            }
            mutation.Audit(AuditOutcome.Succeeded, command.Id.ToString(), new() { ["agent"] = agentId.ToString(), ["action"] = action.Name, ["policy"] = action.Policy });
            var result = mutation.Remember(StatusCodes.Status201Created, ToDto(command, now), now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            // Its poll, if it's waiting in this process, takes it now.
            services.GetService<AgentWakeups>()?.Wake(agentId);
            return result;
        }

        /// <summary>
        /// <c>agent.update</c> names a version; the platform adds the hash of the package it offers for it, so the agent installs exactly the bytes the
        /// platform checked. A hash given with it must be that one.
        /// </summary>
        private static JsonElement? UpdateParameters(JsonElement parameters, AgentPackageCatalog? catalog, out IResult? problem)
        {
            problem = null;
            if (catalog is not { Enabled: true })
            {
                problem = Mutation.Problem(StatusCodes.Status409Conflict, "Updates are not enabled", "This deployment offers no agent packages: set Agents:PackageDirectory and Agents:ReleaseKeyFiles.");
                return null;
            }
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
            {
                problem = Mutation.Invalid(["parameters.version is required: the agent version to install."]);
                return null;
            }
            var version = versionElement.GetString()!;
            if (catalog.Find(version) is not { } package)
            {
                problem = Mutation.Invalid([$"No signed package of version {version} is offered: see GET agents/packages."]);
                return null;
            }
            if (parameters.TryGetProperty("sha256", out var given) && given.GetString() != package.Sha256)
            {
                problem = Mutation.Invalid([$"sha256 doesn't match the package of version {version} offered here."]);
                return null;
            }
            return JsonSerializer.SerializeToElement(new AgentUpdateParameters(version, package.Sha256), CommandSignature.Json);
        }

        /// <summary>The packages an admin can update agents to: those whose release signature checks out here.</summary>
        private static IResult ListPackages(IServiceProvider services)
            => Results.Ok(new Page<AgentPackageDto>([.. (services.GetService<AgentPackageCatalog>()?.List() ?? [])
                .Select(p => new AgentPackageDto(p.Version, p.Sha256, p.Size, p.SignedBy, p.AddedAt))], null));

        private static async Task<IResult> ListCommandsAsync(Guid tenantId, Guid agentId, int? limit, string? cursor, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.AgentCommands.AsNoTracking().Where(c => c.TenantId == tenantId && c.AgentId == agentId);
            if (after is not null) query = query.Where(c => c.Id < after.After);
            var rows = await query.OrderByDescending(c => c.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var now = clock.GetUtcNow();
            return Results.Ok(new Page<AgentCommandDto>([.. page.Select(c => ToDto(c, now))], rows.Count > take ? new Cursor("ac", page[^1].Id).Encode() : null));
        }

        private static async Task<IResult> GetCommandAsync(Guid tenantId, Guid agentId, Guid commandId, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
            => await db.AgentCommands.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.AgentId == agentId && c.Id == commandId, ct) is { } command
                ? Results.Ok(ToDto(command, clock.GetUtcNow()))
                : Mutation.Problem(StatusCodes.Status404NotFound, "Command not found", "No such command for this agent.");
    }
}
