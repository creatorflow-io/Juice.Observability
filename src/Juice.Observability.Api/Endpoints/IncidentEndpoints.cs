using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record IncidentSummaryDto(Guid Id, string Title, string Severity, string State, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt);

    public sealed record IncidentEventDto(DateTimeOffset At, string Actor, string Type, string? From, string? To, string? Text, Guid? AlertId);

    /// <summary><c>OpenAlerts</c> is how many of its alerts still fire or wait: resolving with some open is allowed, and the page says so.</summary>
    public sealed record IncidentDetailDto(IncidentSummaryDto Incident, IReadOnlyList<AlertDto> Alerts, IReadOnlyList<IncidentEventDto> Events, int OpenAlerts);

    public sealed record CreateIncidentRequest(string? Title, string? Severity, List<Guid>? AlertIds, string? Note);

    public sealed record IncidentTransitionRequest(string? To, string? Comment);

    public sealed record IncidentNoteRequest(string? Text);

    public sealed record IncidentAlertRequest(Guid? AlertId);

    /// <summary>Which incident transitions are allowed. Pure, so every path is unit-tested.</summary>
    public static class IncidentLifecycle
    {
        /// <summary>Forward, skipping steps if it went quickly. Back from Mitigated if it wasn't, and reopening a resolved one back to Investigating.</summary>
        public static bool CanMove(IncidentState from, IncidentState to) => (from, to) switch
        {
            (IncidentState.Open, IncidentState.Investigating or IncidentState.Mitigated or IncidentState.Resolved) => true,
            (IncidentState.Investigating, IncidentState.Mitigated or IncidentState.Resolved) => true,
            (IncidentState.Mitigated, IncidentState.Investigating or IncidentState.Resolved) => true,
            (IncidentState.Resolved, IncidentState.Investigating) => true,
            _ => false,
        };

        /// <summary>Closing and reopening are the moves someone later asks about.</summary>
        public static bool NeedsComment(IncidentState from, IncidentState to) => to == IncidentState.Resolved || from == IncidentState.Resolved;
    }

    /// <summary>
    /// Incidents (read: any role; change: operator or admin). Every change writes an audit record and an event on the incident's own record in one transaction.
    /// Text from people is masked as change summaries are, since it gets pasted from terminals. Incidents and alerts don't drive each other.
    /// </summary>
    public static class IncidentEndpoints
    {
        public const int MaxTitle = 200;
        public const int MaxText = 2000;
        public const int MaxAlerts = 100;
        public const int MaxEvents = 500;

        public static RouteGroupBuilder MapIncidentEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/incidents", ListAsync);
            tenant.MapPost("/incidents", CreateAsync);
            tenant.MapGet("/incidents/{incidentId:guid}", GetAsync);
            tenant.MapPost("/incidents/{incidentId:guid}/transitions", TransitionAsync);
            tenant.MapPost("/incidents/{incidentId:guid}/notes", NoteAsync);
            tenant.MapPost("/incidents/{incidentId:guid}/alerts", AddAlertAsync);
            tenant.MapDelete("/incidents/{incidentId:guid}/alerts/{alertId:guid}", RemoveAlertAsync);
            return tenant;
        }

        internal static IncidentSummaryDto ToSummary(Incident i) => new(i.Id, i.Title, i.Severity.ToString(), i.State.ToString(), i.CreatedBy, i.CreatedAt, i.UpdatedAt, i.ResolvedAt);

        private static IResult NotFound() => Mutation.Problem(StatusCodes.Status404NotFound, "Incident not found", "No such incident.");

        /// <summary>Newest first, filtered by <c>state</c>.</summary>
        private static async Task<IResult> ListAsync(Guid tenantId, string? state, int? limit, string? cursor, PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.Incidents.AsNoTracking().Where(i => i.TenantId == tenantId);
            if (state is not null)
            {
                if (!Enum.TryParse<IncidentState>(state, ignoreCase: true, out var parsed))
                {
                    return Mutation.Problem(StatusCodes.Status400BadRequest, "Invalid state", "state must be one of: open, investigating, mitigated, resolved.");
                }
                query = query.Where(i => i.State == parsed);
            }
            if (after is not null) query = query.Where(i => i.Id < after.After);
            var rows = await query.OrderByDescending(i => i.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            return Results.Ok(new Page<IncidentSummaryDto>(page.Select(ToSummary).ToList(), rows.Count > take ? new Cursor("in", page[^1].Id).Encode() : null));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid incidentId, PlatformDbContext db, CancellationToken ct)
        {
            var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            return incident is null ? NotFound() : Results.Ok(await DetailAsync(db, incident, ct));
        }

        private static async Task<IncidentDetailDto> DetailAsync(PlatformDbContext db, Incident incident, CancellationToken ct)
        {
            var alerts = await db.IncidentAlerts.AsNoTracking().Where(l => l.IncidentId == incident.Id)
                .Join(db.Alerts.AsNoTracking(), l => l.AlertId, a => a.Id, (l, a) => a).OrderByDescending(a => a.Id).ToListAsync(ct);
            var events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == incident.Id).OrderBy(e => e.At).ThenBy(e => e.Id).Take(MaxEvents)
                .Select(e => new IncidentEventDto(e.At, e.Actor, e.Type, e.FromState.HasValue ? e.FromState.Value.ToString() : null, e.ToState.HasValue ? e.ToState.Value.ToString() : null, e.Text, e.AlertId))
                .ToListAsync(ct);
            return new IncidentDetailDto(ToSummary(incident), await AlertEndpoints.ToDtosAsync(db, incident.TenantId, alerts, ct), events, alerts.Count(a => a.State != AlertState.Resolved));
        }

        /// <summary>The alerts of an incident: they must be the tenant's, and not in another unresolved incident. Returns the problem, or null.</summary>
        private static async Task<IResult?> CheckAlertsAsync(Guid tenantId, Guid? incidentId, List<Guid> ids, PlatformDbContext db, CancellationToken ct)
        {
            var known = await db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
            if (known.Count != ids.Count)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Alert not found", "An alert doesn't exist.");
            }
            var taken = await db.IncidentAlerts.AsNoTracking().Where(l => ids.Contains(l.AlertId) && l.IncidentId != incidentId)
                .Join(db.Incidents.AsNoTracking(), l => l.IncidentId, i => i.Id, (l, i) => i).AnyAsync(i => i.State != IncidentState.Resolved, ct);
            return taken ? Mutation.Problem(StatusCodes.Status409Conflict, "Alert in another incident", "An alert is already in an unresolved incident.") : null;
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, CreateIncidentRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "incident.create", "incident");
            if (await mutation.RequireAsync(null, ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync("POST incidents", request, ct) is { } replay) return replay;
            var title = ChangeSanitizer.Text(request.Title, MaxTitle);
            if (title.Length == 0) return Mutation.Invalid(["title is required."]);
            var ids = (request.AlertIds ?? []).Distinct().ToList();
            if (ids.Count > MaxAlerts) return Mutation.Invalid([$"At most {MaxAlerts} alerts."]);
            if (await CheckAlertsAsync(tenantId, null, ids, db, ct) is { } alertProblem) return alertProblem;
            // Unsaid, it's as serious as its worst alert.
            AlertSeverity severity;
            if (request.Severity is { Length: > 0 })
            {
                if (!Enum.TryParse(request.Severity, ignoreCase: true, out severity) || !Enum.IsDefined(severity)) return Mutation.Invalid(["severity is info, warning or critical."]);
            }
            else
            {
                severity = (await db.Alerts.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Severity).ToListAsync(ct)).DefaultIfEmpty(AlertSeverity.Warning).Max();
            }
            var now = clock.GetUtcNow();
            var incident = new Incident { TenantId = tenantId, Title = title, Severity = severity, State = IncidentState.Open, CreatedBy = mutation.Actor, CreatedAt = now, UpdatedAt = now };
            db.Incidents.Add(incident);
            db.IncidentEvents.Add(new IncidentEvent { TenantId = tenantId, IncidentId = incident.Id, At = now, Actor = mutation.Actor, Type = IncidentEventTypes.Created, ToState = IncidentState.Open, Text = ChangeSanitizer.Text(request.Note, MaxText) is { Length: > 0 } note ? note : null });
            // A microsecond apart: ids made in one millisecond sort at random, and "Alert added" must not read as coming before "Opened".
            var order = 0;
            foreach (var id in ids)
            {
                db.IncidentAlerts.Add(new IncidentAlert { TenantId = tenantId, IncidentId = incident.Id, AlertId = id, AddedBy = mutation.Actor, AddedAt = now });
                db.IncidentEvents.Add(new IncidentEvent { TenantId = tenantId, IncidentId = incident.Id, At = now.AddMicroseconds(++order), Actor = mutation.Actor, Type = IncidentEventTypes.AlertAdded, AlertId = id });
            }
            mutation.Audit(AuditOutcome.Succeeded, incident.Id.ToString(), new() { ["alerts"] = ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var result = mutation.Remember(StatusCodes.Status201Created, ToSummary(incident), now);
            await db.SaveChangesAsync(ct);
            return result;
        }

        private static async Task<IResult> TransitionAsync(Guid tenantId, Guid incidentId, IncidentTransitionRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "incident.transition", "incident");
            if (await mutation.RequireAsync(incidentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync($"POST incidents/{incidentId}/transitions", request, ct) is { } replay) return replay;
            if (!Enum.TryParse<IncidentState>(request.To, ignoreCase: true, out var to) || !Enum.IsDefined(to))
            {
                return Mutation.Invalid(["to is investigating, mitigated or resolved."]);
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM incidents WHERE id = {incidentId} FOR UPDATE", ct);
            var incident = await db.Incidents.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            if (incident is null) return NotFound();
            var from = incident.State;
            if (!IncidentLifecycle.CanMove(from, to))
            {
                mutation.Audit(AuditOutcome.Failed, incidentId.ToString(), new() { ["reason"] = "transition", ["from"] = from.ToString(), ["to"] = to.ToString() });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Mutation.Problem(StatusCodes.Status409Conflict, "Not allowed from this state", $"An incident that is {from} can't become {to}.");
            }
            var comment = ChangeSanitizer.Text(request.Comment, MaxText);
            if (comment.Length == 0 && IncidentLifecycle.NeedsComment(from, to))
            {
                return Mutation.Invalid(["Say why: a comment is required to resolve or reopen an incident."]);
            }
            var now = clock.GetUtcNow();
            incident.State = to;
            incident.UpdatedAt = now;
            incident.ResolvedAt = to == IncidentState.Resolved ? now : null;
            db.IncidentEvents.Add(new IncidentEvent { TenantId = tenantId, IncidentId = incidentId, At = now, Actor = mutation.Actor, Type = IncidentEventTypes.State, FromState = from, ToState = to, Text = comment.Length > 0 ? comment : null });
            mutation.Audit(AuditOutcome.Succeeded, incidentId.ToString(), new() { ["from"] = from.ToString(), ["to"] = to.ToString() });
            var result = mutation.Remember(StatusCodes.Status200OK, ToSummary(incident), now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        private static async Task<IResult> NoteAsync(Guid tenantId, Guid incidentId, IncidentNoteRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "incident.note", "incident");
            if (await mutation.RequireAsync(incidentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (request is null) return Mutation.Invalid(["A body is required."]);
            if (await mutation.ReplayAsync($"POST incidents/{incidentId}/notes", request, ct) is { } replay) return replay;
            var incident = await db.Incidents.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            if (incident is null) return NotFound();
            var text = ChangeSanitizer.Text(request.Text, MaxText);
            if (text.Length == 0) return Mutation.Invalid(["text is required."]);
            var now = clock.GetUtcNow();
            incident.UpdatedAt = now;
            var entry = new IncidentEvent { TenantId = tenantId, IncidentId = incidentId, At = now, Actor = mutation.Actor, Type = IncidentEventTypes.Note, Text = text };
            db.IncidentEvents.Add(entry);
            // The audit holds that a note was added, not what it says: the incident's record has the words.
            mutation.Audit(AuditOutcome.Succeeded, incidentId.ToString());
            var result = mutation.Remember(StatusCodes.Status201Created, new IncidentEventDto(entry.At, entry.Actor, entry.Type, null, null, entry.Text, null), now);
            await db.SaveChangesAsync(ct);
            return result;
        }

        private static async Task<IResult> AddAlertAsync(Guid tenantId, Guid incidentId, IncidentAlertRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "incident.alert.add", "incident");
            if (await mutation.RequireAsync(incidentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            if (request?.AlertId is not { } alertId) return Mutation.Invalid(["alertId is required."]);
            var incident = await db.Incidents.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            if (incident is null) return NotFound();
            if (await db.IncidentAlerts.AnyAsync(l => l.IncidentId == incidentId && l.AlertId == alertId, ct)) return Results.Ok(await DetailAsync(db, incident, ct));
            if (await db.IncidentAlerts.CountAsync(l => l.IncidentId == incidentId, ct) >= MaxAlerts) return Mutation.Problem(StatusCodes.Status409Conflict, "Too many alerts", $"An incident has at most {MaxAlerts} alerts.");
            if (await CheckAlertsAsync(tenantId, incidentId, [alertId], db, ct) is { } alertProblem) return alertProblem;
            var now = clock.GetUtcNow();
            incident.UpdatedAt = now;
            db.IncidentAlerts.Add(new IncidentAlert { TenantId = tenantId, IncidentId = incidentId, AlertId = alertId, AddedBy = mutation.Actor, AddedAt = now });
            db.IncidentEvents.Add(new IncidentEvent { TenantId = tenantId, IncidentId = incidentId, At = now, Actor = mutation.Actor, Type = IncidentEventTypes.AlertAdded, AlertId = alertId });
            mutation.Audit(AuditOutcome.Succeeded, incidentId.ToString(), new() { ["alert"] = alertId.ToString() });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await DetailAsync(db, incident, ct));
        }

        private static async Task<IResult> RemoveAlertAsync(Guid tenantId, Guid incidentId, Guid alertId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "incident.alert.remove", "incident");
            if (await mutation.RequireAsync(incidentId.ToString(), ct, Roles.Operator, Roles.Admin) is { } denied) return denied;
            var incident = await db.Incidents.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == incidentId, ct);
            if (incident is null) return NotFound();
            var link = await db.IncidentAlerts.SingleOrDefaultAsync(l => l.IncidentId == incidentId && l.AlertId == alertId, ct);
            if (link is null) return Results.NoContent();
            var now = clock.GetUtcNow();
            db.IncidentAlerts.Remove(link);
            incident.UpdatedAt = now;
            db.IncidentEvents.Add(new IncidentEvent { TenantId = tenantId, IncidentId = incidentId, At = now, Actor = mutation.Actor, Type = IncidentEventTypes.AlertRemoved, AlertId = alertId });
            mutation.Audit(AuditOutcome.Succeeded, incidentId.ToString(), new() { ["alert"] = alertId.ToString() });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
    }
}
