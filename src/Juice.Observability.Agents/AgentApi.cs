using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// The API agents call, on the mTLS listener only. Enrollment needs a one-time token; every other call needs a client certificate from the agent
    /// CA that is the agent's current one (or the one it just renewed from) and an agent that isn't revoked, checked on each request, so revoking takes
    /// effect at the next call. The platform never calls an agent: commands wait here until it asks.
    /// </summary>
    public static class AgentApi
    {
        /// <summary>A command handed over but not answered is handed over again after this, until it expires: the agent answers a repeat without running it twice.</summary>
        public static readonly TimeSpan Redelivery = TimeSpan.FromSeconds(30);
        public const int MaxPerPoll = 20;
        /// <summary>How often a waiting poll looks at the database, for commands issued on another replica and redeliveries.</summary>
        public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);
        private const string Caller = "juice.agent";

        public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
        {
            var agents = app.MapGroup("").AllowAnonymous().AddEndpointFilter(OnAgentListener);
            agents.MapPost(AgentRoutes.Enroll, EnrollAsync);
            var authenticated = agents.MapGroup("").AddEndpointFilter(AuthenticateAsync);
            authenticated.MapPost(AgentRoutes.Certificate, RenewAsync);
            authenticated.MapGet(AgentRoutes.Commands, PollAsync);
            authenticated.MapPost("/agent/v1/commands/{commandId:guid}/result", ResultAsync);
            authenticated.MapGet("/agent/v1/packages/{version}", Package);
            authenticated.MapPost(AgentRoutes.Actions, ReportActionsAsync);
            return app;
        }

        /// <summary>The largest action report taken, as JSON.</summary>
        public const int MaxReportBytes = 128 * 1024;

        /// <summary>
        /// What the agent can run and what its host permits, as it reports when it starts. Kept on the agent for the Console and for checking a
        /// custom action's parameters before signing it; audited when it changes. A report that doesn't hold (<see cref="ActionsReport.Validate"/>) is
        /// refused whole.
        /// </summary>
        private static async Task<IResult> ReportActionsAsync(HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var agent = Agent(http);
            if (http.Request.ContentLength > MaxReportBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large", detail: $"At most {MaxReportBytes} bytes.");
            }
            ActionsReport? report;
            try
            {
                report = await JsonSerializer.DeserializeAsync<ActionsReport>(http.Request.Body, CommandSignature.Tolerant, ct);
            }
            catch (JsonException e)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid report", detail: e.Message);
            }
            var problems = report is null ? ["a body is required"] : report.Validate().Take(20).ToList();
            if (problems.Count > 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid report", detail: string.Join("; ", problems));
            }
            // A built-in this platform doesn't know (a newer agent's) can't be issued from here: left out, not refused, so the rest still shows.
            report = report! with { Actions = [.. report.Actions.Where(a => a.Action.Custom || AgentActions.All.Contains(a.Action.Name))] };
            var json = JsonSerializer.Serialize(report, CommandSignature.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxReportBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large", detail: $"At most {MaxReportBytes} bytes.");
            }
            var changed = !JsonEquals(agent.Actions, json);
            agent.Actions = json;
            agent.ActionsReportedAt = clock.GetUtcNow();
            if (changed)
            {
                var custom = report!.Actions.Where(a => a.Action.Custom).Select(a => a.Action.Name);
                var permitted = report.Actions.Where(a => a.Permitted).Select(a => a.Action.Name);
                db.AuditRecords.Add(Audit(agent.TenantId, $"agent:{agent.Id}", "agent.actions.report", agent.Id.ToString(), AuditOutcome.Succeeded, new()
                {
                    ["custom"] = string.Join(",", custom), ["permitted"] = string.Join(",", permitted),
                }));
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        /// <summary>The same report, whatever jsonb did to its spacing and key order.</summary>
        private static bool JsonEquals(string? stored, string json)
        {
            if (stored is null) return false;
            try
            {
                return JsonElement.DeepEquals(JsonDocument.Parse(stored).RootElement, JsonDocument.Parse(json).RootElement);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// A package the platform offers, with its release signature in a header. Only to an enrolled agent: the bytes aren't secret, but serving them
        /// to anyone would make the listener a file server.
        /// </summary>
        private static IResult Package(string version, HttpContext http)
        {
            if (http.RequestServices.GetRequiredService<AgentPackageCatalog>().Find(version) is not { } package)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such package", detail: $"No signed package of version {version} is offered.");
            }
            http.Response.Headers[AgentRoutes.PackageSignatureHeader] = Convert.ToBase64String(package.Signature);
            return Results.File(package.File, "application/zip", AgentPackages.FileName(version));
        }

        /// <summary>The agent API answers only on the agent listener, and the listener serves nothing else (<see cref="AgentListener"/>).</summary>
        private static async ValueTask<object?> OnAgentListener(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AgentPlatformOptions>>().Value;
            return options.Enabled && context.HttpContext.Connection.LocalPort == options.Port ? await next(context) : Results.NotFound();
        }

        private static IResult Refused(string detail) => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Agent refused", detail: detail);

        private static async ValueTask<object?> AuthenticateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted);
            if (services.GetRequiredService<AgentAuthority>().AgentOf(certificate, now) is not { } id)
            {
                return Refused("A client certificate from the agent CA, in date, is required.");
            }
            var db = services.GetRequiredService<PlatformDbContext>();
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == id, http.RequestAborted);
            if (agent is null || agent.Status != AgentStatus.Active)
            {
                return Refused("This agent is not enrolled, or was revoked.");
            }
            var serial = certificate!.SerialNumber;
            if (serial != agent.CertificateSerial && serial != agent.PreviousCertificateSerial)
            {
                return Refused("This certificate was replaced.");
            }
            if (serial == agent.CertificateSerial && agent.PreviousCertificateSerial is not null)
            {
                agent.PreviousCertificateSerial = null;   // the renewal reached it
            }
            http.Items[Caller] = agent;
            return await next(context);
        }

        private static EnrolledAgent Agent(HttpContext http) => (EnrolledAgent)http.Items[Caller]!;

        private static AuditRecord Audit(Guid tenantId, string actor, string operation, string? target, AuditOutcome outcome, Dictionary<string, string>? metadata = null) => new()
        {
            TenantId = tenantId, Actor = actor, Operation = operation, TargetType = "agent", TargetId = target, Outcome = outcome, Metadata = metadata ?? [],
        };

        private static async Task<IResult> EnrollAsync(EnrollRequest? request, HttpContext http, PlatformDbContext db,
            IOptions<PlatformOptions> platform, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
        {
            var logger = loggers.CreateLogger(typeof(AgentApi));
            // Resolved here, not bound: with agents off they aren't registered, and the listener filter has already answered 404.
            var authority = http.RequestServices.GetRequiredService<AgentAuthority>();
            var guard = http.RequestServices.GetRequiredService<EnrollmentGuard>();
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (await guard.IsBlockedAsync(db, address, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed enrollments",
                    detail: $"Try again in a minute: more than {EnrollmentGuard.MaxFailures} enrollments from this address failed.");
            }
            var now = clock.GetUtcNow();
            if (request is null || string.IsNullOrEmpty(request.Token) || string.IsNullOrEmpty(request.Csr))
            {
                await guard.FailAsync(db, address, ct);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid enrollment", detail: "token and csr are required.");
            }
            var tenant = await db.Tenants.SingleAsync(t => t.Key == platform.Value.TenantKey, ct);
            var hash = AgentAuthority.Hash(request.Token);
            var token = await db.AgentEnrollmentTokens.SingleOrDefaultAsync(t => t.TenantId == tenant.Id && t.TokenHash == hash, ct);
            if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
            {
                await guard.FailAsync(db, address, ct);
                if (token is null)
                {
                    // Anyone can send these: logged, not audited, so they can't fill the audit table.
                    logger.LogWarning("Enrollment with an unknown token from {Address}", address);
                }
                else
                {
                    // A real token used again or too late: someone has it, which is worth a record.
                    db.AuditRecords.Add(Audit(tenant.Id, "agent", "agent.enroll", token.Id.ToString(), AuditOutcome.Denied,
                        new() { ["reason"] = token.UsedAt is not null ? "used_token" : "expired_token", ["address"] = address }));
                    await db.SaveChangesAsync(ct);
                }
                // The same answer whatever was wrong: a caller learns nothing about which tokens exist.
                return Refused("The enrollment token is not valid.");
            }
            var hostname = Clip(request.Hostname, 253);
            var agentId = Guid.CreateVersion7();
            (string Pem, string Serial, DateTimeOffset NotAfter) certificate;
            try
            {
                certificate = authority.Issue(request.Csr, agentId, now);
            }
            catch (ArgumentException e)
            {
                logger.LogWarning("Enrollment refused a CSR: {Error}", e.Message);
                await guard.FailAsync(db, address, ct);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSR", detail: e.Message);
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                logger.LogWarning("Enrollment refused a CSR: {Error}", e.Message);
                await guard.FailAsync(db, address, ct);
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSR", detail: "The CSR doesn't read, or its signature doesn't verify.");
            }
            token.UsedAt = now;
            token.AgentId = agentId;
            var agent = new EnrolledAgent
            {
                Id = agentId, TenantId = tenant.Id, Hostname = hostname, OperatingSystem = Clip(request.OperatingSystem, 200), Version = Clip(request.AgentVersion, 64),
                CertificateSerial = certificate.Serial, CertificateNotAfter = certificate.NotAfter, EnrollmentTokenId = token.Id, EnrolledBy = token.CreatedBy,
                EnrolledAt = now, LastSeenAt = now, ResourceId = await HostAsync(db, tenant.Id, hostname, ct),
            };
            db.Agents.Add(agent);
            db.AuditRecords.Add(Audit(tenant.Id, $"agent:{agentId}", "agent.enroll", agentId.ToString(), AuditOutcome.Succeeded,
                new() { ["token"] = token.Id.ToString(), ["tokenCreatedBy"] = token.CreatedBy, ["hostname"] = hostname }));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Refused("The enrollment token is not valid.");   // another agent used it at the same moment
            }
            return Results.Ok(new EnrollResponse(agentId, tenant.Key, certificate.Pem, authority.CommandKeyPem, authority.CommandKeyId));
        }

        private static async Task<IResult> RenewAsync(RenewRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var agent = Agent(http);
            var authority = http.RequestServices.GetRequiredService<AgentAuthority>();
            if (request is null || string.IsNullOrEmpty(request.Csr))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid renewal", detail: "csr is required.");
            }
            (string Pem, string Serial, DateTimeOffset NotAfter) certificate;
            try
            {
                certificate = authority.Issue(request.Csr, agent.Id, clock.GetUtcNow());
            }
            catch (Exception e) when (e is ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid CSR", detail: "The CSR doesn't read, its signature doesn't verify, or its key is too weak.");
            }
            var current = (await http.Connection.GetClientCertificateAsync(ct))!.SerialNumber;
            agent.PreviousCertificateSerial = current;
            agent.CertificateSerial = certificate.Serial;
            agent.CertificateNotAfter = certificate.NotAfter;
            db.AuditRecords.Add(Audit(agent.TenantId, $"agent:{agent.Id}", "agent.renew", agent.Id.ToString(), AuditOutcome.Succeeded, new() { ["notAfter"] = certificate.NotAfter.ToString("O") }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RenewResponse(certificate.Pem));
        }

        /// <summary>
        /// Waits up to <c>wait</c> seconds for commands and hands over those waiting: new ones, and ones handed over earlier but not answered within
        /// <see cref="Redelivery"/>. A command issued in this process wakes the poll at once (<see cref="AgentWakeups"/>); one issued on another replica
        /// is found at the next look, every <see cref="Recheck"/>. Commands past their expiry are closed as expired instead.
        /// </summary>
        private static async Task<IResult> PollAsync(int? wait, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var agent = Agent(http);
            var wakeups = http.RequestServices.GetRequiredService<AgentWakeups>();
            var now = clock.GetUtcNow();
            agent.LastSeenAt = now;
            if (http.Request.Headers[AgentRoutes.VersionHeader].ToString() is { Length: > 0 } version) agent.Version = Clip(version, 64);
            if (http.Request.Headers[AgentRoutes.HostnameHeader].ToString() is { Length: > 0 } hostname && hostname != agent.Hostname)
            {
                agent.Hostname = Clip(hostname, 253);
                agent.ResourceId = null;
            }
            if (agent.ResourceId is null && http.RequestServices.GetRequiredService<HostLinks>().ShouldLook(agent.Id, agent.Hostname))
            {
                agent.ResourceId = await HostAsync(db, agent.TenantId, agent.Hostname, ct);
            }
            await db.SaveChangesAsync(ct);

            // The current command key, if the agent doesn't trust it yet: handed over before the commands it signs.
            var keys = http.Request.Headers.TryGetValue(AgentRoutes.KeysHeader, out var header)
                ? http.RequestServices.GetRequiredService<AgentAuthority>().AnnouncementsFor(header.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                : null;
            if (keys is not null)
            {
                return Results.Ok(new PollResponse([], keys));
            }

            var until = now + TimeSpan.FromSeconds(Math.Clamp(wait ?? 25, 0, 55));
            await db.AgentCommands.Where(c => c.AgentId == agent.Id && (c.State == AgentCommandState.Pending || c.State == AgentCommandState.Delivered) && c.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, AgentCommandState.Expired).SetProperty(c => c.CompletedAt, now), ct);
            while (true)
            {
                // Taken before the look, so a command issued between the look and the wait still wakes it.
                var woken = wakeups.Waiter(agent.Id);
                now = clock.GetUtcNow();
                var redeliverBefore = now - Redelivery;
                var due = await db.AgentCommands
                    .Where(c => c.AgentId == agent.Id && c.ExpiresAt > now
                        && (c.State == AgentCommandState.Pending || (c.State == AgentCommandState.Delivered && c.DeliveredAt < redeliverBefore)))
                    .OrderBy(c => c.IssuedAt).Take(MaxPerPoll).ToListAsync(ct);
                if (due.Count > 0 || now >= until)
                {
                    foreach (var command in due)
                    {
                        command.State = AgentCommandState.Delivered;
                        command.DeliveredAt = now;
                        command.DeliveryCount++;
                    }
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new PollResponse([.. due.Select(c => new SignedCommand(c.Payload, c.Signature, c.KeyId))]));
                }
                var pause = until - now < Recheck ? until - now : Recheck;
                await Task.WhenAny(woken, Task.Delay(pause, clock, ct));
                ct.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// What the agent did with a command it was sent, once: a command already answered keeps its first answer. Only the agent it was for can answer
        /// it, and a refusal is recorded with the agent's reason.
        /// </summary>
        private static async Task<IResult> ResultAsync(Guid commandId, CommandResult? result, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var agent = Agent(http);
            if (result is null || result.CommandId != commandId)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid result", detail: "The body must be the result of this command.");
            }
            var command = await db.AgentCommands.SingleOrDefaultAsync(c => c.Id == commandId && c.AgentId == agent.Id, ct);
            if (command is null)
            {
                return Results.NotFound();
            }
            if (command.State is AgentCommandState.Succeeded or AgentCommandState.Failed or AgentCommandState.Rejected)
            {
                return Results.NoContent();
            }
            var data = result.Data is { } element ? element.GetRawText() : null;
            if (data is not null && System.Text.Encoding.UTF8.GetByteCount(data) > CommandLimits.MaxResultBytes)
            {
                data = null;
                result = result with { Outcome = CommandOutcome.Failed, Reason = "result_too_large" };
            }
            command.State = result.Outcome switch
            {
                CommandOutcome.Succeeded => AgentCommandState.Succeeded,
                CommandOutcome.Rejected => AgentCommandState.Rejected,
                _ => AgentCommandState.Failed,
            };
            command.Reason = result.Reason is { } reason ? Clip(reason, 200) : null;
            command.Result = command.State == AgentCommandState.Succeeded ? data : null;
            command.DurationMs = Math.Max(0, result.DurationMs);
            command.CompletedAt = clock.GetUtcNow();
            db.AuditRecords.Add(new AuditRecord
            {
                TenantId = agent.TenantId, Actor = $"agent:{agent.Id}", Operation = "agent.command.result", TargetType = "agent_command", TargetId = command.Id.ToString(),
                Outcome = command.State == AgentCommandState.Succeeded ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                Metadata = new() { ["action"] = command.Action, ["state"] = command.State.ToString(), ["reason"] = command.Reason ?? "" },
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        /// <summary>The host discovery knows by this name, if any: that's the resource the agent belongs to.</summary>
        private static async Task<Guid?> HostAsync(PlatformDbContext db, Guid tenantId, string hostname, CancellationToken ct)
        {
            var name = hostname.ToLowerInvariant();
            return await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && r.Type == ResourceType.Host && r.Key.ToLower() == name)
                .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        }

        private static string Clip(string? value, int max) => value is null ? "" : value.Length > max ? value[..max] : value;
    }
}
