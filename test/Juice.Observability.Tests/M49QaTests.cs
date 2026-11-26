using System.Text.Json;
using Juice.Observability.Health.DeadMan;
using Juice.Observability.Health.Delivery;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M49, #15): the dead-man alarm in the real host. In new with a broken provisioning file the receiver hears not-started, signed with the
    /// alarm's own secret; restarted healthy on the same database, the new lease holder tells it resolved.
    /// </summary>
    public class M49QaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string SecretVariable = "JUICE_QA_DEADMAN";
        private const string Secret = "qa-deadman-secret";

        private static Dictionary<string, string> Settings(WebhookTests.Receiver receiver, string? provisioning) => new()
        {
            ["Notifications:Pipeline"] = "new",
            ["Notifications:DeadMan:Url"] = receiver.Url, ["Notifications:DeadMan:SecretRef"] = $"env:{SecretVariable}",
            ["Notifications:DeadMan:AllowHttpToAllowlisted"] = "true", ["Notifications:DeadMan:Interval"] = "00:00:01",
            ["Notifications:DeadMan:Allowlist:0:Destination"] = $"{receiver.Address}/32", ["Notifications:DeadMan:Allowlist:0:Ports:0"] = receiver.Port.ToString(),
            ["Notifications:Provisioning:Path"] = provisioning ?? "",
        };

        [Fact]
        public async Task A_broken_start_raises_not_started_signed_and_a_healthy_restart_resolves_it_Async()
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable(SecretVariable, Secret);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            List<(string Type, string Cause, WebhookTests.Receiver.Received Raw)> Posted() =>
                [.. receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement is var p ? (p.GetProperty("type").GetString()!, p.GetProperty("cause").GetString()!, r) : default)];
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-qa-deadman-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""{ "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }""", Ct);
            try
            {
                await using (var broken = new ApiTests.ApiFactory(database, settings: Settings(receiver, file)))
                {
                    using var client = broken.CreateClient();
                    for (var i = 0; i < 150 && !Posted().Any(p => p.Cause == DeadManCauses.NotStarted); i++) await Task.Delay(100, Ct);
                    var firing = Assert.Single(Posted(), p => p.Cause == DeadManCauses.NotStarted);
                    Assert.Equal("observability.deadman.firing", firing.Type);
                    Assert.True(WebhookSigner.Verify(Secret, firing.Raw.Id, firing.Raw.Timestamp, firing.Raw.Signature, firing.Raw.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)),
                        "the alarm is signed with its own secret");
                    // The host is up, and not ready (the pipeline's start failed).
                    using var ready = await client.GetAsync("/health/ready", Ct);
                    Assert.False(ready.IsSuccessStatusCode);
                    // Rate-limited: three more passes, still once.
                    await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                    Assert.Single(Posted(), p => p.Cause == DeadManCauses.NotStarted && p.Type.EndsWith(".firing", StringComparison.Ordinal));
                }
            }
            finally
            {
                File.Delete(file);
            }

            // Fixed and restarted: the lease holder reconciles, and not-started is told resolved, once.
            await using (var healthy = new ApiTests.ApiFactory(database, settings: Settings(receiver, null)))
            {
                using var client = healthy.CreateClient();
                for (var i = 0; i < 300 && !Posted().Any(p => p.Cause == DeadManCauses.NotStarted && p.Type == "observability.deadman.resolved"); i++) await Task.Delay(100, Ct);
                Assert.Single(Posted(), p => p.Cause == DeadManCauses.NotStarted && p.Type == "observability.deadman.resolved");
                using var ready = await client.GetAsync("/health/ready", Ct);
                Assert.True(ready.IsSuccessStatusCode, $"ready: {ready.StatusCode}");
                Assert.DoesNotContain(Posted(), p => p.Type == "observability.deadman.firing" && p.Cause != DeadManCauses.NotStarted);
            }
        }
    }
}
