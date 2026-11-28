using System.Net;
using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Types;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Observability.Tests.ModuleNotifications
{
    /// <summary>A module's notification types in the host on the local stack: no new type versions, declared once, planned, and a bad one failing the pipeline's start, not the host's.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed class ModuleNotificationHostTests : IDisposable
    {
        private const string SecretVariable = "JUICE_C2M3_DEADMAN";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public ModuleNotificationHostTests() => Environment.SetEnvironmentVariable(SecretVariable, "dead-man-secret");

        public void Dispose() => Environment.SetEnvironmentVariable(SecretVariable, null);

        private static async Task<List<string>> RowsAsync(ScratchDatabase database, string where = "true")
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"SELECT key || '@' || version || '#' || schema_hash FROM notifications.notification_types WHERE {where} ORDER BY key, version", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }
            return rows;
        }

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        private static Dictionary<string, string?> Running() => new() { ["Notifications:Stages:Run"] = "true" };

        [Fact]
        public async Task Existing_database_no_new_type_versions()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // Provisioned as base declared them: every type of All() straight into the options, as AddPlatformNotifications did before the modules.
            await using (await ModuleTestHost.StartAsync(b =>
            {
                var embedded = (NotificationsEmbeddedOptions)b.Services.Single(s => s.ServiceType == typeof(NotificationsEmbeddedOptions)).ImplementationInstance!;
                embedded.Types.Clear();
                embedded.Types.AddRange(ObservabilityTypes.All());
            }, database, hostedServices: true))
            {
            }
            var before = await RowsAsync(database);
            Assert.Equal(ObservabilityTypes.All().Count(), before.Count);

            // Restarted with M3's composition (the agents module declares the action requests): the same rows, no new version.
            await using (await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }

            Assert.Equal(before, await RowsAsync(database));
            Assert.All(before, row => Assert.Contains("@1#", row));
        }

        [Fact]
        public async Task Two_replicas_declare_module_types_once()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var first = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<NotifySampleModule>(), database, hostedServices: true);
            await using var second = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<NotifySampleModule>(), database, hostedServices: true);

            Assert.Equal(1, await CountAsync(database, $"SELECT count(*) FROM notifications.notification_types WHERE key = '{NotifySampleModule.Ping}'"));
            Assert.Equal(ObservabilityTypes.All().Count() + 1, await CountAsync(database, "SELECT count(*) FROM notifications.notification_types"));
        }

        [Fact]
        public async Task Module_type_declared_and_request_planned()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<NotifySampleModule>(), database, hostedServices: true, settings: Running());
            Assert.Equal([$"{NotifySampleModule.Ping}@1"], (await RowsAsync(database, $"key = '{NotifySampleModule.Ping}'")).Select(r => r[..r.IndexOf('#')]));

            var marker = Guid.NewGuid();
            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                await scope.ServiceProvider.GetRequiredService<INotificationClient>().SendAsync(new NotificationToSend(NotifySampleModule.Ping, $"c2m3:{marker}")
                {
                    CorrelationKey = $"c2m3:{marker}",
                    Recipients = [new NotificationRecipientSpec("address", "email", "ops@example.invalid")],
                }, Ct);
                await transaction.CommitAsync(Ct);
            }

            var sql = $"SELECT count(*) FROM notifications.notifications n JOIN notifications.requests r ON r.id = n.request_id WHERE r.type_key = '{NotifySampleModule.Ping}' AND n.state = 'Planned'";
            for (var i = 0; i < 100 && await CountAsync(database, sql) == 0; i++)
            {
                await Task.Delay(100, Ct);
            }
            Assert.Equal(1, await CountAsync(database, sql));
        }

        [Fact]
        public async Task Invalid_module_type_fails_pipeline_readiness_host_up_deadman_reports()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await TestReceiver.StartAsync();
            var bad = NotifySampleModule.Declare(NotifySampleModule.Ping) with { Workflow = JsonSerializer.SerializeToElement(new[] { new { step = "bogus" } }) };
            var settings = Running();
            settings["Notifications:DeadMan:Url"] = receiver.Url;
            settings["Notifications:DeadMan:SecretRef"] = $"env:{SecretVariable}";
            settings["Notifications:DeadMan:AllowHttpToAllowlisted"] = "true";
            settings["Notifications:DeadMan:Interval"] = "00:00:01";
            settings["Notifications:DeadMan:Allowlist:0:Destination"] = $"{receiver.Address}/32";
            settings["Notifications:DeadMan:Allowlist:0:Ports:0"] = receiver.Port.ToString();
            using var _ = NotifySampleModule.Use(() => [bad]);
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<NotifySampleModule>(), database, hostedServices: true, settings: settings);

            using var client = host.Client;
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Ct)).StatusCode);
            Assert.NotNull(host.App.Services.GetRequiredService<PipelineStart>().Failure);
            // The core's own types were declared before the bad one failed? Not asserted: what follows the failed step is skipped, and a restart is the cure.
            List<JsonElement> Posted() => [.. receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.Clone())];
            for (var i = 0; i < 100 && !Posted().Any(p => p.GetProperty("cause").GetString() == "not-started"); i++)
            {
                await Task.Delay(100, Ct);
            }
            Assert.Contains(Posted(), p => p.GetProperty("cause").GetString() == "not-started" && p.GetProperty("type").GetString() == "observability.deadman.firing");
        }
    }
}
