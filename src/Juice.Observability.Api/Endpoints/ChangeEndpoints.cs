using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record ChangeDto(Guid Id, string Kind, string Source, string? Actor, string Summary, Guid? ServiceId, Guid? ResourceId, string? Environment,
        string? FromVersion, string? ToVersion, IReadOnlyDictionary<string, string> Attributes, DateTimeOffset OccurredAt);

    /// <summary>A change a pipeline reports. <c>Service</c> is a discovered service's <c>namespace/name</c> or name; the actor comes from the token.</summary>
    public sealed record ReportChangeRequest(string? Kind, string? Summary, string? Service, Guid? ResourceId, string? Environment, string? FromVersion, string? ToVersion,
        DateTimeOffset? OccurredAt, string? Source, string? ExternalKey, Dictionary<string, string?>? Attributes);

    /// <summary>One line of a timeline: <c>Type</c> is <c>change</c>, <c>health</c> or <c>alert</c>, and <c>Id</c> the change's or the alert's.</summary>
    public sealed record TimelineEntryDto(DateTimeOffset At, string Type, string Title, string? Detail, string? State, Guid? Id, Guid? ServiceId, string? Environment);

    public sealed record TimelineDto(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TimelineEntryDto> Entries, bool Truncated);

    public sealed record RelatedDto(string Relation, string Note, DateTimeOffset BreachStartedAt, IReadOnlyList<CorrelatedItemDto> Items);

    /// <summary>Phase 5: change events, the timeline joining them with health and alerts, and what relates to an alert.</summary>
    public static partial class ChangeEndpoints
    {
        public const int MaxShortText = 128;
        public const int MaxTimelineEntries = 500;
        public static readonly TimeSpan DefaultTimeline = TimeSpan.FromHours(24);
        public static readonly TimeSpan MaxTimeline = TimeSpan.FromDays(7);
        /// <summary>A pipeline's clock may be off by this much; beyond it, the time is more likely wrong.</summary>
        public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(7);

        public static RouteGroupBuilder MapChangeEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/changes", ListAsync);
            tenant.MapPost("/changes", ReportAsync);
            tenant.MapGet("/timeline", TimelineAsync);
            tenant.MapGet("/alerts/{alertId:guid}/related", RelatedAsync);
            return tenant;
        }

        private static ChangeDto ToDto(ChangeEvent c) => new(c.Id, c.Kind.ToString(), c.Source, c.Actor, c.Summary, c.ServiceId, c.ResourceId, c.Environment,
            c.FromVersion, c.ToVersion, c.Attributes, c.OccurredAt);

        private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);

        /// <summary>Changes, newest first, filtered by service, resource, environment and kind.</summary>
        private static async Task<IResult> ListAsync(Guid tenantId, Guid? serviceId, Guid? resourceId, string? environment, string? kind, int? limit, string? cursor,
            PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.ChangeEvents.AsNoTracking().Where(c => c.TenantId == tenantId);
            if (serviceId is { } service) query = query.Where(c => c.ServiceId == service);
            if (resourceId is { } resource) query = query.Where(c => c.ResourceId == resource);
            if (!string.IsNullOrEmpty(environment))
            {
                var key = environment.ToLowerInvariant();
                query = query.Where(c => c.Environment == key);
            }
            if (kind is not null)
            {
                if (!Enum.TryParse<ChangeKind>(kind, ignoreCase: true, out var parsed))
                {
                    return Problem(StatusCodes.Status400BadRequest, "Invalid kind", "kind must be one of: deployment, version, configuration, other.");
                }
                query = query.Where(c => c.Kind == parsed);
            }
            if (after is not null) query = query.Where(c => c.Id < after.After);
            var rows = await query.OrderByDescending(c => c.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            return Results.Ok(new Page<ChangeDto>(page.Select(ToDto).ToList(), rows.Count > take ? new Cursor("ch", page[^1].Id).Encode() : null));
        }

        /// <summary>
        /// Records a change a pipeline reports (operator or admin). Text is masked and attributes allowlisted before anything is stored, and the
        /// audit record holds no request text. Reporting the same <c>externalKey</c> again returns the first event.
        /// </summary>
        private static async Task<IResult> ReportAsync(Guid tenantId, ReportChangeRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var actor = http.User.Actor();
            void Audit(AuditOutcome outcome, Guid? id, string? reason = null) => db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenantId, Actor = actor, Operation = "change.report", TargetType = "change", TargetId = id?.ToString(),
                Outcome = outcome, CorrelationId = CorrelationId.Get(http), Metadata = reason is null ? [] : new() { ["reason"] = reason },
            });
            if (!http.User.IsInRole(Roles.Operator) && !http.User.IsInRole(Roles.Admin))
            {
                Audit(AuditOutcome.Denied, null, "role");
                await db.SaveChangesAsync(ct);
                return Problem(StatusCodes.Status403Forbidden, "Forbidden", "Reporting changes needs the operator or admin role.");
            }

            if (!Enum.TryParse<ChangeKind>(request?.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || kind == ChangeKind.Version && string.IsNullOrWhiteSpace(request?.ToVersion))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid kind", "kind must be one of: deployment, version, configuration, other. A version change needs toVersion.");
            }
            var summary = ChangeSanitizer.Text(request!.Summary, ChangeSanitizer.MaxSummary);
            if (summary.Length == 0)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid summary", "summary is required.");
            }
            var now = clock.GetUtcNow();
            var occurredAt = request.OccurredAt ?? now;
            if (occurredAt > now + MaxClockSkew || occurredAt < now - MaxBackdate)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid occurredAt", "occurredAt is at most 7 days ago, and not in the future.");
            }
            var externalKey = request.ExternalKey?.Trim();
            if (externalKey is { Length: 0 }) externalKey = null;
            if (externalKey is not null && !ExternalKey().IsMatch(externalKey))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid externalKey", "externalKey is 1 to 128 of [A-Za-z0-9._:/-].");
            }

            Guid? serviceId = null;
            if (!string.IsNullOrWhiteSpace(request.Service))
            {
                var name = request.Service.Trim();
                var slash = name.LastIndexOf('/');
                var (ns, plain) = slash >= 0 ? (name[..slash], name[(slash + 1)..]) : ((string?)null, name);
                var matches = await db.Services.AsNoTracking()
                    .Where(s => s.TenantId == tenantId && s.Name == plain && (ns == null || s.Namespace == ns)).Select(s => s.Id).Take(2).ToListAsync(ct);
                if (matches.Count != 1)
                {
                    return Problem(StatusCodes.Status422UnprocessableEntity, "Unknown service",
                        matches.Count == 0 ? "No discovered service has that name." : "More than one service has that name: use namespace/name.");
                }
                serviceId = matches[0];
            }
            if (request.ResourceId is { } resourceId && !await db.Resources.AsNoTracking().AnyAsync(r => r.TenantId == tenantId && r.Id == resourceId, ct))
            {
                return Problem(StatusCodes.Status422UnprocessableEntity, "Unknown resource", "No discovered resource has that id.");
            }
            var source = ChangeSanitizer.Text(request.Source, MaxShortText);
            if (source.Length == 0) source = "api";
            if (source is ChangeEvent.TelemetrySource or ChangeEvent.PlatformSource)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid source", "That source is reserved for what the platform records itself.");
            }
            if (externalKey is not null)
            {
                var existing = await db.ChangeEvents.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Source == source && c.ExternalKey == externalKey, ct);
                if (existing is not null) return Results.Ok(ToDto(existing));
            }

            var attributes = ChangeSanitizer.Attributes(request.Attributes, out var dropped);
            var environment = ChangeSanitizer.Text(request.Environment, MaxShortText).ToLowerInvariant();
            var change = new ChangeEvent
            {
                TenantId = tenantId, Kind = kind, Source = source, Actor = actor, Summary = summary, ServiceId = serviceId, ResourceId = request.ResourceId,
                Environment = environment.Length > 0 ? environment : null,
                FromVersion = Optional(ChangeSanitizer.Text(request.FromVersion, MaxShortText)), ToVersion = Optional(ChangeSanitizer.Text(request.ToVersion, MaxShortText)),
                Attributes = attributes, OccurredAt = occurredAt, RecordedAt = now, ExternalKey = externalKey,
            };
            db.ChangeEvents.Add(change);
            Audit(AuditOutcome.Succeeded, change.Id, dropped > 0 ? $"{dropped} attributes dropped" : null);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (externalKey is not null)
            {
                // Two reports of one change at once: the other one stored it.
                db.ChangeTracker.Clear();
                var existing = await db.ChangeEvents.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Source == source && c.ExternalKey == externalKey, ct);
                if (existing is null) throw;
                return Results.Ok(ToDto(existing));
            }
            return Results.Created($"changes/{change.Id}", ToDto(change));
        }

        private static string? Optional(string text) => text.Length > 0 ? text : null;

        /// <summary>
        /// Changes, health transitions and alert transitions in one order, oldest first, for a service or the tenant. Telemetry is drawn beside
        /// it from <c>/telemetry/service-stats</c> over the same window. Things in a line together aren't said to be related.
        /// </summary>
        private static async Task<IResult> TimelineAsync(Guid tenantId, Guid? serviceId, string? environment, DateTimeOffset? from, DateTimeOffset? to,
            PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var end = to ?? clock.GetUtcNow();
            var start = from ?? end - DefaultTimeline;
            if (start >= end || end - start > MaxTimeline)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid time range", "from must be before to, at most 7 days apart.");
            }
            if (serviceId is { } requested && !await db.Services.AsNoTracking().AnyAsync(s => s.TenantId == tenantId && s.Id == requested, ct))
            {
                return Problem(StatusCodes.Status404NotFound, "Service not found", "No such service.");
            }
            var key = string.IsNullOrEmpty(environment) ? null : environment.ToLowerInvariant();
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.Key, ct);
            Guid? environmentId = key is null ? null : environments.Where(e => e.Value == key).Select(e => (Guid?)e.Key).FirstOrDefault();
            // Enough of each kind that one kind can't crowd the others out of the newest entries.
            var each = MaxTimelineEntries + 1;

            var changes = db.ChangeEvents.AsNoTracking().Where(c => c.TenantId == tenantId && c.OccurredAt >= start && c.OccurredAt < end);
            if (serviceId is { } service) changes = changes.Where(c => c.ServiceId == service);
            // A change with no environment may apply to any.
            if (key is not null) changes = changes.Where(c => c.Environment == key || c.Environment == null);
            var entries = (await changes.OrderByDescending(c => c.OccurredAt).Take(each).ToListAsync(ct)).Select(c => new TimelineEntryDto(c.OccurredAt, "change", c.Summary,
                c.Actor is null ? c.Source : $"{c.Source}, by {c.Actor}", c.Kind.ToString(), c.Id, c.ServiceId, c.Environment)).ToList();

            var health = db.HealthTransitions.AsNoTracking().Where(h => h.TenantId == tenantId && h.SubjectType == HealthSubject.Service && h.EvaluatedAt >= start && h.EvaluatedAt < end);
            if (serviceId is { } healthService) health = health.Where(h => h.SubjectId == healthService);
            if (key is not null) health = environmentId is { } id ? health.Where(h => h.EnvironmentId == id) : health.Where(_ => false);
            entries.AddRange((await health.OrderByDescending(h => h.EvaluatedAt).Take(each).ToListAsync(ct)).Select(h => new TimelineEntryDto(h.EvaluatedAt, "health",
                $"Health {(h.FromState is { } was ? $"{was} → " : "")}{h.ToState}", string.Join(", ", h.ReasonCodes), h.ToState.ToString(), null, h.SubjectId,
                h.EnvironmentId is { } e ? environments.GetValueOrDefault(e) : null)));

            var alerts = db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId);
            if (serviceId is { } alertService) alerts = alerts.Where(a => a.SubjectId == alertService);
            if (key is not null) alerts = environmentId is { } alertEnvironment ? alerts.Where(a => a.EnvironmentId == alertEnvironment) : alerts.Where(_ => false);
            var transitions = await db.AlertTransitions.AsNoTracking().Where(t => t.TenantId == tenantId && t.At >= start && t.At < end)
                .Join(alerts, t => t.AlertId, a => a.Id, (t, a) => new { t.At, t.ToState, t.Reason, a.Id, a.SubjectKey, a.SubjectId, a.EnvironmentId, a.RuleId })
                .OrderByDescending(t => t.At).Take(each).ToListAsync(ct);
            var ruleIds = transitions.Select(t => t.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, ct);
            entries.AddRange(transitions.Select(t => new TimelineEntryDto(t.At, "alert", $"Alert {rules.GetValueOrDefault(t.RuleId, "")} {t.ToState}", $"{t.SubjectKey}: {t.Reason}", t.ToState.ToString(), t.Id, t.SubjectId,
                t.EnvironmentId is { } e ? environments.GetValueOrDefault(e) : null)));

            var truncated = entries.Count > MaxTimelineEntries;
            var kept = entries.OrderByDescending(e => e.At).Take(MaxTimelineEntries).OrderBy(e => e.At).ToList();
            return Results.Ok(new TimelineDto(start, end, kept, truncated));
        }

        /// <summary>
        /// What happened around an alert's breach, on its subject, on what the subject directly depends on, and to its rule, ranked with reasons
        /// (<see cref="CorrelationRanker"/>). Always labelled as correlation.
        /// </summary>
        private static async Task<IResult> RelatedAsync(Guid tenantId, Guid alertId, PlatformDbContext db, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null)
            {
                return Problem(StatusCodes.Status404NotFound, "Alert not found", "No such alert.");
            }
            var (start, end) = (alert.BreachStartedAt - CorrelationRanker.Lookback, alert.BreachStartedAt + CorrelationRanker.LookAhead);
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.Key, ct);
            var environment = alert.EnvironmentId is { } alertEnvironment ? environments.GetValueOrDefault(alertEnvironment) : null;

            var subjectServices = new HashSet<Guid>();
            var subjectResources = new HashSet<Guid>();
            if (alert.SubjectId is { } subject)
            {
                if (alert.SubjectType == HealthSubject.Service) subjectServices.Add(subject);
                else subjectResources.Add(subject);
            }
            var dependencies = await db.Dependencies.AsNoTracking()
                .Where(d => d.TenantId == tenantId && subjectServices.Contains(d.SourceServiceId) && (alert.EnvironmentId == null || d.EnvironmentId == alert.EnvironmentId))
                .Select(d => new { d.TargetServiceId, d.TargetResourceId }).ToListAsync(ct);
            var dependencyServices = dependencies.Where(d => d.TargetServiceId != null).Select(d => d.TargetServiceId!.Value).ToHashSet();
            var dependencyResources = dependencies.Where(d => d.TargetResourceId != null).Select(d => d.TargetResourceId!.Value).ToHashSet();
            var rule = await db.Rules.AsNoTracking().Where(r => r.Id == alert.RuleId).Select(r => r.Key).SingleOrDefaultAsync(ct);

            var services = subjectServices.Concat(dependencyServices).ToList();
            var resources = subjectResources.Concat(dependencyResources).ToList();
            var changes = await db.ChangeEvents.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.OccurredAt >= start && c.OccurredAt <= end
                    && (environment == null || c.Environment == null || c.Environment == environment)
                    && ((c.ServiceId != null && services.Contains(c.ServiceId.Value)) || (c.ResourceId != null && resources.Contains(c.ResourceId.Value))
                        || (c.Kind == ChangeKind.Configuration && c.Source == ChangeEvent.PlatformSource)))
                .ToListAsync(ct);
            var candidates = new List<CorrelationCandidate>();
            foreach (var c in changes)
            {
                CorrelationScope? scope = (c.ServiceId is { } s && subjectServices.Contains(s)) || (c.ResourceId is { } r && subjectResources.Contains(r)) ? CorrelationScope.Subject
                    : (c.ServiceId is { } ds && dependencyServices.Contains(ds)) || (c.ResourceId is { } dr && dependencyResources.Contains(dr)) ? CorrelationScope.Dependency
                    : c.Attributes.GetValueOrDefault(ChangeEvent.RuleAttribute) == rule ? CorrelationScope.Rule : null;
                if (scope is { } found)
                {
                    candidates.Add(new("change", c.Id, c.OccurredAt, c.Summary, c.Actor is null ? c.Source : $"{c.Source}, by {c.Actor}", c.Kind.ToString(), found,
                        c.Environment is null || environment is null ? null : c.Environment == environment));
                }
            }
            // A dependency that got worse before this alert. The subject's own health follows from the same evidence, so it isn't listed.
            var unhealthy = new[] { HealthState.Degraded, HealthState.Unhealthy, HealthState.Critical, HealthState.Offline };
            var dependencyList = dependencyServices.ToList();
            var health = await db.HealthTransitions.AsNoTracking()
                .Where(h => h.TenantId == tenantId && h.SubjectType == HealthSubject.Service && dependencyList.Contains(h.SubjectId) && h.EvaluatedAt >= start && h.EvaluatedAt <= end
                    && unhealthy.Contains(h.ToState) && (alert.EnvironmentId == null || h.EnvironmentId == alert.EnvironmentId))
                .ToListAsync(ct);
            var names = await db.Services.AsNoTracking().Where(s => dependencyList.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            candidates.AddRange(health.Select(h => new CorrelationCandidate("health", h.Id, h.EvaluatedAt, $"{names.GetValueOrDefault(h.SubjectId, "A dependency")} became {h.ToState}",
                string.Join(", ", h.ReasonCodes), null, CorrelationScope.Dependency, alert.EnvironmentId is null ? null : true)));

            return Results.Ok(new RelatedDto(CorrelationRanker.Relation, CorrelationRanker.Note, alert.BreachStartedAt, CorrelationRanker.Rank(alert.BreachStartedAt, candidates)));
        }

        [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,128}$")]
        private static partial Regex ExternalKey();
    }
}
