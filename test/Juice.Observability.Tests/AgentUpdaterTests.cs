using System.Security.Cryptography;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// <c>agent.update</c> on the agent (roadmap Phase 7, milestone 2): a package installs only when it is exactly the bytes the command names, signed
    /// by a release key this host trusts, and unpacks inside its own directory; anything else is refused with nothing changed that runs. A package that
    /// checks out is handed to the launcher, and the update's result waits for the launcher's verdict.
    /// </summary>
    public sealed class AgentUpdaterTests : IDisposable
    {
        private const string Running = "1.0.0";
        private const string Next = "1.1.0";
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private static readonly ECDsa PlatformKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly ECDsa ReleaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly Guid Me = Guid.CreateVersion7();
        private static readonly AgentIdentity Identity = new(Me, "acme", PlatformKey, CommandSignature.KeyId(PlatformKey));
        private readonly string _home = Path.Combine(Path.GetTempPath(), $"juice-agent-home-{Guid.NewGuid():N}");

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private AgentUpdater Updater(Guid? trial = null, params ECDsa[] keys) => new(_home, keys.Length > 0 ? keys : [ReleaseKey], trial);

        private static Func<string, CancellationToken, Task<(byte[], byte[])>> Serves(byte[] package, ECDsa signer)
            => (_, _) => Task.FromResult((package, AgentPackages.Sign(signer, package)));

        private static AgentUpdateParameters For(byte[] package, string version = Next) => new(version, AgentPackages.Sha256(package));

        private string VersionDirectory(string version) => Path.Combine(_home, AgentHome.VersionsDirectory, version);

        private async Task<string> RefusalAsync(AgentUpdater updater, AgentUpdateParameters update, Func<string, CancellationToken, Task<(byte[], byte[])>> download)
        {
            var refused = await Assert.ThrowsAsync<UpdateFailedException>(() => updater.PrepareAsync(Guid.NewGuid(), update, Running, download, Ct));
            Assert.False(Directory.Exists(VersionDirectory(update.Version)), "a refused package was unpacked");
            Assert.False(File.Exists(Path.Combine(_home, AgentHome.UpdateFile)), "a refused package was asked for");
            Assert.Empty(Directory.Exists(Path.Combine(_home, AgentHome.VersionsDirectory)) ? Directory.EnumerateFileSystemEntries(Path.Combine(_home, AgentHome.VersionsDirectory)) : []);
            return refused.Message;
        }

        [Fact]
        public async Task A_package_that_checks_out_is_unpacked_beside_the_running_version_and_asked_for_Async()
        {
            var package = AgentPackageFiles.Zip(Next, extra: ("lib/helper.txt", "x"));
            var command = Guid.NewGuid();

            Assert.True(await Updater().PrepareAsync(command, For(package), Running, Serves(package, ReleaseKey), Ct));

            Assert.True(File.Exists(Path.Combine(VersionDirectory(Next), "juice-agent.dll")));
            Assert.True(File.Exists(Path.Combine(VersionDirectory(Next), "lib", "helper.txt")));
            var request = JsonSerializer.Deserialize<AgentHome.UpdateRequest>(File.ReadAllBytes(Path.Combine(_home, AgentHome.UpdateFile)), CommandSignature.Json)!;
            Assert.Equal(new AgentHome.UpdateRequest(command, Next), request);
            // No staging directory left behind.
            Assert.Equal([Next], Directory.EnumerateDirectories(Path.Combine(_home, AgentHome.VersionsDirectory)).Select(Path.GetFileName));
        }

        [Fact]
        public async Task Bytes_other_than_the_ones_the_command_names_are_refused_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            var other = AgentPackageFiles.Zip(Next, extra: ("extra.txt", "slipped in"));
            Assert.Equal("package_mismatch", await RefusalAsync(Updater(), For(package), Serves(other, ReleaseKey)));
        }

        [Fact]
        public async Task A_package_no_trusted_release_key_signed_is_refused_even_when_the_hash_matches_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Equal("invalid_package_signature", await RefusalAsync(Updater(), For(package), Serves(package, stranger)));
            // The platform's own command key is no release key either.
            Assert.Equal("invalid_package_signature", await RefusalAsync(Updater(), For(package), Serves(package, PlatformKey)));
            // A signature over other bytes.
            Assert.Equal("invalid_package_signature", await RefusalAsync(Updater(), For(package), (_, _) => Task.FromResult((package, AgentPackages.Sign(ReleaseKey, [1, 2, 3])))));
        }

        [Theory]
        [InlineData("../escaped.txt")]
        [InlineData("lib/../../escaped.txt")]
        [InlineData("/etc/escaped.txt")]
        [InlineData("C:/escaped.txt")]
        public async Task A_package_with_an_entry_outside_its_directory_is_refused_Async(string entry)
        {
            var package = AgentPackageFiles.Zip(Next, extra: (entry, "out"));
            Assert.Equal("invalid_package", await RefusalAsync(Updater(), For(package), Serves(package, ReleaseKey)));
            Assert.False(File.Exists(Path.Combine(_home, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "escaped.txt")));
        }

        [Theory]
        [InlineData("1.2.0", "juice-agent.dll")]         // signed, but another version than the command's
        [InlineData(Next, "missing.dll")]                // an entry point that isn't in it
        [InlineData(Next, "../juice-agent.dll")]         // an entry point outside it
        public async Task A_package_whose_manifest_doesnt_hold_is_refused_Async(string manifestVersion, string entryPoint)
        {
            var package = AgentPackageFiles.Zip(Next, manifestVersion, entryPoint);
            Assert.Equal("invalid_package", await RefusalAsync(Updater(), For(package), Serves(package, ReleaseKey)));
        }

        [Fact]
        public async Task Something_that_isnt_a_zip_is_refused_Async()
        {
            byte[] junk = [.. "PK not really a zip"u8];
            Assert.Equal("invalid_package", await RefusalAsync(Updater(), For(junk), Serves(junk, ReleaseKey)));
        }

        [Fact]
        public async Task Without_the_launcher_or_a_release_key_nothing_is_downloaded_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            var downloads = 0;
            Task<(byte[], byte[])> Count(string _, CancellationToken __)
            {
                downloads++;
                return Task.FromResult((package, AgentPackages.Sign(ReleaseKey, package)));
            }
            var outside = new AgentUpdater(null, [ReleaseKey], null);
            Assert.Equal("updates_unavailable", (await Assert.ThrowsAsync<UpdateFailedException>(() => outside.PrepareAsync(Guid.NewGuid(), For(package), Running, Count, Ct))).Message);
            var keyless = new AgentUpdater(_home, [], null);
            Assert.Equal("no_release_key", (await Assert.ThrowsAsync<UpdateFailedException>(() => keyless.PrepareAsync(Guid.NewGuid(), For(package), Running, Count, Ct))).Message);
            Assert.Equal(0, downloads);
        }

        [Fact]
        public async Task The_version_already_running_needs_nothing_Async()
        {
            var package = AgentPackageFiles.Zip(Running);
            Assert.False(await Updater().PrepareAsync(Guid.NewGuid(), For(package, Running), Running, (_, _) => throw new InvalidOperationException("downloaded"), Ct));
        }

        [Fact]
        public void Only_the_trial_it_was_started_for_is_confirmed_and_only_once()
        {
            var command = Guid.NewGuid();
            var confirmed = Path.Combine(_home, AgentHome.ConfirmedFile);
            Directory.CreateDirectory(_home);
            Updater().Confirm();
            Assert.False(File.Exists(confirmed), "a version not on trial confirmed");
            var trial = Updater(command);
            trial.Confirm();
            Assert.Equal(command.ToString(), File.ReadAllText(confirmed));
            File.Delete(confirmed);   // the launcher took it
            trial.Confirm();
            Assert.False(File.Exists(confirmed), "confirmed again after the launcher took it");
        }

        // --- in the agent service ---------------------------------------------------------------------------------------------------------------

        private AgentService Service(AgentUpdater updater)
        {
            var service = new AgentService(Options.Create(new AgentOptions()),
                Options.Create(new LocalAllowlist { Actions = { [AgentActions.AgentUpdate] = new ActionPolicy(), [AgentActions.HostSummary] = new ActionPolicy() } }),
                TimeProvider.System, NullLogger<AgentService>.Instance) { Updater = updater };
            return service;
        }

        private static (CommandEnvelope Envelope, SignedCommand Signed) Update(AgentUpdateParameters update)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", Me, AgentActions.AgentUpdate,
                JsonSerializer.SerializeToElement(update, CommandSignature.Json), Now, Now.AddMinutes(10), nonce, nonce + "k", null);
            var bytes = CommandSignature.Serialize(envelope);
            return (envelope, new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(PlatformKey, bytes), Identity.CommandKeyId));
        }

        private sealed class NoDiagnostics : IDiagnostics
        {
            public Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken) => throw new InvalidOperationException("an update ran as a diagnostic");
        }

        [Fact]
        public async Task An_update_that_checks_out_stops_the_agent_for_the_launcher_and_reports_nothing_yet_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            var service = Service(Updater());
            var (_, signed) = Update(For(package));

            Assert.Null(await service.HandleAsync(signed, Identity, new CommandMemory(null), new NoDiagnostics(), Ct, Serves(package, ReleaseKey)));
            Assert.True(service.RestartRequested);
        }

        [Fact]
        public async Task A_refused_update_is_reported_failed_with_its_reason_and_the_agent_stays_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var service = Service(Updater());
            var (envelope, signed) = Update(For(package));

            var result = await service.HandleAsync(signed, Identity, new CommandMemory(null), new NoDiagnostics(), Ct, Serves(package, stranger));

            Assert.Equal(new CommandResult(envelope.CommandId, CommandOutcome.Failed, "invalid_package_signature", null, result!.DurationMs), result);
            Assert.False(service.RestartRequested);
        }

        [Fact]
        public async Task The_update_a_version_is_on_trial_for_isnt_answered_until_the_launcher_decides_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            var (envelope, signed) = Update(For(package));
            var memory = new CommandMemory(null);
            memory.Record(envelope);   // the version before took it, then stopped for the launcher

            // Redelivered to the new version on trial: no "interrupted", no second install.
            var onTrial = Service(Updater(envelope.CommandId));
            Assert.Null(await onTrial.HandleAsync(signed, Identity, memory, new NoDiagnostics(), Ct, (_, _) => throw new InvalidOperationException("downloaded again")));
            Assert.False(onTrial.RestartRequested);

            // The launcher's verdict, reported by whichever version runs: then a redelivery gets that answer.
            var outcome = new AgentHome.UpdateOutcome(envelope.CommandId, Running, Next, false, "version 1.1.0 exited with code 1 during its trial", Now);
            memory.Complete(outcome.CommandId, AgentUpdater.Result(outcome));
            var again = await Service(Updater()).HandleAsync(signed, Identity, memory, new NoDiagnostics(), Ct);
            Assert.Equal(CommandOutcome.Failed, again!.Outcome);
            Assert.Equal("rolled_back: version 1.1.0 exited with code 1 during its trial", again.Reason);
        }

        [Fact]
        public async Task An_update_the_local_allowlist_doesnt_list_is_refused_before_anything_downloads_Async()
        {
            var package = AgentPackageFiles.Zip(Next);
            var service = new AgentService(Options.Create(new AgentOptions()),
                Options.Create(new LocalAllowlist { Actions = { [AgentActions.HostSummary] = new ActionPolicy() } }), TimeProvider.System, NullLogger<AgentService>.Instance)
            { Updater = Updater() };
            var (_, signed) = Update(For(package));

            var result = await service.HandleAsync(signed, Identity, new CommandMemory(null), new NoDiagnostics(), Ct, (_, _) => throw new InvalidOperationException("downloaded"));

            Assert.Equal(CommandOutcome.Rejected, result!.Outcome);
            Assert.Equal(RejectionReasons.ActionNotAllowed, result.Reason);
        }

        public void Dispose()
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
    }
}
