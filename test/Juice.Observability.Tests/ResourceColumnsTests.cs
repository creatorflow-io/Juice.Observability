using System.Text.RegularExpressions;
using Juice.Observability.Discovery;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 11 R12: the resource attributes the platform reads are columns of their own (<see cref="ResourceColumns"/>), added to the
    /// Collector's tables by <c>deploy/local/clickhouse/resource-columns.sh</c>.
    /// </summary>
    public class ResourceColumnsTests
    {
        private static readonly string[] Tables =
        [
            "otel_traces", "otel_logs", "otel_metrics_gauge", "otel_metrics_sum", "otel_metrics_histogram",
            "otel_metrics_exponential_histogram", "otel_metrics_summary",
        ];

        private static string Repository()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "deploy", "local", "clickhouse", "resource-columns.sh")))
                {
                    return dir.FullName;
                }
            }
            throw new InvalidOperationException("deploy/local/clickhouse/resource-columns.sh not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void The_script_adds_exactly_the_columns_the_platform_reads()
        {
            var script = File.ReadAllText(Path.Combine(Repository(), "deploy", "local", "clickhouse", "resource-columns.sh"));
            var list = Regex.Match(script, "^columns=\"(?<list>[^\"]*)\"", RegexOptions.Multiline).Groups["list"].Value;
            var added = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToDictionary(pair => pair[1], pair => pair[0]);

            Assert.Equal(ResourceColumns.Keys.OrderBy(kv => kv.Key), added.OrderBy(kv => kv.Key));
        }

        [Fact]
        public void No_platform_query_reads_a_listed_key_from_the_map()
        {
            var keys = ResourceColumns.Keys.Values.ToHashSet();
            var reads = Directory.EnumerateFiles(Path.Combine(Repository(), "src"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"ResourceAttributes\['(?<key>[^']+)'\]")
                    .Where(m => keys.Contains(m.Groups["key"].Value))
                    .Select(m => $"{Path.GetFileName(file)}: {m.Value}"))
                .Where(read => !read.StartsWith(nameof(ResourceColumns) + ".cs", StringComparison.Ordinal))
                .ToList();

            Assert.Empty(reads);
        }

        [Fact]
        public async Task Every_column_holds_its_key_on_the_running_stack_Async()
        {
            LocalStack.RequireAvailable();
            var mismatches = string.Join(" + ", ResourceColumns.Keys.Select(kv => $"countIf({kv.Key} != ResourceAttributes['{kv.Value}'])"));
            foreach (var table in Tables)
            {
                var kinds = await LocalStack.ClickHouseAsync(
                    $"SELECT groupArray(concat(name, ' ', default_kind, ' ', type)) FROM system.columns WHERE database = 'otel' AND table = '{table}' AND name LIKE 'Juice%'");
                foreach (var column in ResourceColumns.Keys.Keys)
                {
                    Assert.Contains($"{column} MATERIALIZED LowCardinality(String)", kinds);
                }
                var time = table.StartsWith("otel_metrics_", StringComparison.Ordinal) ? "TimeUnix" : "Timestamp";
                Assert.Equal("0", await LocalStack.ClickHouseAsync(
                    $"SELECT {mismatches} FROM otel.{table} WHERE {time} > now() - INTERVAL 1 HOUR"));
            }
        }
    }
}
