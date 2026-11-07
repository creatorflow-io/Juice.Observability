using Juice.Observability.Data;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// An endpoint as configured; a webhook's URL is reduced to scheme, host and port, and no secret is ever shown. <c>SecretRef</c>, which only names where a
    /// secret is, is shown to admins alone, who need it to edit the channel. Failures count the last 24 hours.
    /// </summary>
    public sealed record WebhookEndpointDto(Guid Id, string Key, string Kind, string Destination, IReadOnlyList<string> Events, IReadOnlyList<string> RuleKeys,
        IReadOnlyList<string> Environments, IReadOnlyList<string>? Recipients, string? SecretRef, int GroupWindowSeconds, string MinimumSeverity, bool Enabled, string ManagedBy,
        DateTimeOffset LoadedAt, int RecentFailures, int Waiting);

    public sealed record WebhookAttemptDto(int Number, DateTimeOffset At, int? StatusCode, string? Error, int DurationMs);

    public sealed record WebhookDeliveryDto(Guid Id, Guid AlertId, string Event, string Status, int Attempts, DateTimeOffset CreatedAt,
        DateTimeOffset? FirstAttemptAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError,
        IReadOnlyList<WebhookAttemptDto> History, Guid? SuppressedBy = null, Guid? GroupedInto = null, int EscalationStep = 0);

    /// <summary><c>Url</c> is a signed webhook's. Left out on a change, it stays as it is, since reads never return it.</summary>
    public sealed record WebhookRequest(string? Key, string? Kind, string? Url, string? SecretRef, List<string>? Events, List<string>? RuleKeys, List<string>? Environments,
        List<string>? Recipients, int? GroupWindowSeconds, string? MinimumSeverity, bool? Enabled);

    public sealed record AllowlistEntryDto(string Destination, IReadOnlyList<int> Ports, string ManagedBy, DateTimeOffset LoadedAt);

    public sealed record AllowlistRequest(string? Destination, List<int>? Ports);

    /// <summary>Webhook endpoints, their delivery history, and the allowlist. Reads for any role; every change is admin only and audited.</summary>
    public static class WebhookEndpoints
    {
        public const int MaxEndpoints = 100;
        public const int MaxAllowlist = 100;
        private const string File = "the webhooks file";
        private static readonly TimeSpan FailureWindow = TimeSpan.FromHours(24);

        public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapGet("/webhooks", ListEndpointsAsync);
            tenant.MapPost("/webhooks", CreateAsync);
            tenant.MapPut("/webhooks/{endpointId:guid}", UpdateAsync);
            tenant.MapGet("/webhooks/{endpointId:guid}/deliveries", ListDeliveriesAsync);
            tenant.MapGet("/webhooks/allowlist", ListAllowlistAsync);
            tenant.MapPut("/webhooks/allowlist", PutAllowlistAsync);
            tenant.MapDelete("/webhooks/allowlist", RemoveAllowlistAsync);
            return tenant;
        }

        private static string Destination(WebhookEndpoint e) => e.Kind switch
        {
            ChannelKind.Webhook => new Uri(e.Url).GetLeftPart(UriPartial.Authority),
            ChannelKind.Email => "SMTP",
            _ => e.Kind.ToString(),
        };

        private static WebhookEndpointDto ToDto(WebhookEndpoint e, HttpContext http, int failures = 0, int waiting = 0) => new(e.Id, e.Key, e.Kind.ToString(), Destination(e), e.Events, e.RuleKeys, e.Environments,
            http.User.IsInRole(Roles.Admin) ? e.Recipients : null, http.User.IsInRole(Roles.Admin) ? e.SecretRef : null, e.GroupWindowSeconds, e.MinimumSeverity.ToString(), e.Enabled, e.ManagedBy, e.LoadedAt, failures, waiting);

        private static async Task<IResult> ListEndpointsAsync(Guid tenantId, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var endpoints = await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Key).ToListAsync(ct);
            var since = clock.GetUtcNow() - FailureWindow;
            var counts = await db.WebhookDeliveries.AsNoTracking()
                .Where(d => d.TenantId == tenantId && (d.Status == DeliveryStatus.Retrying || d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed && d.CreatedAt >= since))
                .GroupBy(d => new { d.EndpointId, d.Status }).Select(g => new { g.Key.EndpointId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
            int Count(Guid id, params DeliveryStatus[] statuses) => counts.Where(c => c.EndpointId == id && statuses.Contains(c.Status)).Sum(c => c.Count);
            return Results.Ok(new Page<WebhookEndpointDto>(endpoints.Select(e => ToDto(e, http, Count(e.Id, DeliveryStatus.Failed), Count(e.Id, DeliveryStatus.Pending, DeliveryStatus.Retrying))).ToList(), null));
        }

        private static (EndpointDefinition? Definition, IResult? Problem) Read(WebhookRequest? request, string? key, WebhookEndpoint? existing, WebhookOptions options)
        {
            if (request is null) return (null, Mutation.Invalid(["A body is required."]));
            if (key is not null && request.Key is { Length: > 0 } named && named != key) return (null, Mutation.Invalid(["An endpoint's key can't change."]));
            var kind = ChannelKind.Webhook;
            if (request.Kind is { Length: > 0 } && (!Enum.TryParse(request.Kind, ignoreCase: true, out kind) || !Enum.IsDefined(kind)))
            {
                return (null, Mutation.Invalid(["kind is webhook, slack, teams or email."]));
            }
            if (existing is not null && existing.Kind != kind) return (null, Mutation.Invalid(["An endpoint's kind can't change. Create another one."]));
            var severity = AlertSeverity.Info;
            if (request.MinimumSeverity is { Length: > 0 } && (!Enum.TryParse(request.MinimumSeverity, ignoreCase: true, out severity) || !Enum.IsDefined(severity)))
            {
                return (null, Mutation.Invalid(["minimumSeverity is info, warning or critical."]));
            }
            var definition = new EndpointDefinition
            {
                Key = key ?? request.Key ?? "", Kind = kind, Url = request.Url?.Trim() ?? existing?.Url ?? "", SecretRef = request.SecretRef?.Trim() ?? "",
                Events = request.Events ?? [NotificationPlan.Firing, NotificationPlan.Resolved], RuleKeys = request.RuleKeys ?? [], Environments = request.Environments ?? [],
                Recipients = request.Recipients ?? [], GroupWindowSeconds = request.GroupWindowSeconds ?? 0, MinimumSeverity = severity, Enabled = request.Enabled ?? true,
            };
            var errors = definition.Validate(options.AllowHttpToAllowlisted).ToList();
            if (kind != ChannelKind.Email && !SecretReference.IsAllowedFromApi(definition.SecretRef, options))
            {
                errors.Add($"secretRef must be env:{options.ApiSecretEnvPrefix}NAME{(options.ApiSecretDirectory is { Length: > 0 } dir ? $" or a file directly in {dir}" : "")}.");
            }
            if (kind == ChannelKind.Email && !options.Smtp.IsConfigured)
            {
                errors.Add("This deployment has no SMTP server (Webhooks:Smtp).");
            }
            return errors.Count > 0 ? (null, Mutation.Invalid(errors)) : (definition, null);
        }

        /// <summary>The audit records the host and the reference, never the full URL, whose path may carry a token.</summary>
        private static Dictionary<string, string> Metadata(EndpointDefinition d)
        {
            var metadata = new Dictionary<string, string> { ["kind"] = d.Kind.ToString(), ["enabled"] = d.Enabled ? "true" : "false" };
            if (d.SecretRef.Length > 0) metadata["secretRef"] = d.SecretRef;
            if (d.Kind == ChannelKind.Webhook) metadata["host"] = new Uri(d.Url).Host;
            if (d.Kind == ChannelKind.Email) metadata["recipients"] = d.Recipients.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return metadata;
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, WebhookRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, IOptions<WebhookOptions> options, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.create", "webhook");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            var (definition, problem) = Read(request, null, null, options.Value);
            if (problem is not null) return problem;
            if (await db.WebhookEndpoints.AnyAsync(e => e.TenantId == tenantId && e.Key == definition!.Key, ct))
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Endpoint exists", $"An endpoint with the key '{definition!.Key}' exists.");
            }
            if (await db.WebhookEndpoints.CountAsync(e => e.TenantId == tenantId, ct) >= MaxEndpoints)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Too many endpoints", $"A tenant has at most {MaxEndpoints} endpoints.");
            }
            var now = clock.GetUtcNow();
            var endpoint = new WebhookEndpoint
            {
                TenantId = tenantId, Key = definition!.Key, Kind = definition.Kind, Url = definition.Url, SecretRef = definition.SecretRef, Events = [.. definition.Events],
                RuleKeys = [.. definition.RuleKeys], Environments = [.. definition.Environments], Recipients = [.. definition.Recipients], GroupWindowSeconds = definition.GroupWindowSeconds,
                MinimumSeverity = definition.MinimumSeverity, Enabled = definition.Enabled, DefinitionHash = definition.Hash(), LoadedAt = now, ManagedBy = ManagedBy.Api,
            };
            db.WebhookEndpoints.Add(endpoint);
            mutation.Audit(AuditOutcome.Succeeded, endpoint.Key, Metadata(definition));
            mutation.RecordChange($"webhook {endpoint.Key}: webhook.create", now);
            await db.SaveChangesAsync(ct);
            return Results.Created($"{http.Request.Path}/{endpoint.Id}", ToDto(endpoint, http));
        }

        private static async Task<IResult> UpdateAsync(Guid tenantId, Guid endpointId, WebhookRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, IOptions<WebhookOptions> options, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.update", "webhook");
            if (await mutation.RequireAsync(endpointId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            var endpoint = await db.WebhookEndpoints.SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct);
            if (endpoint is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Webhook endpoint not found", "No such endpoint.");
            }
            if (endpoint.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(endpoint.Key, File, ct);
            var (definition, problem) = Read(request, endpoint.Key, endpoint, options.Value);
            if (problem is not null) return problem;
            var now = clock.GetUtcNow();
            endpoint.Url = definition!.Url;
            endpoint.SecretRef = definition.SecretRef;
            endpoint.Events = [.. definition.Events];
            endpoint.RuleKeys = [.. definition.RuleKeys];
            endpoint.Environments = [.. definition.Environments];
            endpoint.Recipients = [.. definition.Recipients];
            endpoint.GroupWindowSeconds = definition.GroupWindowSeconds;
            endpoint.MinimumSeverity = definition.MinimumSeverity;
            endpoint.Enabled = definition.Enabled;
            endpoint.DefinitionHash = definition.Hash();
            endpoint.LoadedAt = now;
            mutation.Audit(AuditOutcome.Succeeded, endpoint.Key, Metadata(definition));
            mutation.RecordChange($"webhook {endpoint.Key}: webhook.update", now);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(endpoint, http));
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
                        detail: "status must be one of: pending, retrying, delivered, failed, suppressed, dropped, grouped.");
                }
                query = query.Where(d => d.Status == parsed);
            }
            if (after is not null) query = query.Where(d => d.Id < after.After);
            var rows = await query.OrderByDescending(d => d.Id).Take(take + 1).ToListAsync(ct);
            var page = rows.Take(take).ToList();
            var items = await ToDeliveryDtosAsync(db, page, ct);
            return Results.Ok(new Page<WebhookDeliveryDto>(items, rows.Count > take ? new Cursor("wd", page[^1].Id).Encode() : null));
        }

        internal static async Task<List<WebhookDeliveryDto>> ToDeliveryDtosAsync(PlatformDbContext db, IReadOnlyList<WebhookDelivery> deliveries, CancellationToken ct)
        {
            var ids = deliveries.Select(d => d.Id).ToList();
            var attempts = (await db.WebhookAttempts.AsNoTracking().Where(a => ids.Contains(a.DeliveryId)).OrderBy(a => a.Number).ToListAsync(ct)).ToLookup(a => a.DeliveryId);
            return deliveries.Select(d => new WebhookDeliveryDto(d.Id, d.AlertId, d.Event, d.Status.ToString(), d.Attempts, d.CreatedAt,
                d.FirstAttemptAt, d.DeliveredAt, d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying ? d.NextAttemptAt : null,
                d.LastStatusCode, d.LastError, attempts[d.Id].Select(a => new WebhookAttemptDto(a.Number, a.At, a.StatusCode, a.Error, a.DurationMs)).ToList(),
                d.SuppressedBy, d.GroupedInto, d.EscalationStep)).ToList();
        }

        private static async Task<IResult> ListAllowlistAsync(Guid tenantId, PlatformDbContext db, CancellationToken ct)
        {
            var rows = await db.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination).ToListAsync(ct);
            return Results.Ok(new Page<AllowlistEntryDto>(rows.Select(a => new AllowlistEntryDto(a.Destination, a.Ports, a.ManagedBy, a.LoadedAt)).ToList(), null));
        }

        /// <summary>Adds an entry, or changes its ports. This opens a private address to outgoing requests, so it's admin only and always audited.</summary>
        private static async Task<IResult> PutAllowlistAsync(Guid tenantId, AllowlistRequest? request, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.allowlist.put", "webhook_allowlist");
            var destination = request?.Destination?.Trim() ?? "";
            if (await mutation.RequireAsync(destination, ct, Roles.Admin) is { } denied) return denied;
            var ports = (request?.Ports ?? []).Distinct().Order().ToArray();
            var invalid = destination.Length is 0 or > 256 ? "destination is a CIDR or a host name." : DestinationPolicy.ValidateEntry(new AllowedDestination(destination, ports));
            if (invalid is not null) return Mutation.Invalid([invalid]);
            var entry = await db.WebhookAllowlist.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Destination == destination, ct);
            if (entry?.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(destination, File, ct);
            if (entry is null && await db.WebhookAllowlist.CountAsync(a => a.TenantId == tenantId, ct) >= MaxAllowlist)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Too many entries", $"A tenant has at most {MaxAllowlist} allowlist entries.");
            }
            var now = clock.GetUtcNow();
            if (entry is null)
            {
                entry = new WebhookAllowlistEntry { TenantId = tenantId, Destination = destination, Ports = ports, LoadedAt = now, ManagedBy = ManagedBy.Api };
                db.WebhookAllowlist.Add(entry);
            }
            else
            {
                entry.Ports = ports;
                entry.LoadedAt = now;
            }
            mutation.Audit(AuditOutcome.Succeeded, destination, new() { ["ports"] = string.Join(",", ports) });
            mutation.RecordChange($"webhook_allowlist {destination}: put", now);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AllowlistEntryDto(entry.Destination, entry.Ports, entry.ManagedBy, entry.LoadedAt));
        }

        /// <summary>By query parameter, since a CIDR holds a slash. Connections already open may last their pool lifetime, at most a minute.</summary>
        private static async Task<IResult> RemoveAllowlistAsync(Guid tenantId, string? destination, HttpContext http, PlatformDbContext db, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.allowlist.remove", "webhook_allowlist");
            if (await mutation.RequireAsync(destination, ct, Roles.Admin) is { } denied) return denied;
            var entry = await db.WebhookAllowlist.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Destination == destination, ct);
            if (entry is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Not on the allowlist", "No such entry.");
            }
            if (entry.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(destination, File, ct);
            db.WebhookAllowlist.Remove(entry);
            mutation.Audit(AuditOutcome.Succeeded, destination);
            mutation.RecordChange($"webhook_allowlist {destination}: remove", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
    }
}
