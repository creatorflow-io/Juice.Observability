using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Agents
{
    /// <summary>Why a request's preconditions don't hold: <see cref="Code"/> is <c>stale_evidence</c> or <c>precondition_failed</c>.</summary>
    public sealed record PreconditionProblem(string Code, string Detail)
    {
        public const string Stale = "stale_evidence";
        public const string Failed = "precondition_failed";

        /// <summary>A failed precondition won't come back by waiting; stale evidence might, once the engines catch up.</summary>
        public bool IsFinal => Code == Failed;
    }

    /// <summary>
    /// The health evidence behind a remediation (roadmap Phase 8): before it's approved or issued, the alert that prompted it must still be firing and
    /// its target still unhealthy, both by evidence newer than <c>Actions:StaleEvidence</c>; after it ran, the target counts as recovered only when the
    /// health engine reports it healthy from telemetry newer than the action.
    /// </summary>
    public static class ActionEvidence
    {
        /// <summary>Null when the request may go ahead.</summary>
        public static async Task<PreconditionProblem?> CheckAsync(PlatformDbContext db, Guid tenantId, Guid? alertId, HealthSubject? targetType, Guid? targetId,
            Guid? environmentId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
        {
            if (alertId is { } id)
            {
                var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == id, ct);
                if (alert is null) return new(PreconditionProblem.Failed, "the alert no longer exists");
                if (alert.State != AlertState.Firing) return new(PreconditionProblem.Failed, $"the alert is {alert.State.ToString().ToLowerInvariant()}: nothing to remediate");
                if (alert.NoData || now - alert.LastEvaluatedAt > staleAfter)
                    return new(PreconditionProblem.Stale, $"the alert was last evaluated with data {Ago(now - alert.LastEvaluatedAt)}: its evidence is stale");
            }
            if (targetType is { } type && targetId is { } target)
            {
                var snapshot = await SnapshotAsync(db, tenantId, type, target, environmentId, ct);
                if (snapshot is null) return new(PreconditionProblem.Stale, "the target has no health evidence");
                if (now - snapshot.EvaluatedAt > staleAfter) return new(PreconditionProblem.Stale, $"the target's health was last evaluated {Ago(now - snapshot.EvaluatedAt)}: its evidence is stale");
                if (snapshot.State == HealthState.Healthy) return new(PreconditionProblem.Failed, "the target is healthy: nothing to remediate");
            }
            return null;
        }

        public static Task<HealthSnapshot?> SnapshotAsync(PlatformDbContext db, Guid tenantId, HealthSubject type, Guid id, Guid? environmentId, CancellationToken ct)
            => db.HealthSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.SubjectType == type && s.SubjectId == id && s.EnvironmentId == environmentId, ct);

        /// <summary>Where a request's verification stands now: the new state and why, or null to keep waiting.</summary>
        public static (ActionVerification State, string Note, DateTimeOffset At)? Verify(AgentCommand? command, HealthSnapshot? snapshot, DateTimeOffset now, TimeSpan window)
        {
            if (command is null) return (ActionVerification.NotVerified, "the command is gone", now);
            switch (command.State)
            {
                case AgentCommandState.Failed or AgentCommandState.Rejected or AgentCommandState.Expired or AgentCommandState.Cancelled:
                    return (ActionVerification.NotVerified, $"the action didn't succeed: {command.State.ToString().ToLowerInvariant()}{(command.Reason is { } r ? $" ({r})" : "")}", now);
                case AgentCommandState.Succeeded when command.CompletedAt is { } done:
                    // Evidence from before the action says nothing about what it did.
                    if (snapshot is { State: HealthState.Healthy, LastEvidenceAt: { } evidence } && evidence > done && snapshot.EvaluatedAt > done)
                        return (ActionVerification.Verified, $"healthy by telemetry at {evidence:u}, after the action ended at {done:u}", snapshot.EvaluatedAt);
                    if (now - done > window)
                        return (ActionVerification.NotVerified, $"not seen healthy within {Ago(window, "")} of the action: {(snapshot is null ? "no health evidence" : $"{snapshot.State.ToString().ToLowerInvariant()} as of {snapshot.EvaluatedAt:u}")}", now);
                    return null;
                default:
                    return null;
            }
        }

        private static string Ago(TimeSpan span, string suffix = " ago")
            => (span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{Math.Max(0, (int)span.TotalSeconds)} s") + suffix;
    }
}
