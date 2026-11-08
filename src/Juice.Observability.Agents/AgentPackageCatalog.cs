using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Agents
{
    /// <summary>One package the platform offers: checked against a release key and its own manifest.</summary>
    public sealed record AgentPackage(string Version, string Sha256, long Size, string SignedBy, DateTimeOffset AddedAt, string File, byte[] Signature);

    /// <summary>
    /// The agent packages in <see cref="AgentPlatformOptions.PackageDirectory"/>. A package is offered only when its <c>.sig</c> verifies with one of
    /// the release keys and its manifest names the version in its file name; anything else is skipped, so a bad upload can't reach an agent through
    /// the platform. Agents check the same signature against their own release keys, whatever the platform says. Files are read again when they
    /// change: dropping in a package and its signature is enough.
    /// </summary>
    public sealed class AgentPackageCatalog
    {
        private const string Prefix = "juice-agent-";
        private const string Extension = ".zip";

        private readonly string? _directory;
        private readonly IReadOnlyList<(string KeyId, ECDsa Key)> _releaseKeys;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (DateTime Written, long Length, AgentPackage? Package)> _checked = [];

        public AgentPackageCatalog(IOptions<AgentPlatformOptions> options)
        {
            _directory = options.Value.PackageDirectory is { Length: > 0 } directory ? directory : null;
            _releaseKeys = [.. options.Value.ReleaseKeyFiles.Select(file =>
            {
                var key = ECDsa.Create();
                key.ImportFromPem(File.ReadAllText(file));
                return (CommandSignature.KeyId(key), key);
            })];
        }

        public bool Enabled => _directory is not null;

        /// <summary>Every package that checks out, newest first by when it was added.</summary>
        public IReadOnlyList<AgentPackage> List()
        {
            if (_directory is null)
            {
                return [];
            }
            return [.. Directory.EnumerateFiles(_directory, Prefix + "*" + Extension)
                .Select(file => Path.GetFileName(file)[Prefix.Length..^Extension.Length])
                .Select(Find).OfType<AgentPackage>().OrderByDescending(p => p.AddedAt)];
        }

        /// <summary>The package of this version, if it's here and checks out.</summary>
        public AgentPackage? Find(string version)
        {
            if (_directory is null || new AgentUpdateParameters(version, new string('0', 64)).Validate() is not null)
            {
                return null;
            }
            var file = Path.Combine(_directory, AgentPackages.FileName(version));
            var signatureFile = Path.Combine(_directory, AgentPackages.SignatureFileName(version));
            if (!File.Exists(file) || !File.Exists(signatureFile))
            {
                return null;
            }
            var info = new FileInfo(file);
            var written = info.LastWriteTimeUtc > File.GetLastWriteTimeUtc(signatureFile) ? info.LastWriteTimeUtc : File.GetLastWriteTimeUtc(signatureFile);
            lock (_gate)
            {
                if (_checked.TryGetValue(version, out var known) && known.Written == written && known.Length == info.Length)
                {
                    return known.Package;
                }
            }
            var package = Check(version, file, signatureFile, info, written);
            lock (_gate)
            {
                _checked[version] = (written, info.Length, package);
            }
            return package;
        }

        private AgentPackage? Check(string version, string file, string signatureFile, FileInfo info, DateTime written)
        {
            if (info.Length > AgentPackages.MaxBytes)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(file);
            var signature = File.ReadAllBytes(signatureFile);
            var signer = _releaseKeys.FirstOrDefault(k => AgentPackages.Verify(k.Key, bytes, signature));
            if (signer.Key is null || ManifestVersion(bytes) != version)
            {
                return null;
            }
            return new AgentPackage(version, AgentPackages.Sha256(bytes), info.Length, signer.KeyId, new DateTimeOffset(written, TimeSpan.Zero), file, signature);
        }

        private static string? ManifestVersion(byte[] package)
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
                if (zip.GetEntry(AgentPackages.ManifestEntry) is not { Length: < 64 * 1024 } entry)
                {
                    return null;
                }
                using var stream = entry.Open();
                return JsonSerializer.Deserialize<AgentPackages.Manifest>(stream, CommandSignature.Tolerant)?.Version;
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                return null;
            }
        }
    }
}
