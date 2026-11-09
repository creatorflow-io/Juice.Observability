using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agents;
using Juice.Observability.Data;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The two remediations (Phase 8, milestone 2) on the host: what a command may name, what the host's allowlist lets through, what a cleanup
    /// deletes and never deletes; and on the platform, what counts as recovered.
    /// </summary>
    public class RemediationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "juice-cleanup-" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        public RemediationTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static object? Parse(string action, object parameters) => AgentActions.Parse(action, JsonSerializer.SerializeToElement(parameters), out _);

        [Theory]
        [InlineData("orders.service", true)]
        [InlineData("worker@2.service", true)]
        [InlineData("W3SVC", true)]
        [InlineData("-orders", false)]
        [InlineData("--now", false)]
        [InlineData("orders; reboot", false)]
        [InlineData("orders service", false)]
        [InlineData("../orders", false)]
        [InlineData("/etc/systemd/system/orders.service", false)]
        [InlineData("$(id)", false)]
        [InlineData("", false)]
        public void A_service_is_a_plain_name_never_an_option_a_path_or_shell(string service, bool valid)
            => Assert.Equal(valid, Parse(AgentActions.ServiceRestart, new { service }) is not null);

        [Fact]
        public void Systemctl_gets_the_unit_as_its_own_argument_after_the_options_end()
            => Assert.Equal(["restart", "--", "orders.service"], Systemd.Arguments("restart", "orders.service"));

        [Fact]
        public void A_cleanup_names_an_absolute_folder_and_a_bounded_keep_time()
        {
            Assert.NotNull(Parse(AgentActions.FilesCleanup, new { folder = "/var/log/legacy" }));
            Assert.NotNull(Parse(AgentActions.FilesCleanup, new { folder = "/var/log/legacy", keepHours = 100, dryRun = true }));
            Assert.Null(Parse(AgentActions.FilesCleanup, new { folder = "var/log" }));
            Assert.Null(Parse(AgentActions.FilesCleanup, new { folder = "/var/log/../../etc" }));
            Assert.Null(Parse(AgentActions.FilesCleanup, new { folder = "/var/log/legacy", keepHours = 0 }));
            Assert.Null(Parse(AgentActions.FilesCleanup, new { folder = "/var/log/legacy", pattern = "*" }));
        }

        [Fact]
        public void A_dry_run_is_low_risk_and_a_real_cleanup_medium_and_a_restart_high()
        {
            var cleanup = AgentActions.Descriptors.Single(d => d.Name == AgentActions.FilesCleanup);
            Assert.Equal(ActionRisks.Low, AgentActions.RiskOf(cleanup, JsonSerializer.SerializeToElement(new { folder = "/x", dryRun = true })));
            Assert.Equal(ActionRisks.Medium, AgentActions.RiskOf(cleanup, JsonSerializer.SerializeToElement(new { folder = "/x" })));
            Assert.Equal(ActionRisks.High, AgentActions.Descriptors.Single(d => d.Name == AgentActions.ServiceRestart).RiskOf());
            Assert.Contains(AgentActions.ServiceRestart, AgentActions.Changing);
        }

        private LocalAllowlist Allowlist(int keepHours = 24, bool recursive = false, string pattern = "*.log") => new()
        {
            Actions =
            {
                [AgentActions.ServiceRestart] = new ActionPolicy { Services = ["orders.service"] },
                [AgentActions.FilesCleanup] = new ActionPolicy { Folders = [new CleanupFolder { Path = _root, Pattern = pattern, KeepHours = keepHours, Recursive = recursive }] },
            },
        };

        [Fact]
        public void The_host_allows_only_the_services_and_folders_it_lists_and_never_a_shorter_keep_time()
        {
            var allowlist = Allowlist();
            Assert.Null(allowlist.Check(AgentActions.ServiceRestart, new ServiceRestartParameters("orders.service")));
            Assert.Equal(RejectionReasons.OutsideLocalBounds, allowlist.Check(AgentActions.ServiceRestart, new ServiceRestartParameters("sshd.service")));
            Assert.Null(allowlist.Check(AgentActions.FilesCleanup, new FilesCleanupParameters(_root, null, false)));
            Assert.Null(allowlist.Check(AgentActions.FilesCleanup, new FilesCleanupParameters(_root, 48, false)));
            Assert.Equal(RejectionReasons.OutsideLocalBounds, allowlist.Check(AgentActions.FilesCleanup, new FilesCleanupParameters(_root, 1, false)));
            Assert.Equal(RejectionReasons.OutsideLocalBounds, allowlist.Check(AgentActions.FilesCleanup, new FilesCleanupParameters(Path.Combine(_root, "sub"), null, false)));
            Assert.Equal(RejectionReasons.OutsideLocalBounds, new LocalAllowlist { Actions = { [AgentActions.ServiceRestart] = new ActionPolicy() } }
                .Check(AgentActions.ServiceRestart, new ServiceRestartParameters("orders.service")));
        }

        [Fact]
        public void The_platform_is_told_which_services_and_folders_and_how_long_files_stay()
        {
            var report = Allowlist().Report(new AgentActionCatalog([])).Actions.ToDictionary(a => a.Action.Name);
            Assert.Equal(["orders.service"], report[AgentActions.ServiceRestart].Action.Parameters.Single(p => p.Name == "service").Allowed);
            Assert.Equal([_root], report[AgentActions.FilesCleanup].Action.Parameters.Single(p => p.Name == "folder").Allowed);
            Assert.Contains(report[AgentActions.FilesCleanup].Limits, l => l.Contains("*.log", StringComparison.Ordinal) && l.Contains("24 h", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("/", "*.log", 24, "root")]
        [InlineData("C:\\", "*.log", 24, "root")]
        [InlineData("/var/log/app", "../*.log", 24, "Pattern")]
        [InlineData("/var/log/app", "sub/*.log", 24, "Pattern")]
        [InlineData("/var/log/app", "*.log", 0, "KeepHours")]
        [InlineData("relative/logs", "*.log", 24, "absolute")]
        public void A_host_cant_list_a_root_a_pattern_with_folders_or_no_keep_time(string path, string pattern, int keepHours, string expected)
        {
            var errors = new LocalAllowlist { Actions = { [AgentActions.FilesCleanup] = new ActionPolicy { Folders = [new CleanupFolder { Path = path, Pattern = pattern, KeepHours = keepHours }] } } }
                .Validate().ToList();
            Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
        }

        private string Write(string relative, TimeSpan age)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('x', 100));
            File.SetLastWriteTimeUtc(path, (_clock.GetUtcNow() - age).UtcDateTime);
            return path;
        }

        [Fact]
        public void A_cleanup_deletes_only_old_matching_files_in_its_folder()
        {
            var old = Write("app-1.log", TimeSpan.FromDays(3));
            var recent = Write("app-2.log", TimeSpan.FromHours(2));
            var other = Write("data.db", TimeSpan.FromDays(30));
            var nested = Write(Path.Combine("archive", "app-0.log"), TimeSpan.FromDays(30));
            var result = new Remediation(Allowlist(), _clock).Clean(new FilesCleanupParameters(_root, null, false), CancellationToken.None);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(nested));   // not Recursive
            Assert.Equal((1, 100L, 1, false), (result.FilesRemoved, result.BytesRemoved, result.FilesKept, result.Limited));
            Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromHours(2), result.OldestKept!.Value, TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void A_longer_keep_time_keeps_more_and_recursive_reaches_subfolders()
        {
            var threeDays = Write("app-1.log", TimeSpan.FromDays(3));
            var nested = Write(Path.Combine("archive", "app-0.log"), TimeSpan.FromDays(30));
            var result = new Remediation(Allowlist(recursive: true), _clock).Clean(new FilesCleanupParameters(_root, 24 * 7, false), CancellationToken.None);
            Assert.True(File.Exists(threeDays));
            Assert.False(File.Exists(nested));
            Assert.Equal((1, 24 * 7), (result.FilesRemoved, result.KeepHours));
        }

        [Fact]
        public void A_dry_run_deletes_nothing_and_says_what_would_go()
        {
            var old = Write("app-1.log", TimeSpan.FromDays(3));
            var result = new Remediation(Allowlist(), _clock).Clean(new FilesCleanupParameters(_root, null, true), CancellationToken.None);
            Assert.True(File.Exists(old));
            Assert.Equal((true, 1), (result.DryRun, result.FilesRemoved));
        }

        [Fact]
        public void A_run_stops_at_its_limit_and_says_so()
        {
            for (var i = 0; i < 5; i++) Write($"app-{i}.log", TimeSpan.FromDays(3));
            var result = new Remediation(Allowlist(), _clock) { MaxFiles = 3 }.Clean(new FilesCleanupParameters(_root, null, false), CancellationToken.None);
            Assert.Equal((3, true), (result.FilesRemoved, result.Limited));
            Assert.Equal(2, Directory.GetFiles(_root).Length);
        }

        [Fact]
        public void A_link_is_never_followed_or_deleted()
        {
            var outside = Path.Combine(Path.GetTempPath(), "juice-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            try
            {
                var target = Path.Combine(outside, "precious.log");
                File.WriteAllText(target, "keep");
                File.SetLastWriteTimeUtc(target, (_clock.GetUtcNow() - TimeSpan.FromDays(30)).UtcDateTime);
                try
                {
                    File.CreateSymbolicLink(Path.Combine(_root, "link.log"), target);
                    Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException && OperatingSystem.IsWindows())
                {
                    // Symbolic links need Developer Mode on Windows; a junction doesn't, and is the same kind of reparse point to walk out through.
                    using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                    {
                        ArgumentList = { "/c", "mklink", "/J", Path.Combine(_root, "linked"), outside }, RedirectStandardOutput = true, UseShellExecute = false,
                    })!;
                    mklink.WaitForExit();
                    Assert.True(mklink.ExitCode == 0 && Directory.Exists(Path.Combine(_root, "linked", "")), "couldn't create a junction either");
                }
                var result = new Remediation(Allowlist(recursive: true), _clock).Clean(new FilesCleanupParameters(_root, null, false), CancellationToken.None);
                Assert.True(File.Exists(target));
                Assert.Equal(0, result.FilesRemoved);
            }
            finally
            {
                // The link itself first: removing it never touches what it points to.
                if (Directory.Exists(Path.Combine(_root, "linked"))) Directory.Delete(Path.Combine(_root, "linked"));
                Directory.Delete(outside, recursive: true);
            }
        }

        [Fact]
        public void A_folder_the_host_no_longer_lists_fails_the_run()
            => Assert.Throws<ActionFailedException>(() => new Remediation(new LocalAllowlist(), _clock).Clean(new FilesCleanupParameters(_root, null, false), CancellationToken.None));

        private static AgentCommand Command(AgentCommandState state, DateTimeOffset? completed, string? reason = null) => new()
        {
            Action = AgentActions.ServiceRestart, Parameters = "{}", IssuedBy = "a", Nonce = "n", IdempotencyKey = "k", Payload = "p", Signature = "s", KeyId = "k",
            State = state, CompletedAt = completed, Reason = reason,
        };

        private static HealthSnapshot Snapshot(HealthState state, DateTimeOffset evaluated, DateTimeOffset? evidence) => new()
        {
            PolicyVersion = "test", State = state, EvaluatedAt = evaluated, LastEvidenceAt = evidence,
        };

        [Fact]
        public void Recovery_is_verified_only_by_healthy_evidence_newer_than_the_action()
        {
            var done = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
            var window = TimeSpan.FromMinutes(10);
            var succeeded = Command(AgentCommandState.Succeeded, done);
            // Still running: nothing to say yet.
            Assert.Null(ActionEvidence.Verify(Command(AgentCommandState.Delivered, null), null, done, window));
            // Healthy, but by telemetry from before the action: waits.
            Assert.Null(ActionEvidence.Verify(succeeded, Snapshot(HealthState.Healthy, done.AddMinutes(1), done.AddSeconds(-5)), done.AddMinutes(1), window));
            // Still unhealthy within the window: waits.
            Assert.Null(ActionEvidence.Verify(succeeded, Snapshot(HealthState.Unhealthy, done.AddMinutes(1), done.AddMinutes(1)), done.AddMinutes(1), window));
            // Healthy by newer telemetry: verified.
            Assert.Equal(ActionVerification.Verified, ActionEvidence.Verify(succeeded, Snapshot(HealthState.Healthy, done.AddMinutes(2), done.AddMinutes(2)), done.AddMinutes(2), window)!.Value.State);
            // The window passed without it: not verified, saying why.
            var late = ActionEvidence.Verify(succeeded, Snapshot(HealthState.Unhealthy, done.AddMinutes(11), done.AddMinutes(11)), done.AddMinutes(11), window)!.Value;
            Assert.Equal(ActionVerification.NotVerified, late.State);
            Assert.Contains("unhealthy", late.Note, StringComparison.Ordinal);
            // The action itself failed: not verified, whatever the health says.
            var failed = ActionEvidence.Verify(Command(AgentCommandState.Failed, done, "systemctl not found"), Snapshot(HealthState.Healthy, done, done), done, window)!.Value;
            Assert.Equal(ActionVerification.NotVerified, failed.State);
            Assert.Contains("systemctl not found", failed.Note, StringComparison.Ordinal);
        }
    }
}
