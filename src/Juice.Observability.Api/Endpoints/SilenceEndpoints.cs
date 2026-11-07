using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record SilenceDto(Guid Id, string Kind, string Comment, IReadOnlyList<string> RuleKeys, string? Service, string? Environment, string? Host, string? MaximumSeverity, bool AllAlerts,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool RepeatWeekly, bool Active, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt, string? CancelledBy, int Held);

    public sealed record SilenceRequest(string? Kind, string? Comment, List<string>? RuleKeys, string? Service, string? Environment, string? Host, string? MaximumSeverity, bool? AllAlerts,
        DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool? RepeatWeekly);

    public sealed record EscalationPolicyDto(Guid Id, string Key, string Name, bool Enabled, IReadOnlyList<string> RuleKeys, IReadOnlyList<string> Environments, string MinimumSeverity,
        bool StopOnAcknowledge, IReadOnlyList<EscalationStep> Steps, DateTimeOffset UpdatedAt);

    public sealed record EscalationPolicyRequest(string? Key, string? Name, bool? Enabled, List<string>? RuleKeys, List<string>? Environments, string? MinimumSeverity, bool? StopOnAcknowledge, List<EscalationStep>? Steps);

    public sealed record AlertEscalationDto(string Policy, string Name, string State, int StepsSent, int Steps, DateTimeOffset? NextAt, string? StopReason, DateTimeOffset? EndedAt);

    public sealed record AlertNotificationDto(string EndpointKey, string Kind, WebhookDeliveryDto Delivery);

    /// <summary>Everything notification has done or will do about one alert.</summary>
    public sealed record AlertNotificationsDto(IReadOnlyList<AlertNotificationDto> Deliveries, IReadOnlyList<AlertEscalationDto> Escalations, IReadOnlyList<SilenceDto> Silences, IReadOnlyList<IncidentSummaryDto> Incidents);

    /// <summary>
    /// Silences and maintenance windows (operator or admin: stopping noise during an outage shouldn't need an admin), escalation policies (admin, since they say who
    /// is woken), and one alert's notifications. A silence changes what is sent, never an alert. Cancelling keeps it as history.
    /// </summary>
    public static partial class SilenceEndpoints
    {
        public const int MaxActive = 200;
        public const int MaxPolicies = 100;
        public static readonly TimeSpan MaxLength = TimeSpan.FromDays(7);
        /// <summary>Beyond this it would come round continuously, or nearly: a weekly window is for maintenance, not a permanent mute.</summary>
        public static readonly TimeSpan MaxWeeklyLength = TimeSpan.FromHours(72);
        public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(90);

        public static RouteGroupBuilder MapSilenceEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/silences", ListAsync);
            tenant.MapPost("/silences", CreateAsync);
            tenant.MapDelete("/silences/{silenceId:guid}", CancelAsync);
            tenant.MapGet("/escalation-policies", ListPoliciesAsync);
            tenant.MapPost("/escalation-policies", CreatePolicyAsync);
            tenant.MapPut("/escalation-policies/{key}", UpdatePolicyAsync);
            tenant.MapGet("/alerts/{alertId:guid}/notifications", ForAlertAsync);
            return tenant;
        }

        private static SilenceDto ToDto(Silence s, DateTimeOffset now, int held = 0) => new(s.Id, s.Kind.ToString(), s.Comment, s.RuleKeys, s.Service, s.Environment, s.Host, s.MaximumSeverity?.ToString(), s.AllAlerts,
            s.StartsAt, s.EndsAt, s.RepeatWeekly, SilenceMatcher.IsActive(s, now), s.CreatedBy, s.CreatedAt, s.CancelledAt, s.CancelledBy, held);

        /// <summary>Silences newest first; by default only those not over, since those are the ones that matter. <c>all=true</c> for the history.</summary>
        private static async Task<IResult> ListAsync(Guid tenantId, bool? all, int? limit, string? cursor, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var now = clock.GetUtcNow();
            var query = db.Silences.AsNoTracking().Where(s => s.TenantId == tenantId);
            if (all != true) query = query.Where(s => s.CancelledAt == null && (s.RepeatWeekly || s.EndsAt > now));
            if (after is not null) query = query.Where(s => s.Id < after.After);
            var rows = await query.OrderByDescending(s => s.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var ids = page.Select(s => (Guid?)s.Id).ToList();
            var held = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Status == DeliveryStatus.Suppressed && ids.Contains(d.SuppressedBy))
                .GroupBy(d => d.SuppressedBy).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.Count, ct);
            return Results.Ok(new Page<SilenceDto>(page.Select(s => ToDto(s, now, held.GetValueOrDefault(s.Id))).ToList(), rows.Count > take ? new Cursor("si", page[^1].Id).Encode() : null));
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, SilenceRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "silence.create", "silence");
            if (await mutation.RequireAsync(null, ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync("POST silences", request, ct) is { } replay) return replay;

            var now = clock.GetUtcNow();
            var errors = new List<string>();
            var kind = SilenceKind.Silence;
            if (request.Kind is { Length: > 0 } && (!Enum.TryParse(request.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind))) errors.Add("kind is silence or maintenance.");
            var comment = ChangeSanitizer.Text(request.Comment, 500);
            if (comment.Length == 0) errors.Add("comment is required: say why, for whoever wonders where the alerts went.");
            AlertSeverity? maximum = null;
            if (request.MaximumSeverity is { Length: > 0 })
            {
                if (Enum.TryParse<AlertSeverity>(request.MaximumSeverity, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) maximum = parsed;
                else errors.Add("maximumSeverity is info, warning or critical.");
            }
            var starts = request.StartsAt ?? now;
            var ends = request.EndsAt ?? DateTimeOffset.MinValue;
            var weekly = request.RepeatWeekly == true;
            var length = ends - starts;
            if (ends <= starts) errors.Add("endsAt is required, and after startsAt: a silence nobody ends is how an outage goes unnoticed.");
            else if (length > MaxLength) errors.Add("A silence lasts at most 7 days.");
            if (ends <= now && !weekly) errors.Add("endsAt is in the past.");
            if (starts > now + MaxAhead) errors.Add("startsAt is at most 90 days ahead.");
            if (starts < now - TimeSpan.FromMinutes(5)) errors.Add("startsAt isn't in the past.");
            if (weekly && kind != SilenceKind.Maintenance) errors.Add("Only a maintenance window repeats.");
            if (weekly && length > MaxWeeklyLength) errors.Add("A weekly window lasts at most 72 hours.");
            var environment = request.Environment?.Trim().ToLowerInvariant();
            var silence = new Silence
            {
                TenantId = tenantId, Kind = kind, Comment = comment, RuleKeys = [.. (request.RuleKeys ?? []).Where(r => r.Length > 0).Distinct()],
                Service = Empty(request.Service), Environment = Empty(environment), Host = Empty(request.Host), MaximumSeverity = maximum, AllAlerts = request.AllAlerts == true,
                StartsAt = starts, EndsAt = ends, RepeatWeekly = weekly, CreatedBy = mutation.Actor, CreatedAt = now,
            };
            if (!silence.AllAlerts && !SilenceMatcher.HasScope(silence)) errors.Add("Say what to silence: a rule, a service, an environment, a host or a severity. To silence everything, set allAlerts.");
            if (silence.AllAlerts && SilenceMatcher.HasScope(silence)) errors.Add("allAlerts takes no other scope.");
            if (new[] { silence.Service, silence.Environment, silence.Host }.Any(v => v is { Length: > 256 })) errors.Add("Scope values are at most 256 characters.");
            if (silence.RuleKeys.Length > 50) errors.Add("At most 50 rules.");
            if (errors.Count > 0) return Mutation.Invalid(errors);
            if (await db.Silences.CountAsync(s => s.TenantId == tenantId && s.CancelledAt == null && (s.RepeatWeekly || s.EndsAt > now), ct) >= MaxActive)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Too many silences", $"A tenant has at most {MaxActive} silences at once.");
            }
            db.Silences.Add(silence);
            mutation.Audit(AuditOutcome.Succeeded, silence.Id.ToString(), new()
            {
                ["kind"] = kind.ToString(), ["startsAt"] = starts.ToString("O"), ["endsAt"] = ends.ToString("O"), ["allAlerts"] = silence.AllAlerts ? "true" : "false",
            });
            var result = mutation.Remember(StatusCodes.Status201Created, ToDto(silence, now), now);
            await db.SaveChangesAsync(ct);
            return result;
        }

        private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Ends a silence now. What it held is released on the worker's next pass. Cancelling twice changes nothing.</summary>
        private static async Task<IResult> CancelAsync(Guid tenantId, Guid silenceId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "silence.cancel", "silence");
            if (await mutation.RequireAsync(silenceId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            var silence = await db.Silences.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == silenceId, ct);
            if (silence is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Silence not found", "No such silence.");
            }
            if (silence.CancelledAt is null)
            {
                silence.CancelledAt = clock.GetUtcNow();
                silence.CancelledBy = mutation.Actor;
                mutation.Audit(AuditOutcome.Succeeded, silenceId.ToString());
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }

        private static EscalationPolicyDto ToDto(EscalationPolicy p) => new(p.Id, p.Key, p.Name, p.Enabled, p.RuleKeys, p.Environments, p.MinimumSeverity.ToString(), p.StopOnAcknowledge, p.Steps, p.UpdatedAt);

        private static async Task<IResult> ListPoliciesAsync(Guid tenantId, PlatformDbContext db, CancellationToken ct)
        {
            var rows = await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == tenantId).OrderBy(p => p.Key).ToListAsync(ct);
            return Results.Ok(new Page<EscalationPolicyDto>(rows.Select(ToDto).ToList(), null));
        }

        private static async Task<(AlertSeverity Severity, IResult? Problem)> ValidateAsync(Guid tenantId, EscalationPolicyRequest? request, PlatformDbContext db, CancellationToken ct)
        {
            var errors = new List<string>();
            if (request is null) return (default, Mutation.Invalid(["A body is required."]));
            if (request.Name is not { Length: > 0 and <= 200 }) errors.Add("name is required (at most 200 characters).");
            var severity = AlertSeverity.Info;
            if (request.MinimumSeverity is { Length: > 0 } && (!Enum.TryParse(request.MinimumSeverity, ignoreCase: true, out severity) || !Enum.IsDefined(severity))) errors.Add("minimumSeverity is info, warning or critical.");
            var steps = request.Steps ?? [];
            if (steps.Count is 0 or > 10) errors.Add("A policy has 1 to 10 steps.");
            var previous = -1;
            foreach (var step in steps)
            {
                if (step.AfterMinutes is < 0 or > 10_080) errors.Add("afterMinutes is 0 to 10080.");
                if (step.AfterMinutes <= previous) errors.Add("Each step comes later than the one before.");
                previous = step.AfterMinutes;
                if (step.EndpointKeys.Count is 0 or > 10) errors.Add("A step notifies 1 to 10 endpoints.");
            }
            var named = steps.SelectMany(s => s.EndpointKeys).Distinct().ToList();
            var known = await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenantId && named.Contains(e.Key)).Select(e => e.Key).ToListAsync(ct);
            errors.AddRange(named.Except(known).Select(k => $"No endpoint '{k}'."));
            if ((request.Environments ?? []).Any(e => e != e.ToLowerInvariant())) errors.Add("environments are lowercase keys.");
            return (severity, errors.Count > 0 ? Mutation.Invalid(errors) : null);
        }

        private static async Task<IResult> CreatePolicyAsync(Guid tenantId, EscalationPolicyRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "escalation.create", "escalation_policy");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            var (severity, problem) = await ValidateAsync(tenantId, request, db, ct);
            if (problem is not null) return problem;
            if (request!.Key is not { } key || !PolicyKey().IsMatch(key)) return Mutation.Invalid(["key is lowercase letters, digits and dashes, 2 to 63 long."]);
            if (await db.EscalationPolicies.AnyAsync(p => p.TenantId == tenantId && p.Key == key, ct)) return Mutation.Problem(StatusCodes.Status409Conflict, "Policy exists", $"A policy with the key '{key}' exists.");
            if (await db.EscalationPolicies.CountAsync(p => p.TenantId == tenantId, ct) >= MaxPolicies) return Mutation.Problem(StatusCodes.Status409Conflict, "Too many policies", $"A tenant has at most {MaxPolicies} policies.");
            var policy = new EscalationPolicy
            {
                TenantId = tenantId, Key = key, Name = request.Name!, Enabled = request.Enabled ?? true, RuleKeys = [.. request.RuleKeys ?? []], Environments = [.. request.Environments ?? []],
                MinimumSeverity = severity, StopOnAcknowledge = request.StopOnAcknowledge ?? true, Steps = request.Steps!, UpdatedAt = clock.GetUtcNow(),
            };
            db.EscalationPolicies.Add(policy);
            mutation.Audit(AuditOutcome.Succeeded, key, new() { ["steps"] = policy.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await db.SaveChangesAsync(ct);
            return Results.Created($"{http.Request.Path}/{key}", ToDto(policy));
        }

        private static async Task<IResult> UpdatePolicyAsync(Guid tenantId, string key, EscalationPolicyRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "escalation.update", "escalation_policy");
            if (await mutation.RequireAsync(key, ct, Roles.Admin) is { } denied) return denied;
            var policy = await db.EscalationPolicies.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Key == key, ct);
            if (policy is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Policy not found", $"No policy '{key}'.");
            var (severity, problem) = await ValidateAsync(tenantId, request, db, ct);
            if (problem is not null) return problem;
            policy.Name = request!.Name!;
            policy.Enabled = request.Enabled ?? true;
            policy.RuleKeys = [.. request.RuleKeys ?? []];
            policy.Environments = [.. request.Environments ?? []];
            policy.MinimumSeverity = severity;
            policy.StopOnAcknowledge = request.StopOnAcknowledge ?? true;
            policy.Steps = request.Steps!;
            policy.UpdatedAt = clock.GetUtcNow();
            mutation.Audit(AuditOutcome.Succeeded, key, new() { ["steps"] = policy.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["enabled"] = policy.Enabled ? "true" : "false" });
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(policy));
        }

        /// <summary>What was sent about an alert, what is held or still to come, and which incidents it's in.</summary>
        private static async Task<IResult> ForAlertAsync(Guid tenantId, Guid alertId, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Alert not found", "No such alert.");
            }
            var now = clock.GetUtcNow();
            var deliveries = await db.WebhookDeliveries.AsNoTracking().Where(d => d.TenantId == tenantId && d.AlertId == alertId).OrderByDescending(d => d.Id).Take(200).ToListAsync(ct);
            var dtos = await WebhookEndpoints.ToDeliveryDtosAsync(db, deliveries, ct);
            var endpointIds = deliveries.Select(d => d.EndpointId).Distinct().ToList();
            var endpoints = await db.WebhookEndpoints.AsNoTracking().Where(e => endpointIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

            var escalations = await db.AlertEscalations.AsNoTracking().Where(e => e.TenantId == tenantId && e.AlertId == alertId).ToListAsync(ct);
            var policyIds = escalations.Select(e => e.PolicyId).ToList();
            var policies = await db.EscalationPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

            var rule = await db.Rules.AsNoTracking().Where(r => r.Id == alert.RuleId).Select(r => r.Key).SingleAsync(ct);
            var context = new AlertContext(rule, alert.SubjectKey, alert.Severity);
            var silences = (await db.Silences.AsNoTracking().Where(s => s.TenantId == tenantId && s.CancelledAt == null && s.StartsAt <= now && (s.RepeatWeekly || s.EndsAt > now)).ToListAsync(ct))
                .Where(s => SilenceMatcher.IsActive(s, now) && SilenceMatcher.Matches(s, context)).Select(s => ToDto(s, now)).ToList();

            var incidents = await db.IncidentAlerts.AsNoTracking().Where(l => l.TenantId == tenantId && l.AlertId == alertId)
                .Join(db.Incidents.AsNoTracking(), l => l.IncidentId, i => i.Id, (l, i) => i).OrderByDescending(i => i.Id).ToListAsync(ct);
            return Results.Ok(new AlertNotificationsDto(
                dtos.Select((d, i) => new AlertNotificationDto(endpoints[deliveries[i].EndpointId].Key, endpoints[deliveries[i].EndpointId].Kind.ToString(), d)).ToList(),
                escalations.Select(e => new AlertEscalationDto(policies[e.PolicyId].Key, policies[e.PolicyId].Name, e.State.ToString(), e.NextStep, policies[e.PolicyId].Steps.Count,
                    e.State == EscalationState.Active ? e.NextAt : null, e.StopReason, e.EndedAt)).ToList(),
                silences, incidents.Select(IncidentEndpoints.ToSummary).ToList()));
        }

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex PolicyKey();
    }
}
