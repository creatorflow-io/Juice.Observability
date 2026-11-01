using System.Net;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Tests
{
    /// <summary>SSRF rules and webhook signatures (requirements §8), without a network.</summary>
    public class WebhookSecurityTests
    {
        private static readonly AllowedDestination[] Allowlist =
            [new("10.20.0.0/16", [8443]), new("hooks.internal", []), new("127.0.0.0/8", [80])];

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("::ffff:127.0.0.1")]        // IPv4-mapped loopback
        [InlineData("169.254.169.254")]         // cloud metadata
        [InlineData("fd00:ec2::254")]           // AWS IPv6 metadata
        [InlineData("fe80::1")]
        [InlineData("0.0.0.0")]
        [InlineData("224.0.0.1")]
        [InlineData("255.255.255.255")]
        public void Never_reachable_addresses_are_blocked(string address)
            => Assert.Equal(DestinationClass.Blocked, DestinationPolicy.Classify(IPAddress.Parse(address)));

        [Theory]
        [InlineData("10.1.2.3")]
        [InlineData("172.20.0.5")]
        [InlineData("192.168.1.10")]
        [InlineData("100.64.0.1")]
        [InlineData("fd12:3456::1")]
        public void Private_addresses_need_an_allowlist_entry(string address)
            => Assert.Equal(DestinationClass.Private, DestinationPolicy.Classify(IPAddress.Parse(address)));

        [Fact]
        public void Public_address_is_reachable_over_https_only()
        {
            IPAddress[] publicIp = [IPAddress.Parse("93.184.216.34")];
            Assert.Null(DestinationPolicy.Check("https", "example.com", 443, publicIp, [], allowHttpToAllowlisted: true));
            Assert.Equal("https_required", DestinationPolicy.Check("http", "example.com", 80, publicIp, [], allowHttpToAllowlisted: true));
        }

        [Fact]
        public void Allowlist_opens_private_destinations_on_their_ports()
        {
            IPAddress[] inside = [IPAddress.Parse("10.20.5.6")];
            Assert.Null(DestinationPolicy.Check("https", "ops", 8443, inside, Allowlist, false));
            Assert.StartsWith("private_address_not_allowlisted", DestinationPolicy.Check("https", "ops", 443, inside, Allowlist, false));
            Assert.StartsWith("private_address_not_allowlisted", DestinationPolicy.Check("https", "ops", 8443, [IPAddress.Parse("10.30.0.1")], Allowlist, false));
            // A host-name entry covers whatever private address the name resolves to, on 443 by default.
            Assert.Null(DestinationPolicy.Check("https", "hooks.internal", 443, [IPAddress.Parse("192.168.9.9")], Allowlist, false));
            // HTTP to an allowlisted destination only when enabled.
            Assert.Equal("https_required", DestinationPolicy.Check("http", "ops", 8443, inside, Allowlist, false));
            Assert.Null(DestinationPolicy.Check("http", "ops", 8443, inside, Allowlist, true));
        }

        [Fact]
        public void Loopback_stays_blocked_even_when_allowlisted()
            => Assert.StartsWith("blocked_address", DestinationPolicy.Check("http", "x", 80, [IPAddress.Loopback], Allowlist, true));

        [Fact]
        public void One_internal_answer_among_public_ones_blocks_the_destination()
            => Assert.StartsWith("blocked_address", DestinationPolicy.Check("https", "rebind.example", 443,
                [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("169.254.169.254")], [], false));

        [Theory]
        [InlineData("127.0.0.0/8")]
        [InlineData("169.254.0.0/16")]
        [InlineData("not a host!")]
        public void Allowlist_entries_that_can_never_work_are_configuration_errors(string destination)
            => Assert.NotNull(DestinationPolicy.ValidateEntry(new AllowedDestination(destination, [])));

        [Fact]
        public void Signature_verifies_and_rejects_tampering_and_replays()
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            var ts = now.ToUnixTimeSeconds();
            const string body = """{"type":"alert.firing"}""";
            var signature = WebhookSigner.Sign("s3cret", "msg-1", ts, body);
            Assert.StartsWith("v1,", signature);
            var window = TimeSpan.FromMinutes(5);

            Assert.True(WebhookSigner.Verify("s3cret", "msg-1", ts.ToString(), signature, body, now, window));
            Assert.True(WebhookSigner.Verify("s3cret", "msg-1", ts.ToString(), $"v1,AAAA {signature}", body, now, window));   // rotation: several signatures
            Assert.False(WebhookSigner.Verify("s3cret", "msg-1", ts.ToString(), signature, body + " ", now, window));          // body changed
            Assert.False(WebhookSigner.Verify("other", "msg-1", ts.ToString(), signature, body, now, window));                 // wrong secret
            Assert.False(WebhookSigner.Verify("s3cret", "msg-2", ts.ToString(), signature, body, now, window));                // id changed
            Assert.False(WebhookSigner.Verify("s3cret", "msg-1", ts.ToString(), signature, body, now.AddMinutes(6), window));  // replayed too late
            Assert.False(WebhookSigner.Verify("s3cret", "msg-1", "not-a-time", signature, body, now, window));
        }

        [Fact]
        public void Secret_references_resolve_from_environment_or_file()
        {
            Environment.SetEnvironmentVariable("JUICE_TEST_SECRET_REF", "from-env");
            Assert.Equal("from-env", SecretReference.Resolve("env:JUICE_TEST_SECRET_REF"));
            var file = Path.GetTempFileName();
            File.WriteAllText(file, "from-file\n");
            Assert.Equal("from-file", SecretReference.Resolve($"file:{file}"));
            File.Delete(file);
            Assert.Throws<InvalidOperationException>(() => SecretReference.Resolve("env:JUICE_TEST_SECRET_MISSING"));
            Assert.False(SecretReference.IsValid("plain-secret"));
        }
    }
}
