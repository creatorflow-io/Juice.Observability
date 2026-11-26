using System.Text.Json;
using Juice.Observability.Health.DeadMan;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Proof point 10, the dead-man drill (docs/drills.md): the dispatcher stalls, the dead-man arrives, the dispatcher recovers, the dead-man
    /// resolves, in one running host on the local stack. Explicit: it waits out the documented minimum threshold (2 minutes), so it runs only
    /// when asked (<c>dotnet test --filter DeadManDrill</c> with xUnit's explicit tests on).
    /// <para>The dispatcher is stalled the way a stuck one looks from the database: a delivery due for its retry is held locked by another
    /// session, so the dispatcher (which claims with <c>SKIP LOCKED</c>) can't take it, and it stays due and unleased.</para>
    /// </summary>
    public class DeadManDrill
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string SecretVariable = "JUICE_DEADMAN_DRILL";

        [Fact(Explicit = true)]
        public async Task The_dispatcher_stalls_the_dead_man_arrives_the_dispatcher_recovers_the_dead_man_resolves_Async()
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable(SecretVariable, "dead-man-drill");
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var alarm = await WebhookTests.Receiver.StartAsync();
            // The first attempt fails, so the delivery comes due again for its retry.
            await using var target = await WebhookTests.Receiver.StartAsync(500);
            var provisioning = Path.Combine(Path.GetTempPath(), $"deadman-drill-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(provisioning, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": {
                    "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{SecretVariable}}",
                                                 "targets": { "ops": { "address": "{{target.Url}}" } } } },
                    "routes": { "alerts": { "match": { "type": "observability.*" }, "targets": ["ops"] } },
                    "allowlist": [ { "destination": "{{target.Address}}", "ports": [{{target.Port}}] } ]
                } } }
                """, Ct);
            var log = new List<string>();
            void Note(string what) { log.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {what}"); TestContext.Current.SendDiagnosticMessage(log[^1]); }
            try
            {
                await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
                {
                    ["Notifications:Pipeline"] = "new", ["Notifications:Provisioning:Path"] = provisioning,
                    ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true", ["Notifications:Delivery:RetryBase"] = "00:00:02",
                    ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                    ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2",
                    ["Notifications:DeadMan:Url"] = alarm.Url, ["Notifications:DeadMan:SecretRef"] = $"env:{SecretVariable}",
                    ["Notifications:DeadMan:AllowHttpToAllowlisted"] = "true", ["Notifications:DeadMan:Interval"] = "00:00:02",
                    ["Notifications:DeadMan:DueThreshold"] = "00:02:00",
                    ["Notifications:DeadMan:Allowlist:0:Destination"] = $"{alarm.Address}/32", ["Notifications:DeadMan:Allowlist:0:Ports:0"] = alarm.Port.ToString(),
                });
                _ = factory.Server;
                var (_, firing) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(factory, database, firing);
                Note("alert fired; its request written");

                // The first attempt fails (500); then, before its retry is taken, the dispatcher is stalled on it.
                await using var stall = new NpgsqlConnection(database.ConnectionString);
                await stall.OpenAsync(Ct);
                Guid delivery = Guid.Empty;
                for (var i = 0; i < 300 && delivery == Guid.Empty; i++)
                {
                    await using var find = new NpgsqlCommand("SELECT id FROM notifications.deliveries WHERE attempts >= 1 AND next_at IS NOT NULL LIMIT 1", stall);
                    delivery = await find.ExecuteScalarAsync(Ct) is Guid id ? id : Guid.Empty;
                    if (delivery == Guid.Empty) await Task.Delay(50, Ct);
                }
                Assert.NotEqual(Guid.Empty, delivery);
                await using var transaction = await stall.BeginTransactionAsync(Ct);
                await using (var hold = new NpgsqlCommand($"SELECT id FROM notifications.deliveries WHERE id = '{delivery}' FOR UPDATE", stall, transaction))
                {
                    await hold.ExecuteScalarAsync(Ct);
                }
                Note($"dispatcher stalled: delivery {delivery} held, due for its retry");

                JsonElement? Alarm(string type) => alarm.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.Clone())
                    .Where(r => r.GetProperty("type").GetString() == type).Cast<JsonElement?>().FirstOrDefault();
                for (var i = 0; i < 200 && Alarm("observability.deadman.firing") is null; i++) await Task.Delay(1000, Ct);
                var firingAlarm = Alarm("observability.deadman.firing");
                Assert.NotNull(firingAlarm);
                Assert.Equal(DeadManCauses.Stalled, firingAlarm.Value.GetProperty("cause").GetString());
                Assert.Contains("dispatcher", firingAlarm.Value.GetProperty("detail").GetString());
                Note($"dead-man arrived: {firingAlarm.Value.GetProperty("detail").GetString()}");

                await transaction.RollbackAsync(Ct);
                Note("dispatcher released");
                for (var i = 0; i < 60 && (Alarm("observability.deadman.resolved") is null || target.Requests.Count(r => r.Body.Length > 0) < 2); i++) await Task.Delay(1000, Ct);
                Assert.True(target.Requests.Count(r => r.Body.Length > 0) >= 2, "the retry reached the target");
                var resolved = Alarm("observability.deadman.resolved");
                Assert.NotNull(resolved);
                Note("delivery sent on its retry; dead-man resolved");
                // Each once: the alarm isn't repeated inside its resend time.
                Assert.Equal(2, alarm.Requests.Count);
            }
            finally
            {
                File.Delete(provisioning);
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "deadman-drill.log"), log);
            }
        }
    }
}
