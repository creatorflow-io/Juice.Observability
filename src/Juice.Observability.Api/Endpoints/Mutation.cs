using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// What every Phase 6 mutation does the same way (requirements §6.1): role check with the denial audited, an audit record in the state change's
    /// transaction, problem details, and an optional <c>Idempotency-Key</c> whose retry returns the first response.
    /// </summary>
    internal sealed partial class Mutation(HttpContext http, PlatformDbContext db, Guid tenantId, string operation, string targetType)
    {
        private string? _key;
        private string? _operationKey;
        private string? _requestHash;

        public string Actor { get; } = http.User.Actor();

        public static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);

        public static IResult Invalid(IEnumerable<string> errors) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: string.Join(" ", errors));

        public void Audit(AuditOutcome outcome, string? target, Dictionary<string, string>? metadata = null) => db.AuditRecords.Add(new AuditRecord
        {
            TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = targetType, TargetId = target, Outcome = outcome,
            CorrelationId = CorrelationId.Get(http), Metadata = metadata ?? [],
        });

        /// <summary>A configuration change, on the timeline beside deployments. Only names: never a URL, an address or a secret reference.</summary>
        public void RecordChange(string summary, DateTimeOffset now, Dictionary<string, string>? attributes = null) => db.ChangeEvents.Add(new ChangeEvent
        {
            TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = Actor, Summary = summary,
            Attributes = attributes ?? [], OccurredAt = now, RecordedAt = now,
        });

        /// <summary>Null when the caller holds one of the roles; otherwise the audited refusal.</summary>
        public async Task<IResult?> RequireAsync(string? target, CancellationToken ct, params string[] roles)
        {
            if (roles.Any(http.User.IsInRole))
            {
                return null;
            }
            Audit(AuditOutcome.Denied, target, new() { ["reason"] = "role" });
            await db.SaveChangesAsync(ct);
            return Problem(StatusCodes.Status403Forbidden, "Forbidden", $"This needs the {string.Join(" or ", roles)} role.");
        }

        /// <summary>The refusal to touch what the deployment's file owns, audited since someone tried to change configuration.</summary>
        public async Task<IResult> ManagedByConfigurationAsync(string? target, string file, CancellationToken ct)
        {
            Audit(AuditOutcome.Failed, target, new() { ["reason"] = "managed_by_configuration" });
            await db.SaveChangesAsync(ct);
            return Problem(StatusCodes.Status409Conflict, "Managed by deployment configuration", $"This is defined in {file}. Change it there, or remove it there to manage it here.");
        }

        /// <summary>The first response to a repeated key, a refusal if the key was used differently, or null to go on. Call <see cref="Remember"/> on success.</summary>
        public async Task<IResult?> ReplayAsync(string route, object request, CancellationToken ct)
        {
            _key = http.Request.Headers["Idempotency-Key"].ToString();
            if (_key.Length == 0)
            {
                return null;
            }
            if (!IdempotencyKey().IsMatch(_key))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid Idempotency-Key", "Idempotency-Key is 1 to 128 of [A-Za-z0-9_-].");
            }
            _operationKey = route;
            _requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
            var previous = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == _key && r.Operation == _operationKey, ct);
            if (previous is null)
            {
                return null;
            }
            if (previous.RequestHash != _requestHash)
            {
                return Problem(StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused", "This Idempotency-Key was used with a different request.");
            }
            http.Response.Headers["Idempotency-Replayed"] = "true";
            return Results.Text(previous.ResponseBody, "application/json", Encoding.UTF8, previous.StatusCode);
        }

        /// <summary>The response as JSON, stored for the key's retries in the same transaction when there is a key.</summary>
        public IResult Remember<T>(int status, T response, DateTimeOffset now)
        {
            var body = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
            if (_key is { Length: > 0 })
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    TenantId = tenantId, Key = _key, Operation = _operationKey!, RequestHash = _requestHash!, StatusCode = status, ResponseBody = body, CreatedAt = now,
                });
            }
            return Results.Text(body, "application/json", Encoding.UTF8, status);
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
        private static partial Regex IdempotencyKey();
    }
}
