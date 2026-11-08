using System.Security.Cryptography;
using System.Text;

namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// An agent package: a zip of the published agent with a <see cref="ManifestEntry"/> naming its version and entry point, and a detached release
    /// signature over the zip's bytes. The signature is ECDSA P-256 with SHA-256 in DER form, as <c>openssl dgst -sha256 -sign</c> writes it, so a
    /// release can be signed with ordinary tools. The release key is neither the command key nor the CA: an agent trusts the release keys in its own
    /// configuration, so the platform can't supply one.
    /// </summary>
    public static class AgentPackages
    {
        public const string ManifestEntry = "manifest.json";
        /// <summary>Larger packages are refused before they're read.</summary>
        public const long MaxBytes = 200L * 1024 * 1024;

        /// <param name="Version">The agent version inside, which must be the one the update asked for.</param>
        /// <param name="EntryPoint">The agent's assembly, relative to the package root: what the launcher starts.</param>
        public sealed record Manifest(string Version, string EntryPoint);

        public static string FileName(string version) => $"juice-agent-{version}.zip";

        public static string SignatureFileName(string version) => FileName(version) + ".sig";

        public static string Sha256(byte[] package) => Convert.ToHexStringLower(SHA256.HashData(package));

        public static bool Verify(ECDsa releaseKey, byte[] package, byte[] signature)
        {
            try
            {
                return releaseKey.VerifyData(package, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public static byte[] Sign(ECDsa releaseKey, byte[] package)
            => releaseKey.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// A new command key, announced to agents signed by a key they already trust (<see cref="SignedBy"/>), so the platform can move to it without
    /// enrolling every agent again. An agent adds it only when that signature verifies.
    /// </summary>
    public sealed record SignedKey(string KeyId, string PublicKeyPem, string SignedBy, string Signature)
    {
        /// <summary>What the announcing key signs: the new key's id and its public key, so neither can be swapped.</summary>
        public static byte[] Content(string keyId, string publicKeyPem) => Encoding.UTF8.GetBytes($"juice-agent-command-key\n{keyId}\n{publicKeyPem.Trim()}");
    }
}
