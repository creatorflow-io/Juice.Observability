using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agents;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>The agent CA on its own: what it issues from a CSR, what it refuses to, and which client certificates it recognizes.</summary>
    public sealed class AgentAuthorityTests : IDisposable
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private readonly AgentPki _pki = new();
        private readonly AgentAuthority _authority;

        public AgentAuthorityTests() => _authority = new AgentAuthority(Options.Create(new AgentPlatformOptions
        {
            CaCertificateFile = _pki.AgentCaCertificate, CaKeyFile = _pki.AgentCaKey, CommandKeyFile = _pki.CommandKey,
        }));

        private static string Csr(AsymmetricAlgorithm key) => key switch
        {
            ECDsa ec => new CertificateRequest("CN=host", ec, HashAlgorithmName.SHA256).CreateSigningRequestPem(),
            RSA rsa => new CertificateRequest("CN=host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem(),
            _ => throw new ArgumentException(null, nameof(key)),
        };

        private static X509Certificate2 WithKey(string pem, AsymmetricAlgorithm key)
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            using var withKey = key is ECDsa ec ? certificate.CopyWithPrivateKey(ec) : certificate.CopyWithPrivateKey((RSA)key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }

        [Fact]
        public void A_certificate_from_a_csr_names_the_agent_and_is_for_client_authentication_only()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var agent = Guid.CreateVersion7();
            var (pem, serial, notAfter) = _authority.Issue(Csr(key), agent, Now);
            using var certificate = WithKey(pem, key);
            Assert.Equal(agent, _authority.AgentOf(certificate, Now));
            Assert.Equal(serial, certificate.SerialNumber);
            Assert.Equal(notAfter.ToUnixTimeSeconds(), new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeSeconds());
            var usage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
            Assert.Equal(["1.3.6.1.5.5.7.3.2"], usage.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));
            Assert.False(Assert.Single(certificate.Extensions.OfType<X509BasicConstraintsExtension>()).CertificateAuthority);
        }

        [Theory]
        [InlineData("rsa", 2048, true)]
        [InlineData("rsa", 1024, false)]
        [InlineData("p384", 0, true)]
        [InlineData("p521", 0, false)]
        public void Only_strong_enough_keys_get_a_certificate(string kind, int bits, bool issued)
        {
            using AsymmetricAlgorithm key = kind switch
            {
                "rsa" => RSA.Create(bits),
                "p384" => ECDsa.Create(ECCurve.NamedCurves.nistP384),
                _ => ECDsa.Create(ECCurve.NamedCurves.nistP521),
            };
            var csr = Csr(key);
            if (issued) Assert.NotNull(_authority.Issue(csr, Guid.CreateVersion7(), Now).Pem);
            else Assert.Throws<ArgumentException>(() => _authority.Issue(csr, Guid.CreateVersion7(), Now));
        }

        [Fact]
        public void A_csr_whose_signature_doesnt_verify_gets_nothing()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var der = Convert.FromBase64String(string.Concat(Csr(key).Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal))));
            der[^5] ^= 0x01;
            var tampered = $"-----BEGIN CERTIFICATE REQUEST-----\n{Convert.ToBase64String(der)}\n-----END CERTIFICATE REQUEST-----";
            Assert.ThrowsAny<CryptographicException>(() => _authority.Issue(tampered, Guid.CreateVersion7(), Now));
        }

        [Fact]
        public void Only_its_own_in_date_certificates_are_recognized()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var agent = Guid.CreateVersion7();
            using var certificate = WithKey(_authority.Issue(Csr(key), agent, Now).Pem, key);
            Assert.Null(_authority.AgentOf(certificate, Now.AddDays(8)));     // past its 7 days
            Assert.Null(_authority.AgentOf(certificate, Now.AddDays(-1)));    // before it was issued
            Assert.Null(_authority.AgentOf(null, Now));
            // Same agent, same serial, another CA.
            using var forged = AgentFlowTests.Forged(agent, Convert.FromHexString(certificate.SerialNumber));
            Assert.Null(_authority.AgentOf(forged, Now));
        }

        [Fact]
        public void Commands_it_signs_verify_with_the_key_it_gives_agents()
        {
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", Guid.CreateVersion7(), AgentActions.HostSummary,
                System.Text.Json.JsonSerializer.SerializeToElement(new { }), Now, Now.AddMinutes(1), AgentAuthority.Token(), AgentAuthority.Token(), null);
            var signed = _authority.Sign(envelope);
            using var key = ECDsa.Create();
            key.ImportFromPem(_authority.CommandKeyPem);
            Assert.Equal(_authority.CommandKeyId, signed.KeyId);
            Assert.True(CommandSignature.Verify(key, Convert.FromBase64String(signed.Payload), signed.Signature));
        }

        public void Dispose()
        {
            _authority.Dispose();
            _pki.Dispose();
        }
    }
}
