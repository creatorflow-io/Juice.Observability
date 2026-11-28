using Juice.Observability.Rules;
using Microsoft.Extensions.Configuration;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 12 R7: the shipped host Collector config (<c>deploy/host-collector</c>) and the local stack's <c>host-collector</c> service,
    /// and the shipped <c>deploy/local/rules.json</c>.
    /// </summary>
    public class HostCollectorTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Repo()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Juice.Observability.slnx"))) return dir.FullName;
            }
            throw new InvalidOperationException("repository root not found");
        }

        /// <summary>
        /// The YAML subset the Collector configs use, as a tree: <c>key:</c> nests what is indented under it, <c>- item</c> and <c>[a, b]</c> are
        /// leaves. Enough to read which scrapers, exclusions and pipelines a config has; the Collector's own <c>validate</c> checks the rest.
        /// </summary>
        private static Dictionary<string, object> ParseYaml(string text)
        {
            var root = new Dictionary<string, object>();
            var stack = new Stack<(int Indent, Dictionary<string, object> Node)>();
            stack.Push((-1, root));
            foreach (var raw in text.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                var line = raw.TrimStart();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var indent = raw.Length - line.Length;
                while (stack.Peek().Indent >= indent && stack.Count > 1) stack.Pop();
                var node = stack.Peek().Node;
                if (line.StartsWith("- "))
                {
                    var items = node.TryGetValue("-", out var existing) ? (List<string>)existing : [];
                    items.Add(line[2..].Trim().Trim('\'', '"'));
                    node["-"] = items;
                    continue;
                }
                var colon = line.IndexOf(':');
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (value.Length == 0 || value.StartsWith('#'))
                {
                    var child = new Dictionary<string, object>();
                    node[key] = child;
                    stack.Push((indent, child));
                }
                else if (value.StartsWith('['))
                {
                    node[key] = new Dictionary<string, object> { ["-"] = value.Trim('[', ']').Split(',').Select(v => v.Trim().Trim('\'', '"')).ToList() };
                }
                else
                {
                    node[key] = new Dictionary<string, object> { ["="] = value };
                }
            }
            return root;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> node, params string[] path)
        {
            foreach (var key in path)
            {
                Assert.True(node.TryGetValue(key, out var next), $"missing {string.Join(".", path)} at '{key}'");
                node = (Dictionary<string, object>)next!;
            }
            return node;
        }

        private static List<string> Items(Dictionary<string, object> node, params string[] path) => (List<string>)Child(node, path)["-"];

        [Fact]
        public void Host_collector_config_parses()
        {
            var config = ParseYaml(File.ReadAllText(Path.Combine(Repo(), "deploy", "host-collector", "config.yaml")));

            var scrapers = Child(config, "receivers", "host_metrics", "scrapers");
            Assert.All(new[] { "memory", "cpu", "filesystem" }, scraper => Assert.True(scrapers.ContainsKey(scraper), $"scraper {scraper}"));
            Assert.Contains("true", Child(scrapers, "cpu", "metrics", "system.cpu.utilization", "enabled")["="].ToString());   // the fallback HostCpu reads

            var types = Items(scrapers, "filesystem", "exclude_fs_types", "fs_types");
            Assert.All(new[] { "tmpfs", "overlay", "proc", "sysfs", "squashfs", "devtmpfs", "cgroup2" }, type => Assert.Contains(type, types));
            Assert.Equal("strict", Child(scrapers, "filesystem", "exclude_fs_types", "match_type")["="]);

            var mounts = string.Join(' ', Items(scrapers, "filesystem", "exclude_mount_points", "mount_points"));
            Assert.All(new[] { "proc", "sys", "dev", "run", "snap", "var/lib/docker" }, mount => Assert.Contains(mount, mounts));
            Assert.Equal("regexp", Child(scrapers, "filesystem", "exclude_mount_points", "match_type")["="]);

            // The pipeline forwards over OTLP with a bearer token read from a file, and the host root is a variable (mounted read-only).
            var pipeline = Child(config, "service", "pipelines", "metrics");
            Assert.Contains("host_metrics", Items(pipeline, "receivers"));
            Assert.Contains("otlp_grpc", Items(pipeline, "exporters"));
            Assert.Contains("bearertokenauth/client", Items(config, "service", "extensions"));
            Assert.Contains("${env:JUICE_OTLP_TOKEN_FILE}", Child(config, "extensions", "bearertokenauth/client", "filename")["="].ToString());
            Assert.Contains("${env:JUICE_HOST_ROOT", Child(config, "receivers", "host_metrics", "root_path")["="].ToString());
        }

        [Fact]
        public void Local_compose_mounts_the_host_root_read_only_and_keeps_the_sample_in_its_profile()
        {
            var compose = ParseYaml(File.ReadAllText(Path.Combine(Repo(), "deploy", "local", "docker-compose.yml")));

            var hostCollector = Child(compose, "services", "host-collector");
            Assert.Contains("host", Items(hostCollector, "profiles"));                              // opt-in: one scraper per host, the platform Collector already covers this one
            Assert.Contains("/:/hostfs:ro", Items(hostCollector, "volumes"));
            Assert.All(Items(hostCollector, "volumes").Where(v => v.Contains("/etc/otelcol/")), v => Assert.EndsWith(":ro", v));

            Assert.Contains("sample", Items(Child(compose, "services"), "sample-service", "profiles"));
        }

        [Fact]
        public void Scripts_read_the_first_line_of_otlp_tokens_only()
        {
            var deploy = Path.Combine(Repo(), "deploy", "local");
            var readers = Directory.EnumerateFiles(deploy, "*.sh", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("-f1 secrets/otlp-tokens") || File.ReadAllText(f).Contains("head -n1 secrets/otlp-tokens")).ToList();
            Assert.True(readers.Count >= 5);
            Assert.All(readers, f => Assert.DoesNotContain("(cut -d' ' -f1 secrets/otlp-tokens)", File.ReadAllText(f)));

            // The pipeline itself, on three lines as init.sh leaves the file (one per service), where bash exists.
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("bash") { RedirectStandardInput = true, RedirectStandardOutput = true };
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("head -n1 | cut -d' ' -f1");
                using var bash = System.Diagnostics.Process.Start(info)!;
                bash.StandardInput.Write("first-token local\nsecond-token host-collector\nthird-token sample\n");
                bash.StandardInput.Close();
                var output = bash.StandardOutput.ReadToEnd();
                bash.WaitForExit();
                if (bash.ExitCode == 0) Assert.Equal("first-token", output.Trim());   // a bash that cannot run (WSL without a distro) is skipped
            }
            catch (System.ComponentModel.Win32Exception) { /* no bash on this machine: the text check above stands */ }
        }

        [Fact]
        public void Each_new_service_mounts_only_its_own_otlp_token()
        {
            var compose = ParseYaml(File.ReadAllText(Path.Combine(Repo(), "deploy", "local", "docker-compose.yml")));
            var init = File.ReadAllText(Path.Combine(Repo(), "deploy", "local", "scripts", "init.sh"));

            foreach (var (service, file) in new[] { ("host-collector", "host-collector-otlp-token"), ("sample-service", "sample-otlp-token") })
            {
                var volumes = Items(Child(compose, "services", service), "volumes");
                Assert.DoesNotContain(volumes, v => v.Contains("otlp-tokens"));                // not the file of every token
                Assert.Contains(volumes, v => v.StartsWith($"./secrets/{file}:") && v.EndsWith(":ro"));
                Assert.Contains($"own_otlp_token {service.Replace("-service", "")}", init);   // init.sh writes it, and adds it to otlp-tokens
            }
        }

        [Fact]
        public void Host_collector_takes_the_host_name_from_JUICE_HOST_NAME()
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "deploy", "host-collector", "config.yaml"));
            var config = ParseYaml(text);

            // host.name is set from the variable (only when it is set), after detection, and the pipeline runs the processor.
            var statements = string.Join('\n', Items(config, "processors", "transform/host_name", "metric_statements", "statements"));
            Assert.Contains("host.name", statements);
            Assert.Contains("${env:JUICE_HOST_NAME", statements);
            Assert.Contains("where", statements);
            var processors = Items(config, "service", "pipelines", "metrics", "processors");
            Assert.True(processors.IndexOf("transform/host_name") > processors.IndexOf("resource_detection"), "after resource_detection");

            // The local stack passes the same variable.
            var compose = File.ReadAllText(Path.Combine(Repo(), "deploy", "local", "docker-compose.yml"));
            Assert.Contains("JUICE_HOST_NAME: ${JUICE_HOST_NAME", compose);
        }

        [Fact]
        public void Shipped_rules_json_validates()
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(Repo(), "deploy", "local", "rules.json")).Build();
            var rules = configuration.GetSection("Rules").Get<RulesOptions>()!.Definitions;

            Assert.All(rules, rule => Assert.Empty(rule.Validate(Signals.SignalPacks.All)));
            Assert.Equal(rules.Count, rules.Select(r => r.Key).Distinct().Count());
            Assert.Contains(rules, r => r.Key == "host-cpu" && r.Template == Data.RuleTemplate.HostCpu);
            Assert.Contains(rules, r => r.Key == "host-disk" && r.Template == Data.RuleTemplate.HostDisk);
            var sample = Assert.Single(rules, r => r.Key == "sample-queue-depth");
            Assert.Equal(Data.RuleTemplate.MetricThreshold, sample.Template);
            Assert.Equal((MetricKind.Gauge, "sample.queue.depth"), (sample.Metric!.Kind, sample.Metric.Name));   // rules.json must give Kind: the catalog isn't read at start
        }

        [Fact]
        public async Task HostCollector_sends_cpu_filesystem_memory_Async()
        {
            LocalStack.RequireAvailable();
            // The platform tenant: the host Collector sends to the platform Collector, which stamps the deployment's tenant. What the platform
            // Collector scrapes itself carries no juice.collector attribute, so this reads only what the host Collector sent.
            var tenant = LocalStack.Secret("JUICE_PLATFORM_TENANT_ID");
            var host = Environment.GetEnvironmentVariable("JUICE_HOST_NAME") ?? "local-docker-host";
            string Recent(string table, string metric, string extra = "") =>
                $"SELECT count() FROM otel.{table} WHERE JuiceTenant = '{tenant}' AND MetricName = '{metric}' AND TimeUnix > now() - INTERVAL 2 MINUTE " +
                $"AND ResourceAttributes['juice.collector'] = 'host-collector' AND lower(ResourceAttributes['host.name']) = '{host}' {extra}";

            var expected = new[]
            {
                ("otel_metrics_sum", "system.cpu.time", ""),
                ("otel_metrics_sum", "system.memory.usage", ""),
                ("otel_metrics_sum", "system.filesystem.usage", "AND Attributes['state'] != '' AND Attributes['mode'] != ''"),
            };
            foreach (var (table, metric, extra) in expected)
            {
                var count = 0;
                for (var i = 0; i < 150 && count == 0; i++)
                {
                    count = int.Parse(await LocalStack.ClickHouseAsync(Recent(table, metric, extra)));
                    if (count == 0) await Task.Delay(1000, Ct);
                }
                Assert.True(count > 0, $"{metric} of host {host} from the host Collector did not arrive within 2.5 minutes (is `docker compose --profile host up -d host-collector` running, with the platform Collector's own host_metrics off for that host?)");
            }

            // Only real disks: no tmpfs or overlay mount, and none of the excluded mount points.
            var pseudo = await LocalStack.ClickHouseAsync($"""
                SELECT count() FROM otel.otel_metrics_sum
                WHERE JuiceTenant = '{tenant}' AND MetricName = 'system.filesystem.usage' AND TimeUnix > now() - INTERVAL 2 MINUTE
                  AND ResourceAttributes['juice.collector'] = 'host-collector'
                  AND (Attributes['type'] IN ('tmpfs', 'overlay', 'squashfs', 'proc', 'sysfs', 'devtmpfs')
                       OR match(Attributes['mountpoint'], '^/(proc|sys|dev|run|snap)(/|$)') OR match(Attributes['mountpoint'], '^/var/lib/docker(/|$)'))
                """);
            Assert.Equal("0", pseudo);
        }
    }
}
