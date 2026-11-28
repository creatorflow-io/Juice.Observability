using System.Net;
using Juice.Observability.Api;
using Juice.Observability.Tests.Modules;
using Npgsql;

namespace Juice.Observability.Tests.ModuleData
{
    /// <summary>A module's own schema and history, and its writes committing with the core's audit and idempotency records (closes W-19's gap). Local stack.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class ModuleDataTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Items(ModuleTestHost host, string? mode = null) => $"/api/v1/tenants/{host.TenantId}/data-sample/items" + (mode is null ? "" : $"?mode={mode}");

        private static Task<ModuleTestHost> HostAsync(ScratchDatabase database)
            => ModuleTestHost.StartAsync(b => b.AddObservabilityModule<DataSampleModule>(), database, hostedServices: true);

        internal static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        private static StringContent Json(string name) => new($"{{\"name\":\"{name}\"}}", System.Text.Encoding.UTF8, "application/json");

        [Fact]
        public async Task Own_schema_and_history_table()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);

            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'data_sample' AND table_name = 'items'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.__ef_migrations_history WHERE \"MigrationId\" = '20261129000000_CreateItems'"));
            // The core's history is the core's: the module's migration isn't in it.
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM public.__ef_migrations_history WHERE migration_id LIKE '%CreateItems'"));
        }

        [Fact]
        public async Task Module_write_and_audit_commit_together()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");

            var created = await client.PostAsync(Items(host), Json("a"), Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create' AND outcome = 'Succeeded'"));

            // The handler dies after the module's row was written, in the transaction: neither the row nor an audit record survives.
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsync(Items(host, "throw"), Json("b"), Ct)).StatusCode);
            // The audit record fails to save (a target over its length): the module's row, saved first in the same transaction, goes with it.
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsync(Items(host, "bad-audit"), Json("c"), Ct)).StatusCode);

            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create'"));
        }

        [Fact]
        public async Task Save_without_explicit_transaction_still_atomic()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");

            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync(Items(host, "no-tx"), Json("a"), Ct)).StatusCode);
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create'"));

            // The audit record fails to save: the module's row, saved first in the transaction the save opened, goes with it.
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsync(Items(host, "no-tx-bad-audit"), Json("b"), Ct)).StatusCode);
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create'"));
        }

        [Fact]
        public async Task Concurrent_same_idempotency_key_one_module_row()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");
            HttpRequestMessage Post(string key, string name) => new(HttpMethod.Post, Items(host)) { Content = Json(name), Headers = { { "Idempotency-Key", key } } };

            var both = await Task.WhenAll(client.SendAsync(Post("k1", "a"), Ct), client.SendAsync(Post("k1", "a"), Ct));

            Assert.All(both, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
            Assert.Equal(1, both.Count(r => r.Headers.Contains("Idempotency-Replayed")));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM idempotency_records WHERE key = 'k1'"));
        }

        [Fact]
        public async Task Denial_after_the_module_wrote_commits_only_the_denial()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");

            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/tenants/{host.TenantId}/data-sample/items-late-deny", Json("a"), Ct)).StatusCode);

            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create' AND outcome = 'Denied'"));
        }

        [Fact]
        public async Task Denied_request_audits_the_denial_and_writes_nothing_of_the_module()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var viewer = host.As("viewer");

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(Items(host), Json("a"), Ct)).StatusCode);

            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM data_sample.items"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'data-sample.item.create' AND outcome = 'Denied'"));
        }
    }
}
