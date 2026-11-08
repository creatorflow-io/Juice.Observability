using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// A throwaway PKI for agent tests, as files the way a deployment provides them: the agent CA, the command key, and a server CA with a
    /// <c>localhost</c> certificate for the agent listener. Deleted with the instance.
    /// </summary>
    internal sealed class AgentPki : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"juice-agent-pki-{Guid.NewGuid():N}");
        public string AgentCaCertificate => Path.Combine(Directory, "agent-ca.crt");
        public string AgentCaKey => Path.Combine(Directory, "agent-ca.key");
        public string CommandKey => Path.Combine(Directory, "command.key");
        public string ServerCa => Path.Combine(Directory, "server-ca.crt");
        public string ServerCertificate => Path.Combine(Directory, "server.crt");
        public string ServerKey => Path.Combine(Directory, "server.key");

        /// <param name="intermediate">Issue the listener's certificate from an intermediate CA under the server CA, and put the intermediate in its file.</param>
        public AgentPki(bool intermediate = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var agentCa = SelfSignedCa("Test agent CA");
            File.WriteAllText(AgentCaCertificate, agentCa.ExportCertificatePem());
            File.WriteAllText(AgentCaKey, agentCa.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
            using var command = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(CommandKey, command.ExportPkcs8PrivateKeyPem());

            using var serverCa = SelfSignedCa("Test server CA");
            File.WriteAllText(ServerCa, serverCa.ExportCertificatePem());
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var issuer = intermediate ? Intermediate(serverCa) : serverCa;
            if (intermediate) _intermediate = issuer.Thumbprint;
            using var server = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(12));
            File.WriteAllText(ServerCertificate, server.ExportCertificatePem() + (intermediate ? "\n" + issuer.ExportCertificatePem() : ""));
            File.WriteAllText(ServerKey, serverKey.ExportPkcs8PrivateKeyPem());
        }

        /// <summary>A CA of its own, for certificates the platform must refuse.</summary>
        public static X509Certificate2 SelfSignedCa(string name)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        }

        /// <summary>A CA issued by <paramref name="root"/>, with its key, for issuing the listener's certificate one level down.</summary>
        private static X509Certificate2 Intermediate(X509Certificate2 root)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Test intermediate CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var certificate = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(12));
            return certificate.CopyWithPrivateKey(key);
        }

        /// <summary>The settings that turn agents on for a host with this PKI.</summary>
        public Dictionary<string, string> Settings(int agentPort, int httpPort) => new()
        {
            ["Agents:Enabled"] = "true", ["Agents:Port"] = agentPort.ToString(), ["HTTP_PORTS"] = httpPort.ToString(),
            ["Agents:CaCertificateFile"] = AgentCaCertificate, ["Agents:CaKeyFile"] = AgentCaKey, ["Agents:CommandKeyFile"] = CommandKey,
            ["Agents:ServerCertificateFile"] = ServerCertificate, ["Agents:ServerKeyFile"] = ServerKey,
        };

        public static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private readonly string? _intermediate;

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
            // On Windows, a Kestrel listener that sends an intermediate also saves it in the user's intermediate store: take ours out again.
            if (_intermediate is not null && OperatingSystem.IsWindows())
            {
                using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var saved in store.Certificates.Find(X509FindType.FindByThumbprint, _intermediate, validOnly: false))
                {
                    store.Remove(saved);
                }
            }
        }
    }
}
