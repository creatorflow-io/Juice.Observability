using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Health;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary><c>Etag</c> goes in <c>If-Match</c> when changing the rule.</summary>
    public sealed record RuleDto(string Key, string Name, string Template, string Severity, bool Enabled, RuleDefinition Definition, DateTimeOffset LoadedAt, string ManagedBy, string Etag);

    public sealed record AlertDto(Guid Id, string Rule, string SubjectKey, string SubjectType, Guid? SubjectId, string? Environment,
        string Severity, string State, DateTimeOffset BreachStartedAt, DateTimeOffset? FiredAt, DateTimeOffset? ResolvedAt,
        DateTimeOffset LastEvaluatedAt, double? LastValue, bool NoData, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

    public sealed record AlertTransitionDto(string? From, string To, DateTimeOffset At, double? Value, string Reason);

    public sealed record AlertDetailDto(AlertDto Alert, IReadOnlyDictionary<string, string> Evidence, string? AcknowledgeComment,
        IReadOnlyList<AlertTransitionDto> History, IReadOnlyList<HealthDto> SubjectHealth, IReadOnlyList<IntegrationLinkDto> Links);

    public sealed record AcknowledgeRequest(string? Comment);

    /// <summary>Phase 3 endpoints (requirements §6.1): reading rules, alerts, acknowledgement. Changing rules is in <see cref="RuleAdminEndpoints"/>.</summary>
    public static partial class AlertEndpoints
    {
        public const int MaxHistory = 500;

        public static RouteGroupBuilder MapAlertEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/rules", ListRulesAsync);
            tenant.MapGet("/alerts", ListAlertsAsync);
            tenant.MapGet("/alerts/{alertId:guid}", GetAlertAsync);
            tenant.MapPost("/alerts/{alertId:guid}/acknowledgement", AcknowledgeAsync);
            return tenant;
        }

        private static async Task<IResult> ListRulesAsync(Guid tenantId, int? limit, string? cursor, PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId);
            if (after is not null) query = query.Where(r => r.Id > after.After);
            var rows = await query.OrderBy(r => r.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).Select(RuleAdminEndpoints.ToDto).ToList();
            return Results.Ok(new Page<RuleDto>(page, rows.Count > take ? new Cursor("r", rows[take - 1].Id).Encode() : null));
        }

        /// <summary>
        /// Alerts, newest first by id order, filtered by <c>state</c>, <c>rule</c> key and <c>environment</c> key. A host alert
        /// has no environment, so an environment filter leaves it out.
        /// </summary>
        private static async Task<IResult> ListAlertsAsync(Guid tenantId, string? state, string? rule, string? environment, int? limit, string? cursor,
            PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.Alerts.AsNoTracking().Where(a => a.TenantId == tenantId);
            if (state is not null)
            {
                if (!Enum.TryParse<AlertState>(state, ignoreCase: true, out var parsed))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid state",
                        detail: "state must be one of: pending, firing, resolved.");
                }
                query = query.Where(a => a.State == parsed);
            }
            if (rule is not null)
            {
                query = query.Where(a => db.Rules.Any(r => r.Id == a.RuleId && r.Key == rule));
            }
            if (!string.IsNullOrEmpty(environment))
            {
                var key = environment.ToLowerInvariant();
                query = query.Where(a => db.Environments.Any(e => e.Id == a.EnvironmentId && e.Key == key));
            }
            if (after is not null) query = query.Where(a => a.Id < after.After);
            var rows = await query.OrderByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var items = await ToDtosAsync(db, tenantId, page, ct);
            return Results.Ok(new Page<AlertDto>(items, rows.Count > take ? new Cursor("al", page[^1].Id).Encode() : null));
        }

        /// <summary>An alert with its evidence, history, the subject's current health and any integration links (requirements §11 item 9).</summary>
        private static async Task<IResult> GetAlertAsync(Guid tenantId, Guid alertId, PlatformDbContext db, IOptions<HealthPolicy> policy,
            IOptions<IntegrationOptions> integrations, TimeProvider clock, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alert not found");
            }
            var history = await db.AlertTransitions.AsNoTracking().Where(t => t.AlertId == alert.Id)
                .OrderBy(t => t.At).ThenBy(t => t.Id).Take(MaxHistory)
                .Select(t => new AlertTransitionDto(t.FromState.HasValue ? t.FromState.Value.ToString() : null, t.ToState.ToString(), t.At, t.Value, t.Reason))
                .ToListAsync(ct);
            var health = alert.SubjectId is { } subject
                ? await HealthEndpoints.ToDtosAsync(db, tenantId, await db.HealthSnapshots.AsNoTracking()
                    .Where(h => h.TenantId == tenantId && h.SubjectId == subject && (h.EnvironmentId == alert.EnvironmentId || h.EnvironmentId == null))
                    .ToListAsync(ct), policy.Value, clock, ct)
                : [];
            IReadOnlyList<IntegrationLinkDto> links = [];
            if (alert.SubjectType == HealthSubject.Service && alert.SubjectId is { } serviceId
                && await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId, ct) is { } service)
            {
                links = IntegrationLinks.ForService(integrations.Value, service.Name, alert.ResolvedAt ?? clock.GetUtcNow());
            }
            var dto = (await ToDtosAsync(db, tenantId, [alert], ct))[0];
            return Results.Ok(new AlertDetailDto(dto, alert.Evidence, alert.AcknowledgeComment, history, health, links));
        }

        /// <summary>
        /// Acknowledges an open alert (operator or admin). Writes an audit record in the same transaction; a denied
        /// attempt is audited too. With an <c>Idempotency-Key</c>, a retry returns the first response and the same key
        /// with a different request is refused (requirements §6.1).
        /// </summary>
        private static async Task<IResult> AcknowledgeAsync(Guid tenantId, Guid alertId, AcknowledgeRequest? request, HttpContext http,
            PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var actor = http.User.Actor();
            var correlationId = CorrelationId.Get(http);
            var comment = request?.Comment?.Trim();
            void Audit(AuditOutcome outcome, string? reason = null) => db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenantId, Actor = actor, Operation = "alert.acknowledge", TargetType = "alert", TargetId = alertId.ToString(),
                Outcome = outcome, CorrelationId = correlationId,
                Metadata = reason is null ? [] : new() { ["reason"] = reason },
            });

            if (!http.User.IsInRole(Roles.Operator) && !http.User.IsInRole(Roles.Admin))
            {
                Audit(AuditOutcome.Denied, "role");
                await db.SaveChangesAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden",
                    detail: "Acknowledging alerts needs the operator or admin role.");
            }
            if (comment is { Length: > 1000 })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid comment", detail: "comment is at most 1000 characters.");
            }

            var key = http.Request.Headers["Idempotency-Key"].ToString();
            var operation = $"POST alerts/{alertId}/acknowledgement";
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alertId, comment }))));
            if (key.Length > 0)
            {
                if (!IdempotencyKey().IsMatch(key))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid Idempotency-Key",
                        detail: "Idempotency-Key is 1 to 128 of [A-Za-z0-9_-].");
                }
                var previous = await db.IdempotencyRecords.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key && r.Operation == operation, ct);
                if (previous is not null)
                {
                    if (previous.RequestHash != requestHash)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Idempotency-Key reused",
                            detail: "This Idempotency-Key was used with a different request.");
                    }
                    http.Response.Headers["Idempotency-Replayed"] = "true";
                    return Results.Text(previous.ResponseBody, "application/json", Encoding.UTF8, previous.StatusCode);
                }
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Locked, so an escalation running now either sees this acknowledgement or finishes first (Phase 6).
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM alerts WHERE id = {alertId} AND tenant_id = {tenantId} FOR UPDATE", ct);
            var alert = await db.Alerts.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == alertId, ct);
            if (alert is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alert not found");
            }
            if (alert.State == AlertState.Resolved)
            {
                Audit(AuditOutcome.Failed, "resolved");
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Alert resolved", detail: "A resolved alert cannot be acknowledged.");
            }
            if (alert.AcknowledgedAt is null)
            {
                alert.AcknowledgedAt = clock.GetUtcNow();
                alert.AcknowledgedBy = actor;
                alert.AcknowledgeComment = comment;
                Audit(AuditOutcome.Succeeded);
            }
            var body = JsonSerializer.Serialize((await ToDtosAsync(db, tenantId, [alert], ct))[0], JsonSerializerOptions.Web);
            if (key.Length > 0)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    TenantId = tenantId, Key = key, Operation = operation, RequestHash = requestHash, StatusCode = StatusCodes.Status200OK,
                    ResponseBody = body, CreatedAt = clock.GetUtcNow(),
                });
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
        }

        internal static async Task<List<AlertDto>> ToDtosAsync(PlatformDbContext db, Guid tenantId, IReadOnlyList<Alert> alerts, CancellationToken ct)
        {
            var ruleIds = alerts.Select(a => a.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, ct);
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.Key, ct);
            return alerts.Select(a => new AlertDto(a.Id, rules.GetValueOrDefault(a.RuleId, ""), a.SubjectKey, a.SubjectType.ToString().ToLowerInvariant(),
                a.SubjectId, a.EnvironmentId is { } e ? environments.GetValueOrDefault(e) : null, a.Severity.ToString(), a.State.ToString(),
                a.BreachStartedAt, a.FiredAt, a.ResolvedAt, a.LastEvaluatedAt, a.LastValue, a.NoData, a.AcknowledgedAt, a.AcknowledgedBy)).ToList();
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
        private static partial Regex IdempotencyKey();
    }
}
