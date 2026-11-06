using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Discovery
{
    public sealed record ReconcileResult(int Services, int Instances, int Resources, int Dependencies, int Deactivated, int Pruned);

    /// <summary>
    /// Upserts observations into the domain model by natural key, within one tenant. Applying the same observations
    /// again changes nothing: first-seen only moves earlier and last-seen only later. Never deletes services or
    /// resources; instances go inactive and are pruned after the retention (D3). The caller owns the transaction.
    /// </summary>
    public sealed class Reconciler(PlatformDbContext db, Normalizer normalizer, DiscoveryOptions options, TimeProvider clock)
    {
        private readonly Dictionary<string, DeploymentEnvironment> _environments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Application> _applications = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), Service> _services = new();
        private readonly Dictionary<(ResourceType, string), Resource> _resources = new();
        private readonly Dictionary<(Guid, Guid, string), ServiceInstance> _instances = new();
        private readonly Dictionary<(Guid, Guid), ServiceEnvironment> _serviceEnvironments = new();
        private readonly Dictionary<(Guid, Guid, DependencyRelation, Guid?, Guid?), Dependency> _dependencies = new();
        // Dependencies created by this pass: their spans before the checkpoint were never counted.
        private readonly HashSet<(Guid, Guid, DependencyRelation, Guid?, Guid?)> _created = new();
        private int _newServices, _newInstances, _newResources, _newDependencies;

        public async Task<ReconcileResult> ApplyAsync(Guid tenantId, IReadOnlyList<ResourceObservation> resources,
            IReadOnlyList<ServiceLinkObservation> links, IReadOnlyList<ResourceUseObservation> uses, CancellationToken cancellationToken)
        {
            await LoadAsync(tenantId, resources, links, uses, cancellationToken);
            var versions = new List<(Service Service, DeploymentEnvironment Environment, string Version, string? Known, DateTimeOffset At)>();

            foreach (var o in resources)
            {
                var hosts = normalizer.Resources(o).Select(r => UpsertResource(tenantId, r, o.FirstSeen, o.LastSeen)).ToList();
                if (normalizer.Service(o.ServiceNamespace, o.ServiceName) is not { } key)
                {
                    continue; // host-only telemetry
                }
                var service = UpsertService(tenantId, key, o.FirstSeen, o.LastSeen);
                var environment = UpsertEnvironment(tenantId, o.Environment, o.FirstSeen, o.LastSeen);
                UpsertServiceEnvironment(tenantId, service, environment, o.FirstSeen, o.LastSeen);
                if (normalizer.Instance(o) is { } instanceKey)
                {
                    var known = _instances.ContainsKey((service.Id, environment.Id, instanceKey.Key));
                    var instance = UpsertInstance(tenantId, service, environment, instanceKey, o.FirstSeen, o.LastSeen);
                    if (o.LastSeen >= instance.LastSeen)
                    {
                        var version = normalizer.Value(o.ServiceVersion);
                        if (version.Length > 0)
                        {
                            // Only a new instance or one that changed version can announce a version: during a rollout the old
                            // instances keep reporting theirs, which must not read as going back.
                            if (!known || instance.ServiceVersion != version)
                            {
                                versions.Add((service, environment, version, KnownVersion(service, environment, instance), o.FirstSeen));
                            }
                            instance.ServiceVersion = version;
                        }
                        if (hosts.FirstOrDefault(r => r.Type == ResourceType.Host) is { } host) instance.HostResourceId = host.Id;
                    }
                }
            }

            foreach (var o in links)
            {
                if (normalizer.Service(o.SourceNamespace, o.SourceName) is not { } source
                    || normalizer.Service(o.TargetNamespace, o.TargetName) is not { } target)
                {
                    continue;
                }
                var relation = o.SourceKind == "Producer" ? DependencyRelation.PublishesTo : DependencyRelation.Calls;
                var environment = UpsertEnvironment(tenantId, o.Environment, o.FirstSeen, o.LastSeen);
                var from = UpsertService(tenantId, source, o.FirstSeen, o.LastSeen);
                var to = UpsertService(tenantId, target, o.FirstSeen, o.LastSeen);
                UpsertDependency(tenantId, environment, from, relation, to.Id, null, DependencyConfidence.Observed, o.Spans, o.FreshSpans, o.FirstSeen, o.LastSeen);
            }

            foreach (var o in uses)
            {
                if (normalizer.Service(o.ServiceNamespace, o.ServiceName) is not { } source
                    || normalizer.UsedResource(o) is not { } used)
                {
                    continue;
                }
                var relation = used.Type == ResourceType.Database ? DependencyRelation.UsesDatabase
                    : o.SpanKind == "Consumer" ? DependencyRelation.ConsumesFrom : DependencyRelation.PublishesTo;
                var environment = UpsertEnvironment(tenantId, o.Environment, o.FirstSeen, o.LastSeen);
                var from = UpsertService(tenantId, source, o.FirstSeen, o.LastSeen);
                var resource = UpsertResource(tenantId, used, o.FirstSeen, o.LastSeen);
                // Only the service reports the interaction; the destination or database never does (D3).
                UpsertDependency(tenantId, environment, from, relation, null, resource.Id, DependencyConfidence.Inferred, o.Spans, o.FreshSpans, o.FirstSeen, o.LastSeen);
            }
            RecordVersionChanges(tenantId, versions);

            await db.SaveChangesAsync(cancellationToken);

            var now = clock.GetUtcNow();
            var inactiveBefore = now - options.InstanceInactiveAfter;
            var deactivated = await db.Instances
                .Where(i => i.TenantId == tenantId && i.State == InstanceState.Active && i.LastSeen < inactiveBefore)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, InstanceState.Inactive), cancellationToken);
            var pruneBefore = now - options.InstanceRetention;
            var pruned = await db.Instances
                .Where(i => i.TenantId == tenantId && i.State == InstanceState.Inactive && i.LastSeen < pruneBefore)
                .ExecuteDeleteAsync(cancellationToken);

            return new ReconcileResult(_newServices, _newInstances, _newResources, _newDependencies, deactivated, pruned);
        }

        /// <summary>
        /// A service's first version isn't a change, since nothing says what came before. After that, a different version from a newer instance is
        /// recorded, with no actor: telemetry shows that it changed, not who changed it.
        /// </summary>
        private void RecordVersionChanges(Guid tenantId, List<(Service Service, DeploymentEnvironment Environment, string Version, string? Known, DateTimeOffset At)> versions)
        {
            foreach (var (service, environment, version, known, at) in versions.OrderBy(v => v.At))
            {
                var link = _serviceEnvironments[(service.Id, environment.Id)];
                if (link.CurrentVersionAt is { } latest && at < latest)
                {
                    continue; // an older instance reported late
                }
                var previous = link.CurrentVersion ?? known;
                link.CurrentVersion = version;
                link.CurrentVersionAt = at;
                if (previous is null || previous == version)
                {
                    continue;
                }
                var name = service.Namespace.Length > 0 ? $"{service.Namespace}/{service.Name}" : service.Name;
                db.ChangeEvents.Add(new ChangeEvent
                {
                    TenantId = tenantId, Kind = ChangeKind.Version, Source = ChangeEvent.TelemetrySource,
                    Summary = Bounded($"{name} moved from {previous} to {version} in {environment.Key}", 500),
                    ServiceId = service.Id, Environment = environment.Key, FromVersion = Bounded(previous, 128), ToVersion = Bounded(version, 128),
                    OccurredAt = at, RecordedAt = clock.GetUtcNow(),
                    ExternalKey = $"version:{service.Id:N}:{environment.Id:N}:{at.UtcTicks}",
                });
            }
        }

        /// <summary>
        /// The version already on record for a service, for when <see cref="ServiceEnvironment.CurrentVersion"/> isn't set yet: an install from before
        /// it existed knows its instances' versions, and shouldn't lose the first change after upgrading.
        /// </summary>
        private string? KnownVersion(Service service, DeploymentEnvironment environment, ServiceInstance instance)
            => instance.ServiceVersion ?? _instances.Values
                .Where(i => i.ServiceId == service.Id && i.EnvironmentId == environment.Id && i != instance && i.ServiceVersion != null)
                .OrderByDescending(i => i.FirstSeen).Select(i => i.ServiceVersion).FirstOrDefault();

        private static string Bounded(string text, int length) => text.Length <= length ? text : text[..length];

        // Loads the existing records the observations can match, bounded by the names they carry.
        private async Task LoadAsync(Guid tenantId, IReadOnlyList<ResourceObservation> resources,
            IReadOnlyList<ServiceLinkObservation> links, IReadOnlyList<ResourceUseObservation> uses, CancellationToken cancellationToken)
        {
            foreach (var e in await db.Environments.Where(e => e.TenantId == tenantId).ToListAsync(cancellationToken))
                _environments[e.Key] = e;
            foreach (var a in await db.Applications.Where(a => a.TenantId == tenantId).ToListAsync(cancellationToken))
                _applications[a.Key] = a;

            var names = resources.Select(o => normalizer.Service(o.ServiceNamespace, o.ServiceName))
                .Concat(links.Select(o => normalizer.Service(o.SourceNamespace, o.SourceName)))
                .Concat(links.Select(o => normalizer.Service(o.TargetNamespace, o.TargetName)))
                .Concat(uses.Select(o => normalizer.Service(o.ServiceNamespace, o.ServiceName)))
                .Where(k => k is not null).Select(k => k!.Value.Name).Distinct().ToList();
            foreach (var s in await db.Services.Where(s => s.TenantId == tenantId && names.Contains(s.Name)).ToListAsync(cancellationToken))
                _services[(s.Namespace, s.Name)] = s;

            var resourceKeys = resources.SelectMany(normalizer.Resources)
                .Concat(uses.Select(normalizer.UsedResource).Where(r => r is not null).Select(r => r!))
                .Select(r => r.Key).Distinct().ToList();
            foreach (var r in await db.Resources.Where(r => r.TenantId == tenantId && resourceKeys.Contains(r.Key)).ToListAsync(cancellationToken))
                _resources[(r.Type, r.Key)] = r;

            var serviceIds = _services.Values.Select(s => s.Id).ToList();
            foreach (var se in await db.ServiceEnvironments.Where(x => x.TenantId == tenantId && serviceIds.Contains(x.ServiceId)).ToListAsync(cancellationToken))
                _serviceEnvironments[(se.ServiceId, se.EnvironmentId)] = se;
            foreach (var i in await db.Instances.Where(i => i.TenantId == tenantId && serviceIds.Contains(i.ServiceId)).ToListAsync(cancellationToken))
                _instances[(i.ServiceId, i.EnvironmentId, i.InstanceKey)] = i;
            foreach (var d in await db.Dependencies.Where(d => d.TenantId == tenantId && serviceIds.Contains(d.SourceServiceId)).ToListAsync(cancellationToken))
                _dependencies[(d.EnvironmentId, d.SourceServiceId, d.Relation, d.TargetServiceId, d.TargetResourceId)] = d;
        }

        private DeploymentEnvironment UpsertEnvironment(Guid tenantId, string raw, DateTimeOffset first, DateTimeOffset last)
        {
            var key = normalizer.EnvironmentKey(raw);
            if (!_environments.TryGetValue(key, out var environment))
            {
                environment = new DeploymentEnvironment
                {
                    TenantId = tenantId, Key = key, Type = Normalizer.EnvironmentTypeOf(key), FirstSeen = first, LastSeen = last
                };
                db.Environments.Add(environment);
                _environments[key] = environment;
            }
            Seen(environment, first, last, (e, f) => e.FirstSeen = f, (e, l) => e.LastSeen = l, e => e.FirstSeen, e => e.LastSeen);
            return environment;
        }

        private Service UpsertService(Guid tenantId, (string Namespace, string Name) key, DateTimeOffset first, DateTimeOffset last)
        {
            if (!_services.TryGetValue(key, out var service))
            {
                var application = UpsertApplication(tenantId, Normalizer.ApplicationKey(key.Namespace), first, last);
                service = new Service
                {
                    TenantId = tenantId, Namespace = key.Namespace, Name = key.Name, ApplicationId = application.Id,
                    FirstSeen = first, LastSeen = last
                };
                db.Services.Add(service);
                _services[key] = service;
                _newServices++;
            }
            Seen(service, first, last, (s, f) => s.FirstSeen = f, (s, l) => s.LastSeen = l, s => s.FirstSeen, s => s.LastSeen);
            if (service.ApplicationId is { } applicationId
                && _applications.Values.FirstOrDefault(a => a.Id == applicationId) is { } app)
            {
                Seen(app, first, last, (a, f) => a.FirstSeen = f, (a, l) => a.LastSeen = l, a => a.FirstSeen, a => a.LastSeen);
            }
            return service;
        }

        private void UpsertServiceEnvironment(Guid tenantId, Service service, DeploymentEnvironment environment,
            DateTimeOffset first, DateTimeOffset last)
        {
            if (!_serviceEnvironments.TryGetValue((service.Id, environment.Id), out var link))
            {
                link = new ServiceEnvironment { TenantId = tenantId, ServiceId = service.Id, EnvironmentId = environment.Id, FirstSeen = first, LastSeen = last };
                db.ServiceEnvironments.Add(link);
                _serviceEnvironments[(service.Id, environment.Id)] = link;
            }
            Seen(link, first, last, (l, f) => l.FirstSeen = f, (l, v) => l.LastSeen = v, l => l.FirstSeen, l => l.LastSeen);
        }

        private Application UpsertApplication(Guid tenantId, string key, DateTimeOffset first, DateTimeOffset last)
        {
            if (!_applications.TryGetValue(key, out var application))
            {
                application = new Application { TenantId = tenantId, Key = key, DisplayName = key, FirstSeen = first, LastSeen = last };
                db.Applications.Add(application);
                _applications[key] = application;
            }
            return application;
        }

        private ServiceInstance UpsertInstance(Guid tenantId, Service service, DeploymentEnvironment environment,
            (string Key, InstanceKeySource Source) key, DateTimeOffset first, DateTimeOffset last)
        {
            if (!_instances.TryGetValue((service.Id, environment.Id, key.Key), out var instance))
            {
                instance = new ServiceInstance
                {
                    TenantId = tenantId, ServiceId = service.Id, EnvironmentId = environment.Id, InstanceKey = key.Key,
                    KeySource = key.Source, FirstSeen = first, LastSeen = last
                };
                db.Instances.Add(instance);
                _instances[(service.Id, environment.Id, key.Key)] = instance;
                _newInstances++;
            }
            Seen(instance, first, last, (i, f) => i.FirstSeen = f, (i, l) => i.LastSeen = l, i => i.FirstSeen, i => i.LastSeen);
            if (instance.LastSeen >= clock.GetUtcNow() - options.InstanceInactiveAfter)
            {
                instance.State = InstanceState.Active;
            }
            return instance;
        }

        private Resource UpsertResource(Guid tenantId, ResourceKey key, DateTimeOffset first, DateTimeOffset last)
        {
            if (!_resources.TryGetValue((key.Type, key.Key), out var resource))
            {
                resource = new Resource
                {
                    TenantId = tenantId, Type = key.Type, Key = key.Key, DisplayName = key.DisplayName,
                    Attributes = key.Attributes, FirstSeen = first, LastSeen = last
                };
                db.Resources.Add(resource);
                _resources[(key.Type, key.Key)] = resource;
                _newResources++;
            }
            else if (last > resource.LastSeen)
            {
                resource.DisplayName = key.DisplayName;
                resource.Attributes = Normalizer.Bounded(new Dictionary<string, string>(resource.Attributes.Concat(key.Attributes)
                    .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)));
            }
            Seen(resource, first, last, (r, f) => r.FirstSeen = f, (r, l) => r.LastSeen = l, r => r.FirstSeen, r => r.LastSeen);
            return resource;
        }

        private void UpsertDependency(Guid tenantId, DeploymentEnvironment environment, Service source, DependencyRelation relation,
            Guid? targetService, Guid? targetResource, DependencyConfidence confidence, long spans, long freshSpans, DateTimeOffset first, DateTimeOffset last)
        {
            var key = (environment.Id, source.Id, relation, targetService, targetResource);
            if (!_dependencies.TryGetValue(key, out var dependency))
            {
                dependency = new Dependency
                {
                    TenantId = tenantId, EnvironmentId = environment.Id, SourceServiceId = source.Id, Relation = relation,
                    TargetServiceId = targetService, TargetResourceId = targetResource, Confidence = confidence,
                    FirstSeen = first, LastSeen = last
                };
                db.Dependencies.Add(dependency);
                _dependencies[key] = dependency;
                _created.Add(key);
                _newDependencies++;
            }
            else if (confidence == DependencyConfidence.Observed)
            {
                dependency.Confidence = DependencyConfidence.Observed;
            }
            // Span counts accumulate per window. A window re-reads an overlap before the checkpoint (late telemetry), so
            // a known dependency adds only the spans after it; one created now adds them all, as no earlier pass saw any.
            dependency.SpanCount += _created.Contains(key) ? spans : freshSpans;
            Seen(dependency, first, last, (d, f) => d.FirstSeen = f, (d, l) => d.LastSeen = l, d => d.FirstSeen, d => d.LastSeen);
        }

        private static void Seen<T>(T item, DateTimeOffset first, DateTimeOffset last, Action<T, DateTimeOffset> setFirst,
            Action<T, DateTimeOffset> setLast, Func<T, DateTimeOffset> getFirst, Func<T, DateTimeOffset> getLast)
        {
            if (first < getFirst(item)) setFirst(item, first);
            if (last > getLast(item)) setLast(item, last);
        }
    }
}
