using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// How the agent copes when things go wrong: a diagnostic that throws, a command that was interrupted, unreadable files and a renewal cut short
    /// mustn't stop it or leave it unable to start; and <c>disk.usage</c> reports nothing outside the path its allowlist permits.
    /// </summary>
    public sealed class AgentResilienceTests : IDisposable
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private static readonly ECDsa PlatformKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly Guid Me = Guid.CreateVersion7();
        private static readonly AgentIdentity Identity = new(Me, "acme", PlatformKey, CommandSignature.KeyId(PlatformKey));
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"juice-agent-resilience-{Guid.NewGuid():N}");

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static AgentService Service() => new(Options.Create(new AgentOptions()),
            Options.Create(new LocalAllowlist { Actions = { [AgentActions.HostSummary] = new ActionPolicy() } }), TimeProvider.System, NullLogger<AgentService>.Instance);

        private static (CommandEnvelope Envelope, SignedCommand Signed) Command()
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", Me, AgentActions.HostSummary,
                JsonSerializer.SerializeToElement(new { }), Now, Now.AddMinutes(2), nonce, nonce + "k", null);
            var bytes = CommandSignature.Serialize(envelope);
            return (envelope, new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(PlatformKey, bytes), Identity.CommandKeyId));
        }

        /// <summary>A diagnostic that fails in a way nobody planned for.</summary>
        private sealed class Throwing(Exception exception) : IDiagnostics
        {
            public int Runs;

            public Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken)
            {
                Runs++;
                throw exception;
            }
        }

        [Fact]
        public async Task A_diagnostic_that_throws_is_reported_failed_and_the_agent_goes_on_Async()
        {
            var (_, signed) = Command();
            var result = await Service().HandleAsync(signed, Identity, new CommandMemory(null), new Throwing(new FormatException("bad /proc/loadavg")), Ct);
            Assert.Equal((CommandOutcome.Failed, nameof(FormatException)), (result!.Outcome, result.Reason));
        }

        [Fact]
        public async Task A_command_interrupted_before_it_finished_is_not_run_again_Async()
        {
            var (envelope, signed) = Command();
            var memory = new CommandMemory(null);
            memory.Record(envelope);   // started, then the agent stopped: no result
            var diagnostics = new Throwing(new InvalidOperationException("would crash again"));

            var result = await Service().HandleAsync(signed, Identity, memory, diagnostics, Ct);

            Assert.Equal((CommandOutcome.Failed, AgentService.Interrupted), (result!.Outcome, result.Reason));
            Assert.Equal(0, diagnostics.Runs);
            // And it answers the same way again, still without running it.
            Assert.Equal(result, await Service().HandleAsync(signed, Identity, memory, diagnostics, Ct));
            Assert.Equal(0, diagnostics.Runs);
        }

        [Fact]
        public async Task The_agent_keeps_running_when_its_files_dont_read_or_the_platform_is_away_Async()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "identity.json"), "not json");
            File.WriteAllText(Path.Combine(_directory, "agent.pem"), "not a certificate");
            var ca = Path.Combine(_directory, "ca.crt");
            using (var self = AgentPki.SelfSignedCa("x")) File.WriteAllText(ca, self.ExportCertificatePem());
            var agent = new AgentService(Options.Create(new AgentOptions { PlatformUrl = "https://localhost:1", PlatformCaFile = ca, DataDirectory = _directory, PollWaitSeconds = 1 }),
                Options.Create(new LocalAllowlist()), TimeProvider.System, NullLogger<AgentService>.Instance);

            await agent.StartAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.False(agent.ExecuteTask!.IsCompleted, agent.ExecuteTask.Exception?.ToString());
            await agent.StopAsync(CancellationToken.None);
        }

        [Fact]
        public void A_command_memory_that_doesnt_read_is_set_aside_and_the_agent_starts_with_an_empty_one()
        {
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, "commands.json");
            File.WriteAllText(file, "{ cut sh");
            var memory = CommandMemory.Open(file, out var setAside);
            Assert.NotNull(setAside);
            Assert.Equal("{ cut sh", File.ReadAllText(setAside));
            var (envelope, _) = Command();
            Assert.Equal(Recall.New, memory.Recall(envelope, Now).Recall);
            memory.Record(envelope);
            Assert.Equal(Recall.Redelivered, CommandMemory.Open(file, out var again).Recall(envelope, Now).Recall);
            Assert.Null(again);
        }

        [Fact]
        public async Task An_agent_whose_command_memory_doesnt_read_still_starts_Async()
        {
            var store = new AgentStore(_directory);
            var (key, pem) = Certificate("host");
            using var command = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            store.SaveEnrollment(key, new EnrollResponse(Me, "acme", pem, command.ExportSubjectPublicKeyInfoPem(), CommandSignature.KeyId(command)));
            File.WriteAllText(store.MemoryFile, "not json");
            var ca = Path.Combine(_directory, "ca.crt");
            using (var self = AgentPki.SelfSignedCa("x")) File.WriteAllText(ca, self.ExportCertificatePem());
            var agent = new AgentService(Options.Create(new AgentOptions { PlatformUrl = "https://localhost:1", PlatformCaFile = ca, DataDirectory = _directory, PollWaitSeconds = 1 }),
                Options.Create(new LocalAllowlist()), TimeProvider.System, NullLogger<AgentService>.Instance);

            await agent.StartAsync(Ct);
            for (var i = 0; i < 50 && !Directory.GetFiles(_directory, "commands.json.unreadable-*").Any(); i++) await Task.Delay(100, Ct);
            Assert.Single(Directory.GetFiles(_directory, "commands.json.unreadable-*"));
            Assert.False(agent.ExecuteTask!.IsCompleted);
            await agent.StopAsync(CancellationToken.None);
        }

        // A time limit of its own: if reads stop being abandoned, this fails instead of hanging the run.
        [Fact(Timeout = 10_000)]
        public async Task A_read_that_hangs_is_abandoned_at_its_timeout_and_the_next_one_waits_for_it_Async()
        {
            var diagnostics = new Diagnostics("/", "test");
            using var release = new ManualResetEventSlim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostics.BlockingAsync(() => { release.Wait(); return 1; }, timeout.Token));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"waited {started.Elapsed} for a hung read");

            await Assert.ThrowsAsync<DiagnosticBusyException>(() => diagnostics.BlockingAsync(() => 2, Ct));
            release.Set();
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    Assert.Equal(3, await diagnostics.BlockingAsync(() => 3, Ct));
                    return;
                }
                catch (DiagnosticBusyException)
                {
                    await Task.Delay(20, Ct);
                }
            }
            Assert.Fail("still busy after the hung read returned");
        }

        private static (ECDsa Key, string Pem) Certificate(string name)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var certificate = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddMinutes(-1), Now.AddDays(1));
            return (key, certificate.ExportCertificatePem());
        }

        [Fact]
        public void A_renewal_replaces_the_key_and_certificate_as_one_file()
        {
            var store = new AgentStore(_directory);
            var (firstKey, firstPem) = Certificate("first");
            using var command = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            store.SaveEnrollment(firstKey, new EnrollResponse(Me, "acme", firstPem, command.ExportSubjectPublicKeyInfoPem(), CommandSignature.KeyId(command)));
            var (secondKey, secondPem) = Certificate("second");

            store.SaveRenewal(secondKey, secondPem);

            Assert.Equal(["agent.pem", "identity.json"], Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
            using var loaded = store.LoadCertificate();
            Assert.Equal("CN=second", loaded.Subject);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(secondKey.ExportSubjectPublicKeyInfo(), loaded.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo());
        }

        [Fact]
        public void An_agent_enrolled_with_two_files_still_loads_and_moves_to_one_at_its_next_renewal()
        {
            Directory.CreateDirectory(_directory);
            var (key, pem) = Certificate("legacy");
            File.WriteAllText(Path.Combine(_directory, "agent.key"), key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(Path.Combine(_directory, "agent.crt"), pem);
            var store = new AgentStore(_directory);
            using (var loaded = store.LoadCertificate()) Assert.Equal("CN=legacy", loaded.Subject);

            var (next, nextPem) = Certificate("next");
            store.SaveRenewal(next, nextPem);
            Assert.False(File.Exists(Path.Combine(_directory, "agent.key")));
            using var renewed = store.LoadCertificate();
            Assert.Equal("CN=next", renewed.Subject);
        }

        // ─── disk.usage stays inside its path ───────────────────────────────────

        private static Diagnostics.FilesystemData Fs(string mount, long total = 100) => new(mount, "ext4", total, 10, 5);

        private static readonly Diagnostics.FilesystemData[] Mounts =
        [
            Fs("/", 500), Fs("/boot", 1), Fs("/hostfs", 400), Fs("/hostfs/var", 300), Fs("/hostfs/var/lib/docker", 200), Fs("/hostfsx", 9), Fs("/data", 50),
        ];

        private static List<string> Selected(string? path) => [.. Diagnostics.Select(Mounts, path, StringComparison.Ordinal).Filesystems.Select(f => $"{f.MountPoint}:{f.TotalBytes}")];

        [Fact]
        public void Disk_usage_reports_only_what_is_mounted_at_or_under_the_path()
            => Assert.Equal(["/hostfs/var:300", "/hostfs/var/lib/docker:200"], Selected("/hostfs/var"));

        [Fact]
        public void A_path_inside_a_filesystem_is_reported_under_the_path_never_the_mount_above_it()
        {
            // /hostfs/etc lives on /hostfs: its size is reported, named /hostfs/etc, and nothing above or beside it.
            Assert.Equal(["/hostfs/etc:400"], Selected("/hostfs/etc"));
            // A path on the root filesystem shows the root's numbers under that path, and never "/" itself.
            Assert.Equal(["/srv/app:500"], Selected("/srv/app"));
        }

        [Fact]
        public void A_name_that_merely_starts_like_the_path_is_not_under_it()
            => Assert.DoesNotContain(Selected("/hostfs"), f => f.StartsWith("/hostfsx", StringComparison.Ordinal));

        [Fact]
        public void With_no_path_every_filesystem_is_reported() => Assert.Equal(Mounts.Length, Selected(null).Count);

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}
