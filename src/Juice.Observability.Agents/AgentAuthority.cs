using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Agents
{
    /// <summary>Configuration section <c>Agents</c>. Off unless enabled, since it needs the deployment's agent CA and command key.</summary>
    public sealed class AgentPlatformOptions
    {
        public const string Section = "Agents";

        public bool Enabled { get; set; }
        /// <summary>The agent CA: it issues agent client certificates and nothing else. PEM.</summary>
        public string CaCertificateFile { get; set; } = "";
        public string CaKeyFile { get; set; } = "";
        /// <summary>The ECDSA P-256 key that signs commands, PEM. Separate from the CA, so one can be rotated or kept apart from the other.</summary>
        public string CommandKeyFile { get; set; } = "";
        /// <summary>The port of the mTLS listener agents connect to. Nothing else is served there, and the agent API is served nowhere else.</summary>
        public int Port { get; set; } = 8443;
        /// <summary>The listener's server certificate and key, PEM, for the name agents use.</summary>
        public string ServerCertificateFile { get; set; } = "";
        public string ServerKeyFile { get; set; } = "";
        /// <summary>How long an agent certificate lasts. The agent renews it when a third is left.</summary>
        public TimeSpan CertificateLifetime { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// CA certificates this one replaced, PEM, without their keys: certificates they issued are still accepted, and an agent gets one from the
        /// current CA at its next renewal. Keep one for a <see cref="CertificateLifetime"/> after the switch, then remove it.
        /// </summary>
        public List<string> PreviousCaCertificateFiles { get; set; } = [];

        /// <summary>
        /// Command keys the current one replaced, PEM, with their private keys: each signs an announcement of the current key, so agents that trust
        /// it take the new one at their next poll without enrolling again. Nothing else is signed with them. Keep one until every agent polled.
        /// </summary>
        public List<string> PreviousCommandKeyFiles { get; set; } = [];

        /// <summary>
        /// Where agent packages are (<c>juice-agent-{version}.zip</c>, each with its <c>.sig</c>), for <c>agent.update</c>. None: no updates.
        /// </summary>
        public string? PackageDirectory { get; set; }

        /// <summary>
        /// Release public keys, PEM: a package is offered only when one of them signed it. Agents check the same signature against the keys in their
        /// own configuration; these let the platform refuse a bad package before any agent sees it.
        /// </summary>
        public List<string> ReleaseKeyFiles { get; set; } = [];

        public IEnumerable<string> Validate()
        {
            foreach (var (name, file) in new[] { ("CaCertificateFile", CaCertificateFile), ("CaKeyFile", CaKeyFile), ("CommandKeyFile", CommandKeyFile),
                ("ServerCertificateFile", ServerCertificateFile), ("ServerKeyFile", ServerKeyFile) })
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) yield return $"Agents:{name} must name a readable file";
            }
            foreach (var (name, files) in new[] { ("PreviousCaCertificateFiles", PreviousCaCertificateFiles), ("PreviousCommandKeyFiles", PreviousCommandKeyFiles), ("ReleaseKeyFiles", ReleaseKeyFiles) })
            {
                foreach (var file in files.Where(f => !File.Exists(f))) yield return $"Agents:{name}: {file} isn't a readable file";
            }
            if (PackageDirectory is { Length: > 0 } packages)
            {
                if (!Directory.Exists(packages)) yield return "Agents:PackageDirectory must name a directory";
                if (ReleaseKeyFiles.Count == 0) yield return "Agents:ReleaseKeyFiles is required with a PackageDirectory: only signed packages are offered";
            }
            if (Port is < 1 or > 65535) yield return "Agents:Port is 1 to 65535";
            if (CertificateLifetime < TimeSpan.FromHours(1) || CertificateLifetime > TimeSpan.FromDays(90)) yield return "Agents:CertificateLifetime is 1 hour to 90 days";
        }
    }

    /// <summary>
    /// The platform's agent trust: the CA that issues agent certificates from the agents' own CSRs, the check that a client certificate is one of
    /// them, and the key commands are signed with. Loaded once at start; a missing or unreadable file stops the host.
    /// </summary>
    public sealed class AgentAuthority : IDisposable
    {
        public const string AgentUriPrefix = "urn:juice:agent:";
        private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

        private readonly X509Certificate2 _ca;
        /// <summary>The current CA first, then those it replaced: certificates from any of them identify an agent.</summary>
        private readonly IReadOnlyList<X509Certificate2> _accepted;
        private readonly ECDsa _commandKey;
        private readonly TimeSpan _lifetime;

        public AgentAuthority(IOptions<AgentPlatformOptions> options)
        {
            var settings = options.Value;
            _ca = X509Certificate2.CreateFromPemFile(settings.CaCertificateFile, settings.CaKeyFile);
            _accepted = [_ca, .. settings.PreviousCaCertificateFiles.Select(X509CertificateLoader.LoadCertificateFromFile)];
            _commandKey = LoadCommandKey(settings.CommandKeyFile, "Agents:CommandKeyFile");
            _lifetime = settings.CertificateLifetime;
            CommandKeyId = CommandSignature.KeyId(_commandKey);
            CommandKeyPem = _commandKey.ExportSubjectPublicKeyInfoPem();
            var announcements = new List<SignedKey>();
            foreach (var file in settings.PreviousCommandKeyFiles)
            {
                using var previous = LoadCommandKey(file, "Agents:PreviousCommandKeyFiles");
                var previousId = CommandSignature.KeyId(previous);
                if (previousId == CommandKeyId)
                {
                    throw new InvalidOperationException($"Agents:PreviousCommandKeyFiles: {file} is the current command key");
                }
                announcements.Add(new SignedKey(CommandKeyId, CommandKeyPem, previousId, CommandSignature.Sign(previous, SignedKey.Content(CommandKeyId, CommandKeyPem))));
            }
            Announcements = announcements;
        }

        private static ECDsa LoadCommandKey(string file, string setting)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(file));
            if (key.KeySize != 256)
            {
                key.Dispose();
                throw new InvalidOperationException($"{setting} must be an ECDSA P-256 key");
            }
            return key;
        }

        public string CommandKeyId { get; }
        /// <summary>The public half, given to every agent at enrollment.</summary>
        public string CommandKeyPem { get; }

        /// <summary>The current command key, announced by each key it replaced. Empty when nothing was rotated.</summary>
        public IReadOnlyList<SignedKey> Announcements { get; }

        /// <summary>
        /// What an agent that trusts these keys needs to trust the current one: nothing when it already does, or when it didn't say which it trusts
        /// (an agent from before rotation, which couldn't read an announcement anyway); otherwise the announcements signed by a key it trusts.
        /// </summary>
        public IReadOnlyList<SignedKey>? AnnouncementsFor(IReadOnlyCollection<string>? trusted)
        {
            if (trusted is null || trusted.Contains(CommandKeyId))
            {
                return null;
            }
            var usable = Announcements.Where(a => trusted.Contains(a.SignedBy)).ToList();
            return usable.Count > 0 ? usable : null;
        }

        /// <summary>
        /// A client certificate for the agent, from its CSR: the CSR's signature must verify, and its key must be ECDSA P-256 or P-384, or RSA of
        /// 2048 bits or more. The certificate names the agent in a URI and is for client authentication only.
        /// </summary>
        public (string Pem, string Serial, DateTimeOffset NotAfter) Issue(string csrPem, Guid agentId, DateTimeOffset now)
        {
            var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
            if (!IsStrongEnough(csr.PublicKey))
            {
                throw new ArgumentException("The key must be ECDSA P-256 or P-384, or RSA of at least 2048 bits");
            }
            var request = new CertificateRequest(new X500DistinguishedName($"CN={agentId}"), csr.PublicKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri(AgentUriPrefix + agentId));
            request.CertificateExtensions.Add(san.Build(true));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            var notAfter = now + _lifetime;
            // Through a generator for the CA's own key: an agent's key may be of another kind (an RSA agent key under an ECDSA CA).
            using var certificate = request.Create(_ca.SubjectName, Signer(), now.AddMinutes(-5), notAfter, serial);
            return (certificate.ExportCertificatePem(), certificate.SerialNumber, notAfter);
        }

        private X509SignatureGenerator Signer()
            => _ca.GetECDsaPrivateKey() is { } ec ? X509SignatureGenerator.CreateForECDsa(ec)
            : _ca.GetRSAPrivateKey() is { } rsa ? X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1)
            : throw new InvalidOperationException("The agent CA's key must be ECDSA or RSA");

        private static bool IsStrongEnough(PublicKey key)
        {
            using var ec = key.GetECDsaPublicKey();
            if (ec is not null) return ec.KeySize is 256 or 384;
            using var rsa = key.GetRSAPublicKey();
            return rsa is not null && rsa.KeySize >= 2048;
        }

        /// <summary>
        /// The agent a client certificate belongs to, if this CA (or one it replaced) issued it for client authentication and it's in date; otherwise
        /// null.
        /// </summary>
        public Guid? AgentOf(X509Certificate2? certificate, DateTimeOffset now)
        {
            if (certificate is null || now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            {
                return null;
            }
            if (!_accepted.Any(ca => IssuedBy(certificate, ca, now)))
            {
                return null;
            }
            return certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is { } san
                && UriOf(san) is { } uri && uri.StartsWith(AgentUriPrefix, StringComparison.Ordinal) && Guid.TryParse(uri[AgentUriPrefix.Length..], out var id)
                ? id : null;
        }

        /// <summary>Issued directly by this CA, for client authentication: one CA at a time, so each is checked as the only root.</summary>
        private static bool IssuedBy(X509Certificate2 certificate, X509Certificate2 ca, DateTimeOffset now)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(ClientAuth);
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            return chain.Build(certificate) && chain.ChainElements.Count == 2 && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
        }

        /// <summary>The first URI of a subject alternative name extension (tag 6, an IA5String).</summary>
        private static string? UriOf(X509SubjectAlternativeNameExtension san)
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
            while (reader.HasData)
            {
                var tag = reader.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    return reader.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                }
                reader.ReadEncodedValue();
            }
            return null;
        }

        /// <summary>Signs an envelope as it will be sent: the exact bytes, with the key id.</summary>
        public SignedCommand Sign(CommandEnvelope envelope)
        {
            var bytes = CommandSignature.Serialize(envelope);
            if (bytes.Length > CommandLimits.MaxPayloadBytes)
            {
                throw new ArgumentException("The command is too large");
            }
            return new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(_commandKey, bytes), CommandKeyId);
        }

        /// <summary>A random token for nonces and enrollment, URL-safe.</summary>
        public static string Token(int bytes = 24) => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

        public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        /// <summary>Stored JSON as an element that owns its data: no pooled document is left undisposed.</summary>
        internal static JsonElement Parameters(string json) => JsonSerializer.Deserialize<JsonElement>(json);

        public void Dispose()
        {
            foreach (var ca in _accepted)
            {
                ca.Dispose();
            }
            _commandKey.Dispose();
        }
    }
}
