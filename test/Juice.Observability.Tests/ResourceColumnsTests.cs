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
        public void Every_column_a_platform_query_names_is_one_the_script_adds()
        {
            // The queries name the columns in their SQL text (review of milestone 5): a misspelt one would compile and fail only in
            // ClickHouse. JuiceAgent is the agent's entry point, not a column.
            var columns = ResourceColumns.Keys.Keys.Append("JuiceAgent").ToHashSet(StringComparer.Ordinal);
            var unknown = Directory.EnumerateFiles(Path.Combine(Repository(), "src"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\bJuice[A-Z][A-Za-z]*\b")
                    .Where(m => !columns.Contains(m.Value))
                    .Select(m => $"{Path.GetFileName(file)}: {m.Value}"))
                .ToList();

            Assert.Empty(unknown);
            Assert.Equal(ResourceColumns.PodUid, ResourceColumns.ColumnOf("k8s.pod.uid"));
            Assert.Null(ResourceColumns.ColumnOf("k8s.deployment.name"));
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
