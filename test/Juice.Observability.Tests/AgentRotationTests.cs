using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Rotating the platform's agent trust without enrolling agents again (roadmap Phase 7, milestone 2). A new command key reaches an agent only as an
    /// announcement signed by a key it already trusts; a new CA takes over at each agent's next renewal, while certificates from the one it replaced
    /// still identify their agents.
    /// </summary>
    public sealed class AgentRotationTests : IDisposable
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private readonly AgentPki _before = new();
        private readonly AgentPki _after = new();
        private readonly AgentAuthority _old;
        private readonly AgentAuthority _rotated;
        private readonly string _data = Path.Combine(Path.GetTempPath(), $"juice-agent-rotation-{Guid.NewGuid():N}");

        public AgentRotationTests()
        {
            _old = new AgentAuthority(Options.Create(new AgentPlatformOptions
            {
                CaCertificateFile = _before.AgentCaCertificate, CaKeyFile = _before.AgentCaKey, CommandKeyFile = _before.CommandKey,
            }));
            _rotated = new AgentAuthority(Options.Create(new AgentPlatformOptions
            {
                CaCertificateFile = _after.AgentCaCertificate, CaKeyFile = _after.AgentCaKey, CommandKeyFile = _after.CommandKey,
                PreviousCaCertificateFiles = [_before.AgentCaCertificate], PreviousCommandKeyFiles = [_before.CommandKey],
            }));
        }

        private static X509Certificate2 Certificate(AgentAuthority authority, Guid agent)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = authority.Issue(new CertificateRequest("CN=host", key, HashAlgorithmName.SHA256).CreateSigningRequestPem(), agent, Now).Pem;
            using var certificate = X509Certificate2.CreateFromPem(pem);
            using var withKey = certificate.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }

        private static ECDsa Public(string pem)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key;
        }

        /// <summary>An agent enrolled before the rotation: it trusts the old command key only.</summary>
        private AgentIdentity EnrolledBefore()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            new AgentStore(_data).SaveEnrollment(key, new EnrollResponse(Guid.CreateVersion7(), "acme", "unused", _old.CommandKeyPem, _old.CommandKeyId));
            return new AgentStore(_data).LoadIdentity();
        }

        [Fact]
        public void Certificates_from_the_replaced_ca_still_identify_their_agents_and_new_ones_come_from_the_new_ca()
        {
            var agent = Guid.CreateVersion7();
            using var before = Certificate(_old, agent);
            using var after = Certificate(_rotated, agent);
            Assert.Equal(agent, _rotated.AgentOf(before, Now));
            Assert.Equal(agent, _rotated.AgentOf(after, Now));
            // The new certificate is the new CA's: the old platform, which never learned that CA, doesn't recognize it.
            Assert.Null(_old.AgentOf(after, Now));
            // Still nothing from a CA that's neither.
            using var forged = AgentFlowTests.Forged(agent, Convert.FromHexString(before.SerialNumber));
            Assert.Null(_rotated.AgentOf(forged, Now));
        }

        [Fact]
        public void The_new_command_key_is_announced_only_to_agents_that_trust_a_key_it_replaced()
        {
            Assert.Null(_rotated.AnnouncementsFor(null));                              // an agent from before announcements: it couldn't read one
            Assert.Null(_rotated.AnnouncementsFor([_rotated.CommandKeyId]));           // already has it
            Assert.Null(_rotated.AnnouncementsFor(["0123456789abcdef"]));              // trusts nothing that could announce it
            var announcement = Assert.Single(_rotated.AnnouncementsFor([_old.CommandKeyId])!);
            Assert.Equal((_rotated.CommandKeyId, _old.CommandKeyId), (announcement.KeyId, announcement.SignedBy));
            Assert.Null(_old.AnnouncementsFor([_old.CommandKeyId]));
        }

        [Fact]
        public void An_agent_takes_an_announced_key_only_when_a_key_it_trusts_signed_exactly_that_key()
        {
            var identity = EnrolledBefore();
            var genuine = Assert.Single(_rotated.AnnouncementsFor([_old.CommandKeyId])!);
            using (var accepted = CommandVerifier.VerifyAnnouncement(genuine, identity))
            {
                Assert.NotNull(accepted);
                Assert.Equal(_rotated.CommandKeyId, CommandSignature.KeyId(accepted));
            }

            using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var attackerPem = attacker.ExportSubjectPublicKeyInfoPem();
            var attackerId = CommandSignature.KeyId(attacker);
            // Another key in a genuine announcement, with its own id or the announced one.
            Assert.Null(CommandVerifier.VerifyAnnouncement(genuine with { PublicKeyPem = attackerPem }, identity));
            Assert.Null(CommandVerifier.VerifyAnnouncement(genuine with { PublicKeyPem = attackerPem, KeyId = attackerId }, identity));
            // Signed by a key the agent doesn't trust, even claiming to be by one it does.
            var selfSigned = new SignedKey(attackerId, attackerPem, attackerId, CommandSignature.Sign(attacker, SignedKey.Content(attackerId, attackerPem)));
            Assert.Null(CommandVerifier.VerifyAnnouncement(selfSigned, identity));
            Assert.Null(CommandVerifier.VerifyAnnouncement(selfSigned with { SignedBy = _old.CommandKeyId }, identity));
            // A genuine signature whose id doesn't match the key.
            using var oldKey = ECDsa.Create();
            oldKey.ImportFromPem(File.ReadAllText(_before.CommandKey));
            var mislabelled = new SignedKey("0123456789abcdef", attackerPem, _old.CommandKeyId, CommandSignature.Sign(oldKey, SignedKey.Content("0123456789abcdef", attackerPem)));
            Assert.Null(CommandVerifier.VerifyAnnouncement(mislabelled, identity));
        }

        [Fact]
        public void After_an_announcement_the_agent_runs_commands_from_the_new_key_and_its_announcer_and_remembers_both()
        {
            var identity = EnrolledBefore();
            var service = new AgentService(Options.Create(new AgentOptions { DataDirectory = _data }), Options.Create(new LocalAllowlist()), TimeProvider.System, NullLogger<AgentService>.Instance);

            var updated = service.TrustAnnounced(_rotated.AnnouncementsFor(identity.CommandKeys.Keys.ToList()), identity);

            Assert.Equal(_rotated.CommandKeyId, updated.CommandKeyId);
            Assert.Equal(new[] { _old.CommandKeyId, _rotated.CommandKeyId }.Order(), updated.CommandKeys.Keys.Order());
            var reloaded = new AgentStore(_data).LoadIdentity();
            Assert.Equal(updated.CommandKeys.Keys.Order(), reloaded.CommandKeys.Keys.Order());
            Assert.Equal(identity.AgentId, reloaded.AgentId);

            // Commands from either key verify; the allowlist decides the rest.
            var allow = new LocalAllowlist { Actions = { [AgentActions.HostSummary] = new ActionPolicy() } };
            foreach (var authority in new[] { _old, _rotated })
            {
                var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", identity.AgentId, AgentActions.HostSummary,
                    JsonSerializer.SerializeToElement(new { }), Now, Now.AddMinutes(1), AgentAuthority.Token(), AgentAuthority.Token(), null);
                Assert.Null(CommandVerifier.Verify(authority.Sign(envelope), reloaded, allow, Now).Rejection);
            }
        }

        /// <summary>The tenant pattern: the deployment's tenant moves from its old name to its Id, and agents learn it signed.</summary>
        [Fact]
        public void An_agent_adopts_a_tenant_change_only_when_signed_by_a_trusted_key_from_its_own_tenant()
        {
            var identity = EnrolledBefore();   // tenant "acme"
            var clock = new StepClock(Now);
            var service = new AgentService(Options.Create(new AgentOptions { DataDirectory = _data }), Options.Create(new LocalAllowlist()), clock, NullLogger<AgentService>.Instance);
            const string id = "0193f0aa-0000-7000-8000-0000000000a1";

            // Refused: an untrusted key, another tenant, a name rather than an Id, a changed signature.
            Assert.Equal("acme", service.AdoptTenant(_rotated.SignTenant(identity.AgentId, "acme", id), identity).Tenant);   // a key it doesn't trust yet
            Assert.Equal("acme", service.AdoptTenant(_old.SignTenant(identity.AgentId, "other", id), identity).Tenant);
            Assert.Equal("acme", service.AdoptTenant(_old.SignTenant(identity.AgentId, "acme", "new-name"), identity).Tenant);
            Assert.Equal("acme", service.AdoptTenant(_old.SignTenant(identity.AgentId, "acme", id) with { To = "0193f0aa-0000-7000-8000-0000000000b2" }, identity).Tenant);
            // Refused: made for another agent (a clone of this deployment's announcement replayed here).
            Assert.Equal("acme", service.AdoptTenant(_old.SignTenant(Guid.CreateVersion7(), "acme", id), identity).Tenant);
            Assert.Equal("acme", new AgentStore(_data).LoadIdentity().Tenant);
            // An enrollment written by this version holds no tenant-change fields, so an older agent (which refuses fields it doesn't know) reads it.
            Assert.DoesNotContain("previousTenant", File.ReadAllText(Path.Combine(_data, "identity.json")), StringComparison.OrdinalIgnoreCase);

            var issuedBefore = Now.AddSeconds(-30);
            clock.Advance(TimeSpan.FromSeconds(1));
            var changed = service.AdoptTenant(_old.SignTenant(identity.AgentId, "acme", id), identity);
            Assert.Equal((id, "acme"), (changed.Tenant, changed.PreviousTenant));
            Assert.Equal(changed.Tenant, new AgentStore(_data).LoadIdentity().Tenant);
            // An Id never moves again, even signed.
            Assert.Equal(id, service.AdoptTenant(_old.SignTenant(identity.AgentId, id, "0193f0aa-0000-7000-8000-0000000000b2"), changed).Tenant);

            var allow = new LocalAllowlist { Actions = { [AgentActions.HostSummary] = new ActionPolicy() } };
            string? Verify(string tenant, DateTimeOffset issued) => CommandVerifier.Verify(_old.Sign(new CommandEnvelope(CommandEnvelope.CurrentVersion,
                Guid.CreateVersion7(), tenant, identity.AgentId, AgentActions.HostSummary, JsonSerializer.SerializeToElement(new { }), issued, issued.AddMinutes(5),
                AgentAuthority.Token(), AgentAuthority.Token(), null)), changed, allow, clock.GetUtcNow()).Rejection;
            Assert.Null(Verify(id, clock.GetUtcNow()));
            // Signed with the old tenant before the change: still runs. Signed with it after: refused.
            Assert.Null(Verify("acme", issuedBefore));
            Assert.Equal(RejectionReasons.WrongTenant, Verify("acme", clock.GetUtcNow().AddMinutes(3)));
            Assert.Equal(RejectionReasons.WrongTenant, Verify("other", clock.GetUtcNow()));
        }

        [Fact]
        public void A_second_rotation_drops_the_oldest_key()
        {
            var identity = EnrolledBefore();
            var service = new AgentService(Options.Create(new AgentOptions { DataDirectory = _data }), Options.Create(new LocalAllowlist()), TimeProvider.System, NullLogger<AgentService>.Instance);
            identity = service.TrustAnnounced(_rotated.AnnouncementsFor(identity.CommandKeys.Keys.ToList()), identity);

            using var third = new AgentPki();
            using var again = new AgentAuthority(Options.Create(new AgentPlatformOptions
            {
                CaCertificateFile = third.AgentCaCertificate, CaKeyFile = third.AgentCaKey, CommandKeyFile = third.CommandKey, PreviousCommandKeyFiles = [_after.CommandKey],
            }));
            identity = service.TrustAnnounced(again.AnnouncementsFor(identity.CommandKeys.Keys.ToList()), identity);

            Assert.Equal(new[] { _rotated.CommandKeyId, again.CommandKeyId }.Order(), identity.CommandKeys.Keys.Order());
            Assert.DoesNotContain(_old.CommandKeyId, new AgentStore(_data).LoadIdentity().CommandKeys.Keys);
        }

        [Fact]
        public void A_new_enrollment_forgets_keys_learned_before()
        {
            var identity = EnrolledBefore();
            var service = new AgentService(Options.Create(new AgentOptions { DataDirectory = _data }), Options.Create(new LocalAllowlist()), TimeProvider.System, NullLogger<AgentService>.Instance);
            service.TrustAnnounced(_rotated.AnnouncementsFor(identity.CommandKeys.Keys.ToList()), identity);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            new AgentStore(_data).SaveEnrollment(key, new EnrollResponse(Guid.CreateVersion7(), "acme", "unused", _old.CommandKeyPem, _old.CommandKeyId));

            Assert.Equal([_old.CommandKeyId], new AgentStore(_data).LoadIdentity().CommandKeys.Keys);
        }

        private sealed class StepClock(DateTimeOffset now) : TimeProvider
        {
            private DateTimeOffset _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }

        public void Dispose()
        {
            _old.Dispose();
            _rotated.Dispose();
            _before.Dispose();
            _after.Dispose();
            if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
        }
    }
}
