using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>An update that can't go ahead, with a stable reason code that the platform shows as the command's reason.</summary>
    public sealed class UpdateFailedException(string reason) : Exception(reason);

    /// <summary>
    /// <c>agent.update</c> on this host: download the package, check that it's exactly the bytes the command names and that a release key this host
    /// trusts signed it, unpack it beside the running version, and ask the launcher to switch (<see cref="AgentHome"/>). Nothing is replaced in place:
    /// the running version stays where it is, for the launcher to go back to. Without a launcher (a container, which its image updates) there's no
    /// update.
    /// </summary>
    public sealed class AgentUpdater
    {
        /// <summary>Limits on what a package may unpack to, whatever its compressed size.</summary>
        public const long MaxUnpackedBytes = 1L << 30;
        public const int MaxEntries = 10_000;

        private readonly string? _home;
        private readonly IReadOnlyList<ECDsa> _releaseKeys;

        /// <param name="home">The launcher's home directory; null when not started by the launcher.</param>
        /// <param name="releaseKeys">The release keys this host trusts.</param>
        /// <param name="trial">The update command this run is on trial for, if any.</param>
        public AgentUpdater(string? home, IReadOnlyList<ECDsa> releaseKeys, Guid? trial)
        {
            _home = home;
            _releaseKeys = releaseKeys;
            Trial = trial;
        }

        /// <summary>From the launcher's variables and this host's release keys.</summary>
        public static AgentUpdater Create(AgentOptions options)
        {
            var home = Environment.GetEnvironmentVariable(AgentHome.HomeVariable) is { Length: > 0 } h ? Path.GetFullPath(h) : null;
            var trial = Guid.TryParse(Environment.GetEnvironmentVariable(AgentHome.TrialVariable), out var id) ? id : (Guid?)null;
            var keys = options.ReleaseKeyFiles.Select(file =>
            {
                var key = ECDsa.Create();
                key.ImportFromPem(File.ReadAllText(file));
                return key;
            }).ToList();
            return new AgentUpdater(home, keys, trial);
        }

        public bool UnderLauncher => _home is not null;

        /// <summary>The update command this run is on trial for: it must reach the platform and <see cref="Confirm"/> before the trial ends.</summary>
        public Guid? Trial { get; }

        private string PathOf(string name) => Path.Combine(_home!, name);

        /// <summary>
        /// Gets the update ready. True when the launcher must now start the new version (the caller exits with <see cref="AgentHome.UpdateExitCode"/>);
        /// false when this is already that version. Throws <see cref="UpdateFailedException"/> when it can't go ahead, having changed nothing that runs.
        /// </summary>
        public async Task<bool> PrepareAsync(Guid commandId, AgentUpdateParameters update, string runningVersion,
            Func<string, CancellationToken, Task<(byte[] Package, byte[] Signature)>> download, CancellationToken cancellationToken)
        {
            if (_home is null)
            {
                throw new UpdateFailedException("updates_unavailable");
            }
            if (_releaseKeys.Count == 0)
            {
                throw new UpdateFailedException("no_release_key");
            }
            if (update.Version == runningVersion)
            {
                return false;
            }
            var (package, signature) = await download(update.Version, cancellationToken);
            if (AgentPackages.Sha256(package) != update.Sha256)
            {
                throw new UpdateFailedException("package_mismatch");
            }
            if (!_releaseKeys.Any(key => AgentPackages.Verify(key, package, signature)))
            {
                throw new UpdateFailedException("invalid_package_signature");
            }
            Unpack(package, update.Version);
            Replace(PathOf(AgentHome.UpdateFile), JsonSerializer.Serialize(new AgentHome.UpdateRequest(commandId, update.Version), CommandSignature.Json));
            return true;
        }

        /// <summary>Into <c>versions/{version}</c>, through a staging directory, so a half-unpacked package is never a version.</summary>
        private void Unpack(byte[] package, string version)
        {
            var versions = Path.Combine(_home!, AgentHome.VersionsDirectory);
            Directory.CreateDirectory(versions);
            var staging = Path.Combine(versions, $".{version}.{Guid.NewGuid():N}");
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                    {
                        throw new UpdateFailedException("invalid_package");
                    }
                    var manifest = zip.GetEntry(AgentPackages.ManifestEntry) is { Length: < 64 * 1024 } entry
                        ? Read(entry) : throw new UpdateFailedException("invalid_package");
                    if (manifest.Version != version || !IsInside(manifest.EntryPoint) || zip.GetEntry(manifest.EntryPoint.Replace('\\', '/')) is null)
                    {
                        throw new UpdateFailedException("invalid_package");
                    }
                    var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                    foreach (var file in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(root, file.FullName));
                        if (!IsInside(file.FullName) || !target.StartsWith(root, StringComparison.Ordinal))
                        {
                            throw new UpdateFailedException("invalid_package");
                        }
                        if (file.FullName.EndsWith('/'))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        file.ExtractToFile(target);
                    }
                }
                var destination = Path.Combine(versions, version);
                if (Directory.Exists(destination))
                {
                    // An earlier attempt at the same version: never the one running, which is another version.
                    Directory.Delete(destination, recursive: true);
                }
                Directory.Move(staging, destination);
            }
            catch (InvalidDataException)
            {
                throw new UpdateFailedException("invalid_package");
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        private static AgentPackages.Manifest Read(ZipArchiveEntry entry)
        {
            try
            {
                using var stream = entry.Open();
                return JsonSerializer.Deserialize<AgentPackages.Manifest>(stream, CommandSignature.Tolerant) ?? throw new UpdateFailedException("invalid_package");
            }
            catch (JsonException)
            {
                throw new UpdateFailedException("invalid_package");
            }
        }

        /// <summary>A relative path with no way out: not rooted, no <c>..</c>, no drive.</summary>
        internal static bool IsInside(string path)
            => path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains(':') && !path.StartsWith('/') && !path.StartsWith('\\')
               && !path.Split('/', '\\').Contains("..");

        /// <summary>How the last update ended, written by the launcher; null when there's none to report.</summary>
        public AgentHome.UpdateOutcome? ReadOutcome()
        {
            if (_home is null || !File.Exists(PathOf(AgentHome.OutcomeFile)))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<AgentHome.UpdateOutcome>(File.ReadAllBytes(PathOf(AgentHome.OutcomeFile)), CommandSignature.Tolerant);
            }
            catch (JsonException)
            {
                // Unreadable: nothing to report from it, and keeping it would only fail again.
                ClearOutcome();
                return null;
            }
        }

        /// <summary>After the outcome is in the command memory and queued for the platform.</summary>
        public void ClearOutcome() => File.Delete(PathOf(AgentHome.OutcomeFile));

        /// <summary>This version reached the platform: the launcher keeps it.</summary>
        public void Confirm()
        {
            if (_home is not null && Trial is { } id && !_confirmed)
            {
                Replace(PathOf(AgentHome.ConfirmedFile), id.ToString());
                _confirmed = true;
            }
        }

        private bool _confirmed;

        /// <summary>The result an outcome reports for its command.</summary>
        public static CommandResult Result(AgentHome.UpdateOutcome outcome) => outcome.Succeeded
            ? new CommandResult(outcome.CommandId, CommandOutcome.Succeeded, null, JsonSerializer.SerializeToElement(new { from = outcome.From, to = outcome.To }), 0)
            : new CommandResult(outcome.CommandId, CommandOutcome.Failed, $"rolled_back: {outcome.Reason}", null, 0);

        private static void Replace(string file, string content)
        {
            var temporary = file + ".new";
            File.WriteAllText(temporary, content);
            File.Move(temporary, file, overwrite: true);
        }
    }
}
