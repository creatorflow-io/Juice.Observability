using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>An endpoint as configured; the URL is reduced to scheme, host and port, and the secret is never shown.</summary>
    public sealed record WebhookEndpointDto(Guid Id, string Key, string Destination, IReadOnlyList<string> Events, IReadOnlyList<string> RuleKeys,
        string MinimumSeverity, bool Enabled, DateTimeOffset LoadedAt);

    public sealed record WebhookAttemptDto(int Number, DateTimeOffset At, int? StatusCode, string? Error, int DurationMs);

    public sealed record WebhookDeliveryDto(Guid Id, Guid AlertId, string Event, string Status, int Attempts, DateTimeOffset CreatedAt,
        DateTimeOffset? FirstAttemptAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError,
        IReadOnlyList<WebhookAttemptDto> History);

    /// <summary>Phase 4 endpoints (requirements §6.1): webhook endpoints and their delivery history.</summary>
    public static class WebhookEndpoints
    {
        public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/webhooks", ListEndpointsAsync);
            tenant.MapGet("/webhooks/{endpointId:guid}/deliveries", ListDeliveriesAsync);
            return tenant;
        }

        private static async Task<IResult> ListEndpointsAsync(Guid tenantId, PlatformDbContext db, CancellationToken ct)
        {
            var endpoints = await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Key).ToListAsync(ct);
            return Results.Ok(new Page<WebhookEndpointDto>(endpoints.Select(e => new WebhookEndpointDto(e.Id, e.Key,
                new Uri(e.Url).GetLeftPart(UriPartial.Authority), e.Events, e.RuleKeys, e.MinimumSeverity.ToString(), e.Enabled, e.LoadedAt)).ToList(), null));
        }

        /// <summary>Deliveries newest first, each with its attempts, filtered by <c>status</c>.</summary>
        private static async Task<IResult> ListDeliveriesAsync(Guid tenantId, Guid endpointId, string? status, int? limit, string? cursor,
            PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            if (!await db.WebhookEndpoints.AnyAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Webhook endpoint not found");
            }
            var query = db.WebhookDeliveries.AsNoTracking().Where(d => d.TenantId == tenantId && d.EndpointId == endpointId);
            if (status is not null)
            {
                if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var parsed))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid status",
                        detail: "status must be one of: pending, retrying, delivered, failed.");
                }
                query = query.Where(d => d.Status == parsed);
            }
            if (after is not null) query = query.Where(d => d.Id < after.After);
            var rows = await query.OrderByDescending(d => d.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var ids = page.Select(d => d.Id).ToList();
            var attempts = (await db.WebhookAttempts.AsNoTracking().Where(a => ids.Contains(a.DeliveryId)).OrderBy(a => a.Number).ToListAsync(ct))
                .ToLookup(a => a.DeliveryId);
            var items = page.Select(d => new WebhookDeliveryDto(d.Id, d.AlertId, d.Event, d.Status.ToString(), d.Attempts, d.CreatedAt,
                d.FirstAttemptAt, d.DeliveredAt, d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying ? d.NextAttemptAt : null,
                d.LastStatusCode, d.LastError,
                attempts[d.Id].Select(a => new WebhookAttemptDto(a.Number, a.At, a.StatusCode, a.Error, a.DurationMs)).ToList())).ToList();
            return Results.Ok(new Page<WebhookDeliveryDto>(items, rows.Count > take ? new Cursor("wd", page[^1].Id).Encode() : null));
        }
    }
}
