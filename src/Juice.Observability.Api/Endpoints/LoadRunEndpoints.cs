using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record LoadRunDto(Guid Id, string Name, string? Environment, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string StartedBy);

    public sealed record StartLoadRunRequest(string? Name, string? Environment);

    /// <summary>
    /// Marks the window of a load test run: the test calls these at its start and end (operator or admin). The platform generates no
    /// load. Results are at <c>/load-runs/{id}/results</c>, read from the telemetry of the window.
    /// </summary>
    public static partial class LoadRunEndpoints
    {
        public const int MaxName = 128;

        public static RouteGroupBuilder MapLoadRunEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/load-runs", ListAsync);
            tenant.MapPost("/load-runs", StartAsync);
            tenant.MapPost("/load-runs/{runId:guid}/end", EndAsync);
            return tenant;
        }

        internal static LoadRunDto ToDto(LoadRun run) => new(run.Id, run.Name, run.Environment, run.StartedAt, run.EndedAt, run.StartedBy);

        /// <summary>Runs, newest first, filtered by <c>environment</c> key.</summary>
        private static async Task<IResult> ListAsync(Guid tenantId, string? environment, int? limit, string? cursor, PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.LoadRuns.AsNoTracking().Where(r => r.TenantId == tenantId);
            if (!string.IsNullOrEmpty(environment))
            {
                var key = environment.ToLowerInvariant();
                query = query.Where(r => r.Environment == key);
            }
            if (after is not null) query = query.Where(r => r.Id < after.After);
            var rows = await query.OrderByDescending(r => r.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            return Results.Ok(new Page<LoadRunDto>(page.Select(ToDto).ToList(), rows.Count > take ? new Cursor("lr", page[^1].Id).Encode() : null));
        }

        /// <summary>Starts a run now. With an <c>Idempotency-Key</c>, a test's retry gets the first run instead of a second one.</summary>
        private static async Task<IResult> StartAsync(Guid tenantId, StartLoadRunRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var name = request?.Name?.Trim();
            var environment = request?.Environment?.Trim().ToLowerInvariant();
            environment = environment is { Length: > 0 } ? environment : null;
            if (await DeniedAsync(tenantId, "load-run.start", null, http, db, ct) is { } denied)
            {
                return denied;
            }
            if (string.IsNullOrEmpty(name) || name.Length > MaxName)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid name", detail: $"name is 1 to {MaxName} characters.");
            }
            if (environment is { Length: > MaxName })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid environment", detail: $"environment is at most {MaxName} characters.");
            }

            var key = http.Request.Headers["Idempotency-Key"].ToString();
            const string Operation = "POST load-runs";
            var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name, environment }))));
            if (key.Length > 0)
            {
                if (!IdempotencyKey().IsMatch(key))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid Idempotency-Key",
                        detail: "Idempotency-Key is 1 to 128 of [A-Za-z0-9_-].");
                }
                var previous = await db.IdempotencyRecords.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key && r.Operation == Operation, ct);
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

            var run = new LoadRun { TenantId = tenantId, Name = name, Environment = environment, StartedAt = clock.GetUtcNow(), StartedBy = http.User.Actor() };
            db.LoadRuns.Add(run);
            Audit(db, tenantId, "load-run.start", run.Id, AuditOutcome.Succeeded, http);
            var body = JsonSerializer.Serialize(ToDto(run), JsonSerializerOptions.Web);
            if (key.Length > 0)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    TenantId = tenantId, Key = key, Operation = Operation, RequestHash = requestHash, StatusCode = StatusCodes.Status201Created,
                    ResponseBody = body, CreatedAt = clock.GetUtcNow(),
                });
            }
            await db.SaveChangesAsync(ct);
            return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status201Created);
        }

        /// <summary>Ends a run now. Ending it again changes nothing, so a test's retry is safe.</summary>
        private static async Task<IResult> EndAsync(Guid tenantId, Guid runId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            if (await DeniedAsync(tenantId, "load-run.end", runId, http, db, ct) is { } denied)
            {
                return denied;
            }
            var run = await db.LoadRuns.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
            if (run is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Load run not found");
            }
            if (run.EndedAt is null)
            {
                run.EndedAt = clock.GetUtcNow();
                Audit(db, tenantId, "load-run.end", run.Id, AuditOutcome.Succeeded, http);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(run));
        }

        private static async Task<IResult?> DeniedAsync(Guid tenantId, string operation, Guid? runId, HttpContext http, PlatformDbContext db, CancellationToken ct)
        {
            if (http.User.IsInRole(Roles.Operator) || http.User.IsInRole(Roles.Admin))
            {
                return null;
            }
            Audit(db, tenantId, operation, runId, AuditOutcome.Denied, http);
            await db.SaveChangesAsync(ct);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Marking load runs needs the operator or admin role.");
        }

        private static void Audit(PlatformDbContext db, Guid tenantId, string operation, Guid? runId, AuditOutcome outcome, HttpContext http)
            => db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenantId, Actor = http.User.Actor(), Operation = operation, TargetType = "load-run", TargetId = runId?.ToString(),
                Outcome = outcome, CorrelationId = CorrelationId.Get(http),
            });

        [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
        private static partial Regex IdempotencyKey();
    }
}
