using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Tests
{
    /// <summary>Agent packages for tests: zips built in memory, with whatever manifest and entries a test needs, and signed by a key it chooses.</summary>
    internal static class AgentPackageFiles
    {
        public static byte[] Zip(string version, string? manifestVersion = null, string entryPoint = "juice-agent.dll", params (string Name, string Content)[] extra)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, AgentPackages.ManifestEntry, $"{{\"version\":\"{manifestVersion ?? version}\",\"entryPoint\":\"{entryPoint}\"}}");
                Add(zip, "juice-agent.dll", "not really an assembly");
                foreach (var (name, content) in extra)
                {
                    Add(zip, name, content);
                }
            }
            return buffer.ToArray();
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        /// <summary>Writes a package and its signature into a directory, as a release puts them for the platform.</summary>
        public static void Publish(string directory, string version, byte[] package, ECDsa key)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, AgentPackages.FileName(version)), package);
            File.WriteAllBytes(Path.Combine(directory, AgentPackages.SignatureFileName(version)), AgentPackages.Sign(key, package));
        }

        public static string WritePublicKey(string directory, ECDsa key)
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"release-{Guid.NewGuid():N}.pub");
            File.WriteAllText(file, key.ExportSubjectPublicKeyInfoPem());
            return file;
        }
    }
}
