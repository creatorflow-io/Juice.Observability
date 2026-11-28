using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 12 R7: the HostCpu and HostDisk templates over real host metrics sent through the test Collector (the test tenant), each test on
    /// hosts of its own, so the Collector's own host metrics in that tenant don't count.
    /// </summary>
    public class HostTemplateTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static RuleTemplates Templates()
            => new(new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })));

        private TelemetryWindow Window(string tenant = LocalStack.TenantKey) => new(tenant, _now.AddMinutes(-5), _now, 100);

        private string Host(string name) => $"p12c-{_run}-{name}";

        private static (string, string)[] Resource(string host) => [("host.name", host)];

        private DateTimeOffset Before => _now.AddMinutes(-8);   // before the window
        private DateTimeOffset Early => _now.AddMinutes(-4);
        private DateTimeOffset Late => _now.AddMinutes(-1);

        private static async Task CpuTime(string host, string state, DateTimeOffset start, params (DateTimeOffset At, double Value)[] points)
            => await LocalStack.SendAsync("metrics", OtlpMetrics.Sum(Resource(host), "system.cpu.time", OtlpMetrics.Cumulative, true, start,
                [("cpu", "cpu0"), ("state", state)], points));

        private static async Task CpuUtilization(string host, string state, params (DateTimeOffset At, double Value)[] points)
            => await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge(Resource(host), "system.cpu.utilization", [("cpu", "cpu0"), ("state", state)], points));

        private async Task Usage(string host, string mount, string type, string mode, double used, double free, string device = "")
        {
            foreach (var (state, value) in new[] { ("used", used), ("free", free), ("reserved", 0d) })
            {
                await LocalStack.SendAsync("metrics", OtlpMetrics.Sum(Resource(host), "system.filesystem.usage", OtlpMetrics.Cumulative, false, Before,
                    [("device", device.Length > 0 ? device : $"dev{mount.Replace('/', '_')}"), ("mountpoint", mount), ("type", type), ("mode", mode), ("state", state)],
                    (Late, value)));
            }
        }

        private async Task Utilization(string host, string mount, string type, string mode, double value)
            => await LocalStack.SendAsync("metrics", OtlpMetrics.Gauge(Resource(host), "system.filesystem.utilization",
                [("device", $"dev{mount.Replace('/', '_')}"), ("mountpoint", mount), ("type", type), ("mode", mode)], (Late, value)));

        private static async Task WaitForRowsAsync(string table, string metric, string host, int expected)
        {
            for (var i = 0; i < 75; i++)
            {
                var count = int.Parse(await LocalStack.ClickHouseAsync(
                    $"SELECT count() FROM otel.{table} WHERE MetricName = '{metric}' AND ResourceAttributes['host.name'] = '{host}'"));
                if (count >= expected) return;
                await Task.Delay(200, Ct);
            }
            Assert.Fail($"{metric} of {host} did not reach {expected} rows");
        }

        private static RuleDefinition Rule(RuleTemplate template, string host = "")
            => new() { Key = "t", Name = "t", Template = template, Host = host };

        [Fact]
        public async Task HostCpu_from_cpu_time_increase_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpu");
            // Cumulative seconds, one series per state. Since before the window: user +30, system +10, idle +60: busy 40 of 100.
            await CpuTime(host, "user", Before, (Before, 100), (Early, 115), (Late, 130));
            await CpuTime(host, "system", Before, (Before, 50), (Late, 60));
            await CpuTime(host, "idle", Before, (Before, 400), (Early, 430), (Late, 460));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 7);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.Equal($"host:{host}", observation.SubjectKey);
            Assert.Equal(HealthSubject.Host, observation.SubjectType);
            Assert.InRange(observation.Value!.Value, 0.3999, 0.4001);
            Assert.Equal("system.cpu.time", observation.Detail!["source"]);
        }

        [Fact]
        public async Task HostCpu_falls_back_to_utilization_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuutil");
            // No cpu.time: the mean idle share is 0.7, so 0.3 is busy. Other states are not read.
            await CpuUtilization(host, "idle", (Early, 0.8), (Late, 0.6));
            await CpuUtilization(host, "user", (Early, 0.1), (Late, 0.3));
            await WaitForRowsAsync("otel_metrics_gauge", "system.cpu.utilization", host, 4);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.2999, 0.3001);
            Assert.Equal("system.cpu.utilization", observation.Detail!["source"]);
        }

        [Fact]
        public async Task HostCpu_utilization_fallback_does_not_count_iowait_as_busy_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuutilwait");
            // Mean idle 0.6 and mean iowait 0.1: busy is 0.3 (0.4 with iowait counted).
            await CpuUtilization(host, "idle", (Early, 0.5), (Late, 0.7));
            await CpuUtilization(host, "wait", (Early, 0.1), (Late, 0.1));
            await WaitForRowsAsync("otel_metrics_gauge", "system.cpu.utilization", host, 4);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.2999, 0.3001);
            Assert.Equal("system.cpu.utilization", observation.Detail!["source"]);
        }

        [Fact]
        public async Task HostCpu_prefers_cpu_time_over_utilization_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuboth");
            await CpuTime(host, "user", Before, (Before, 0), (Late, 10));
            await CpuTime(host, "idle", Before, (Before, 0), (Late, 90));
            await CpuUtilization(host, "idle", (Late, 0.1));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 4);
            await WaitForRowsAsync("otel_metrics_gauge", "system.cpu.utilization", host, 1);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.0999, 0.1001);
            Assert.Equal("system.cpu.time", observation.Detail!["source"]);
        }

        [Fact]
        public async Task HostCpu_series_started_long_before_the_window_counts_from_its_first_point_in_window_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuboot");
            // The start time of a hostmetrics counter is the boot time: no point before the window, but the series is not new. Counting it
            // from zero would read the lifetime average (here 0.1017); the window's own increase is user +20 of 80.
            var boot = _now.AddDays(-10);
            await CpuTime(host, "user", boot, (Early, 100), (Late, 120));
            await CpuTime(host, "idle", boot, (Early, 1000), (Late, 1060));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 4);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.2499, 0.2501);
        }

        [Fact]
        public async Task HostCpu_old_series_with_one_point_in_window_is_no_data_not_zero_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuonepoint");
            // Started at boot, nothing before the window, one point inside it: no elapsed time to measure over, so no data (not 0 % busy).
            var boot = _now.AddDays(-10);
            await CpuTime(host, "user", boot, (Late, 120));
            await CpuTime(host, "idle", boot, (Late, 1060));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 2);

            Assert.Empty(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));
        }

        [Fact]
        public async Task HostCpu_counter_drop_under_the_same_start_time_counts_from_zero_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpusamestart");
            // One start time throughout, the user counter drops 100 -> 110 -> 5 (a reset the start time doesn't show): it counts from 0 after the
            // drop, as a histogram does, so user is 5 (not 10) against idle +60: busy 5 of 65.
            await CpuTime(host, "user", Before, (Before, 100), (Early, 110), (Late, 5));
            await CpuTime(host, "idle", Before, (Before, 400), (Late, 460));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 5);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 5d / 65 - 0.0001, 5d / 65 + 0.0001);
        }

        /// <summary>The tenant holds other hosts (the Collector's own), so the cut is read against the whole ordered list: a limit one below it.</summary>
        private static async Task AssertCutLeavesOutTheLastSubjectAsync(RuleTemplate template, Func<int, Task<IReadOnlyList<RuleObservation>>> observeLimited)
        {
            var all = await observeLimited(1000);
            Assert.True(all.Count >= 2, "the test hosts are in the tenant");
            Assert.Equal(all.Select(o => o.SubjectKey).OrderBy(k => k, StringComparer.Ordinal), all.Select(o => o.SubjectKey));   // ordered by host
            using var probe = new TruncationProbe();

            var exact = await observeLimited(all.Count);                                    // fits: nothing is left out
            Assert.Equal(all.Select(o => o.SubjectKey), exact.Select(o => o.SubjectKey));

            var cut = await observeLimited(all.Count - 1);                                  // one too many: the last subject goes, and it is counted
            Assert.Equal(all.Take(all.Count - 1).Select(o => o.SubjectKey), cut.Select(o => o.SubjectKey));
            Assert.True(probe.Count(template.ToString()) >= 1);
        }

        [Fact]
        public async Task HostCpu_row_limit_leaves_out_the_last_host_and_counts_it_Async()
        {
            LocalStack.RequireAvailable();
            foreach (var name in new[] { "cpucut1", "cpucut2" })
            {
                await CpuTime(Host(name), "user", Before, (Before, 0), (Late, 10));
                await CpuTime(Host(name), "idle", Before, (Before, 0), (Late, 90));
                await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", Host(name), 4);
            }
            var templates = Templates();
            await AssertCutLeavesOutTheLastSubjectAsync(RuleTemplate.HostCpu,
                limit => templates.ObserveAsync(Rule(RuleTemplate.HostCpu), Window() with { Limit = limit }, Ct));
        }

        [Fact]
        public async Task HostDisk_row_limit_leaves_out_the_last_host_and_counts_it_Async()
        {
            LocalStack.RequireAvailable();
            foreach (var name in new[] { "diskcut1", "diskcut2" })
            {
                await Usage(Host(name), "/data", "ext4", "rw", 50, 50);
                await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", Host(name), 3);
            }
            var templates = Templates();
            await AssertCutLeavesOutTheLastSubjectAsync(RuleTemplate.HostDisk,
                limit => templates.ObserveAsync(Rule(RuleTemplate.HostDisk), Window() with { Limit = limit }, Ct));
        }

        [Fact]
        public async Task HostCpu_iowait_is_not_busy_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpuwait");
            // user +20, wait +30, idle +50: busy is 0.2, as top counts it (0.5 with iowait counted).
            await CpuTime(host, "user", Before, (Before, 0), (Late, 20));
            await CpuTime(host, "wait", Before, (Before, 0), (Late, 30));
            await CpuTime(host, "idle", Before, (Before, 0), (Late, 50));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 6);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.1999, 0.2001);
        }

        [Fact]
        public async Task HostCpu_counter_reset_not_negative_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("cpureset");
            // The host restarted at Early+: the counters began again from small values under a new start time. A restart counts from zero:
            // user +10 and then 30, idle +10 and then 70: busy 40 of 120, never negative.
            var restart = _now.AddMinutes(-3);
            await CpuTime(host, "user", Before, (Before, 100), (Early, 110));
            await CpuTime(host, "user", restart, (Late, 30));
            await CpuTime(host, "idle", Before, (Before, 100), (Early, 110));
            await CpuTime(host, "idle", restart, (Late, 70));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 6);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.3333, 0.3334);
        }

        [Fact]
        public async Task HostDisk_fullest_rw_mount_named_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("disk");
            await Usage(host, "/", "ext4", "rw", used: 50, free: 50);
            await Usage(host, "/data", "xfs", "rw", used: 90, free: 10);
            await Usage(host, "/var/log", "ext4", "rw", used: 20, free: 80);
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", host, 9);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk, host), Window(), Ct));

            Assert.Equal($"host:{host}", observation.SubjectKey);
            Assert.InRange(observation.Value!.Value, 0.8999, 0.9001);
            Assert.Equal("/data", observation.Detail!["mount"]);
            Assert.Equal("system.filesystem.usage", observation.Detail["source"]);
        }

        [Fact]
        public async Task HostDisk_uses_used_over_used_plus_free_like_df_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("df");
            // 100 used, 100 free, 800 reserved: df says 50 %.
            foreach (var (state, value) in new[] { ("used", 100d), ("free", 100d), ("reserved", 800d) })
            {
                await LocalStack.SendAsync("metrics", OtlpMetrics.Sum(Resource(host), "system.filesystem.usage", OtlpMetrics.Cumulative, false, Before,
                    [("device", "sda1"), ("mountpoint", "/"), ("type", "ext4"), ("mode", "rw"), ("state", state)], (Late, value)));
            }
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", host, 3);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.4999, 0.5001);
        }

        [Fact]
        public async Task HostDisk_ignores_ro_and_pseudo_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("pseudo");
            await Usage(host, "/", "ext4", "rw", used: 30, free: 70);
            await Usage(host, "/media/iso", "iso9660", "ro", used: 100, free: 0);          // read-only
            await Usage(host, "/snap/core/1", "ext4", "rw", used: 100, free: 0);           // a mount the config excludes
            await Usage(host, "/dev/shm", "tmpfs", "rw", used: 99, free: 1);               // a pseudo mount and type
            await Usage(host, "/mnt/scratch", "tmpfs", "rw", used: 99, free: 1);           // a pseudo type on an ordinary path
            await Usage(host, "/var/lib/docker/overlay2/x", "overlay", "rw", used: 99, free: 1);
            await Usage(host, "/mnt/data-ro", "ext4", "ro", used: 98, free: 2);
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", host, 21);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.2999, 0.3001);
            Assert.Equal("/", observation.Detail!["mount"]);

            var onlyPseudo = Host("onlypseudo");
            await Usage(onlyPseudo, "/dev/shm", "tmpfs", "rw", used: 99, free: 1);
            await Usage(onlyPseudo, "/media/iso", "iso9660", "ro", used: 100, free: 0);
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", onlyPseudo, 6);
            Assert.Empty(await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk, onlyPseudo), Window(), Ct));   // no data, never zero
        }

        [Fact]
        public async Task HostDisk_from_utilization_fallback_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("diskutil");
            await Utilization(host, "/", "ext4", "rw", 0.4);
            await Utilization(host, "/data", "ext4", "rw", 0.85);
            await Utilization(host, "/media/iso", "iso9660", "ro", 1.0);
            await Utilization(host, "/dev/shm", "tmpfs", "rw", 0.99);
            await WaitForRowsAsync("otel_metrics_gauge", "system.filesystem.utilization", host, 4);

            var observation = Assert.Single(await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk, host), Window(), Ct));

            Assert.InRange(observation.Value!.Value, 0.8499, 0.8501);
            Assert.Equal("/data", observation.Detail!["mount"]);
            Assert.Equal("system.filesystem.utilization", observation.Detail["source"]);
        }

        [Fact]
        public async Task Host_without_points_no_data_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("silent");
            // Points only before the window: a host that stopped reporting is no data, not zero.
            await CpuTime(host, "user", Before, (Before, 10));
            await CpuTime(host, "idle", Before, (Before, 90));
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 2);

            var templates = Templates();
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.HostCpu, Host("never-reported")), Window(), Ct));
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.HostDisk, Host("never-reported")), Window(), Ct));
        }

        [Fact]
        public async Task Host_scope_filters_one_host_Async()
        {
            LocalStack.RequireAvailable();
            var (one, two) = (Host("scope-one"), Host("scope-two"));
            foreach (var (host, busy) in new[] { (one, 20d), (two, 70d) })
            {
                await CpuTime(host, "user", Before, (Before, 0), (Late, busy));
                await CpuTime(host, "idle", Before, (Before, 0), (Late, 100 - busy));
                await Usage(host, "/", "ext4", "rw", used: busy, free: 100 - busy);
            }
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", two, 4);
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", two, 3);

            var templates = Templates();
            var only = Assert.Single(await templates.ObserveAsync(Rule(RuleTemplate.HostCpu, two.ToUpperInvariant()), Window(), Ct));   // the name is case-insensitive
            Assert.Equal(($"host:{two}", 0.7), (only.SubjectKey, Math.Round(only.Value!.Value, 4)));
            var disk = Assert.Single(await templates.ObserveAsync(Rule(RuleTemplate.HostDisk, one), Window(), Ct));
            Assert.Equal(($"host:{one}", 0.2), (disk.SubjectKey, Math.Round(disk.Value!.Value, 4)));

            // Without a scope, both are observed (the test tenant also holds the Collector's own host).
            var all = await templates.ObserveAsync(Rule(RuleTemplate.HostCpu), Window(), Ct);
            Assert.Contains(all, o => o.Host == one);
            Assert.Contains(all, o => o.Host == two);
        }

        [Fact]
        public async Task Other_tenant_host_never_observed_Async()
        {
            LocalStack.RequireAvailable();
            var host = Host("tenant");
            await CpuTime(host, "user", Before, (Before, 0), (Late, 50));
            await CpuTime(host, "idle", Before, (Before, 0), (Late, 50));
            await Usage(host, "/", "ext4", "rw", used: 50, free: 50);
            await Utilization(host, "/", "ext4", "rw", 0.5);
            await CpuUtilization(host, "idle", (Late, 0.5));
            await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", host, 3);
            await WaitForRowsAsync("otel_metrics_gauge", "system.filesystem.utilization", host, 1);
            await WaitForRowsAsync("otel_metrics_gauge", "system.cpu.utilization", host, 1);
            await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 4);

            var templates = Templates();
            Assert.Single(await templates.ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window(), Ct));          // seen by its own tenant
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.HostCpu, host), Window("another-tenant"), Ct));
            Assert.Empty(await templates.ObserveAsync(Rule(RuleTemplate.HostDisk, host), Window("another-tenant"), Ct));
        }

        [Fact]
        public async Task Row_limit_bounds_hosts_Async()
        {
            LocalStack.RequireAvailable();
            var hosts = new[] { Host("limit-a"), Host("limit-b"), Host("limit-c") };
            foreach (var host in hosts)
            {
                await CpuTime(host, "idle", Before, (Before, 0), (Late, 10));
                await Usage(host, "/", "ext4", "rw", used: 10, free: 90);
            }
            foreach (var host in hosts)
            {
                await WaitForRowsAsync("otel_metrics_sum", "system.cpu.time", host, 2);
                await WaitForRowsAsync("otel_metrics_sum", "system.filesystem.usage", host, 3);
            }

            var limited = new TelemetryWindow(LocalStack.TenantKey, _now.AddMinutes(-5), _now, 2);
            Assert.Equal(2, (await Templates().ObserveAsync(Rule(RuleTemplate.HostCpu), limited, Ct)).Count);
            Assert.Equal(2, (await Templates().ObserveAsync(Rule(RuleTemplate.HostDisk), limited, Ct)).Count);
        }

        [Theory]
        [InlineData(RuleTemplate.HostCpu)]
        [InlineData(RuleTemplate.HostDisk)]
        public void Validate_host_templates_ratio_and_host_only(RuleTemplate template)
        {
            RuleDefinition Make(Action<RuleDefinition>? change = null)
            {
                var rule = new RuleDefinition { Key = "host-rule", Name = "n", Template = template, Threshold = 0.9, RecoveryThreshold = 0.8 };
                change?.Invoke(rule);
                return rule;
            }

            Assert.Empty(Make().Validate());
            Assert.Empty(Make(r => r.Host = "web-1").Validate());
            Assert.Contains(Make(r => r.Threshold = 90).Validate(), e => e.Contains("ratios from 0 to 1"));
            Assert.Contains(Make(r => r.RecoveryThreshold = -0.1).Validate(), e => e.Contains("ratios from 0 to 1"));
            Assert.Contains(Make(r => r.RecoveryThreshold = 0.95).Validate(), e => e.Contains("RecoveryThreshold must not be above Threshold"));
            Assert.Contains(Make(r => r.Service = "orders").Validate(), e => e.Contains("host rules take Host"));
            Assert.Contains(Make(r => r.Environment = "production").Validate(), e => e.Contains("host rules take Host"));
        }

        [Fact]
        public void Host_queries_take_user_input_only_as_parameters()
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Juice.Observability.Rules", "RuleTemplates.Host.cs"));
            Assert.DoesNotContain("{rule.", source);
            Assert.DoesNotContain("{scope", source);
            Assert.Contains("{HostParam}", source);
        }

        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Juice.Observability.slnx"))) return dir.FullName;
            }
            throw new InvalidOperationException("repository root not found");
        }
    }
}
