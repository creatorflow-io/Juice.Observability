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

        /// <summary>
        /// Signs a read-only diagnostic for one active agent, valid for 30 seconds to 10 minutes (2 by default). The action and its parameters are checked
        /// here as the agent will check them; the agent's own allowlist may still refuse it, and that refusal is what the command will show.
        /// </summary>
        private static async Task<IResult> IssueAsync(Guid tenantId, Guid agentId, AgentCommandRequest? request, HttpContext http, PlatformDbContext db,
            IServiceProvider services, IOptions<PlatformOptions> platform, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "agent.command.issue", "agent_command");
            // Changing the host is an admin's call; reading it, an operator's too.
            var changing = request?.Action is { } requested && AgentActions.Changing.Contains(requested);
            if (await mutation.RequireAsync(agentId.ToString(), ct, changing ? [Roles.Admin] : [Roles.Operator, Roles.Admin]) is { } denied) return denied;
            if (services.GetService<AgentAuthority>() is not { } authority) return NotEnabled();
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync($"POST agents/{agentId}/commands", request, ct) is { } replay) return replay;
            var action = request.Action ?? "";
            var parameters = request.Parameters ?? JsonSerializer.SerializeToElement(new { });
            if (!AgentActions.All.Contains(action)) return Mutation.Invalid([$"action is one of: {string.Join(", ", AgentActions.All)}."]);
            if (action == AgentActions.AgentUpdate)
            {
                if (UpdateParameters(parameters, services.GetService<AgentPackageCatalog>(), out var problem) is not { } filled) return problem!;
                parameters = filled;
            }
            if (AgentActions.Parse(action, parameters, out var error) is null) return Mutation.Invalid([error!]);
            // An update needs the time to download, restart and prove the new version before the agent answers.
            var lifetime = TimeSpan.FromSeconds(request.LifetimeSeconds ?? (int)(changing ? CommandLimits.MaxLifetime : CommandLimits.DefaultLifetime).TotalSeconds);
            if (lifetime < TimeSpan.FromSeconds(MinLifetimeSeconds) || lifetime > CommandLimits.MaxLifetime)
            {
                return Mutation.Invalid([$"lifetimeSeconds is {MinLifetimeSeconds} to {(int)CommandLimits.MaxLifetime.TotalSeconds}."]);
            }
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
            if (agent is null) return NotFound();
            if (agent.Status != AgentStatus.Active) return Mutation.Problem(StatusCodes.Status409Conflict, "Agent revoked", "A revoked agent takes no commands.");

            var now = clock.GetUtcNow();
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), platform.Value.TenantKey, agentId, action, parameters,
                now, now + lifetime, AgentAuthority.Token(), AgentAuthority.Token(), null);
            var signed = authority.Sign(envelope);
            var command = new AgentCommand
            {
                Id = envelope.CommandId, TenantId = tenantId, AgentId = agentId, Action = action, Parameters = parameters.GetRawText(), IssuedBy = mutation.Actor,
                IssuedAt = now, ExpiresAt = envelope.ExpiresAt, Nonce = envelope.Nonce, IdempotencyKey = envelope.IdempotencyKey, Payload = signed.Payload,
                Signature = signed.Signature, KeyId = signed.KeyId, State = AgentCommandState.Pending,
            };
            db.AgentCommands.Add(command);
            mutation.Audit(AuditOutcome.Succeeded, command.Id.ToString(), new() { ["agent"] = agentId.ToString(), ["action"] = action });
            var result = mutation.Remember(StatusCodes.Status201Created, ToDto(command, now), now);
            await db.SaveChangesAsync(ct);
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
