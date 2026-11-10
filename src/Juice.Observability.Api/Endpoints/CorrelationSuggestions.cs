using System.Globalization;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>A direct dependency between the two alerts' services: <c>Source</c> depends on <c>Target</c>.</summary>
    public sealed record DependencyFact(string Source, string Target, DependencyRelation Relation, DependencyConfidence Confidence);

    public sealed record ChangeFact(DateTimeOffset At, string Summary);

    /// <summary>What is known about a pair of firing alerts, gathered by <see cref="CorrelationSuggestions"/> and scored by <see cref="CorrelationScorer"/>.</summary>
    public sealed record PairFacts(TimeSpan FiredApart, DependencyFact? Dependency, string? SharedHost, IReadOnlyList<ChangeFact> Changes,
        IReadOnlyList<(string Name, HealthState State)> UnhealthySharedDependencies);

    /// <summary>
    /// The weights of the Phase 9 plan (R9). A starting point, not a tuned model: every decision keeps the score and evidence it was made on, and
    /// <see cref="WeightsVersion"/>, so they can be tuned against real decisions later. Pure, so each weight is unit-tested.
    /// </summary>
    public static class CorrelationScorer
    {
        public const string WeightsVersion = "phase9-1";
        public const double Threshold = 0.5;
        public const double DependencyWeight = 0.40;
        public const double SameHostWeight = 0.30;
        public const double ChangeWeight = 0.30;
        public const double TimingWeight = 0.20;
        public const double UnhealthyDependencyWeight = 0.20;
        /// <summary>Alerts firing further apart than this aren't candidates.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
        /// <summary>Within this, timing adds its full weight; then it slides to nothing at <see cref="Window"/>.</summary>
        public static readonly TimeSpan Together = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan ChangeLookback = TimeSpan.FromMinutes(30);
        public const int MaxChangesNamed = 3;

        /// <summary>The score, at most 1, and every piece of evidence with what it added. Weights are rounded to hundredths, so the sum is what's shown.</summary>
        public static (double Score, List<CorrelationEvidence> Evidence) Score(PairFacts facts)
        {
            var evidence = new List<CorrelationEvidence>();
            // Observed only, as the plan's table says: a dependency only the caller reported may be a name it gave something else.
            if (facts.Dependency is { Confidence: DependencyConfidence.Observed } d)
            {
                evidence.Add(new("dependency", $"{d.Source} depends directly on {d.Target} ({Relation(d.Relation)}, seen from both sides in traces)", DependencyWeight));
            }
            if (facts.SharedHost is { } host)
            {
                evidence.Add(new("same-host", $"Both on host {host}", SameHostWeight));
            }
            if (facts.Changes.Count > 0)
            {
                var named = string.Join("; ", facts.Changes.OrderByDescending(c => c.At).Take(MaxChangesNamed).Select(c => c.Summary));
                var more = facts.Changes.Count > MaxChangesNamed ? $" and {facts.Changes.Count - MaxChangesNamed} more" : "";
                evidence.Add(new("recent-change", $"{Count(facts.Changes.Count, "change")} from {ChangeLookback.TotalMinutes:0} minutes before the first breach to the second: {named}{more}", ChangeWeight));
            }
            var apart = facts.FiredApart.Duration();
            if (apart < Window)
            {
                var share = apart <= Together ? 1 : (Window - apart) / (Window - Together);
                var weight = Math.Round(TimingWeight * share, 2);
                if (weight > 0) evidence.Add(new("fired-together", $"Fired {CorrelationRanker.Describe(apart)} apart", weight));
            }
            if (facts.UnhealthySharedDependencies.Count > 0)
            {
                var names = string.Join(", ", facts.UnhealthySharedDependencies.Select(u => $"{u.Name} ({u.State})"));
                evidence.Add(new("unhealthy-shared-dependency", $"Both depend on {names}, which {(facts.UnhealthySharedDependencies.Count == 1 ? "is" : "are")} unhealthy now", UnhealthyDependencyWeight));
            }
            return (Math.Min(1, Math.Round(evidence.Sum(e => e.Weight), 2)), evidence);
        }

        /// <summary>Hundredths, so 0.3 + 0.2 reads as 0.5 and is suggested.</summary>
        public static bool Suggested(double score) => score >= Threshold - 0.001;

        private static string Relation(DependencyRelation relation) => relation switch
        {
            DependencyRelation.Calls => "calls",
            DependencyRelation.PublishesTo => "publishes to",
            DependencyRelation.ConsumesFrom => "consumes from",
            _ => "uses",
        };

        private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    /// <summary>A pair worth an operator's look, and the unresolved incident one of them is already in (accepting adds the other to it).</summary>
    internal sealed record Suggestion(Alert Alert, Alert Other, double Score, List<CorrelationEvidence> Evidence, Guid? IncidentId);

    /// <summary>
    /// Finds and scores co-firing alerts (R9). Computed when asked, from what the platform already keeps (alerts, the inventory, dependencies, health
    /// and changes), so it adds no worker or stateful part. Only decisions are stored (<see cref="CorrelationDecision"/>).
    /// A pair is a candidate when both fire, within <see cref="CorrelationScorer.Window"/> of each other, in the same environment (a host alert has
    /// none, so it pairs with any), not already decided, and not both in unresolved incidents.
    /// </summary>
    internal static class CorrelationSuggestions
    {
        public const string Relation = "correlation";
        public const string Note = "Suggested because these alerts fired close together and share what's listed. That is correlation, not a proven cause. Nothing is merged until someone accepts.";
        public const int MaxItems = 20;
        /// <summary>
        /// At most this many other firing alerts are scored, those firing closest to any focus alert first, after what can't pair is left out. In an
        /// alert storm the page stays quick, and a pair that far down the list fired too far apart to score much on timing anyway.
        /// </summary>
        public const int MaxCandidates = 500;
        /// <summary>A dependency counts only if traces showed it this recently before the pair fired.</summary>
        public static readonly TimeSpan DependencySeenWithin = TimeSpan.FromDays(7);

        /// <summary>
        /// The suggestions between <paramref name="focus"/> and the other firing alerts, best first. With <paramref name="onePerOther"/>, each other alert
        /// once, with its best pair (an incident page: accepting any pair adds it to the same incident).
        /// </summary>
        public static async Task<List<Suggestion>> ForAsync(PlatformDbContext db, Guid tenantId, IReadOnlyList<Alert> focus, CancellationToken ct, bool onePerOther = false)
        {
            var firing = focus.Where(Firing).ToList();
            if (firing.Count == 0) return [];
            var from = firing.Min(a => a.FiredAt!.Value) - CorrelationScorer.Window;
            var to = firing.Max(a => a.FiredAt!.Value) + CorrelationScorer.Window;
            // A host alert (no environment) pairs with any environment, so any focus host alert opens them all.
            var anyEnvironment = firing.Any(a => a.EnvironmentId is null);
            var environments = firing.Where(a => a.EnvironmentId is not null).Select(a => a.EnvironmentId!.Value).Distinct().ToList();
            var focusIds = firing.Select(a => a.Id).ToHashSet();
            var focusList = focusIds.ToList();

            // First narrow rows only, so what can't pair (the focus itself, decided pairs, pairs already grouped) is dropped before the cap, not after.
            var window = await db.Alerts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.State == AlertState.Firing && a.FiredAt >= from && a.FiredAt <= to && !focusList.Contains(a.Id)
                    && (anyEnvironment || a.EnvironmentId == null || environments.Contains(a.EnvironmentId.Value)))
                .Select(a => new { a.Id, FiredAt = a.FiredAt!.Value, a.EnvironmentId }).ToListAsync(ct);
            if (window.Count == 0) return [];
            var decided = (await db.CorrelationDecisions.AsNoTracking()
                    .Where(d => d.TenantId == tenantId && (focusList.Contains(d.FirstAlertId) || focusList.Contains(d.SecondAlertId)))
                    .Select(d => new { d.FirstAlertId, d.SecondAlertId }).ToListAsync(ct))
                .Select(d => (d.FirstAlertId, d.SecondAlertId)).ToHashSet();
            var incidents = await OpenIncidentsAsync(db, [.. focusIds, .. window.Select(o => o.Id)], ct);
            bool Open(Alert f, Guid other, DateTimeOffset firedAt, Guid? environment)
                => (f.FiredAt!.Value - firedAt).Duration() <= CorrelationScorer.Window
                    && (f.EnvironmentId is null || environment is null || f.EnvironmentId == environment)
                    && !decided.Contains(CorrelationDecision.Pair(f.Id, other))
                    && !(incidents.ContainsKey(f.Id) && incidents.ContainsKey(other));
            // Closest to any focus alert first: on an incident page its alerts may have fired far apart.
            var kept = window
                .Select(o => (o.Id, Partners: firing.Where(f => Open(f, o.Id, o.FiredAt, o.EnvironmentId)).ToList(), o.FiredAt))
                .Where(o => o.Partners.Count > 0)
                .OrderBy(o => o.Partners.Min(f => (f.FiredAt!.Value - o.FiredAt).Duration())).ThenBy(o => o.Id)
                .Take(MaxCandidates).ToList();
            if (kept.Count == 0) return [];
            var keptIds = kept.Select(o => o.Id).ToList();
            var others = await db.Alerts.AsNoTracking().Where(a => keptIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            var open = kept.Where(o => others.ContainsKey(o.Id))
                .SelectMany(o => o.Partners.Select(f => (f, others[o.Id]))).Where(p => Candidates(p.f, p.Item2)).ToList();
            var scored = (await ScoreAsync(db, tenantId, open, incidents, ct)).Where(s => CorrelationScorer.Suggested(s.Score))
                .OrderByDescending(s => s.Score).ThenBy(s => s.Other.FiredAt).ThenBy(s => s.Other.Id);
            // De-duplicated before the cap, so duplicates don't crowd out other alerts.
            return (onePerOther ? scored.DistinctBy(s => s.Other.Id) : scored).Take(MaxItems).ToList();
        }

        public static bool Firing(Alert a) => a.State == AlertState.Firing && a.FiredAt is not null;

        /// <summary>Both firing, close enough in time, and in one environment unless one is a host's.</summary>
        public static bool Candidates(Alert a, Alert b)
            => Firing(a) && Firing(b) && (a.FiredAt!.Value - b.FiredAt!.Value).Duration() <= CorrelationScorer.Window
                && (a.EnvironmentId is null || b.EnvironmentId is null || a.EnvironmentId == b.EnvironmentId);

        /// <summary>The unresolved incident each alert is in, if any.</summary>
        public static async Task<Dictionary<Guid, Guid>> OpenIncidentsAsync(PlatformDbContext db, IReadOnlyCollection<Guid> alertIds, CancellationToken ct)
            => (await db.IncidentAlerts.AsNoTracking().Where(l => alertIds.Contains(l.AlertId))
                    .Join(db.Incidents.AsNoTracking().Where(i => i.State != IncidentState.Resolved), l => l.IncidentId, i => i.Id, (l, i) => new { l.AlertId, IncidentId = i.Id })
                    .ToListAsync(ct))
                .GroupBy(x => x.AlertId).ToDictionary(g => g.Key, g => g.Min(x => x.IncidentId));

        /// <summary>Gathers each pair's facts in a few queries for all pairs, then scores them.</summary>
        public static async Task<List<Suggestion>> ScoreAsync(PlatformDbContext db, Guid tenantId, IReadOnlyList<(Alert A, Alert B)> pairs,
            IReadOnlyDictionary<Guid, Guid> incidents, CancellationToken ct)
        {
            if (pairs.Count == 0) return [];
            var alerts = pairs.SelectMany(p => new[] { p.A, p.B }).DistinctBy(a => a.Id).ToList();
            var services = alerts.Where(a => a.SubjectType == HealthSubject.Service && a.SubjectId is not null).Select(a => a.SubjectId!.Value).Distinct().ToList();

            // Where each service runs, per environment.
            var placements = await db.Instances.AsNoTracking()
                .Where(i => i.TenantId == tenantId && services.Contains(i.ServiceId) && i.State == InstanceState.Active && i.HostResourceId != null)
                .Select(i => new { i.ServiceId, i.EnvironmentId, Host = i.HostResourceId!.Value }).Distinct().ToListAsync(ct);
            // Only dependencies traces still show: discovery keeps one it saw once, long ago, and "depends directly" would no longer be true.
            var seenSince = pairs.Min(p => Min(p.A.FiredAt!.Value, p.B.FiredAt!.Value)) - DependencySeenWithin;
            var dependencies = await db.Dependencies.AsNoTracking()
                .Where(d => d.TenantId == tenantId && services.Contains(d.SourceServiceId) && d.LastSeen >= seenSince).ToListAsync(ct);

            var serviceIds = services.Concat(dependencies.Where(d => d.TargetServiceId != null).Select(d => d.TargetServiceId!.Value)).Distinct().ToList();
            var resourceIds = alerts.Where(a => a.SubjectType == HealthSubject.Host && a.SubjectId is not null).Select(a => a.SubjectId!.Value)
                .Concat(placements.Select(p => p.Host)).Concat(dependencies.Where(d => d.TargetResourceId != null).Select(d => d.TargetResourceId!.Value)).Distinct().ToList();
            var serviceNames = await db.Services.AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var resourceNames = await db.Resources.AsNoTracking().Where(r => resourceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.DisplayName, ct);
            var environmentKeys = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Id, e => e.Key, ct);

            // Current health of what they depend on: services per environment, hosts on their own. Other resources have no health.
            var targetServices = dependencies.Where(d => d.TargetServiceId != null).Select(d => d.TargetServiceId!.Value).Distinct().ToList();
            var targetResources = dependencies.Where(d => d.TargetResourceId != null).Select(d => d.TargetResourceId!.Value).Distinct().ToList();
            var health = await db.HealthSnapshots.AsNoTracking()
                .Where(h => h.TenantId == tenantId && CorrelationRanker.Unhealthy.Contains(h.State)
                    && ((h.SubjectType == HealthSubject.Service && h.EnvironmentId != null && targetServices.Contains(h.SubjectId))
                        || (h.SubjectType == HealthSubject.Host && targetResources.Contains(h.SubjectId))))
                .Select(h => new { h.SubjectType, h.SubjectId, h.EnvironmentId, h.State }).ToListAsync(ct);

            // Changes on either alert's subject or on what both depend on, in the half hour before the earlier breach up to the later one.
            var changeFrom = pairs.Min(p => Min(p.A.BreachStartedAt, p.B.BreachStartedAt)) - CorrelationScorer.ChangeLookback;
            var changeTo = pairs.Max(p => Max(p.A.BreachStartedAt, p.B.BreachStartedAt));
            var changes = await db.ChangeEvents.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.OccurredAt >= changeFrom && c.OccurredAt <= changeTo
                    && ((c.ServiceId != null && serviceIds.Contains(c.ServiceId.Value)) || (c.ResourceId != null && resourceIds.Contains(c.ResourceId.Value))))
                .Select(c => new { c.Id, c.ServiceId, c.ResourceId, c.Environment, c.OccurredAt, c.Summary }).ToListAsync(ct);

            // Indexed once, so each pair looks up what it needs instead of scanning every list.
            var hostsByService = placements.ToLookup(p => (p.ServiceId, p.EnvironmentId), p => p.Host);
            var dependenciesBySource = dependencies.ToLookup(d => (d.SourceServiceId, d.EnvironmentId));
            var unhealthyNow = health.GroupBy(h => (h.SubjectType, h.SubjectId, h.EnvironmentId)).ToDictionary(g => g.Key, g => g.First().State);
            var changesByService = changes.Where(c => c.ServiceId != null).ToLookup(c => c.ServiceId!.Value);
            var changesByResource = changes.Where(c => c.ResourceId != null).ToLookup(c => c.ResourceId!.Value);

            Guid? ServiceOf(Alert a) => a.SubjectType == HealthSubject.Service ? a.SubjectId : null;
            HashSet<Guid> HostsOf(Alert a) => a.SubjectType == HealthSubject.Host
                ? a.SubjectId is { } h ? [h] : []
                : a.SubjectId is { } s && a.EnvironmentId is { } e ? hostsByService[(s, e)].ToHashSet() : [];
            IEnumerable<Dependency> From(Guid? service, Guid? environment, DateTimeOffset since)
                => service is { } s && environment is { } e ? dependenciesBySource[(s, e)].Where(d => d.LastSeen >= since) : [];
            HashSet<(bool Service, Guid Id)> TargetsOf(Guid? service, Guid? environment, DateTimeOffset since)
                => From(service, environment, since).Select(d => d.TargetServiceId is { } s ? (true, s) : (false, d.TargetResourceId!.Value)).ToHashSet();
            string Name((bool Service, Guid Id) t) => t.Service ? serviceNames.GetValueOrDefault(t.Id, "a service") : resourceNames.GetValueOrDefault(t.Id, "a resource");

            var result = new List<Suggestion>();
            foreach (var (a, b) in pairs)
            {
                var environment = a.EnvironmentId ?? b.EnvironmentId;
                var environmentKey = environment is { } e ? environmentKeys.GetValueOrDefault(e) : null;
                var (serviceA, serviceB) = (ServiceOf(a), ServiceOf(b));
                var since = Min(a.FiredAt!.Value, b.FiredAt!.Value) - DependencySeenWithin;

                DependencyFact? dependency = null;
                if (serviceA is { } sa && serviceB is { } sb && sa != sb && environment is not null)
                {
                    var direct = From(sa, environment, since).Where(d => d.TargetServiceId == sb).Concat(From(sb, environment, since).Where(d => d.TargetServiceId == sa))
                        .OrderBy(d => d.Confidence == DependencyConfidence.Observed ? 0 : 1).ThenBy(d => d.Id).FirstOrDefault();
                    if (direct is not null)
                    {
                        dependency = new(serviceNames.GetValueOrDefault(direct.SourceServiceId, "a service"), serviceNames.GetValueOrDefault(direct.TargetServiceId!.Value, "a service"),
                            direct.Relation, direct.Confidence);
                    }
                }

                var sharedHost = HostsOf(a).Intersect(HostsOf(b)).Select(h => resourceNames.GetValueOrDefault(h, "a host")).Order(StringComparer.Ordinal).FirstOrDefault();

                var shared = TargetsOf(serviceA, environment, since);
                shared.IntersectWith(TargetsOf(serviceB, environment, since));
                shared.RemoveWhere(t => t.Service && (t.Id == serviceA || t.Id == serviceB));
                var unhealthy = shared
                    .Select(t => (Target: t, Found: unhealthyNow.TryGetValue(t.Service ? (HealthSubject.Service, t.Id, environment) : (HealthSubject.Host, t.Id, null), out var state), State: state))
                    .Where(x => x.Found).Select(x => (Name(x.Target), x.State)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();

                var onServices = new HashSet<Guid>(shared.Where(t => t.Service).Select(t => t.Id));
                if (serviceA is { } ca) onServices.Add(ca);
                if (serviceB is { } cb) onServices.Add(cb);
                var onResources = new HashSet<Guid>(shared.Where(t => !t.Service).Select(t => t.Id));
                if (a.SubjectType == HealthSubject.Host && a.SubjectId is { } ha) onResources.Add(ha);
                if (b.SubjectType == HealthSubject.Host && b.SubjectId is { } hb) onResources.Add(hb);
                var (from, to) = (Min(a.BreachStartedAt, b.BreachStartedAt) - CorrelationScorer.ChangeLookback, Max(a.BreachStartedAt, b.BreachStartedAt));
                var near = onServices.SelectMany(s => changesByService[s]).Concat(onResources.SelectMany(r => changesByResource[r])).Distinct()
                    .Where(c => c.OccurredAt >= from && c.OccurredAt <= to
                        && (c.Environment is null || environmentKey is null || c.Environment == environmentKey)
                        && ((c.ServiceId is { } cs && onServices.Contains(cs)) || (c.ResourceId is { } cr && onResources.Contains(cr))))
                    .Select(c => new ChangeFact(c.OccurredAt, c.Summary)).ToList();

                var (score, evidence) = CorrelationScorer.Score(new PairFacts(a.FiredAt!.Value - b.FiredAt!.Value, dependency, sharedHost, near, unhealthy));
                var incident = incidents.TryGetValue(a.Id, out var ia) ? ia : incidents.TryGetValue(b.Id, out var ib) ? ib : (Guid?)null;
                result.Add(new Suggestion(a, b, score, evidence, incident));
            }
            return result;
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }
}
