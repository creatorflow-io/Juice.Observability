using Juice.Notifications.Providers;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;

namespace Juice.Observability.Tests
{
    /// <summary>Secret references keep their text and are never resolved (phase-5-plan I70).</summary>
    public class SecretMappingTests
    {
        private static readonly DeliveryOptions Delivery = new() { ApiSecretEnvPrefix = "JUICE_WEBHOOK_", ApiSecretDirectory = "/run/secrets/juice" };

        [Theory]
        [InlineData("env:JUICE_WEBHOOK_OPS")]
        [InlineData("file:/run/secrets/juice/ops")]
        public void A_reference_the_api_may_name_keeps_its_text(string reference)
            => Assert.Equal(reference, SecretMapping.Map(reference, Delivery).Value);

        [Theory]
        [InlineData("env:OTHER", ProblemCodes.SecretOutOfReach)]
        [InlineData("env:JUICE_WEBHOOK_", ProblemCodes.SecretOutOfReach)]
        [InlineData("file:/etc/shadow", ProblemCodes.SecretOutOfReach)]
        [InlineData("file:/run/secrets/juice/../other", ProblemCodes.SecretOutOfReach)]
        [InlineData("vault:kv/x#y", ProblemCodes.SecretRef)]
        [InlineData("plain-secret-value", ProblemCodes.SecretRef)]
        [InlineData("", ProblemCodes.SecretRef)]
        [InlineData(null, ProblemCodes.SecretRef)]
        public void Anything_else_is_refused_with_why(string? reference, string code)
        {
            var mapped = SecretMapping.Map(reference, Delivery);
            Assert.Null(mapped.Value);
            Assert.Equal(code, Assert.Single(mapped.Problems).Code);
        }

        [Fact]
        public void A_file_is_out_of_reach_when_notifications_names_no_folder()
            => Assert.Equal(ProblemCodes.SecretOutOfReach, Assert.Single(SecretMapping.Map("file:/run/secrets/juice/ops", new DeliveryOptions { ApiSecretEnvPrefix = "JUICE_WEBHOOK_" }).Problems).Code);

        [Fact]
        public void No_secret_is_read_while_mapping()
        {
            // The variable and the file exist with values; the mapping gives back the reference, never what it names.
            var name = "JUICE_WEBHOOK_MAPPING_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(name, "the-secret-value");
            var directory = Directory.CreateTempSubdirectory().FullName;
            var file = Path.Combine(directory, "signing");
            File.WriteAllText(file, "the-file-secret");
            try
            {
                var delivery = new DeliveryOptions { ApiSecretEnvPrefix = "JUICE_WEBHOOK_", ApiSecretDirectory = directory };
                var options = new EndpointMappingOptions(delivery);
                foreach (var reference in new[] { "env:" + name, "file:" + file })
                {
                    var setup = EndpointMapping.Map(EndpointMappingTests.Endpoint(secret: reference), options).Value!;
                    var chat = EndpointMapping.Map(EndpointMappingTests.Endpoint(kind: ChannelKind.Slack, secret: reference), options).Value!;
                    var all = System.Text.Json.JsonSerializer.Serialize(new { setup, chat });
                    Assert.DoesNotContain("the-secret-value", all);
                    Assert.DoesNotContain("the-file-secret", all);
                    Assert.Equal(reference, setup.Integration.SecretRef);
                    Assert.Equal(reference, chat.Targets[0].AddressRef);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
                Directory.Delete(directory, true);
            }
        }
    }
}
