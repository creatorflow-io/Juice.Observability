using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Juice.Observability.Health;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    public sealed record TenantDto(Guid Id, string Key, string DisplayName);

    public sealed record ApplicationDto(Guid Id, string Key, string DisplayName, int Services, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

    public sealed record ResourceSummaryDto(Guid Id, string Type, string Key, string DisplayName, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

    public sealed record InstanceDto(Guid Id, string Environment, string InstanceKey, string KeySource, string? ServiceVersion,
        string State, Guid? HostResourceId, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? Health);

    public sealed record ResourceDetailDto(Guid Id, string Type, string Key, string DisplayName, DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen, string? Namespace, string? Name, Guid? ApplicationId, IReadOnlyDictionary<string, string> Attributes,
        IReadOnlyList<InstanceDto> Instances, bool InstancesTruncated, IReadOnlyList<HealthDto> Health, string? GrafanaUrl);

    /// <summary><see cref="Health"/> is the node's state in the requested environment (hosts have one state), or null.</summary>
    public sealed record TopologyNodeDto(Guid Id, string Type, string DisplayName, string? Health);

    public sealed record TopologyEdgeDto(Guid Id, Guid Source, Guid Target, string Relation, string Confidence, string EvidenceSource,
        long SpanCount, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

    public sealed record TopologyDto(string? Environment, IReadOnlyList<TopologyNodeDto> Nodes, IReadOnlyList<TopologyEdgeDto> Edges, bool Truncated);

    /// <summary>Phase 1 endpoints (requirements §6.1): tenants, applications, resources, topology. Read-only.</summary>
    public static class InventoryEndpoints
    {
        public const int MaxInstances = 200;
        public const int MaxEdges = 5000;

        // API names of resource types; services are listed as resources of type "service".
        public const string ServiceType = "service";
        private static readonly Dictionary<ResourceType, string> TypeNames = new()
        {
            [ResourceType.Host] = "host",
            [ResourceType.Container] = "container",
            [ResourceType.KubernetesPod] = "k8s-pod",
            [ResourceType.MessagingDestination] = "messaging-destination",
            [ResourceType.Database] = "database",
        };
        private static readonly Dictionary<string, ResourceType> TypesByName = TypeNames.ToDictionary(kv => kv.Value, kv => kv.Key);

        public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api/v1").RequireAuthorization(Policies.Read);

            api.MapGet("/tenants", ListTenantsAsync);

            var tenant = api.MapGroup("/tenants/{tenantId:guid}").AddEndpointFilter(TenantScopeAsync);
            tenant.MapGet("/applications", ListApplicationsAsync);
            tenant.MapGet("/resources", ListResourcesAsync);
            tenant.MapGet("/resources/{resourceId:guid}", GetResourceAsync);
            tenant.MapGet("/topology", GetTopologyAsync);
            tenant.MapHealthEndpoints();
            tenant.MapAlertEndpoints();
            tenant.MapWebhookEndpoints();
            return app;
        }

        /// <summary>The tenants the caller may use: the one its tenant claim names, when it exists.</summary>
        private static async Task<IResult> ListTenantsAsync(HttpContext http, PlatformDbContext db, IOptions<AuthOptions> auth, CancellationToken ct)
        {
            var key = http.User.FindFirst(auth.Value.TenantClaim)?.Value;
            var tenants = key is null ? [] : await db.Tenants.AsNoTracking()
                .Where(t => t.Key == key && t.Status == TenantStatus.Active)
                .Select(t => new TenantDto(t.Id, t.Key, t.DisplayName)).ToListAsync(ct);
            return Results.Ok(new Page<TenantDto>(tenants, null));
        }

        /// <summary>
        /// The path tenant must be the caller's tenant (requirements §6.1). An unknown id and another tenant's id get the
        /// same 403, so the response does not reveal which tenants exist.
        /// </summary>
        private static async ValueTask<object?> TenantScopeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var claim = http.User.FindFirst(http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.TenantClaim)?.Value;
            var id = Guid.Parse((string)http.GetRouteValue("tenantId")!);
            var db = http.RequestServices.GetRequiredService<PlatformDbContext>();
            var tenant = claim is null ? null : await db.Tenants.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == id && t.Key == claim && t.Status == TenantStatus.Active, http.RequestAborted);
            if (tenant is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Tenant not authorized",
                    detail: "The caller is not authorized for this tenant.");
            }
            return await next(context);
        }

        private static async Task<IResult> ListApplicationsAsync(Guid tenantId, int? limit, string? cursor, PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            var query = db.Applications.AsNoTracking().Where(a => a.TenantId == tenantId);
            if (after is not null)
            {
                query = query.Where(a => a.Id > after.After);
            }
            var rows = await query.OrderBy(a => a.Id).Take(take + 1)
                .Select(a => new ApplicationDto(a.Id, a.Key, a.DisplayName,
                    db.Services.Count(s => s.TenantId == tenantId && s.ApplicationId == a.Id), a.FirstSeen, a.LastSeen))
                .ToListAsync(ct);
            return Results.Ok(PageOf(rows, take, a => new Cursor("a", a.Id)));
        }

        /// <summary>
        /// Services and infrastructure resources, filtered by <c>type</c>. Without a filter, services come first, then
        /// resources; the cursor phase says which list it continues.
        /// </summary>
        private static async Task<IResult> ListResourcesAsync(Guid tenantId, string? type, int? limit, string? cursor,
            PlatformDbContext db, CancellationToken ct)
        {
            if (Cursor.Validate(limit, cursor, out var take, out var after) is { } invalid)
            {
                return invalid;
            }
            ResourceType? resourceType = null;
            if (type is not null && type != ServiceType)
            {
                if (!TypesByName.TryGetValue(type, out var parsed))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid type",
                        detail: $"type must be one of: {ServiceType}, {string.Join(", ", TypeNames.Values)}.");
                }
                resourceType = parsed;
            }
            if (after is not null && after.Phase is not ("s" or "r"))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid cursor");
            }

            var items = new List<ResourceSummaryDto>();
            var servicesPhase = after?.Phase is null or "s";
            if (servicesPhase && (type is null || type == ServiceType))
            {
                var services = db.Services.AsNoTracking().Where(s => s.TenantId == tenantId);
                if (after is not null)
                {
                    services = services.Where(s => s.Id > after.After);
                }
                var rows = await services.OrderBy(s => s.Id).Take(take + 1)
                    .Select(s => new ResourceSummaryDto(s.Id, ServiceType, s.Namespace == "" ? s.Name : s.Namespace + "/" + s.Name,
                        s.Name, s.FirstSeen, s.LastSeen))
                    .ToListAsync(ct);
                if (rows.Count > take)
                {
                    return Results.Ok(new Page<ResourceSummaryDto>(rows[..take], new Cursor("s", rows[take - 1].Id).Encode()));
                }
                items.AddRange(rows);
            }
            if (type != ServiceType)
            {
                var resources = db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId);
                if (resourceType is { } t)
                {
                    resources = resources.Where(r => r.Type == t);
                }
                if (after?.Phase == "r")
                {
                    resources = resources.Where(r => r.Id > after.After);
                }
                var remaining = take - items.Count;
                var rows = await resources.OrderBy(r => r.Id).Take(remaining + 1)
                    .Select(r => new { r.Id, r.Type, r.Key, r.DisplayName, r.FirstSeen, r.LastSeen }).ToListAsync(ct);
                items.AddRange(rows.Take(remaining).Select(r => new ResourceSummaryDto(r.Id, TypeNames[r.Type], r.Key, r.DisplayName, r.FirstSeen, r.LastSeen)));
                if (rows.Count > remaining)
                {
                    return Results.Ok(new Page<ResourceSummaryDto>(items, new Cursor("r", items[^1].Id).Encode()));
                }
            }
            return Results.Ok(new Page<ResourceSummaryDto>(items, null));
        }

        private static async Task<IResult> GetResourceAsync(Guid tenantId, Guid resourceId, PlatformDbContext db,
            IOptions<HealthPolicy> policy, IOptions<GrafanaOptions> grafana, TimeProvider clock, CancellationToken ct)
        {
            async Task<IReadOnlyList<HealthDto>> HealthOf(Guid subject)
                => await HealthEndpoints.ToDtosAsync(db, tenantId, await db.HealthSnapshots.AsNoTracking()
                    .Where(h => h.TenantId == tenantId && h.SubjectId == subject).ToListAsync(ct), policy.Value, clock, ct);

            var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == resourceId, ct);
            if (service is not null)
            {
                var instances = await Instances(db.Instances.Where(i => i.TenantId == tenantId && i.ServiceId == service.Id), db, ct);
                return Results.Ok(new ResourceDetailDto(service.Id, ServiceType, HealthRunner.Name(service), service.Name,
                    service.FirstSeen, service.LastSeen, service.Namespace, service.Name, service.ApplicationId,
                    new Dictionary<string, string>(), instances.Items, instances.Truncated, await HealthOf(service.Id),
                    HealthEndpoints.GrafanaLink(grafana.Value, service.Name, clock.GetUtcNow())));
            }
            var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == resourceId, ct);
            if (resource is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Resource not found");
            }
            // For a host: the service instances running on it.
            var hosted = await Instances(db.Instances.Where(i => i.TenantId == tenantId && i.HostResourceId == resource.Id), db, ct);
            return Results.Ok(new ResourceDetailDto(resource.Id, TypeNames[resource.Type], resource.Key, resource.DisplayName,
                resource.FirstSeen, resource.LastSeen, null, null, null, resource.Attributes, hosted.Items, hosted.Truncated,
                await HealthOf(resource.Id), null));
        }

        /// <summary>Active instances first, then the most recently seen; at most <see cref="MaxInstances"/>.</summary>
        private static async Task<(IReadOnlyList<InstanceDto> Items, bool Truncated)> Instances(IQueryable<ServiceInstance> query,
            PlatformDbContext db, CancellationToken ct)
        {
            var rows = await query.AsNoTracking()
                .OrderBy(i => i.State == InstanceState.Active ? 0 : 1).ThenByDescending(i => i.LastSeen)
                .Take(MaxInstances + 1)
                .Join(db.Environments, i => i.EnvironmentId, e => e.Id, (i, e) => new InstanceDto(i.Id, e.Key, i.InstanceKey,
                    i.KeySource.ToString(), i.ServiceVersion, i.State.ToString(), i.HostResourceId, i.FirstSeen, i.LastSeen,
                    db.HealthSnapshots.Where(h => h.SubjectId == i.Id).Select(h => h.State.ToString()).FirstOrDefault()))
                .ToListAsync(ct);
            return (rows.Take(MaxInstances).ToList(), rows.Count > MaxInstances);
        }

        /// <summary>
        /// Services and the resources they use, with the dependencies between them in one environment (or all). Edges
        /// carry their evidence and confidence; at most <see cref="MaxEdges"/>.
        /// </summary>
        private static async Task<IResult> GetTopologyAsync(Guid tenantId, string? environment, PlatformDbContext db, CancellationToken ct)
        {
            var edges = db.Dependencies.AsNoTracking().Where(d => d.TenantId == tenantId);
            Guid? environmentId = null;
            if (environment is not null)
            {
                var env = await db.Environments.AsNoTracking()
                    .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.Key == environment.ToLowerInvariant(), ct);
                if (env is null)
                {
                    return Results.Ok(new TopologyDto(environment, [], [], false));
                }
                environmentId = env.Id;
                edges = edges.Where(d => d.EnvironmentId == env.Id);
            }
            var rows = await edges.OrderByDescending(d => d.LastSeen).Take(MaxEdges + 1).ToListAsync(ct);
            var truncated = rows.Count > MaxEdges;
            rows = rows.Take(MaxEdges).ToList();

            var serviceIds = rows.Select(d => d.SourceServiceId).Concat(rows.Where(d => d.TargetServiceId is not null).Select(d => d.TargetServiceId!.Value)).ToHashSet();
            var resourceIds = rows.Where(d => d.TargetResourceId is not null).Select(d => d.TargetResourceId!.Value).ToHashSet();
            var nodeIds = serviceIds.Concat(resourceIds).ToList();
            // Service health is per environment; without one, nodes carry no state rather than an arbitrary environment's.
            var health = environmentId is null ? [] : await db.HealthSnapshots.AsNoTracking()
                .Where(h => h.TenantId == tenantId && nodeIds.Contains(h.SubjectId) && (h.EnvironmentId == environmentId || h.EnvironmentId == null))
                .ToDictionaryAsync(h => h.SubjectId, h => h.State.ToString(), ct);
            var nodes = new List<TopologyNodeDto>();
            nodes.AddRange((await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && serviceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name }).ToListAsync(ct))
                .Select(s => new TopologyNodeDto(s.Id, ServiceType, s.Name, health.GetValueOrDefault(s.Id))));
            var resources = await db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId && resourceIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Type, r.DisplayName }).ToListAsync(ct);
            nodes.AddRange(resources.Select(r => new TopologyNodeDto(r.Id, TypeNames[r.Type], r.DisplayName, health.GetValueOrDefault(r.Id))));

            return Results.Ok(new TopologyDto(environment, nodes, rows.Select(d => new TopologyEdgeDto(d.Id, d.SourceServiceId,
                d.TargetServiceId ?? d.TargetResourceId!.Value, Name(d.Relation), Name(d.Confidence), d.EvidenceSource, d.SpanCount,
                d.FirstSeen, d.LastSeen)).ToList(), truncated));
        }

        private static string Name<T>(T value) where T : struct, Enum
        {
            var text = value.ToString();
            return char.ToLowerInvariant(text[0]) + text[1..];
        }

        private static Page<T> PageOf<T>(List<T> rows, int take, Func<T, Cursor> cursorOf)
            => rows.Count > take ? new Page<T>(rows[..take], cursorOf(rows[take - 1]).Encode()) : new Page<T>(rows, null);
    }
}
