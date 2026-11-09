using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// Tells the endpoints in <c>Actions:NotifyEndpoints</c> what happened to a request: that it waits for approval, and how it ended. The deliveries
    /// are written in the same transaction as the change they tell of, and the webhook dispatcher sends them like any other: signed, retried,
    /// through the same destination checks.
    /// </summary>
    public static class ActionRequestNotices
    {
        /// <summary>Adds one delivery per configured endpoint that exists and is enabled; the caller saves them with the request.</summary>
        public static async Task<int> AddAsync(PlatformDbContext db, ActionApprovalOptions options, string tenantKey, ActionRequest request, string hostname,
            string type, DateTimeOffset now, CancellationToken ct)
        {
            if (options.NotifyEndpoints.Count == 0) return 0;
            var endpoints = await db.WebhookEndpoints.AsNoTracking()
                .Where(e => e.TenantId == request.TenantId && e.Enabled && options.NotifyEndpoints.Contains(e.Key)).Select(e => e.Id).ToListAsync(ct);
            foreach (var endpoint in endpoints)
            {
                var id = Guid.CreateVersion7();
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = id, TenantId = request.TenantId, EndpointId = endpoint, ActionRequestId = request.Id, Event = type,
                    Payload = Payload(id, type, tenantKey, request, hostname, now), CreatedAt = now, NextAttemptAt = now,
                });
            }
            return endpoints.Count;
        }

        /// <summary>The body: the request as the approver needs it, with no secrets (parameters are what an admin sees on the Approvals page).</summary>
        public static string Payload(Guid id, string type, string tenantKey, ActionRequest request, string hostname, DateTimeOffset now)
            => JsonSerializer.Serialize(new
            {
                id,
                type,
                occurredAt = now,
                tenant = tenantKey,
                request = new
                {
                    id = request.Id,
                    agent = new { id = request.AgentId, hostname },
                    action = request.Action,
                    parameters = JsonDocument.Parse(request.Parameters).RootElement,
                    risk = request.Risk,
                    policy = request.Policy,
                    reason = request.Reason,
                    requestedBy = request.RequestedBy,
                    requestedAt = request.RequestedAt,
                    expiresAt = request.ExpiresAt,
                    state = request.State.ToString(),
                    decidedBy = request.DecidedBy,
                    decidedAt = request.DecidedAt,
                    comment = request.Comment,
                    alertId = request.AlertId,
                    target = request.TargetId is { } target ? new { type = request.TargetType?.ToString(), id = target, environmentId = request.TargetEnvironmentId } : null,
                    verification = request.Verification.ToString(),
                    verificationNote = request.VerificationNote,
                },
                links = new { request = $"/api/v1/tenants/{request.TenantId}/action-requests/{request.Id}" },
            }, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Closes pending requests nobody decided in time, audits each as <c>action.request.expire</c> and notifies it, so a request doesn't quietly
    /// lapse. Then follows issued remediations to <c>Verified</c> or <c>NotVerified</c> by the target's health (<see cref="ActionEvidence.Verify"/>),
    /// audited as <c>action.request.verify</c> and notified. One replica at a time; a decision racing it waits on the same row lock.
    /// </summary>
    public sealed class ActionRequestSweeper(IServiceScopeFactory scopes, IOptions<ActionApprovalOptions> options, IOptions<PlatformOptions> platform,
        TimeProvider clock, ILogger<ActionRequestSweeper> logger) : BackgroundService
    {
        /// <summary>Who the audit records name for what the platform did itself.</summary>
        public const string Actor = "platform";
        private const int Batch = 100;
        /// <summary>Remediations followed at once; more wait for a later pass.</summary>
        private const int WatchBatch = 500;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var lease = WorkerLease.For(scopes, "action-requests", logger);
            using var timer = new PeriodicTimer(options.Value.SweepInterval);
            do
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        while (await SweepOnceAsync(stoppingToken) == Batch) { }
                        // Once a pass: most of what's watched is still waiting for its evidence.
                        await VerifyOnceAsync(stoppingToken);
                        await DropExpiredBundlesAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Action request sweep failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>Decides verification for up to one batch of issued remediations whose outcome is known; returns how many were looked at.</summary>
        public async Task<int> VerifyOnceAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var watched = await db.ActionRequests.FromSql($"SELECT * FROM action_requests WHERE verification = 'Verifying' ORDER BY id LIMIT {WatchBatch} FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);
            if (watched.Count == 0) return 0;
            var commandIds = watched.Where(r => r.CommandId is not null).Select(r => r.CommandId!.Value).ToList();
            var commands = await db.AgentCommands.AsNoTracking().Where(c => commandIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            var agentIds = watched.Select(r => r.AgentId).Distinct().ToList();
            var hostnames = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Hostname, ct);
            foreach (var request in watched)
            {
                var snapshot = request.TargetType is { } type && request.TargetId is { } target
                    ? await ActionEvidence.SnapshotAsync(db, request.TenantId, type, target, request.TargetEnvironmentId, ct)
                    : null;
                if (ActionEvidence.Verify(request.CommandId is { } c ? commands.GetValueOrDefault(c) : null, snapshot, now, options.Value.VerificationWindow) is not { } outcome) continue;
                request.Verification = outcome.State;
                request.VerificationNote = outcome.Note.Length > 500 ? outcome.Note[..500] : outcome.Note;
                request.VerifiedAt = outcome.At;
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = request.TenantId, Actor = Actor, Operation = "action.request.verify", TargetType = "action_request", TargetId = request.Id.ToString(),
                    Outcome = AuditOutcome.Succeeded, Metadata = new() { ["action"] = request.Action, ["verification"] = outcome.State.ToString(), ["note"] = request.VerificationNote },
                });
                await ActionRequestNotices.AddAsync(db, options.Value, platform.Value.TenantKey, request, hostnames.GetValueOrDefault(request.AgentId, ""),
                    outcome.State == ActionVerification.Verified ? ActionRequestEvents.Verified : ActionRequestEvents.NotVerified, now, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return watched.Count;
        }

        /// <summary>Deletes diagnostic bundles past their retention; returns how many.</summary>
        public async Task<int> DropExpiredBundlesAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            return await db.AgentBundles.Where(b => b.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        }

        /// <summary>Expires up to one batch; returns how many.</summary>
        public async Task<int> SweepOnceAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var due = await db.ActionRequests.FromSql($"SELECT * FROM action_requests WHERE state = 'Pending' AND expires_at <= {now} ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);
            if (due.Count == 0) return 0;
            var agentIds = due.Select(r => r.AgentId).Distinct().ToList();
            var hostnames = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Hostname, ct);
            foreach (var request in due)
            {
                request.State = ActionRequestState.Expired;
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = request.TenantId, Actor = Actor, Operation = "action.request.expire", TargetType = "action_request", TargetId = request.Id.ToString(),
                    Outcome = AuditOutcome.Succeeded, Metadata = new() { ["action"] = request.Action, ["requestedBy"] = request.RequestedBy },
                });
                await ActionRequestNotices.AddAsync(db, options.Value, platform.Value.TenantKey, request, hostnames.GetValueOrDefault(request.AgentId, ""),
                    ActionRequestEvents.Expired, now, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return due.Count;
        }
    }
}
