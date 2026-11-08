using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// This agent's identity on disk: its private key (generated here, never sent), its certificate, and the platform's command key it trusts. Files
    /// are written readable by the agent's user only where the OS allows it.
    /// </summary>
    public sealed class AgentStore(string directory)
    {
        private sealed record Identity(Guid AgentId, string Tenant, string CommandKeyPem, string CommandKeyId);

        /// <summary>The certificate and its key in one file, so they're always replaced together: a crash mid-renewal leaves the old pair or the new, never a mix.</summary>
        private string CredentialFile => Path.Combine(directory, "agent.pem");
        // The two files of agents enrolled before agent.pem; read, never written.
        private string LegacyKeyFile => Path.Combine(directory, "agent.key");
        private string LegacyCertificateFile => Path.Combine(directory, "agent.crt");
        private string IdentityFile => Path.Combine(directory, "identity.json");
        /// <summary>The command keys trusted after a rotation, oldest first; without it, the one from enrollment.</summary>
        private string KeysFile => Path.Combine(directory, "keys.json");
        public string MemoryFile => Path.Combine(directory, "commands.json");

        private sealed record TrustedKey(string KeyId, string PublicKeyPem);

        public bool IsEnrolled => File.Exists(IdentityFile) && (File.Exists(CredentialFile) || (File.Exists(LegacyKeyFile) && File.Exists(LegacyCertificateFile)));

        public AgentIdentity LoadIdentity()
        {
            var identity = JsonSerializer.Deserialize<Identity>(File.ReadAllBytes(IdentityFile), CommandSignature.Json)!;
            var trusted = File.Exists(KeysFile)
                ? JsonSerializer.Deserialize<List<TrustedKey>>(File.ReadAllBytes(KeysFile), CommandSignature.Json)!
                : [new TrustedKey(identity.CommandKeyId, identity.CommandKeyPem)];
            if (trusted.Count == 0)
            {
                throw new InvalidOperationException("keys.json lists no command key: the agent's data directory was changed. Enroll again.");
            }
            var keys = new Dictionary<string, ECDsa>();
            foreach (var entry in trusted)
            {
                var key = ECDsa.Create();
                key.ImportFromPem(entry.PublicKeyPem);
                if (CommandSignature.KeyId(key) != entry.KeyId)
                {
                    throw new InvalidOperationException("A stored command key doesn't match its id: the agent's data directory was changed. Enroll again.");
                }
                keys[entry.KeyId] = key;
            }
            return new AgentIdentity(identity.AgentId, identity.Tenant, keys, trusted[^1].KeyId);
        }

        /// <summary>
        /// Trusts a key a trusted one announced: from now on the new key and its announcer, and no older ones. Commands signed before the rotation
        /// are by the announcer, so they still verify; anything older has expired long before a second rotation.
        /// </summary>
        public void TrustKey(string announcerId, ECDsa announcer, string keyId, ECDsa key)
            => Replace(KeysFile, JsonSerializer.Serialize(new List<TrustedKey>
            {
                new(announcerId, announcer.ExportSubjectPublicKeyInfoPem()),
                new(keyId, key.ExportSubjectPublicKeyInfoPem()),
            }, CommandSignature.Json));

        /// <summary>The certificate with its private key, for mTLS.</summary>
        public X509Certificate2 LoadCertificate()
        {
            using var pemCertificate = File.Exists(CredentialFile)
                ? X509Certificate2.CreateFromPemFile(CredentialFile)
                : X509Certificate2.CreateFromPemFile(LegacyCertificateFile, LegacyKeyFile);
            // Exported and reloaded: on Windows, SslStream needs a key it can use outside an ephemeral PEM import.
            return X509CertificateLoader.LoadPkcs12(pemCertificate.Export(X509ContentType.Pkcs12), null);
        }

        public static (ECDsa Key, string Csr) NewKey(string subject)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
            return (key, request.CreateSigningRequestPem());
        }

        public void SaveEnrollment(ECDsa key, EnrollResponse response)
        {
            Directory.CreateDirectory(directory);
            // A new enrollment trusts the key it came with, and nothing a previous one learned.
            File.Delete(KeysFile);
            SaveCredential(key, response.CertificatePem);
            Replace(IdentityFile, JsonSerializer.Serialize(new Identity(response.AgentId, response.Tenant, response.CommandKeyPem, response.CommandKeyId), CommandSignature.Json));
        }

        /// <summary>The new certificate and key replace the old pair in one rename: whatever happens, the file holds one matching pair.</summary>
        public void SaveRenewal(ECDsa key, string certificatePem)
        {
            SaveCredential(key, certificatePem);
            File.Delete(LegacyKeyFile);
            File.Delete(LegacyCertificateFile);
        }

        private void SaveCredential(ECDsa key, string certificatePem)
            => Replace(CredentialFile, certificatePem.TrimEnd() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");

        /// <summary>Written beside, made private, then renamed over the old one: a reader sees the old file or the new, whole.</summary>
        private static void Replace(string file, string content)
        {
            var temporary = file + ".new";
            File.WriteAllText(temporary, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, file, overwrite: true);
        }
    }
}
