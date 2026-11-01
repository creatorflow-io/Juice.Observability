using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Health
{
    public sealed record HealthRunResult(DateTimeOffset EvidenceTo, int Subjects, int Transitions);

    /// <summary>
    /// One evaluation: reads the evidence window from ClickHouse, evaluates instances, then services per environment
    /// (critical dependencies use the targets' states from the same pass), then applications, then hosts, and writes the
    /// snapshots and any transitions in one transaction. When ClickHouse cannot be read the pass fails and states stay
    /// as they were (shown stale by age), never turned Offline by missing evidence.
    /// </summary>
    public sealed class HealthRunner(IServiceScopeFactory scopes, ClickHouseTelemetrySource clickHouse, IOptions<HealthPolicy> options,
        IOptions<DiscoveryOptions> discovery, IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<HealthRunner> logger)
    {
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Evaluations = Meter.CreateCounter<long>("juice.observability.health.evaluations", "{run}",
            "Health evaluation passes, by outcome");
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("juice.observability.health.duration", "s",
            "Duration of a health evaluation pass");
        private static readonly Histogram<double> EvidenceAge = Meter.CreateHistogram<double>("juice.observability.health.evidence_age", "s",
            "Age of the newest evidence behind a fresh subject's state when it is written (evidence-to-visible)");
        private static readonly Counter<long> TransitionCount = Meter.CreateCounter<long>("juice.observability.health.transitions", "{transition}",
            "Health state transitions, by subject type and new state");

        private const string RequestsQuery = $"""
            SELECT ResourceAttributes['service.namespace'] AS ns, ServiceName AS svc,
                   ResourceAttributes['deployment.environment.name'] AS env, ResourceAttributes['service.instance.id'] AS inst,
                   ResourceAttributes['host.name'] AS host_name, ResourceAttributes['process.pid'] AS pid,
                   countIf(SpanKind IN ('Server', 'Consumer')) AS requests,
                   countIf(SpanKind IN ('Server', 'Consumer') AND StatusCode = 'Error') AS errors
            FROM otel.otel_traces
            WHERE {ClickHouseTelemetrySource.Tenant} AND Timestamp >= {ClickHouseTelemetrySource.From}
              AND Timestamp < {ClickHouseTelemetrySource.To} AND ServiceName != ''
            GROUP BY ns, svc, env, inst, host_name, pid
            LIMIT {ClickHouseTelemetrySource.Limit}
            """;

        private const string MemoryQuery = $"""
            SELECT ResourceAttributes['host.id'] AS host_id, ResourceAttributes['host.name'] AS host_name,
                   Attributes['state'] AS state, argMax(Value, TimeUnix) AS bytes
            FROM otel.otel_metrics_sum
            WHERE {ClickHouseTelemetrySource.Tenant} AND TimeUnix >= {ClickHouseTelemetrySource.From}
              AND TimeUnix < {ClickHouseTelemetrySource.To} AND MetricName = 'system.memory.usage'
            GROUP BY host_id, host_name, state
            LIMIT {ClickHouseTelemetrySource.Limit}
            """;

        public async Task<HealthRunResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await RunAsync(cancellationToken);
                Evaluations.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                return result;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Evaluations.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                throw;
            }
            finally
            {
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }

        private async Task<HealthRunResult> RunAsync(CancellationToken cancellationToken)
        {
            var policy = options.Value;
            var version = policy.Version;
            var evaluator = new HealthEvaluator(policy);
            var normalizer = new Normalizer(discovery.Value.Aliases);
            var now = clock.GetUtcNow();
            var to = new DateTimeOffset((now - policy.Lag).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            var from = to - policy.Window;

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Key == platform.Value.TenantKey, cancellationToken);

            // ─── evidence (read first: if ClickHouse is down nothing is written) ───
            var window = new TelemetryWindow(tenant.Key, from, to, discovery.Value.RowLimit);
            var liveness = await clickHouse.ReadResourcesAsync(window, cancellationToken);
            var requests = await clickHouse.QueryAsync(RequestsQuery, window, r => (Observation: new ResourceObservation
            {
                ServiceNamespace = r.GetString(0), ServiceName = r.GetString(1), Environment = r.GetString(2),
                InstanceId = r.GetString(3), HostName = r.GetString(4), ProcessId = r.GetString(5),
            }, Requests: Convert.ToInt64(r.GetValue(6)), Errors: Convert.ToInt64(r.GetValue(7))), cancellationToken);
            var memory = await clickHouse.QueryAsync(MemoryQuery, window, r => (HostId: r.GetString(0), HostName: r.GetString(1),
                State: r.GetString(2), Bytes: Convert.ToDouble(r.GetValue(3))), cancellationToken);

            // Evidence keyed like discovery's records: (namespace, name, environment, instance key).
            var instanceEvidence = new Dictionary<(string, string, string, string), Evidence>();
            var serviceEvidence = new Dictionary<(string, string, string), Evidence>();
            var hostSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            void Add((string, string)? service, ResourceObservation o, DateTimeOffset? seen, long requestCount, long errorCount)
            {
                if (service is not { } s) return;
                var env = normalizer.EnvironmentKey(o.Environment);
                serviceEvidence[(s.Item1, s.Item2, env)] = Merge(serviceEvidence.GetValueOrDefault((s.Item1, s.Item2, env)), seen, requestCount, errorCount);
                if (normalizer.Instance(o) is { } instance)
                {
                    var key = (s.Item1, s.Item2, env, instance.Key);
                    instanceEvidence[key] = Merge(instanceEvidence.GetValueOrDefault(key), seen, requestCount, errorCount);
                }
            }
            foreach (var o in liveness)
            {
                Add(normalizer.Service(o.ServiceNamespace, o.ServiceName), o, o.LastSeen, 0, 0);
                foreach (var host in normalizer.Resources(o).Where(r => r.Type == ResourceType.Host))
                {
                    hostSeen[host.Key] = hostSeen.TryGetValue(host.Key, out var seen) && seen > o.LastSeen ? seen : o.LastSeen;
                }
            }
            foreach (var (o, requestCount, errorCount) in requests)
            {
                Add(normalizer.Service(o.ServiceNamespace, o.ServiceName), o, null, requestCount, errorCount);
            }
            var memoryByHost = memory
                .GroupBy(m => normalizer.Value(m.HostId).Length > 0 ? normalizer.Value(m.HostId).ToLowerInvariant() : normalizer.Value(m.HostName).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Sum(m => m.Bytes) is > 0 and var total
                    ? g.Where(m => m.State == "used").Sum(m => m.Bytes) / total : (double?)null);

            // ─── subjects ───
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenant.Id).ToDictionaryAsync(e => e.Id, cancellationToken);
            var services = await db.Services.AsNoTracking().Where(s => s.TenantId == tenant.Id).ToDictionaryAsync(s => s.Id, cancellationToken);
            var instances = await db.Instances.AsNoTracking()
                .Where(i => i.TenantId == tenant.Id && i.State == InstanceState.Active).ToListAsync(cancellationToken);
            var hosts = await db.Resources.AsNoTracking().Where(r => r.TenantId == tenant.Id && r.Type == ResourceType.Host).ToListAsync(cancellationToken);

            var results = new List<(HealthSubject Type, Guid Id, Guid? Environment, Assessment Assessment)>();

            var instanceStates = new Dictionary<(Guid Service, Guid Environment), List<Assessment>>();
            foreach (var instance in instances)
            {
                var service = services[instance.ServiceId];
                var env = environments[instance.EnvironmentId].Key;
                var assessment = evaluator.Instance(now, instance.LastSeen,
                    instanceEvidence.GetValueOrDefault((service.Namespace, service.Name, env, instance.InstanceKey)));
                results.Add((HealthSubject.Instance, instance.Id, instance.EnvironmentId, assessment));
                instanceStates.TryAdd((instance.ServiceId, instance.EnvironmentId), []);
                instanceStates[(instance.ServiceId, instance.EnvironmentId)].Add(assessment);
            }

            // Services per environment they were seen in (discovery's service_environments), plus any with evidence in
            // this window that discovery has not recorded yet.
            var seenIn = await db.ServiceEnvironments.AsNoTracking().Where(x => x.TenantId == tenant.Id)
                .ToDictionaryAsync(x => (Service: x.ServiceId, Environment: x.EnvironmentId), x => x.LastSeen, cancellationToken);
            var serviceKeys = seenIn.Keys.Concat(instanceStates.Keys).ToHashSet();
            var envByKey = environments.Values.ToDictionary(e => e.Key);
            var serviceByName = services.Values.ToDictionary(s => (s.Namespace, s.Name));
            foreach (var ((ns, name, env), _) in serviceEvidence)
            {
                if (serviceByName.TryGetValue((ns, name), out var s) && envByKey.TryGetValue(env, out var e)) serviceKeys.Add((s.Id, e.Id));
            }

            Evidence ServiceEvidence((Guid Service, Guid Environment) key)
            {
                var s = services[key.Service];
                return serviceEvidence.GetValueOrDefault((s.Namespace, s.Name, environments[key.Environment].Key)) ?? new Evidence(null, 0, 0);
            }
            DateTimeOffset? SeenBefore((Guid, Guid) key) => seenIn.TryGetValue(key, out var seen) ? seen : null;
            var firstPass = serviceKeys.ToDictionary(k => k, k => evaluator.Service(now, SeenBefore(k), instanceStates.GetValueOrDefault(k) ?? [], ServiceEvidence(k), []));
            var serviceStates = new Dictionary<(Guid, Guid), Assessment>();
            foreach (var key in serviceKeys)
            {
                var dependencies = policy.CriticalDependencies
                    .Where(d => Matches(services[key.Service], d.Service))
                    .Select(d => (d.DependsOn, Target: services.Values.FirstOrDefault(s => Matches(s, d.DependsOn))))
                    .Select(d => (d.DependsOn, d.Target is not null && firstPass.TryGetValue((d.Target.Id, key.Environment), out var t) ? t.State : HealthState.Unknown))
                    .ToList();
                var assessment = dependencies.Count == 0 ? firstPass[key]
                    : evaluator.Service(now, SeenBefore(key), instanceStates.GetValueOrDefault(key) ?? [], ServiceEvidence(key), dependencies);
                serviceStates[key] = assessment;
                results.Add((HealthSubject.Service, key.Service, key.Environment, assessment));
            }

            // A service with no environment at all (created before discovery recorded one, or by hand) is Unknown,
            // recorded without an environment, rather than left without a state.
            var evaluated = serviceKeys.Select(k => k.Service).ToHashSet();
            foreach (var service in services.Values.Where(s => !evaluated.Contains(s.Id)))
            {
                results.Add((HealthSubject.Service, service.Id, null, Assessment.Of(HealthState.Unknown, null, "never_seen")));
            }

            foreach (var group in serviceStates.GroupBy(kv => (services[kv.Key.Item1].ApplicationId, Environment: kv.Key.Item2))
                         .Where(g => g.Key.ApplicationId is not null))
            {
                var children = group.Select(kv =>
                {
                    var name = Name(services[kv.Key.Item1]);
                    var weight = policy.ServiceWeights.FirstOrDefault(w => w.Service == name);
                    return new ChildAssessment(name, kv.Value, weight?.Weight ?? 1, weight?.Critical ?? false);
                }).ToList();
                results.Add((HealthSubject.Application, group.Key.ApplicationId!.Value, group.Key.Environment, evaluator.Application(children)));
            }

            foreach (var host in hosts)
            {
                results.Add((HealthSubject.Host, host.Id, null, evaluator.Host(now, host.LastSeen,
                    hostSeen.TryGetValue(host.Key, out var seen) ? seen : null, memoryByHost.GetValueOrDefault(host.Key))));
            }

            var transitions = await PersistAsync(db, tenant.Id, results, version, now, from, to, cancellationToken);
            return new HealthRunResult(to, results.Count, transitions);
        }

        private async Task<int> PersistAsync(PlatformDbContext db, Guid tenantId,
            List<(HealthSubject Type, Guid Id, Guid? Environment, Assessment Assessment)> results, string version,
            DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var snapshots = await db.HealthSnapshots.Where(h => h.TenantId == tenantId)
                .ToDictionaryAsync(h => (h.SubjectType, h.SubjectId, h.EnvironmentId), cancellationToken);
            // Transitions already written for this exact evidence window and policy: a replay must not add them again.
            var recorded = (await db.HealthTransitions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.EvidenceTo == to && t.PolicyVersion == version)
                .Select(t => new { t.SubjectType, t.SubjectId, t.EnvironmentId }).ToListAsync(cancellationToken))
                .Select(t => (t.SubjectType, t.SubjectId, t.EnvironmentId)).ToHashSet();
            var written = 0;
            foreach (var (type, id, environment, assessment) in results)
            {
                var key = (type, id, environment);
                snapshots.TryGetValue(key, out var snapshot);
                var changed = snapshot is null || snapshot.State != assessment.State;
                if (changed && !recorded.Contains(key))
                {
                    db.HealthTransitions.Add(new HealthTransition
                    {
                        TenantId = tenantId, SubjectType = type, SubjectId = id, EnvironmentId = environment,
                        FromState = snapshot?.State, ToState = assessment.State, ReasonCodes = [.. assessment.Reasons],
                        PolicyVersion = version, EvaluatedAt = now, EvidenceFrom = from, EvidenceTo = to,
                    });
                    written++;
                    TransitionCount.Add(1, new KeyValuePair<string, object?>("subject_type", type.ToString()),
                        new KeyValuePair<string, object?>("state", assessment.State.ToString()));
                }
                if (snapshot is null)
                {
                    snapshot = new HealthSnapshot { TenantId = tenantId, SubjectType = type, SubjectId = id, EnvironmentId = environment, PolicyVersion = version, Since = now };
                    db.HealthSnapshots.Add(snapshot);
                }
                else if (changed)
                {
                    snapshot.Since = now;
                }
                snapshot.State = assessment.State;
                snapshot.ReasonCodes = [.. assessment.Reasons];
                snapshot.PolicyVersion = version;
                snapshot.EvaluatedAt = now;
                snapshot.EvidenceFrom = from;
                snapshot.EvidenceTo = to;
                snapshot.LastEvidenceAt = assessment.LastEvidence;
                if (assessment.LastEvidence is { } last && assessment.State is not (HealthState.Offline or HealthState.Unknown))
                {
                    EvidenceAge.Record((now - last).TotalSeconds, new KeyValuePair<string, object?>("subject_type", type.ToString()));
                }
            }
            // A service now evaluated per environment drops its earlier "never seen" state without an environment.
            var withEnvironment = results.Where(r => r.Type == HealthSubject.Service && r.Environment is not null).Select(r => r.Id).ToHashSet();
            db.HealthSnapshots.RemoveRange(snapshots.Values.Where(h =>
                h.SubjectType == HealthSubject.Service && h.EnvironmentId is null && withEnvironment.Contains(h.SubjectId)));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (written > 0)
            {
                logger.LogInformation("Health: {Transitions} transitions over {Subjects} subjects (evidence to {EvidenceTo}, policy {Policy})",
                    written, results.Count, to, version);
            }
            return written;
        }

        private static Evidence Merge(Evidence? evidence, DateTimeOffset? seen, long requests, long errors)
        {
            var last = evidence?.LastSeen;
            if (seen is { } s && (last is null || s > last)) last = s;
            return new Evidence(last, (evidence?.Requests ?? 0) + requests, (evidence?.Errors ?? 0) + errors);
        }

        /// <summary>The name policies and the API use for a service: <c>namespace/name</c>, or <c>name</c> without a namespace.</summary>
        public static string Name(Service s) => s.Namespace.Length == 0 ? s.Name : $"{s.Namespace}/{s.Name}";

        private static bool Matches(Service s, string name) => string.Equals(Name(s), name, StringComparison.Ordinal);
    }

    /// <summary>Runs <see cref="HealthRunner"/> every <see cref="HealthPolicy.Interval"/>.</summary>
    public sealed class HealthWorker(HealthRunner runner, IOptions<HealthPolicy> options, ILogger<HealthWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    await runner.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Health evaluation failed; states are kept and age until the next pass");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    public static class HealthServiceCollectionExtensions
    {
        /// <summary>Health evaluation (requires <c>AddPlatformDiscovery</c>); the worker runs when <c>Health:Enabled</c> is true.</summary>
        public static IServiceCollection AddPlatformHealth(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<HealthPolicy>(configuration.GetSection(HealthPolicy.Section));
            services.AddSingleton<HealthRunner>();
            if (configuration.GetSection(HealthPolicy.Section).GetValue(nameof(HealthPolicy.Enabled), true))
            {
                services.AddHostedService<HealthWorker>();
            }
            return services;
        }
    }
}
