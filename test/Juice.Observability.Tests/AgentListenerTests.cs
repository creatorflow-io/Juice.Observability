using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Juice.Observability.Agent;
using Juice.Observability.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Juice.Observability.Tests
{
    /// <summary>The agent listener's neighbours: the host keeps its own addresses, and failed enrollments from one address are held back.</summary>
    public class AgentListenerTests
    {
        private static IReadOnlyList<string> Endpoints(params (string Key, string Value)[] settings)
            => [.. AgentListener.HostEndpoints(new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build())
                .Select(e => $"{(e.Https ? "https" : "http")} {e.Host ?? "*"}:{e.Port}")];

        [Fact]
        public void The_host_keeps_its_URLS_as_it_would_without_agents()
        {
            Assert.Equal(["http *:5000", "https localhost:5001", "http 10.0.0.5:80", "http *:8081"],
                Endpoints(("URLS", "http://+:5000;https://localhost:5001;http://10.0.0.5;http://*:8081/")));
            // URLS wins over the port settings, as in ASP.NET Core.
            Assert.Equal(["http *:5000"], Endpoints(("URLS", "http://+:5000"), ("HTTP_PORTS", "9000")));
        }

        [Fact]
        public void A_host_name_in_URLS_listens_everywhere_as_Kestrel_would_instead_of_failing()
            => Assert.Equal(["http *:8080", "http *:5000"], Endpoints(("URLS", "http://api.internal:8080;http://[::]:5000")));

        [Fact]
        public void A_host_with_Kestrel_endpoints_keeps_them_and_gets_only_the_agent_listener()
            => Assert.Empty(Endpoints(("Kestrel:Endpoints:Http:Url", "http://*:8080"), ("HTTP_PORTS", "8080")));

        [Fact]
        public void An_agent_builds_the_chain_with_the_intermediate_the_server_sent_and_still_trusts_only_its_CA()
        {
            static X509Certificate2 Ca(string name, X509Certificate2? issuer)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                using var certificate = issuer is null
                    ? request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1))
                    : request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(12));
                return issuer is null ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null) : certificate.CopyWithPrivateKey(key);
            }
            using var root = Ca($"Listener root {Guid.NewGuid():N}", null);
            using var intermediate = Ca($"Listener intermediate {Guid.NewGuid():N}", root);
            using var other = Ca($"Other root {Guid.NewGuid():N}", null);
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var leaf = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256)
                .Create(intermediate, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(12));
            using var sent = new X509Chain();
            sent.ChainPolicy.ExtraStore.Add(intermediate);

            // First, before this process has seen the intermediate: Windows' chain engine caches intermediates it has met, so once a chain was built
            // with it, a later build without it may still find it.
            Assert.False(PlatformClient.IsPlatform(leaf, null, SslPolicyErrors.None, root), "without the intermediate there's no chain");
            Assert.True(PlatformClient.IsPlatform(leaf, sent, SslPolicyErrors.None, root));
            Assert.False(PlatformClient.IsPlatform(leaf, sent, SslPolicyErrors.None, other), "an intermediate sent doesn't make another root trusted");
        }

        [Fact]
        public void Without_URLS_the_host_keeps_its_HTTP_and_HTTPS_ports_or_8080()
        {
            Assert.Equal(["http *:8080", "http *:8081", "https *:8443"], Endpoints(("HTTP_PORTS", "8080;8081"), ("HTTPS_PORTS", "8443")));
            Assert.Equal(["http *:8080"], Endpoints());
        }

        private sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        [Fact]
        public void An_address_with_too_many_failed_enrollments_waits_out_the_window()
        {
            var clock = new Clock(DateTimeOffset.UtcNow);
            var guard = new EnrollmentGuard(clock);
            for (var i = 0; i < EnrollmentGuard.MaxFailures - 1; i++) guard.Fail("10.0.0.9");
            Assert.False(guard.IsBlocked("10.0.0.9"));
            guard.Fail("10.0.0.9");
            Assert.True(guard.IsBlocked("10.0.0.9"));
            Assert.False(guard.IsBlocked("10.0.0.10"), "another address isn't held back");

            clock.Now += EnrollmentGuard.Window;
            Assert.False(guard.IsBlocked("10.0.0.9"));
            guard.Fail("10.0.0.9");
            Assert.False(guard.IsBlocked("10.0.0.9"), "the count starts again in a new window");
        }

        /// <summary>
        /// Phase 7's limit: each replica counted on its own, so N replicas allowed N times the failures. Now the count is shared: failures on two
        /// replicas add up, and a new window starts again for both.
        /// </summary>
        [Fact]
        public async Task Failed_enrollments_add_up_across_replicas_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var ct = TestContext.Current.CancellationToken;
            var clock = new Clock(DateTimeOffset.UtcNow);
            var (first, second) = (new EnrollmentGuard(clock), new EnrollmentGuard(clock));
            await using var db = database.Context();

            for (var i = 0; i < EnrollmentGuard.MaxFailures / 2; i++) await first.FailAsync(db, "10.0.0.9", ct);
            for (var i = 0; i < EnrollmentGuard.MaxFailures / 2 - 1; i++) await second.FailAsync(db, "10.0.0.9", ct);
            Assert.False(await first.IsBlockedAsync(db, "10.0.0.9", ct));
            Assert.False(await second.IsBlockedAsync(db, "10.0.0.9", ct));

            await second.FailAsync(db, "10.0.0.9", ct);
            Assert.True(await first.IsBlockedAsync(db, "10.0.0.9", ct), "failures on another replica didn't count here");
            Assert.True(await second.IsBlockedAsync(db, "10.0.0.9", ct));
            Assert.False(await first.IsBlockedAsync(db, "10.0.0.10", ct), "another address is held back");

            // Two replicas failing at the same moment both count.
            await Task.WhenAll(Enumerable.Range(0, EnrollmentGuard.MaxFailures).Select(async i =>
            {
                await using var own = database.Context();
                await (i % 2 == 0 ? first : second).FailAsync(own, "10.0.0.11", ct);
            }));
            Assert.Equal(EnrollmentGuard.MaxFailures, (await db.AgentEnrollmentFailures.AsNoTracking().SingleAsync(f => f.Address == "10.0.0.11", ct)).Failures);

            clock.Now += EnrollmentGuard.Window;
            Assert.False(await first.IsBlockedAsync(db, "10.0.0.9", ct));
            await first.FailAsync(db, "10.0.0.9", ct);
            Assert.False(await second.IsBlockedAsync(db, "10.0.0.9", ct), "the shared count didn't start again in a new window");
            Assert.Equal(1, (await db.AgentEnrollmentFailures.AsNoTracking().SingleAsync(f => f.Address == "10.0.0.9", ct)).Failures);
        }

        [Fact]
        public void Failures_from_more_addresses_than_it_remembers_hold_back_new_ones_until_they_expire()
        {
            var clock = new Clock(DateTimeOffset.UtcNow);
            var guard = new EnrollmentGuard(clock);
            for (var i = 0; i < EnrollmentGuard.MaxAddresses; i++) guard.Fail($"2001:db8::{i:x}");
            Assert.True(guard.IsBlocked("2001:db8:ffff::1"), "full: an address it doesn't know waits");
            guard.Fail("2001:db8:ffff::1");
            Assert.False(guard.IsBlocked("2001:db8::1"), "one it knows with a single failure isn't held back");

            clock.Now += EnrollmentGuard.Window;
            Assert.False(guard.IsBlocked("2001:db8:ffff::1"), "the old ones expired and made room");
        }

        [Fact]
        public void An_agent_on_an_unknown_host_is_looked_up_every_ten_minutes_not_every_poll()
        {
            var clock = new Clock(DateTimeOffset.UtcNow);
            var links = new HostLinks(clock);
            var agent = Guid.CreateVersion7();
            Assert.True(links.ShouldLook(agent, "web-1"));
            Assert.False(links.ShouldLook(agent, "web-1"));
            Assert.True(links.ShouldLook(agent, "web-2"), "a renamed host looks at once");
            clock.Now += HostLinks.Interval;
            Assert.True(links.ShouldLook(agent, "web-1"));
        }
    }
}
