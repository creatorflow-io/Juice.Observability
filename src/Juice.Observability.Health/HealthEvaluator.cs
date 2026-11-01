using Juice.Observability.Data;

namespace Juice.Observability.Health
{
    /// <summary>A state with the reasons for it and the newest evidence behind it.</summary>
    public sealed record Assessment(HealthState State, IReadOnlyList<string> Reasons, DateTimeOffset? LastEvidence)
    {
        public static Assessment Of(HealthState state, DateTimeOffset? lastEvidence, params string[] reasons) => new(state, reasons, lastEvidence);
    }

    /// <summary>Requests (server and consumer spans) and errors in the window, and the newest telemetry of any signal.</summary>
    public sealed record Evidence(DateTimeOffset? LastSeen, long Requests, long Errors);

    public sealed record ChildAssessment(string Name, Assessment Assessment, double Weight, bool Critical);

    /// <summary>
    /// The health rules (requirements §7), as pure functions of evidence and policy so every rule is unit-tested.
    /// <list type="bullet">
    /// <item><c>Unknown</c>: never seen, or not enough evidence. Never treated as healthy, zero or <c>Offline</c>.</item>
    /// <item><c>Offline</c>: seen before and silent for longer than the freshness grace.</item>
    /// <item>Severity for known states: <c>Critical</c> &gt; <c>Unhealthy</c> &gt; <c>Degraded</c> &gt; <c>Healthy</c>.</item>
    /// </list>
    /// Reason codes are stable identifiers (with a <c>:subject</c> suffix where one applies) for the API and alerts.
    /// </summary>
    public sealed class HealthEvaluator(HealthPolicy policy)
    {
        public Assessment Instance(DateTimeOffset now, DateTimeOffset? lastSeenBefore, Evidence? evidence)
        {
            var last = Max(lastSeenBefore, evidence?.LastSeen);
            if (last is null)
            {
                return Assessment.Of(HealthState.Unknown, null, "never_seen");
            }
            if (now - last > policy.FreshnessGrace)
            {
                return Assessment.Of(HealthState.Offline, last, "no_telemetry_within_grace");
            }
            return ErrorRate(evidence?.Requests ?? 0, evidence?.Errors ?? 0, last, "heartbeat_only");
        }

        /// <summary>
        /// Default service policy: a failed critical dependency → <c>Critical</c>; no fresh instance → <c>Offline</c> once
        /// a previously seen service passes the grace, else <c>Unknown</c>; the unhealthy quorum → <c>Unhealthy</c>; any
        /// partial failure → <c>Degraded</c>; only fresh healthy evidence → <c>Healthy</c>.
        /// </summary>
        public Assessment Service(DateTimeOffset now, DateTimeOffset? lastSeenBefore, IReadOnlyList<Assessment> instances,
            Evidence service, IReadOnlyList<(string Name, HealthState State)> criticalDependencies)
        {
            var last = instances.Select(i => i.LastEvidence).Aggregate(Max(lastSeenBefore, service.LastSeen), Max);
            var fresh = instances.Where(i => i.State is not (HealthState.Offline or HealthState.Unknown)).ToList();
            if (last is null)
            {
                return Assessment.Of(HealthState.Unknown, null, "never_seen");
            }
            if (now - last > policy.FreshnessGrace)
            {
                return Assessment.Of(HealthState.Offline, last, instances.Count == 0 ? "no_telemetry_within_grace" : "no_active_instances");
            }
            // Recently seen, yet nothing in the window to judge: no fresh instance and no telemetry of the service itself.
            if (fresh.Count == 0 && service.LastSeen is null)
            {
                return Assessment.Of(HealthState.Unknown, last, "insufficient_evidence");
            }

            var states = new List<(HealthState State, string Reason)>();
            foreach (var (name, state) in criticalDependencies)
            {
                if (state is HealthState.Unhealthy or HealthState.Critical or HealthState.Offline)
                    states.Add((HealthState.Critical, $"critical_dependency_failed:{name}"));
                else if (state is HealthState.Unknown)
                    states.Add((HealthState.Healthy, $"critical_dependency_unknown:{name}"));
            }
            var failing = fresh.Count(i => i.State is HealthState.Unhealthy or HealthState.Critical);
            if (fresh.Count > 0 && (double)failing / fresh.Count >= policy.UnhealthyInstanceQuorum)
            {
                states.Add((HealthState.Unhealthy, "instance_quorum_unhealthy"));
            }
            else if (fresh.Any(i => i.State != HealthState.Healthy))
            {
                states.Add((HealthState.Degraded, "partial_instance_failure"));
            }
            // Without instance identity the service is judged on its own telemetry: alive, and its error rate.
            var rate = ErrorRate(service.Requests, service.Errors, last, fresh.Count == 0 ? "heartbeat_only" : "low_traffic");
            states.Add((rate.State, rate.Reasons[0]));
            return Worst(states, last);
        }

        /// <summary>
        /// Weighted rollup with minimum coverage. A critical child decides on failure or missing evidence; optional
        /// children that are missing (Unknown or Offline) never make the application unhealthy. No "worst child wins".
        /// </summary>
        public Assessment Application(IReadOnlyList<ChildAssessment> children)
        {
            var last = children.Select(c => c.Assessment.LastEvidence).Aggregate((DateTimeOffset?)null, Max);
            if (children.Count == 0)
            {
                return Assessment.Of(HealthState.Unknown, null, "no_services");
            }
            var states = new List<(HealthState State, string Reason)>();
            foreach (var child in children.Where(c => c.Critical))
            {
                switch (child.Assessment.State)
                {
                    case HealthState.Unknown:
                        return Assessment.Of(HealthState.Unknown, last, $"critical_service_unknown:{child.Name}");
                    case HealthState.Critical:
                        states.Add((HealthState.Critical, $"critical_service_critical:{child.Name}"));
                        break;
                    case HealthState.Unhealthy or HealthState.Offline:
                        states.Add((HealthState.Unhealthy, $"critical_service_failed:{child.Name}"));
                        break;
                }
            }
            var known = children.Where(c => c.Assessment.State is not (HealthState.Unknown or HealthState.Offline)).ToList();
            var total = children.Sum(c => c.Weight);
            var coverage = total <= 0 ? 0 : known.Sum(c => c.Weight) / total;
            if (coverage < policy.MinimumCoverage && states.Count == 0)
            {
                return children.All(c => c.Assessment.State == HealthState.Offline)
                    ? Assessment.Of(HealthState.Offline, last, "all_services_offline")
                    : Assessment.Of(HealthState.Unknown, last, "insufficient_coverage");
            }
            var knownWeight = known.Sum(c => c.Weight);
            var failingWeight = known.Where(c => c.Assessment.State is HealthState.Unhealthy or HealthState.Critical).Sum(c => c.Weight);
            if (knownWeight > 0 && failingWeight / knownWeight >= policy.UnhealthyInstanceQuorum)
                states.Add((HealthState.Unhealthy, "services_unhealthy"));
            else if (known.Any(c => c.Assessment.State != HealthState.Healthy))
                states.Add((HealthState.Degraded, "partial_service_failure"));
            states.Add((HealthState.Healthy, "services_healthy"));
            return Worst(states, last);
        }

        public Assessment Host(DateTimeOffset now, DateTimeOffset? lastSeenBefore, DateTimeOffset? lastSeen, double? memoryUtilization)
        {
            var last = Max(lastSeenBefore, lastSeen);
            if (last is null)
            {
                return Assessment.Of(HealthState.Unknown, null, "never_seen");
            }
            if (now - last > policy.FreshnessGrace)
            {
                return Assessment.Of(HealthState.Offline, last, "no_telemetry_within_grace");
            }
            return memoryUtilization >= policy.HostMemoryDegraded
                ? Assessment.Of(HealthState.Degraded, last, "memory_high")
                : Assessment.Of(HealthState.Healthy, last, memoryUtilization is null ? "heartbeat_only" : "resources_ok");
        }

        private Assessment ErrorRate(long requests, long errors, DateTimeOffset? last, string withoutTraffic)
        {
            if (requests < policy.MinimumSamples)
            {
                return Assessment.Of(HealthState.Healthy, last, withoutTraffic);
            }
            var rate = (double)errors / requests;
            return rate >= policy.ErrorRateUnhealthy ? Assessment.Of(HealthState.Unhealthy, last, "error_rate_high")
                : rate >= policy.ErrorRateDegraded ? Assessment.Of(HealthState.Degraded, last, "error_rate_elevated")
                : Assessment.Of(HealthState.Healthy, last, "error_rate_ok");
        }

        /// <summary>The most severe state; its reasons, then the other non-healthy reasons and informational ones.</summary>
        private static Assessment Worst(List<(HealthState State, string Reason)> states, DateTimeOffset? last)
        {
            var worst = states.Max(s => Severity(s.State));
            var reasons = states.OrderByDescending(s => Severity(s.State)).Select(s => s.Reason).Distinct().ToList();
            if (worst > 0)
            {
                reasons.RemoveAll(r => r is "error_rate_ok" or "services_healthy" or "low_traffic" or "heartbeat_only");
            }
            return new Assessment(states.First(s => Severity(s.State) == worst).State, reasons, last);
        }

        private static int Severity(HealthState state) => state switch
        {
            HealthState.Critical => 3,
            HealthState.Unhealthy => 2,
            HealthState.Degraded => 1,
            _ => 0,
        };

        private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
            => a is null ? b : b is null ? a : a > b ? a : b;
    }
}
