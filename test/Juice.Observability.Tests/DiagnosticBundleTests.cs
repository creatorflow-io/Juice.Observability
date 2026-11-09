using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// What a diagnostic bundle (Phase 8, milestone 3) holds: the host's facts and the agent's settings, and the last lines of only the logs the host
    /// lists, with credentials redacted, within its size limit.
    /// </summary>
    public class DiagnosticBundleTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "juice-bundle-" + Guid.NewGuid().ToString("N"));
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public DiagnosticBundleTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        [Theory]
        [InlineData("db password=hunter2 connected", "db password=[redacted] connected")]
        [InlineData("Server=pg;Password=s3cr3t;Database=x", "Server=pg;Password=[redacted];Database=x")]
        [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer [redacted]")]
        [InlineData("{\"client_secret\": \"abc123\"}", "{\"client_secret\": [redacted]}")]
        [InlineData("api_key=AKIA1234&next=1", "api_key=[redacted]&next=1")]
        [InlineData("token: 'abc def'", "token: [redacted]")]
        [InlineData("DefaultEndpointsProtocol=https;AccountName=shop;AccountKey=abc+def/123==;EndpointSuffix=core.windows.net",
            "DefaultEndpointsProtocol=https;AccountName=shop;AccountKey=[redacted];EndpointSuffix=core.windows.net")]
        [InlineData("Endpoint=sb://bus/;SharedAccessKeyName=root;SharedAccessKey=xyz=", "Endpoint=sb://bus/;SharedAccessKeyName=root;SharedAccessKey=[redacted]")]
        [InlineData("connecting to postgres://orders:hunter2@pg:5432/orders", "connecting to postgres://orders:[redacted]@pg:5432/orders")]
        [InlineData("calling api with Bearer eyJhbGciOiJIUzI1NiJ9.e30.sig", "calling api with Bearer [redacted]")]
        [InlineData("see https://example.com/docs@v2 for more", "see https://example.com/docs@v2 for more")]
        [InlineData("request served in 12 ms", "request served in 12 ms")]
        [InlineData("tokens counted: 5", "tokens counted: 5")]
        public void What_looks_like_a_credential_is_redacted(string line, string expected)
            => Assert.Equal(expected, DiagnosticBundle.Redact(line).Line);

        [Fact]
        public void A_log_is_read_from_its_end_and_a_line_cut_by_the_window_is_dropped()
        {
            var path = Path.Combine(_root, "big.log");
            var builder = new StringBuilder();
            for (var i = 0; i < 20_000; i++) builder.Append("line ").Append(i).Append(' ').Append(new string('x', 40)).Append('\n');
            File.WriteAllText(path, builder.ToString());
            var all = DiagnosticBundle.Tail(new FileInfo(path), 100_000);
            Assert.True(all.Length * 47 <= BundleLimits.MaxBytesPerLog + 100, "read past the window");
            Assert.StartsWith("line ", all[0], StringComparison.Ordinal);
            Assert.StartsWith("line 19999 ", all[^1], StringComparison.Ordinal);
            Assert.Equal(["line 19998", "line 19999"], DiagnosticBundle.Tail(new FileInfo(path), 2).Select(l => string.Join(' ', l.Split(' ').Take(2))));
        }

        /// <summary>The bundle with host.summary and disk.usage allowed too, unless <paramref name="factsAllowed"/> is false.</summary>
        private LocalAllowlist Allowlist(string[] logs, bool factsAllowed = true, List<string>? diskPaths = null)
        {
            var allowlist = new LocalAllowlist { Actions = { [AgentActions.DiagnosticsBundle] = new ActionPolicy { LogFiles = [.. logs] } } };
            if (factsAllowed)
            {
                allowlist.Actions[AgentActions.HostSummary] = new ActionPolicy();
                allowlist.Actions[AgentActions.DiskUsage] = new ActionPolicy { PathPrefixes = diskPaths ?? [] };
            }
            return allowlist;
        }

        private LocalAllowlist Allowlist(params string[] logs) => Allowlist(logs, true);

        private static DiagnosticBundle Bundle(LocalAllowlist allowlist, IDiagnostics? diagnostics = null)
            => new(diagnostics ?? new Diagnostics("/", "1.2.3"), "1.2.3", allowlist, TimeProvider.System);

        private static Dictionary<string, string> Read(byte[] zip)
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            return archive.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
        }

        [Fact]
        public async Task A_bundle_holds_the_host_the_agent_and_the_listed_logs_only_redacted()
        {
            var app = Path.Combine(_root, "app.log");
            File.WriteAllLines(app, ["starting", "connecting with password=hunter2", "ready"]);
            var notListed = Path.Combine(_root, "other.log");
            File.WriteAllText(notListed, "private");
            var (zip, summary) = await Bundle(Allowlist(app, Path.Combine(_root, "missing.log"))).MakeAsync(new DiagnosticsBundleParameters(2), Ct);
            var files = Read(zip);
            Assert.Equal(["agent.json", "disk.json", "host.json", "logs/01-app.log", "manifest.json"], files.Keys.Order());
            Assert.Equal("connecting with password=[redacted]\nready\n", files["logs/01-app.log"]);
            Assert.DoesNotContain(files.Values, v => v.Contains("hunter2", StringComparison.Ordinal) || v.Contains("private", StringComparison.Ordinal));
            Assert.Contains("\"1.2.3\"", files["agent.json"], StringComparison.Ordinal);
            Assert.Equal(1, summary.Redacted);
            Assert.Contains(summary.Skipped, s => s.Contains("missing.log: missing", StringComparison.Ordinal));
            Assert.Equal(zip.Length, summary.SizeBytes);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)), summary.Sha256);
            Assert.Equal(2, JsonDocument.Parse(files["manifest.json"]).RootElement.GetProperty("logLines").GetInt32());
        }

        [Fact]
        public async Task A_bundle_stays_under_its_limit_leaving_out_logs_that_dont_fit()
        {
            var random = new Random(7);
            var logs = Enumerable.Range(1, 14).Select(i =>
            {
                var path = Path.Combine(_root, $"noise-{i}.log");
                // Lines that don't compress, so each excerpt takes its whole 512 KB in the zip too.
                File.WriteAllLines(path, Enumerable.Range(0, 6000).Select(_ => Convert.ToBase64String(Enumerable.Range(0, 66).Select(_ => (byte)random.Next(256)).ToArray())));
                return path;
            }).ToArray();
            var (zip, summary) = await Bundle(Allowlist(logs)).MakeAsync(new DiagnosticsBundleParameters(5000), Ct);
            Assert.True(zip.Length <= BundleLimits.MaxBytes, $"{zip.Length} bytes");
            Assert.Contains(summary.Skipped, s => s.Contains("no room left", StringComparison.Ordinal));
            Assert.True(summary.Entries.Count(e => e.Source is not null) >= 5, "it should still carry most of what fits");
        }

        [Fact]
        public async Task A_bundle_reads_the_host_only_as_far_as_the_hosts_own_diagnostics_may()
        {
            var (none, noneSummary) = await Bundle(Allowlist([], factsAllowed: false)).MakeAsync(new DiagnosticsBundleParameters(null), Ct);
            Assert.Equal(["agent.json", "manifest.json"], Read(none).Keys.Order());
            Assert.Contains(noneSummary.Skipped, s => s.Contains("host.summary isn't allowed", StringComparison.Ordinal));
            Assert.Contains(noneSummary.Skipped, s => s.Contains("disk.usage isn't allowed", StringComparison.Ordinal));

            // disk.usage bounded to one path: the bundle reports that path, not every filesystem.
            var (bounded, _) = await Bundle(Allowlist([], factsAllowed: true, diskPaths: [_root])).MakeAsync(new DiagnosticsBundleParameters(null), Ct);
            var disk = JsonDocument.Parse(Read(bounded)["disk.json"]).RootElement;
            var mounts = disk.EnumerateArray().SelectMany(d => d.GetProperty("filesystems").EnumerateArray()).Select(f => f.GetProperty("mountPoint").GetString()).ToList();
            Assert.Equal([_root], mounts);
        }

        /// <summary>A diagnostic that stays busy: stands in for a read stuck on a hung mount.</summary>
        private sealed class Busy : IDiagnostics
        {
            public Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken) => throw new DiagnosticBusyException();
        }

        /// <summary>Records each read it's asked for.</summary>
        private sealed class Recording : IDiagnostics
        {
            public List<string> Reads { get; } = [];

            public Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken)
            {
                Reads.Add(parameters.GetType().Name);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            }
        }

        [Fact]
        public async Task A_bundle_reads_through_the_agents_own_diagnostics_so_a_stuck_read_isnt_stacked()
        {
            var recording = new Recording();
            await Bundle(Allowlist(), recording).MakeAsync(new DiagnosticsBundleParameters(null), Ct);
            Assert.Equal([nameof(HostSummaryParameters), nameof(DiskUsageParameters)], recording.Reads);
            await Assert.ThrowsAsync<DiagnosticBusyException>(() => Bundle(Allowlist(), new Busy()).MakeAsync(new DiagnosticsBundleParameters(null), Ct));
        }

        [Fact]
        public void A_host_lists_absolute_log_files_and_not_too_many()
        {
            Assert.Contains(Allowlist("logs/app.log").Validate(), e => e.Contains("absolute", StringComparison.Ordinal));
            Assert.Contains(Allowlist([.. Enumerable.Range(0, 21).Select(i => $"/var/log/{i}.log")]).Validate(), e => e.Contains("at most", StringComparison.Ordinal));
            Assert.Empty(Allowlist("/var/log/app.log").Validate());
            var limits = Allowlist("/var/log/app.log").Report(new AgentActionCatalog([])).Actions.Single(a => a.Action.Name == AgentActions.DiagnosticsBundle).Limits;
            Assert.Contains(limits, l => l.Contains("/var/log/app.log", StringComparison.Ordinal));
        }

        [Fact]
        public void A_bundle_asks_for_a_bounded_number_of_lines_and_is_low_risk()
        {
            Assert.NotNull(AgentActions.Parse(AgentActions.DiagnosticsBundle, JsonSerializer.SerializeToElement(new { }), out _));
            Assert.NotNull(AgentActions.Parse(AgentActions.DiagnosticsBundle, JsonSerializer.SerializeToElement(new { logLines = 500 }), out _));
            Assert.Null(AgentActions.Parse(AgentActions.DiagnosticsBundle, JsonSerializer.SerializeToElement(new { logLines = 1_000_000 }), out _));
            Assert.Null(AgentActions.Parse(AgentActions.DiagnosticsBundle, JsonSerializer.SerializeToElement(new { path = "/etc/shadow" }), out _));
            Assert.Equal(ActionRisks.Low, AgentActions.Descriptors.Single(d => d.Name == AgentActions.DiagnosticsBundle).RiskOf());
        }
    }
}
