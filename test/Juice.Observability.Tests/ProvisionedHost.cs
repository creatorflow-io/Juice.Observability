using System.Text.Json;
using Juice.Observability.Health.Delivery;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// A host whose endpoints and allowlist are declared the way 2.0 does: a provisioning file (<c>Notifications:Provisioning:Path</c>) with an
    /// integration whose target is a real receiver, a route and the receiver's address in the allowlist.
    /// </summary>
    internal static class ProvisionedHost
    {
        public const string SecretVariable = "JUICE_M50_PROVISIONED";
        public const string Secret = "m50-signing-secret";

        public sealed class TempFile(string path) : IAsyncDisposable
        {
            public string Path { get; } = path;

            public ValueTask DisposeAsync()
            {
                File.Delete(Path);
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// The file: a webhook integration with a target for each receiver (<c>ops</c>, then <c>ops2</c>…), a route from the alerts' labels to all of them, and the
        /// first receiver's address and port allowlisted (more in <paramref name="allowlist"/>).
        /// </summary>
        public static TempFile Write(TestReceiver receiver, string? tenantKey = null, IEnumerable<object>? allowlist = null, params TestReceiver[] more)
        {
            Environment.SetEnvironmentVariable(SecretVariable, Secret);
            var receivers = new[] { receiver }.Concat(more).ToList();
            var targets = receivers.Select((r, i) => (Key: i == 0 ? "ops" : $"ops{i + 1}", r)).ToList();
            var file = new
            {
                tenants = new Dictionary<string, object>
                {
                    [tenantKey ?? LocalStack.TenantKey] = new
                    {
                        integrations = new Dictionary<string, object>
                        {
                            ["hooks"] = new
                            {
                                channel = "webhook", kind = "webhook", secretRef = $"env:{SecretVariable}",
                                targets = targets.ToDictionary(t => t.Key, t => (object)new { address = t.r.Url }),
                            },
                        },
                        routes = new Dictionary<string, object>
                        {
                            // The producer's labels: the type, the rule, the environment and the severity (lower case).
                            ["alerts"] = new
                            {
                                match = new { type = "observability.alert.*", labels = new Dictionary<string, string> { ["rule"] = "orders-errors", ["environment"] = "production", ["severity"] = "critical" } },
                                targets = targets.Select(t => t.Key).ToArray(),
                            },
                        },
                        allowlist = new object[] { new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } } }.Concat(allowlist ?? []).ToArray(),
                    },
                },
            };
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"juice-obs-m50-provisioning-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(file));
            return new TempFile(path);
        }

        /// <summary>A host that runs the pipeline's stages quickly, sends once, and may send over http to what the file allowlists (the receiver is http on a private address).</summary>
        public static Dictionary<string, string> Settings(string path) => new()
        {
            ["Notifications:Provisioning:Path"] = path,
            ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true", ["Notifications:Delivery:MaxAttempts"] = "1",
            ["Notifications:Stages:Planner:Poll"] = "00:00:00.200", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.200", ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.200",
            ["Notifications:ReleasePassInterval"] = "00:00:01", ["Notifications:HoldPassInterval"] = "01:00:00",
        };

        public static async Task WaitAsync(Func<bool> done, string what, int seconds = 30)
        {
            for (var i = 0; i < seconds * 5 && !done(); i++)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
            Assert.True(done(), $"timed out waiting for {what}");
        }

        public static bool Verifies(TestReceiver.Received request)
            => WebhookSigner.Verify(Secret, request.Id, request.Timestamp, request.Signature, request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }
}
