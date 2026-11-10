using System.Globalization;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary><c>IncidentId</c>: the unresolved incident one of the two is in. Accepting adds the other to it; without one, accepting opens an incident.</summary>
    public sealed record SuggestionDto(AlertDto Alert, AlertDto Other, double Score, IReadOnlyList<CorrelationEvidence> Evidence, Guid? IncidentId);

    public sealed record SuggestionsDto(string Relation, string Note, double Threshold, IReadOnlyList<SuggestionDto> Items);

    public sealed record CorrelationDecisionDto(string Decision, Guid AlertId, Guid OtherAlertId, double Score, IReadOnlyList<CorrelationEvidence> Evidence,
        string WeightsVersion, Guid? IncidentId, string? Reason, string DecidedBy, DateTimeOffset DecidedAt);

    public sealed record AcceptSuggestionRequest(string? Title);

    public sealed record DismissSuggestionRequest(string? Reason);

    /// <summary>
    /// Correlation suggestions (Phase 9, R9): read by anyone, decided by an operator or admin. Accepting merges the pair into one incident, a new one or the
    /// unresolved one either is already in; dismissing keeps the pair from being suggested again. Both are audited and stored with the score and evidence
    /// they were made on. Nothing merges on its own.
    /// </summary>
    public static class SuggestionEndpoints
    {
        public const int MaxReason = 500;

        public static RouteGroupBuilder MapSuggestionEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/alerts/{alertId:guid}/suggestions", ForAlertAsync);
            tenant.MapGet("/incidents/{incidentId:guid}/suggestions", ForIncidentAsync);
            tenant.MapPost("/alerts/{alertId:guid}/suggestions/{otherId:guid}/accept", AcceptAsync);
            tenant.MapPost("/alerts/{alertId:guid}/suggestions/{otherId:guid}/dismiss", DismissAsync);
            return tenant;
        }

        private static async Task<IResult> ForAlertAsync(Guid tenantId, Guid alertId, PlatformDbContext db, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Alert not found", "No such alert.");
            return Results.Ok(await ToDtoAsync(db, tenantId, await CorrelationSuggestions.ForAsync(db, tenantId, [alert], ct), ct));
        }

        /// <summary>Alerts outside the incident suggested with one inside it, best first. A resolved incident gets none: it's done.</summary>
        private static async Task<IResult> ForIncidentAsync(Guid tenantId, Guid incidentId, PlatformDbContext db, CancellationToken ct)
        {
            var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            if (incident is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Incident not found", "No such incident.");
            List<Suggestion> found = [];
            if (incident.State != IncidentState.Resolved)
            {
                var alerts = await db.IncidentAlerts.AsNoTracking().Where(l => l.IncidentId == incidentId)
                    .Join(db.Alerts.AsNoTracking(), l => l.AlertId, a => a.Id, (l, a) => a).ToListAsync(ct);
                // Each outside alert once, with the alert inside it scores best with: accepting any of its pairs adds it to this incident.
                found = await CorrelationSuggestions.ForAsync(db, tenantId, alerts, ct, onePerOther: true);
            }
            return Results.Ok(await ToDtoAsync(db, tenantId, found, ct));
        }

        private static async Task<SuggestionsDto> ToDtoAsync(PlatformDbContext db, Guid tenantId, List<Suggestion> found, CancellationToken ct)
        {
            var alerts = found.SelectMany(s => new[] { s.Alert, s.Other }).DistinctBy(a => a.Id).ToList();
            var dtos = (await AlertEndpoints.ToDtosAsync(db, tenantId, alerts, ct)).ToDictionary(a => a.Id);
            return new SuggestionsDto(CorrelationSuggestions.Relation, CorrelationSuggestions.Note, CorrelationScorer.Threshold,
                found.Select(s => new SuggestionDto(dtos[s.Alert.Id], dtos[s.Other.Id], s.Score, s.Evidence, s.IncidentId)).ToList());
        }

        private static Task<IResult> AcceptAsync(Guid tenantId, Guid alertId, Guid otherId, AcceptSuggestionRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
            => DecideAsync(tenantId, alertId, otherId, CorrelationDecisionKind.Accepted, request ?? new(null), http, db, clock, ct);

        private static Task<IResult> DismissAsync(Guid tenantId, Guid alertId, Guid otherId, DismissSuggestionRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
            => DecideAsync(tenantId, alertId, otherId, CorrelationDecisionKind.Dismissed, request ?? new(null), http, db, clock, ct);

        /// <summary>
        /// Both alerts' incident membership is locked first (<see cref="IncidentEndpoints.LockMembershipAsync"/>), so two people deciding the same pair at once,
        /// or someone adding one of them to an incident by hand, are taken one after the other and the second sees the first.
        /// The pair is scored again under the lock: what's stored is what it was decided on, and a pair no longer suggested (one resolved, the evidence gone,
        /// or both now in incidents) is refused rather than merged on a stale page.
        /// </summary>
        private static async Task<IResult> DecideAsync(Guid tenantId, Guid alertId, Guid otherId, CorrelationDecisionKind kind, object request,
            HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var accepting = kind == CorrelationDecisionKind.Accepted;
            var (first, second) = CorrelationDecision.Pair(alertId, otherId);
            var target = $"{first}:{second}";
            var mutation = new Mutation(http, db, tenantId, accepting ? "correlation.accept" : "correlation.dismiss", "correlation");
            if (await mutation.RequireAsync(target, ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (alertId == otherId) return Mutation.Invalid(["A suggestion is two different alerts."]);
            var title = accepting ? ChangeSanitizer.Text(((AcceptSuggestionRequest)request).Title, IncidentEndpoints.MaxTitle) : "";
            var reason = accepting ? "" : ChangeSanitizer.Text(((DismissSuggestionRequest)request).Reason, MaxReason);
            if (!accepting && reason.Length == 0) return Mutation.Invalid(["Say why: a reason is required to dismiss a suggestion."]);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await IncidentEndpoints.LockMembershipAsync(db, [first, second], ct);
            // Under the lock, so a retry sent while the first was still running gets its answer, not "already decided".
            if (await mutation.ReplayAsync($"POST alerts/{alertId}/suggestions/{otherId}/{(accepting ? "accept" : "dismiss")}", request, ct) is { } replay) return replay;
            var alerts = await db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId && (a.Id == alertId || a.Id == otherId)).ToListAsync(ct);
            if (alerts.Count != 2) return Mutation.Problem(StatusCodes.Status404NotFound, "Alert not found", "An alert doesn't exist.");
            var (alert, other) = (alerts.Single(a => a.Id == alertId), alerts.Single(a => a.Id == otherId));

            async Task<IResult> RefuseAsync(string why, string title, string detail)
            {
                mutation.Audit(AuditOutcome.Failed, target, new() { ["reason"] = why });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Mutation.Problem(StatusCodes.Status409Conflict, title, detail);
            }

            if (await db.CorrelationDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenantId && d.FirstAlertId == first && d.SecondAlertId == second, ct) is { } earlier)
            {
                return await RefuseAsync("decided", "Already decided",
                    $"{earlier.DecidedBy} {earlier.Decision.ToString().ToLowerInvariant()} this suggestion at {earlier.DecidedAt.ToString("u", CultureInfo.InvariantCulture)}.");
            }
            if (!CorrelationSuggestions.Candidates(alert, other))
            {
                return await RefuseAsync("not_candidates", "No longer suggested",
                    $"Both alerts must be firing, within {CorrelationScorer.Window.TotalMinutes:0} minutes of each other, in the same environment.");
            }
            var incidents = await CorrelationSuggestions.OpenIncidentsAsync(db, [alertId, otherId], ct);
            if (incidents.ContainsKey(alertId) && incidents.ContainsKey(otherId))
            {
                return await RefuseAsync("in_incidents", "Already in incidents", incidents[alertId] == incidents[otherId]
                    ? "Both alerts are already in the same incident."
                    : "Each alert is in a different unresolved incident. Move one there by hand if they belong together.");
            }
            var suggestion = (await CorrelationSuggestions.ScoreAsync(db, tenantId, [(alert, other)], incidents, ct))[0];
            if (!CorrelationScorer.Suggested(suggestion.Score))
            {
                return await RefuseAsync("below_threshold", "No longer suggested",
                    $"The evidence now scores {suggestion.Score.ToString("0.00", CultureInfo.InvariantCulture)}, under {CorrelationScorer.Threshold.ToString("0.0", CultureInfo.InvariantCulture)}.");
            }

            var now = clock.GetUtcNow();
            var score = suggestion.Score.ToString("0.00", CultureInfo.InvariantCulture);
            Guid? incidentId = null;
            if (accepting)
            {
                var from = $"From a correlation suggestion, score {score}";
                if (suggestion.IncidentId is { } existing)
                {
                    // Lock it as a transition does, so it can't be resolved while the alert joins it.
                    await db.Database.ExecuteSqlAsync($"SELECT 1 FROM incidents WHERE id = {existing} FOR UPDATE", ct);
                    var incident = await db.Incidents.SingleAsync(i => i.Id == existing, ct);
                    if (incident.State == IncidentState.Resolved)
                    {
                        return await RefuseAsync("incident_resolved", "Incident resolved", "The incident was resolved meanwhile. Look again.");
                    }
                    if (await db.IncidentAlerts.CountAsync(l => l.IncidentId == existing, ct) >= IncidentEndpoints.MaxAlerts)
                    {
                        return await RefuseAsync("incident_full", "Too many alerts", $"An incident has at most {IncidentEndpoints.MaxAlerts} alerts.");
                    }
                    IncidentEndpoints.Join(db, incident, incidents.ContainsKey(alertId) ? otherId : alertId, mutation.Actor, now, from);
                    incidentId = existing;
                }
                else
                {
                    if (title.Length == 0) title = await DefaultTitleAsync(db, alert, other, ct);
                    var severity = (AlertSeverity)Math.Max((int)alert.Severity, (int)other.Severity);
                    incidentId = IncidentEndpoints.Open(db, tenantId, title, severity, mutation.Actor, from, [alertId, otherId], now).Id;
                }
            }

            var decision = new CorrelationDecision
            {
                TenantId = tenantId, FirstAlertId = first, SecondAlertId = second, Decision = kind, Score = suggestion.Score, Evidence = suggestion.Evidence,
                WeightsVersion = CorrelationScorer.WeightsVersion, Reason = accepting ? null : reason, IncidentId = incidentId, DecidedBy = mutation.Actor, DecidedAt = now,
            };
            db.CorrelationDecisions.Add(decision);
            var metadata = new Dictionary<string, string> { ["score"] = score, ["evidence"] = string.Join(",", suggestion.Evidence.Select(e => e.Code)) };
            if (incidentId is { } merged) metadata["incident"] = merged.ToString();
            mutation.Audit(AuditOutcome.Succeeded, target, metadata);
            var result = mutation.Remember(StatusCodes.Status200OK, new CorrelationDecisionDto(kind.ToString(), alertId, otherId, decision.Score, decision.Evidence,
                decision.WeightsVersion, incidentId, decision.Reason, decision.DecidedBy, now), now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        /// <summary>"error-rate on service:shop/orders@production and slo-fast-burn on service:shop/payments@production", cut to fit.</summary>
        private static async Task<string> DefaultTitleAsync(PlatformDbContext db, Alert a, Alert b, CancellationToken ct)
        {
            var rules = await db.Rules.AsNoTracking().Where(r => r.Id == a.RuleId || r.Id == b.RuleId).ToDictionaryAsync(r => r.Id, r => r.Key, ct);
            var title = $"{rules.GetValueOrDefault(a.RuleId, "alert")} on {a.SubjectKey} and {rules.GetValueOrDefault(b.RuleId, "alert")} on {b.SubjectKey}";
            return title.Length <= IncidentEndpoints.MaxTitle ? title : title[..(IncidentEndpoints.MaxTitle - 1)] + "…";
        }
    }
}
