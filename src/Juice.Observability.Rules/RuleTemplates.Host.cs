using Juice.Observability.Data;
using Juice.Observability.Discovery;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// HostCpu and HostDisk (Phase 12 R7): one observation per host, subject <c>host:{h}</c> as HostMemory. The scope reaches the query only as the
    /// <c>{host}</c> parameter; every constant below is built from platform constants.
    /// </summary>
    public sealed partial class RuleTemplates
    {
        public const string CpuTime = "system.cpu.time", CpuUtilization = "system.cpu.utilization";
        public const string FilesystemUsage = "system.filesystem.usage", FilesystemUtilization = "system.filesystem.utilization";

        // The same host key as HostMemory: the host id when there is one, else the host name, lower-cased; the rule's Host matches either.
        private const string HostExpr = "if(JuiceHostId != '', lower(JuiceHostId), lower(JuiceHostName))";
        private const string HostFilter = $"({HostParam} = '' OR lower(JuiceHostName) = {HostParam} OR lower(JuiceHostId) = {HostParam})";

        // Filesystems that say nothing about capacity: the pseudo and virtual types, and the kernel's and the container runtime's mounts. The host
        // Collector's config leaves them out; the query does too, so a Collector that doesn't can't make a disk look full.
        private static readonly string[] PseudoFileSystems =
        [
            "autofs", "binfmt_misc", "bpf", "cgroup", "cgroup2", "configfs", "debugfs", "devpts", "devtmpfs", "fusectl", "hugetlbfs", "iso9660",
            "mqueue", "nsfs", "overlay", "proc", "procfs", "pstore", "ramfs", "rpc_pipefs", "securityfs", "selinuxfs", "squashfs", "sysfs", "tmpfs", "tracefs",
        ];

        private static readonly string MountFilter = $"""
            Attributes['mode'] != 'ro'
            AND Attributes['type'] NOT IN ({string.Join(", ", PseudoFileSystems.Select(t => $"'{t}'"))})
            AND NOT match(Attributes['mountpoint'], '^/(proc|sys|dev|run|snap)(/|$)')
            AND NOT match(Attributes['mountpoint'], '^/var/lib/docker(/|$)')
            """;

        // CPU time is a cumulative counter per (cpu, state), counted as the other counters are (CounterIncrease) with one difference. The increase
        // is measured from the series' last value before the window; failing that, from zero for a series that started in the window (a restart),
        // but from the series' first point in the window for an older one: a hostmetrics series starts at boot, so counting it from zero would
        // read the lifetime average. (This differs on purpose from MetricThreshold counters and histograms, where a new attribute set carries the
        // instrument's start time and so counts from zero; the rule is HostCpu's alone, because its start time is the boot time and a CPU share
        // is a rate over elapsed time, so an old series with a single point in the window has none to measure over and is left out.) A series without a point in the window is left out. Busy is every state but idle and iowait ('wait', which
        // top doesn't count as use); the share is busy / total over all states.
        private static readonly string HostCpuTime = $"""
            SELECT host, sumIf(increase, state NOT IN ('idle', 'wait')) AS busy, sum(increase) AS total
            FROM (
                SELECT {HostExpr} AS host, Attributes['state'] AS state,
                       argMaxIf(Value, TimeUnix, TimeUnix >= {From}) AS last_v,
                       -- a last value below an earlier one is a restart under the same start time: it counts from zero (as a histogram's was_reset)
                       last_v < greatest(maxIf(Value, TimeUnix >= {From}), maxIf(Value, TimeUnix < {From})) AS was_reset,
                       if(was_reset, last_v, greatest(last_v - if(countIf(TimeUnix < {From}) > 0, maxIf(Value, TimeUnix < {From}),
                           if(StartTimeUnix >= {From}, 0, argMinIf(Value, TimeUnix, TimeUnix >= {From}))), 0)) AS increase
                FROM otel.otel_metrics_sum
                WHERE {Tenant} AND TimeUnix >= {From} - INTERVAL 1 HOUR AND TimeUnix < {To} AND MetricName = '{CpuTime}' AND {HostFilter}
                GROUP BY host, state, cityHash64(ResourceAttributes, Attributes), StartTimeUnix
                HAVING countIf(TimeUnix >= {From}) > 0
                   AND NOT (countIf(TimeUnix < {From}) = 0 AND StartTimeUnix < {From} AND countIf(TimeUnix >= {From}) < 2)
            )
            WHERE host != ''
            GROUP BY host
            HAVING total > 0
            ORDER BY host
            LIMIT {Limit}
            """;

        // The fallback, for a Collector without the cpu.time metric: one minus the mean idle and iowait shares over the window.
        private static readonly string HostCpuUtilization = $"""
            SELECT {HostExpr} AS host, 1 - avgIf(Value, Attributes['state'] = 'idle') - ifNotFinite(avgIf(Value, Attributes['state'] = 'wait'), 0) AS busy
            FROM otel.otel_metrics_gauge
            WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = '{CpuUtilization}' AND {HostFilter}
            GROUP BY host
            HAVING host != '' AND countIf(Attributes['state'] = 'idle') > 0
            ORDER BY host
            LIMIT {Limit}
            """;

        // The fullest read-write real mount of each host, used / (used + free) as df has it (the reserved blocks count as neither).
        private static readonly string HostDiskUsage = $"""
            SELECT host, mount, used / (used + free) AS ratio
            FROM (
                SELECT {HostExpr} AS host, Attributes['mountpoint'] AS mount, Attributes['device'] AS device,
                       argMaxIf(Value, TimeUnix, Attributes['state'] = 'used') AS used,
                       argMaxIf(Value, TimeUnix, Attributes['state'] = 'free') AS free,
                       countIf(Attributes['state'] = 'used') AS used_points, countIf(Attributes['state'] = 'free') AS free_points
                FROM otel.otel_metrics_sum
                WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = '{FilesystemUsage}' AND {HostFilter}
                  AND {MountFilter}
                GROUP BY host, mount, device
                HAVING used_points > 0 AND free_points > 0 AND used + free > 0
            )
            WHERE host != ''
            ORDER BY host, ratio DESC, mount
            LIMIT 1 BY host
            LIMIT {Limit}
            """;

        private static readonly string HostDiskUtilization = $"""
            SELECT host, mount, ratio
            FROM (
                SELECT {HostExpr} AS host, Attributes['mountpoint'] AS mount, Attributes['device'] AS device, argMax(Value, TimeUnix) AS ratio
                FROM otel.otel_metrics_gauge
                WHERE {Tenant} AND TimeUnix >= {From} AND TimeUnix < {To} AND MetricName = '{FilesystemUtilization}' AND {HostFilter}
                  AND {MountFilter}
                GROUP BY host, mount, device
            )
            WHERE host != ''
            ORDER BY host, ratio DESC, mount
            LIMIT 1 BY host
            LIMIT {Limit}
            """;

        private static RuleObservation Host(string host, double value, string source, string? mount = null)
        {
            var detail = new Dictionary<string, string> { ["source"] = source };
            if (mount is not null) detail["mount"] = mount;
            return new RuleObservation($"host:{host}", HealthSubject.Host, "", "", "", host, value, 1) { Detail = detail };
        }

        // The two sources of one metric, a host once: from the first that has it. Bounded by the window's row limit, as each query is.
        private static IReadOnlyList<RuleObservation> PreferFirst(RuleTemplate template, IReadOnlyList<RuleObservation> preferred, IReadOnlyList<RuleObservation> fallback, int limit)
        {
            var seen = preferred.Select(o => o.SubjectKey).ToHashSet();
            // Each source is cut on its own; the two together are ordered by subject and cut the same way.
            var merged = preferred.Concat(fallback.Where(o => !seen.Contains(o.SubjectKey))).OrderBy(o => o.SubjectKey, StringComparer.Ordinal).ToList();
            return CutLast(merged.Take(limit + 1).ToList(), limit, template);
        }

        private async partial Task<IReadOnlyList<RuleObservation>> ObserveHostCpuAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
        {
            var fromTime = await QueryBoundedAsync(RuleTemplate.HostCpu, HostCpuTime, window, r => Host(r.GetString(0),
                Math.Clamp(Convert.ToDouble(r.GetValue(1)) / Convert.ToDouble(r.GetValue(2)), 0, 1), CpuTime), cancellationToken, scope);
            var fromUtilization = await QueryBoundedAsync(RuleTemplate.HostCpu, HostCpuUtilization, window, r => Host(r.GetString(0),
                Math.Clamp(Convert.ToDouble(r.GetValue(1)), 0, 1), CpuUtilization), cancellationToken, scope);
            return PreferFirst(RuleTemplate.HostCpu, fromTime, fromUtilization, window.Limit);
        }

        private async partial Task<IReadOnlyList<RuleObservation>> ObserveHostDiskAsync(TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken cancellationToken)
        {
            var fromUsage = await QueryBoundedAsync(RuleTemplate.HostDisk, HostDiskUsage, window, r => Host(r.GetString(0),
                Math.Clamp(Convert.ToDouble(r.GetValue(2)), 0, 1), FilesystemUsage, r.GetString(1)), cancellationToken, scope);
            var fromUtilization = await QueryBoundedAsync(RuleTemplate.HostDisk, HostDiskUtilization, window, r => Host(r.GetString(0),
                Math.Clamp(Convert.ToDouble(r.GetValue(2)), 0, 1), FilesystemUtilization, r.GetString(1)), cancellationToken, scope);
            return PreferFirst(RuleTemplate.HostDisk, fromUsage, fromUtilization, window.Limit);
        }
    }
}
