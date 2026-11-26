using Juice.Observability.Notifications.Migration;

namespace Juice.Observability.Tests
{
    /// <summary>A legacy allowlist entry as a <c>destination_allowlist</c> row, and back (phase-5-plan I66).</summary>
    public class AllowlistMappingTests
    {
        [Theory]
        [InlineData("10.20.0.0/16", new int[0])]
        [InlineData("10.20.0.0/16", new[] { 443, 8443 })]
        [InlineData("receiver.internal.example", new[] { 8080 })]
        [InlineData("192.168.1.10/32", new int[0])]
        public void An_entry_maps_and_maps_back(string destination, int[] ports)
        {
            var legacy = new LegacyAllowlistEntry(destination, ports);
            var mapped = AllowlistMapping.Map(legacy);
            Assert.True(mapped.Succeeded, string.Join("; ", mapped.Problems.Select(p => p.Message)));
            Assert.Equal(new AllowlistDefinition(destination, ports), mapped.Value);
            Assert.Equal(legacy, AllowlistMapping.Unmap(mapped.Value!));
        }

        [Theory]
        [InlineData("10.20.1.5/16", "10.20.0.0/16")]
        [InlineData("Receiver.Internal.Example.", "receiver.internal.example")]
        [InlineData("192.168.1.10", "192.168.1.10/32")]
        public void A_destination_is_written_as_notifications_stores_it(string destination, string stored)
            => Assert.Equal(stored, AllowlistMapping.Map(new LegacyAllowlistEntry(destination, [])).Value!.Destination);

        [Fact]
        public void Ports_are_in_order_once_each()
            => Assert.Equal([443, 8080], AllowlistMapping.Map(new LegacyAllowlistEntry("10.0.0.0/8", [8080, 443, 8080])).Value!.Ports);

        [Theory]
        [InlineData("not a host!", new int[0])]
        [InlineData("127.0.0.1/32", new int[0])]
        [InlineData("169.254.169.254/32", new int[0])]
        [InlineData("10.0.0.0/8", new[] { 70000 })]
        public void What_notifications_never_reaches_is_refused(string destination, int[] ports)
            => Assert.Equal(ProblemCodes.Destination, Assert.Single(AllowlistMapping.Map(new LegacyAllowlistEntry(destination, ports)).Problems).Code);
    }
}
