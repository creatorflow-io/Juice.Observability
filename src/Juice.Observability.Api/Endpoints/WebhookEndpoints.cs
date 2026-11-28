using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Health.Delivery;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>A delivery: of an alert's notification (<c>AlertId</c>) or an action request's (<c>ActionRequestId</c>).</summary>
    public sealed record WebhookDeliveryDto(Guid Id, Guid? AlertId, string Event, string Status, int Attempts, DateTimeOffset CreatedAt,
        DateTimeOffset? FirstAttemptAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError,
        IReadOnlyList<WebhookAttemptDto> History, Guid? SuppressedBy = null, Guid? GroupedInto = null, int EscalationStep = 0, Guid? ActionRequestId = null);

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
            // The webhooks alias (I69): every answer says these routes are deprecated, and where their successor is.
            foreach (var route in new[]
            {
                tenant.MapGet("/webhooks", ListEndpointsAsync),
                tenant.MapPost("/webhooks", CreateAsync),
                tenant.MapPut("/webhooks/{endpointId:guid}", UpdateAsync),
                tenant.MapGet("/webhooks/{endpointId:guid}/deliveries", ListDeliveriesAsync),
                tenant.MapPost("/webhooks/{endpointId:guid}/test", TestAsync),
                tenant.MapPost("/webhooks/{endpointId:guid}/deliveries/{deliveryId:guid}/retry", RetryAsync),
                tenant.MapGet("/webhooks/allowlist", ListAllowlistAsync),
                tenant.MapPut("/webhooks/allowlist", PutAllowlistAsync),
                tenant.MapDelete("/webhooks/allowlist", RemoveAllowlistAsync),
            })
            {
                route.AddEndpointFilter(DeprecatedAsync);
            }
            return tenant;
        }

        /// <summary>The successor of these routes: the notifications admin the host mounts (Q7, the prefix form).</summary>
        public const string Successor = "/notifications";

        /// <summary><c>Deprecation: true</c> and <c>Link: &lt;/notifications&gt;; rel="successor-version"</c> on every answer, refusals included (I69).</summary>
        private static async ValueTask<object?> DeprecatedAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Deprecation"] = "true";
            headers.Link = $"<{Successor}>; rel=\"successor-version\"";
            return await next(context);
        }

        private static WebhookEndpointDto ToDto(AliasEndpoint a, HttpContext http, int failures = 0, int waiting = 0)
        {
            var d = a.Definition;
            return new(a.Id, d.Key, d.Kind.ToString(), Destination(d.Kind, d.Url), d.Events, d.RuleKeys, d.Environments, http.User.IsInRole(Roles.Admin) ? d.Recipients : null,
                http.User.IsInRole(Roles.Admin) ? d.SecretRef : null, d.GroupWindowSeconds, d.MinimumSeverity.ToString(), d.Enabled, a.ManagedBy, a.LoadedAt, failures, waiting);
        }

        private static string Destination(ChannelKind kind, string url) => kind switch
        {
            ChannelKind.Webhook => new Uri(url).GetLeftPart(UriPartial.Authority),
            ChannelKind.Email => "SMTP",
            _ => kind.ToString(),
        };

        /// <summary>
        /// The endpoints as the API answers them. While legacy rows are written (legacy, shadow) they are authoritative: every one is answered from
        /// <c>webhook_endpoints</c>, as legacy sends them, including one the alias couldn't map (a digit-led key, a secret outside its limits) or
        /// wrote nowhere (the pipeline down), and nothing of the pipeline is read, so its failure can't take the API down. In new, the alias's.
        /// </summary>
        private static async Task<IReadOnlyList<AliasEndpoint>> EndpointsAsync(PlatformDbContext db, Guid tenantId, WebhookAlias alias, CancellationToken ct)
            => alias.WritesLegacy
                ? [.. (await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Key).ToListAsync(ct)).Select(AsAnswer)]
                : await alias.ListAsync(null, ct);

        /// <summary>One endpoint as <see cref="EndpointsAsync"/> answers it, or null.</summary>
        private static async Task<AliasEndpoint?> EndpointAsync(PlatformDbContext db, Guid tenantId, Guid endpointId, WebhookAlias alias, CancellationToken ct)
            => alias.WritesLegacy
                ? await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct) is { } e ? AsAnswer(e) : null
                : await alias.FindAsync(endpointId, null, ct);

        private static AliasEndpoint AsAnswer(WebhookEndpoint e) => new(e.Id, LegacyEndpoint.From(e), e.ManagedBy, e.LoadedAt);

        /// <summary>
        /// The alias's half of a legacy write, in the caller's open transaction: what it refused, for the caller to roll back, or none. In new it is
        /// the whole write. While legacy rows are written (legacy, shadow) legacy's change never fails on the pipeline's account (review of #10):
        /// <list type="bullet">
        /// <item>when the pipeline didn't start (its schema missing), the alias isn't asked; the next start's alias provisioner and the migration
        /// bring the notifications rows in step with legacy's;</item>
        /// <item>a refusal undoes legacy's change too only when <paramref name="bothOrNeither"/> says so (it is answered null to mean the
        /// alias never held the row, legacy's own); otherwise what the alias wrote is rolled back to a savepoint and legacy's stands;</item>
        /// <item>an unexpected error is the same: the alias's part rolled back, legacy's kept, logged.</item>
        /// </list>
        /// </summary>
        internal static async Task<IReadOnlyList<MappingProblem>> AliasWriteAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, WebhookAlias alias,
            IServiceProvider services, string what, Func<Task<bool>>? bothOrNeither, Func<Task<IReadOnlyList<MappingProblem>>> write, CancellationToken ct)
        {
            if (!alias.WritesLegacy)
            {
                return await write();
            }
            var logger = services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Juice.Observability.Webhooks.Alias");
            if (services.GetService<Juice.Observability.Notifications.PipelineStart>()?.Failure is { } failure)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, "{What} kept in legacy only: the notification pipeline didn't start ({Failure})", what, failure);
                return [];
            }
            await transaction.CreateSavepointAsync("alias", ct);
            try
            {
                var problems = await write();
                if (problems.Count == 0 || bothOrNeither is null || await bothOrNeither())
                {
                    return problems;
                }
                // The alias never held it (a digit-led key, a secret outside its limits): legacy's own, as before the alias.
                await transaction.RollbackToSavepointAsync("alias", ct);
                return [];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await transaction.RollbackToSavepointAsync("alias", ct);
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "{What} kept in legacy only: the alias's write failed", what);
                return [];
            }
        }

        private static async Task<IResult> ListEndpointsAsync(Guid tenantId, HttpContext http, PlatformDbContext db, WebhookAlias alias, TimeProvider clock, CancellationToken ct)
        {
            var endpoints = await EndpointsAsync(db, tenantId, alias, ct);
            var since = clock.GetUtcNow() - FailureWindow;
            if (!alias.WritesLegacy)
            {
                var fromNotifications = await alias.CountsAsync(since, ct);
                return Results.Ok(new Page<WebhookEndpointDto>([.. endpoints.Select(e => fromNotifications.GetValueOrDefault(e.Id) is var (failures, waiting)
                    ? ToDto(e, http, failures, waiting) : ToDto(e, http))], null));
            }
            // Legacy still sends: its deliveries are the ones that happened (an endpoint's id is its legacy row's).
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
            if (kind != ChannelKind.Email && !SecretReference.IsAllowedFromApi(definition.SecretRef, options.ApiSecretEnvPrefix, options.ApiSecretDirectory))
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

        /// <summary>A definition as the alias's mapping takes it.</summary>
        private static LegacyEndpoint Definition(EndpointDefinition d) => new(d.Key, d.Kind, d.Url, d.SecretRef, d.Events, d.RuleKeys, d.Environments, d.Recipients,
            d.GroupWindowSeconds, d.MinimumSeverity, d.Enabled);

        /// <summary>What the alias refused, as legacy's API answers a refusal: a key taken is a conflict, the rest invalid.</summary>
        private static IResult Refused(IReadOnlyList<MappingProblem> problems)
            => problems.Any(p => p.Code is ProblemCodes.KeyTaken or Juice.Notifications.ErrorCodes.ManagedByApi or Juice.Notifications.ErrorCodes.ManagedByConfiguration)
                ? Mutation.Problem(StatusCodes.Status409Conflict, "Endpoint exists", string.Join(" ", problems.Select(p => p.Message)))
                : Mutation.Invalid(problems.Select(p => p.Message));

        /// <summary>
        /// Creates an endpoint through the alias (I69; decisions on Q-A, Q-B): in legacy and shadow, the legacy row and the notifications rows in one
        /// platform transaction, the integration taking the legacy row's id; in new, notifications only, with a new id. A refusal on either side
        /// leaves neither.
        /// </summary>
        private static async Task<IResult> CreateAsync(Guid tenantId, WebhookRequest? request, HttpContext http, PlatformDbContext db, WebhookAlias alias,
            TimeProvider clock, IOptions<WebhookOptions> options, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.create", "webhook");
            if (await mutation.RequireAsync(null, ct, Roles.Admin) is { } denied) return denied;
            var (definition, problem) = Read(request, null, null, options.Value);
            if (problem is not null) return problem;
            var existing = await EndpointsAsync(db, tenantId, alias, ct);
            if (existing.Any(e => e.Definition.Key == definition!.Key) || await db.WebhookEndpoints.AnyAsync(e => e.TenantId == tenantId && e.Key == definition!.Key, ct))
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Endpoint exists", $"An endpoint with the key '{definition!.Key}' exists.");
            }
            if (existing.Count >= MaxEndpoints)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Too many endpoints", $"A tenant has at most {MaxEndpoints} endpoints.");
            }
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var id = Guid.CreateVersion7();
            if (alias.WritesLegacy)
            {
                var endpoint = new WebhookEndpoint
                {
                    Id = id, TenantId = tenantId, Key = definition!.Key, Kind = definition.Kind, Url = definition.Url, SecretRef = definition.SecretRef,
                    Events = [.. definition.Events], RuleKeys = [.. definition.RuleKeys], Environments = [.. definition.Environments], Recipients = [.. definition.Recipients],
                    GroupWindowSeconds = definition.GroupWindowSeconds, MinimumSeverity = definition.MinimumSeverity, Enabled = definition.Enabled,
                    DefinitionHash = definition.Hash(), LoadedAt = now, ManagedBy = ManagedBy.Api,
                };
                db.WebhookEndpoints.Add(endpoint);
            }
            mutation.Audit(AuditOutcome.Succeeded, definition!.Key, Metadata(definition));
            mutation.RecordChange($"webhook {definition.Key}: webhook.create", now);
            await db.SaveChangesAsync(ct);
            if (await AliasWriteAsync(transaction, alias, http.RequestServices, $"webhook {definition.Key}", null,
                    () => alias.PutAsync(Definition(definition), id, ct), ct) is { Count: > 0 } refused)
            {
                await transaction.RollbackAsync(ct);
                return Refused(refused);
            }
            await transaction.CommitAsync(ct);
            return Results.Created($"{http.Request.Path}/{id}", ToDto(new AliasEndpoint(id, Definition(definition), ManagedBy.Api, now), http));
        }

        private static async Task<IResult> UpdateAsync(Guid tenantId, Guid endpointId, WebhookRequest? request, HttpContext http, PlatformDbContext db, WebhookAlias alias,
            TimeProvider clock, IOptions<WebhookOptions> options, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.update", "webhook");
            if (await mutation.RequireAsync(endpointId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            var found = await EndpointAsync(db, tenantId, endpointId, alias, ct);
            if (found is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Webhook endpoint not found", "No such endpoint.");
            }
            if (found.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(found.Definition.Key, File, ct);
            var current = found.Definition;
            var asLegacy = new WebhookEndpoint { Key = current.Key, Kind = current.Kind, Url = current.Url, SecretRef = current.SecretRef, DefinitionHash = "" };
            var (definition, problem) = Read(request, current.Key, asLegacy, options.Value);
            if (problem is not null) return problem;
            var now = clock.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (alias.WritesLegacy && await db.WebhookEndpoints.SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct) is { } endpoint)
            {
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
            }
            mutation.Audit(AuditOutcome.Succeeded, definition!.Key, Metadata(definition));
            mutation.RecordChange($"webhook {definition.Key}: webhook.update", now);
            await db.SaveChangesAsync(ct);
            // Both or neither, for an endpoint the alias holds. One it never could (a digit-led key, a secret outside its limits: legacy's own,
            // as before the alias) changes in legacy only; the migration's report names what keeps it out.
            if (await AliasWriteAsync(transaction, alias, http.RequestServices, $"webhook {definition.Key}", () => alias.HoldsAsync(endpointId, ct),
                    () => alias.PutAsync(Definition(definition), endpointId, ct), ct) is { Count: > 0 } refused)
            {
                await transaction.RollbackAsync(ct);
                return Refused(refused);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(ToDto(new AliasEndpoint(endpointId, Definition(definition), found.ManagedBy, now), http));
        }

        /// <summary>
        /// Sends a test notification now, through the same checks, signing and formatting as an alert's, and answers with what happened. Works on a
        /// disabled endpoint too, to check a channel before turning it on. Admin only, audited; nothing is queued or retried.
        /// </summary>
        private static async Task<IResult> TestAsync(Guid tenantId, Guid endpointId, HttpContext http, PlatformDbContext db, WebhookAlias alias, IServiceProvider services, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.test", "webhook");
            if (await mutation.RequireAsync(endpointId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            WebhookTestResult result;
            string key;
            if (!alias.WritesLegacy)
            {
                // Q-C: in new, through the channel's provider with legacy's test payload, to the endpoint's integration of the same id.
                if (await alias.FindAsync(endpointId, null, ct) is not { } found)
                {
                    return Mutation.Problem(StatusCodes.Status404NotFound, "Webhook endpoint not found", "No such endpoint.");
                }
                result = (await alias.TestAsync(services, tenantId, endpointId, mutation.Actor, ct))!;
                key = found.Definition.Key;
            }
            else
            {
                var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct);
                if (endpoint is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Webhook endpoint not found", "No such endpoint.");
                if (services.GetService<WebhookDispatcher>() is not { } dispatcher)
                {
                    return Mutation.Problem(StatusCodes.Status409Conflict, "Notifications are off", "This deployment doesn't send notifications (Webhooks:Enabled is false).");
                }
                result = await dispatcher.TestAsync(endpoint, mutation.Actor, ct);
                key = endpoint.Key;
            }
            var metadata = new Dictionary<string, string> { ["delivered"] = result.Delivered ? "true" : "false", ["durationMs"] = result.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (result.StatusCode is { } code) metadata["status"] = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (result.Error is { } error) metadata["error"] = error;
            mutation.Audit(result.Delivered ? AuditOutcome.Succeeded : AuditOutcome.Failed, key, metadata);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        }

        /// <summary>
        /// Sends a failed delivery once more, now: what the runbook's SQL did, with the role check and an audit. The attempt is numbered after the
        /// earlier ones; if it fails again the delivery is <c>Failed</c> again, with that attempt's error. Admin only; the endpoint must be enabled.
        /// </summary>
        private static async Task<IResult> RetryAsync(Guid tenantId, Guid endpointId, Guid deliveryId, HttpContext http, PlatformDbContext db, WebhookAlias alias,
            Juice.MediatR.IMediator mediator, TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.delivery.retry", "webhook_delivery");
            if (await mutation.RequireAsync(deliveryId.ToString(), ct, Roles.Admin) is { } denied) return denied;
            if (!alias.WritesLegacy)
            {
                // Q-C: notifications' own retry of a dead delivery.
                var outcome = await alias.RetryAsync(mediator, endpointId, deliveryId, ct);
                if (outcome is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Delivery not found", "No such delivery for this endpoint.");
                if (outcome.Kind is not (Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created))
                {
                    return Mutation.Problem(StatusCodes.Status409Conflict, "Not failed", outcome.Message ?? "This delivery can't be retried.");
                }
                mutation.Audit(AuditOutcome.Succeeded, deliveryId.ToString(), new() { ["endpoint"] = endpointId.ToString() });
                await db.SaveChangesAsync(ct);
                var retried = (await alias.DeliveriesAsync(endpointId, null, 1, null, ct, only: deliveryId)).Single();
                return Results.Ok(ToDto(retried));
            }
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId, ct);
            var delivery = endpoint is null ? null : await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.TenantId == tenantId && d.EndpointId == endpointId && d.Id == deliveryId, ct);
            if (delivery is null) return Mutation.Problem(StatusCodes.Status404NotFound, "Delivery not found", "No such delivery for this endpoint.");
            if (!endpoint!.Enabled) return Mutation.Problem(StatusCodes.Status409Conflict, "Endpoint disabled", "Enable the endpoint before retrying its deliveries.");
            if (delivery.Status != DeliveryStatus.Failed)
            {
                return Mutation.Problem(StatusCodes.Status409Conflict, "Not failed", $"Only a failed delivery can be retried; this one is {delivery.Status}.");
            }
            delivery.Status = DeliveryStatus.Pending;
            delivery.NextAttemptAt = clock.GetUtcNow();
            mutation.Audit(AuditOutcome.Succeeded, deliveryId.ToString(), new() { ["endpoint"] = endpoint.Key, ["attempts"] = delivery.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture), ["lastError"] = delivery.LastError ?? "" });
            await db.SaveChangesAsync(ct);
            return Results.Ok((await ToDeliveryDtosAsync(db, [delivery], ct))[0]);
        }

        /// <summary>Deliveries newest first, each with its attempts, filtered by <c>status</c>.</summary>
        private static async Task<IResult> ListDeliveriesAsync(Guid tenantId, Guid endpointId, string? status, int? limit, string? cursor,
            PlatformDbContext db, WebhookAlias alias, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            if (!alias.WritesLegacy)
            {
                // Q-C: the deliveries are notifications', in legacy's shape.
                if (await alias.FindAsync(endpointId, null, ct) is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Webhook endpoint not found");
                }
                DeliveryStatus? wanted = null;
                if (status is not null)
                {
                    if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var asked))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid status",
                            detail: "status must be one of: pending, retrying, delivered, failed, suppressed, dropped, grouped.");
                    }
                    wanted = asked == DeliveryStatus.Retrying ? DeliveryStatus.Pending : asked;
                }
                var found = await alias.DeliveriesAsync(endpointId, wanted, take + 1, after?.After, ct);
                var shown = found.Take(take).ToList();
                return Results.Ok(new Page<WebhookDeliveryDto>([.. shown.Select(ToDto)], found.Count > take ? new Cursor("wd", shown[^1].Id).Encode() : null));
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

        /// <summary>A notifications delivery in legacy's shape (Q-C): no group or silence of legacy's own.</summary>
        private static WebhookDeliveryDto ToDto(AliasDelivery d) => new(d.Id, d.AlertId, d.Event, d.Status.ToString(), d.Attempts, d.CreatedAt, d.FirstAttemptAt,
            d.DeliveredAt, d.NextAttemptAt, d.LastStatusCode, d.LastError, [.. d.History.Select(a => new WebhookAttemptDto(a.Number, a.At, a.StatusCode, a.Error, a.DurationMs))],
            null, null, d.EscalationStep, d.ActionRequestId);

        internal static async Task<List<WebhookDeliveryDto>> ToDeliveryDtosAsync(PlatformDbContext db, IReadOnlyList<WebhookDelivery> deliveries, CancellationToken ct)
        {
            var ids = deliveries.Select(d => d.Id).ToList();
            var attempts = (await db.WebhookAttempts.AsNoTracking().Where(a => ids.Contains(a.DeliveryId)).OrderBy(a => a.Number).ToListAsync(ct)).ToLookup(a => a.DeliveryId);
            return deliveries.Select(d => new WebhookDeliveryDto(d.Id, d.AlertId, d.Event, d.Status.ToString(), d.Attempts, d.CreatedAt,
                d.FirstAttemptAt, d.DeliveredAt, d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying ? d.NextAttemptAt : null,
                d.LastStatusCode, d.LastError, attempts[d.Id].Select(a => new WebhookAttemptDto(a.Number, a.At, a.StatusCode, a.Error, a.DurationMs)).ToList(),
                d.SuppressedBy, d.GroupedInto, d.EscalationStep, d.ActionRequestId)).ToList();
        }

        private static async Task<IResult> ListAllowlistAsync(Guid tenantId, PlatformDbContext db, WebhookAlias alias, CancellationToken ct)
        {
            if (!alias.WritesLegacy)
            {
                return Results.Ok(new Page<AllowlistEntryDto>([.. (await alias.ListAllowlistAsync(ct)).Select(a => new AllowlistEntryDto(a.Entry.Destination, a.Entry.Ports, a.ManagedBy, a.UpdatedAt))], null));
            }
            var rows = await db.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination).ToListAsync(ct);
            return Results.Ok(new Page<AllowlistEntryDto>(rows.Select(a => new AllowlistEntryDto(a.Destination, a.Ports, a.ManagedBy, a.LoadedAt)).ToList(), null));
        }

        /// <summary>Adds an entry, or changes its ports. This opens a private address to outgoing requests, so it's admin only and always audited.</summary>
        private static async Task<IResult> PutAllowlistAsync(Guid tenantId, AllowlistRequest? request, HttpContext http, PlatformDbContext db, WebhookAlias alias,
            TimeProvider clock, CancellationToken ct)
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
            // The legacy entry and notifications' in one transaction (I69): legacy's while it still sends.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (entry is null)
            {
                entry = new WebhookAllowlistEntry { TenantId = tenantId, Destination = destination, Ports = ports, LoadedAt = now, ManagedBy = ManagedBy.Api };
                if (alias.WritesLegacy)
                {
                    db.WebhookAllowlist.Add(entry);
                }
            }
            else
            {
                entry.Ports = ports;
                entry.LoadedAt = now;
            }
            mutation.Audit(AuditOutcome.Succeeded, destination, new() { ["ports"] = string.Join(",", ports) });
            mutation.RecordChange($"webhook_allowlist {destination}: put", now);
            await db.SaveChangesAsync(ct);
            if (await AliasWriteAsync(transaction, alias, http.RequestServices, $"allowlist entry {destination}", null,
                    () => alias.PutAllowlistAsync(new LegacyAllowlistEntry(destination, ports), ct), ct) is { Count: > 0 } refused)
            {
                await transaction.RollbackAsync(ct);
                return Refused(refused);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new AllowlistEntryDto(entry.Destination, entry.Ports, entry.ManagedBy, entry.LoadedAt));
        }

        /// <summary>By query parameter, since a CIDR holds a slash. Connections already open may last their pool lifetime, at most a minute.</summary>
        private static async Task<IResult> RemoveAllowlistAsync(Guid tenantId, string? destination, HttpContext http, PlatformDbContext db, WebhookAlias alias,
            TimeProvider clock, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "webhook.allowlist.remove", "webhook_allowlist");
            if (await mutation.RequireAsync(destination, ct, Roles.Admin) is { } denied) return denied;
            var entry = await db.WebhookAllowlist.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Destination == destination, ct);
            if (entry is null && !alias.WritesLegacy && (await alias.ListAllowlistAsync(ct)).FirstOrDefault(a => a.Entry.Destination == destination) is { Entry: { } held })
            {
                // New: the entry is notifications' only.
                entry = new WebhookAllowlistEntry { TenantId = tenantId, Destination = held.Destination, Ports = [.. held.Ports], ManagedBy = ManagedBy.Api };
            }
            if (entry is null)
            {
                return Mutation.Problem(StatusCodes.Status404NotFound, "Not on the allowlist", "No such entry.");
            }
            if (entry.ManagedBy == ManagedBy.Configuration) return await mutation.ManagedByConfigurationAsync(destination, File, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (alias.WritesLegacy)
            {
                db.WebhookAllowlist.Remove(entry);
            }
            mutation.Audit(AuditOutcome.Succeeded, destination);
            mutation.RecordChange($"webhook_allowlist {destination}: remove", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            // Legacy's removal always stands (review of #10): a destination notifications keeps under another owner stays theirs, and is logged.
            if (await AliasWriteAsync(transaction, alias, http.RequestServices, $"allowlist entry {destination}", () => Task.FromResult(false),
                    () => alias.RemoveAllowlistAsync(AllowlistMapping.Map(new LegacyAllowlistEntry(destination!, entry.Ports)).Value?.Destination ?? destination!, ct), ct)
                is { Count: > 0 } refused)
            {
                await transaction.RollbackAsync(ct);
                return Refused(refused);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
    }
}
