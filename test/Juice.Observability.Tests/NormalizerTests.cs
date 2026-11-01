using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;

namespace Juice.Observability.Tests
{
    public class NormalizerTests
    {
        private readonly Normalizer _normalizer = new([new ServiceAlias { FromName = "orders-v1", ToNamespace = "shop", ToName = "orders" }]);

        [Theory]
        [InlineData("", "unspecified", EnvironmentType.Unspecified)]
        [InlineData("  Production ", "production", EnvironmentType.Production)]
        [InlineData("STG", "stg", EnvironmentType.Staging)]
        [InlineData("local", "local", EnvironmentType.Development)]
        [InlineData("qa", "qa", EnvironmentType.Test)]
        [InlineData("blue", "blue", EnvironmentType.Other)]
        public void Environment_key_is_lowercase_and_typed(string raw, string key, EnvironmentType type)
        {
            Assert.Equal(key, _normalizer.EnvironmentKey(raw));
            Assert.Equal(type, Normalizer.EnvironmentTypeOf(key));
        }

        [Fact]
        public void Empty_service_name_is_host_only_telemetry()
            => Assert.Null(_normalizer.Service("shop", " "));

        [Fact]
        public void Alias_maps_a_renamed_service()
        {
            Assert.Equal(("shop", "orders"), _normalizer.Service("", "orders-v1"));
            Assert.Equal(("other", "orders-v1"), _normalizer.Service("other", "orders-v1"));
        }

        [Fact]
        public void Instance_key_prefers_service_instance_id()
        {
            Assert.Equal(("abc", InstanceKeySource.Observed),
                _normalizer.Instance(new ResourceObservation { InstanceId = "abc", HostName = "h", ProcessId = "1" }));
            Assert.Equal(("web-1:42", InstanceKeySource.Derived),
                _normalizer.Instance(new ResourceObservation { HostName = "WEB-1", ProcessId = "42" }));
            Assert.Equal(("web-1", InstanceKeySource.Derived), _normalizer.Instance(new ResourceObservation { HostName = "web-1" }));
            Assert.Null(_normalizer.Instance(new ResourceObservation()));
        }

        [Fact]
        public void Long_values_are_truncated_and_counted()
        {
            var value = _normalizer.Value(new string('x', 300));
            Assert.Equal(Normalizer.MaxValueLength, value.Length);
            Assert.Equal(1, _normalizer.Truncated);
        }

        [Fact]
        public void Host_key_is_host_id_when_reported()
        {
            var host = Assert.Single(_normalizer.Resources(new ResourceObservation { HostName = "Web-1", HostId = "ABC123" }));
            Assert.Equal((ResourceType.Host, "abc123", "web-1"), (host.Type, host.Key, host.DisplayName));
            var byName = Assert.Single(_normalizer.Resources(new ResourceObservation { HostName = "Web-1" }));
            Assert.Equal("web-1", byName.Key);
        }

        [Fact]
        public void Destination_and_database_keys()
        {
            var destination = _normalizer.UsedResource(new ResourceUseObservation { MessagingSystem = "RabbitMQ", MessagingDestination = "orders" })!;
            Assert.Equal((ResourceType.MessagingDestination, "rabbitmq:orders"), (destination.Type, destination.Key));
            var database = _normalizer.UsedResource(new ResourceUseObservation { DbSystem = "microsoft.sql_server", ServerAddress = "SQL", DbNamespace = "Billing" })!;
            Assert.Equal((ResourceType.Database, "microsoft.sql_server:sql:Billing", "Billing"), (database.Type, database.Key, database.DisplayName));
            Assert.Null(_normalizer.UsedResource(new ResourceUseObservation()));
        }

        [Fact]
        public void Attributes_are_bounded()
        {
            var many = Enumerable.Range(0, 100).ToDictionary(i => $"k{i:000}", i => "v");
            many["empty"] = "";
            var bounded = Normalizer.Bounded(many);
            Assert.Equal(Normalizer.MaxAttributes, bounded.Count);
            Assert.DoesNotContain("empty", bounded.Keys);
        }
    }

    public class CursorTests
    {
        [Fact]
        public void Cursor_round_trips()
        {
            var cursor = new Cursor("s", Guid.CreateVersion7());
            Assert.True(Cursor.TryDecode(cursor.Encode(), out var decoded));
            Assert.Equal(cursor, decoded);
        }

        [Theory]
        [InlineData("not base64 !")]
        [InlineData("YWJj")] // "abc"
        [InlineData("czpub3QtYS1ndWlk")] // "s:not-a-guid"
        public void Invalid_cursor_is_rejected(string value)
            => Assert.False(Cursor.TryDecode(value, out _));

        [Theory]
        [InlineData(0)]
        [InlineData(201)]
        public void Limit_out_of_range_is_a_problem(int limit)
            => Assert.NotNull(Cursor.Validate(limit, null, out _, out _));
    }
}
