using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M45-1, waves §5.2): the adapter live in <c>new</c> with the pipeline's stages running, to a real receiver; legacy touching none of the
    /// pipeline's tables; the silence pass on two replicas at once; and what a failed start kept, replayed in order and once.
    /// </summary>
    public class M45AdapterQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string SecretVariable = "JUICE_WEBHOOK_QA_ADAPTER";

        private static Dictionary<string, string> New() => new()
        {
            ["Notifications:Pipeline"] = "new",
            ["Notifications:Delivery:ApiSecretEnvPrefix"] = "JUICE_WEBHOOK_",
            // The receiver is http on a private address, as legacy allows with Webhooks:AllowHttpToAllowlisted; the pipeline has its own switch.
            ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
            ["Notifications:Stages:Planner:Poll"] = "00:00:00.200",
            ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.200",
            ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.200",
        };

        private static async Task<T> PipelineAsync<T>(WebApplicationFactory<Program> factory, Juice.MediatR.IRequest<T> command)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var context = Juice.Notifications.Data.NotificationsMessageContext.Begin("qa");
            return await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
        }

        private static async Task<long> ScalarAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        // ─── new, live: the platform's changes reach a receiver through the pipeline, and legacy sends nothing ────────

        [Fact]
        public async Task In_new_a_firing_and_its_resolve_reach_a_receiver_through_the_pipeline_and_legacy_sends_nothing_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            Environment.SetEnvironmentVariable(SecretVariable, "qa-adapter-signing-secret");
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: New());
            _ = factory.Server;
            var tenant = Guid.Parse(LocalStack.TenantKey);
            Assert.True((await PipelineAsync(factory, new PutAllowlistEntry(tenant, receiver.Address.ToString(), [receiver.Port]))).Code is null);
            var integration = await PipelineAsync(factory, new PutIntegration(tenant, "qa", "webhook", "webhook", JsonDocument.Parse("{}").RootElement,
                $"env:{SecretVariable}", 0, true));
            Assert.True(integration.Code is null, $"{integration.Code} {integration.Message}");
            var target = await PipelineAsync(factory, new PutTarget(tenant, "qa", "qa", receiver.Url, null, true));
            Assert.True(target.Code is null, $"{target.Code} {target.Message}");
            var route = await PipelineAsync(factory, new PutRoute(tenant, "qa", JsonDocument.Parse("{}").RootElement, ["qa"], true));
            Assert.True(route.Code is null, $"{route.Code} {route.Message} {string.Join("; ", route.Errors?.Select(e => e.Pointer + " " + e.Message) ?? [])}");

            var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, firing);
            try
            {
                await WaitAsync(() => receiver.Requests.Count >= 1, "the firing to reach the receiver");
            }
            catch (Xunit.Sdk.XunitException)
            {
                throw new Xunit.Sdk.XunitException("timed out: " + await DiagnoseAsync(database));
            }
            var first = receiver.Requests.ToArray()[0];
            Assert.Contains(alert.ToString(), first.Body);
            Assert.Contains("alert.firing", first.Body);

            var resolved = await ProducerAdapterTests.ResolveAsync(database, alert);
            await ProducerAdapterTests.PlanAsync(factory, database, resolved);
            await WaitAsync(() => receiver.Requests.Count >= 2, "the resolved notice to reach the receiver");
            Assert.Contains("alert.resolved", receiver.Requests.ToArray()[1].Body);
            Assert.Equal("Resolved", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));

            // Once each, and nothing through legacy.
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Assert.Equal(2, receiver.Requests.Count);
            await using var db = database.Context();
            Assert.Empty(await db.WebhookDeliveries.ToListAsync(Ct));
            Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM notifications.deliveries WHERE status = 'Delivered'"));
        }

        /// <summary>Where the pipeline's rows stand, for a timeout's message.</summary>
        private static async Task<string> DiagnoseAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            var lines = new List<string>();
            foreach (var sql in new[]
            {
                "SELECT 'notification ' || state || ' ' || coalesce(failure, '') FROM notifications.notifications",
                "SELECT 'recipient ' || outcome || ' ' || coalesce(reason, '') FROM notifications.notification_recipients",
                "SELECT 'delivery ' || status || ' ' || coalesce(last_error, '') FROM notifications.deliveries",
                "SELECT 'attempt ' || outcome || ' ' || coalesce(error, '') FROM notifications.delivery_attempts",
            })
            {
                try
                {
                    await using var command = new NpgsqlCommand(sql, connection);
                    await using var reader = await command.ExecuteReaderAsync(Ct);
                    while (await reader.ReadAsync(Ct)) lines.Add(reader.IsDBNull(0) ? "(null)" : reader.GetString(0));
                }
                catch (PostgresException ex)
                {
                    lines.Add($"({ex.MessageText})");
                }
            }
            return string.Join(" | ", lines);
        }

        private static async Task WaitAsync(Func<bool> done, string what)
        {
            for (var i = 0; i < 150 && !done(); i++)
            {
                await Task.Delay(200, Ct);
            }
            Assert.True(done(), $"timed out waiting for {what}");
        }

        // ─── legacy runs nothing of the pipeline ────────

        [Fact]
        public async Task Legacy_reads_none_of_the_pipelines_tables_while_it_runs_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database);
            _ = factory.Server;
            await Task.Delay(TimeSpan.FromSeconds(3), Ct); // past start
            var before = await ReadsAsync(database);
            // Longer than every poll the pipeline would run (stages, outbox delivery, backlog monitor, retention, the release and hold passes).
            await Task.Delay(TimeSpan.FromSeconds(20), Ct);
            var after = await ReadsAsync(database);
            Assert.Equal(before, after);
            // And the host keeps fewer connections to its database than a pipeline host would need.
            var connections = await ScalarAsync(database, $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{database.Name}'");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m45-1.txt"), $"{DateTime.UtcNow:O} legacy host connections: {connections}{Environment.NewLine}");
        }

        /// <summary>Scans and fetches on the notifications schema's tables (statistics are flushed by the next stat read).</summary>
        private static async Task<long> ReadsAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", connection))
            {
                try { await flush.ExecuteNonQueryAsync(Ct); } catch (PostgresException) { } // PostgreSQL 15+
            }
            await Task.Delay(1500, Ct); // the stats collector's interval
            await using var command = new NpgsqlCommand(
                "SELECT coalesce(sum(seq_scan + coalesce(idx_scan, 0) + n_tup_ins + n_tup_upd + n_tup_del), 0) FROM pg_stat_user_tables WHERE schemaname = 'notifications'", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        // ─── Two replicas: the silence pass at once ────────

        [Fact]
        public async Task Two_replicas_running_the_silence_pass_at_once_keep_one_hold_and_lose_none_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var one = new ApiTests.ApiFactory(database, settings: New());
            await using var two = new ApiTests.ApiFactory(database, settings: New());
            _ = one.Server;
            _ = two.Server;
            var (alert, firing) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(one, database, firing);
            var passes = new[] { Pass(one), Pass(two) };

            for (var round = 0; round < 6; round++)
            {
                // A silence that covers the alert starts by time (inserted, as a future start reaches now), then ends by time.
                var silence = await ProducerAdapterTests.SilenceAsync(database);
                await Task.WhenAll(passes.Select(p => Task.Run(() => p.RunOnceAsync(Ct), Ct)).Concat(passes.Select(p => Task.Run(() => p.RunOnceAsync(Ct), Ct))));
                Assert.Equal(1, await ScalarAsync(database, $"SELECT count(*) FROM alert_holds WHERE alert_id = '{alert}' AND silence_id = '{silence}'"));
                Assert.Equal(1, await ScalarAsync(database, $"SELECT count(*) FROM notifications.correlations WHERE key = 'alert:{alert}' AND hold_reason = 'silence:{silence}'"));

                await using (var db = database.Context())
                {
                    await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
                }
                await Task.WhenAll(passes.Select(p => Task.Run(() => p.RunOnceAsync(Ct), Ct)).Concat(passes.Select(p => Task.Run(() => p.RunOnceAsync(Ct), Ct))));
                Assert.Equal(0, await ScalarAsync(database, $"SELECT count(*) FROM alert_holds WHERE alert_id = '{alert}'"));
                Assert.Equal(0, await ScalarAsync(database, $"SELECT count(*) FROM notifications.correlations WHERE key = 'alert:{alert}' AND held_at IS NOT NULL"));
            }
            // One firing request through it all.
            Assert.Single(await ProducerAdapterTests.RequestsAsync(database), r => r.Key == $"transition:{firing}");
        }

        private static SilenceHoldPass Pass(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()), NullLogger<SilenceHoldPass>.Instance);

        private static NotificationsReleasePass Release(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new Webhooks.WebhookOptions()), TimeProvider.System,
                NullLogger<NotificationsReleasePass>.Instance);

        // ─── A failed start: everything kept is replayed in order, once ────────

        [Fact]
        public async Task What_a_failed_start_kept_is_replayed_in_order_once_by_two_replicas_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-qa-provisioning-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }
                """, Ct);
            var tenant = await ProducerAdapterTests.TenantAsync(database);
            var request = new ActionRequest
            {
                TenantId = tenant, AgentId = Guid.NewGuid(), Action = "restart-service", Parameters = "{}", Risk = "medium", Policy = "admin", Reason = "stuck",
                RequestedBy = "erin", RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), State = ActionRequestState.Pending,
            };
            Guid alert;
            try
            {
                await using var down = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>(New()) { ["Notifications:Provisioning:Path"] = file });
                _ = down.Server;
                // While down: an alert fires (its firing is kept), an action request is made and then rejected (both notices kept).
                Guid firing;
                (alert, firing) = await ProducerAdapterTests.FireAsync(database);
                await ProducerAdapterTests.PlanAsync(down, database, firing);
                foreach (var (state, type) in new[] { (ActionRequestState.Pending, ActionRequestEvents.Created), (ActionRequestState.Rejected, ActionRequestEvents.Rejected) })
                {
                    request.State = state;
                    await using var scope = down.Services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                    await Agents.ActionRequestNotices.AddAsync(db, scope.ServiceProvider, new Agents.ActionApprovalOptions(), LocalStack.TenantKey, request, "web-1",
                        type, DateTimeOffset.UtcNow, Ct);
                    await db.SaveChangesAsync(Ct);
                    await transaction.CommitAsync(Ct);
                }
                Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM notifications.requests WHERE idempotency_key LIKE 'action:%' OR idempotency_key LIKE 'transition:%'"));
                Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM notification_parked_calls WHERE replayed_at IS NULL"));
                Assert.Equal(1, await ScalarAsync(database, $"SELECT count(*) FROM notification_holds WHERE alert_id = '{alert}' AND decided_at IS NULL"));
            }
            finally
            {
                File.Delete(file);
            }

            // Up again, on two replicas, whose passes run at once with the hosts' own.
            await using var one = new ApiTests.ApiFactory(database, settings: New());
            await using var two = new ApiTests.ApiFactory(database, settings: New());
            _ = one.Server;
            _ = two.Server;
            var releases = new[] { Release(one), Release(two) };
            await Task.WhenAll(releases.Select(r => Task.Run(() => r.ReleaseOnceAsync(Ct), Ct)).Concat(releases.Select(r => Task.Run(() => r.ReleaseOnceAsync(Ct), Ct))));
            await Task.WhenAll(releases.Select(r => r.ReleaseOnceAsync(Ct)));

            var requests = await ProducerAdapterTests.RequestsAsync(database);
            // Once each: the firing, the created notice (correlated while open) and the rejected one (its end, uncorrelated), in the order they happened.
            Assert.Single(requests, r => r.Key.StartsWith("transition:", StringComparison.Ordinal));
            var notices = requests.Where(r => r.Key.StartsWith($"action:{request.Id}:", StringComparison.Ordinal)).Select(r => (r.Key, r.Correlation)).ToList();
            Assert.Equal([($"action:{request.Id}:created", $"action:{request.Id}"), ($"action:{request.Id}:rejected", (string?)null)], notices);
            Assert.Equal("Resolved", await ProducerAdapterTests.CorrelationStateAsync(database, $"action:{request.Id}"));
            Assert.Equal("Open", await ProducerAdapterTests.CorrelationStateAsync(database, $"alert:{alert}"));
            Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM notification_parked_calls WHERE replayed_at IS NULL OR attempts > 0"));
            Assert.Equal(1, await ScalarAsync(database, $"SELECT count(*) FROM notification_holds WHERE alert_id = '{alert}' AND outcome = 'sent' AND attempts = 0"));
        }
    }
}
