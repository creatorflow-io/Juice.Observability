using System.Net;
using System.Net.Http.Json;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests.Modules
{
    /// <summary>A module in the host on the local stack: tenancy, authorization, audit, idempotency and migrations.</summary>
    [Trait("Milestone", "C2-M1")]
    public sealed class ModuleIntegrationTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Items(ModuleTestHost host) => $"/api/v1/tenants/{host.TenantId}/sample/items";

        private static Task<ModuleTestHost> HostAsync(ScratchDatabase database, bool workers = true, Dictionary<string, string?>? settings = null)
            => ModuleTestHost.StartAsync(b => b.AddObservabilityModule<TestModule>(), database, hostedServices: workers, settings: settings);

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        private static StringContent Json(string name) => new($"{{\"name\":\"{name}\"}}", System.Text.Encoding.UTF8, "application/json");

        [Fact]
        public async Task Module_route_other_tenant_403_same_as_unknown()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("viewer");
            var before = TestModule.Handled;

            var other = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/sample/items", Ct);
            var unknown = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/sample/items", Ct);

            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Equal(Normalize(await other.Content.ReadAsStringAsync(Ct)), Normalize(await unknown.Content.ReadAsStringAsync(Ct)));
            Assert.Equal(before, TestModule.Handled);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Items(host), Ct)).StatusCode);
        }

        private static string Normalize(string problem) => System.Text.RegularExpressions.Regex.Replace(problem, "\"(traceId|correlationId)\":\"[^\"]*\"", "");

        [Fact]
        public async Task Module_route_token_without_tenant_claim_403()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("viewer", tenant: null);
            var before = TestModule.Handled;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Items(host), Ct)).StatusCode);
            Assert.Equal(before, TestModule.Handled);
        }

        [Fact]
        public async Task Module_route_viewer_denied_by_module_policy_403_with_correlationId()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var viewer = host.As("viewer");
            var response = await viewer.GetAsync($"/api/v1/tenants/{host.TenantId}/sample/write-only", Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("correlationId", await response.Content.ReadAsStringAsync(Ct));
            using var operatorClient = host.As("operator");
            Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync($"/api/v1/tenants/{host.TenantId}/sample/write-only", Ct)).StatusCode);
        }

        [Fact]
        public async Task Module_mutation_audited_with_module_prefix()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var operatorClient = host.As("operator");
            var created = await operatorClient.PostAsync(Items(host), Json("a"), Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var viewer = host.As("viewer");
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(Items(host), Json("b"), Ct)).StatusCode);

            await using var db = database.Context();
            var records = await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "sample.item.create").ToListAsync(Ct);
            var succeeded = Assert.Single(records, r => r.Outcome == AuditOutcome.Succeeded);
            Assert.Equal("operator-user", succeeded.Actor);
            Assert.False(string.IsNullOrEmpty(succeeded.CorrelationId));
            var denied = Assert.Single(records, r => r.Outcome == AuditOutcome.Denied);
            Assert.Equal("viewer-user", denied.Actor);
        }

        [Theory]
        [InlineData("wrong-tenant")]
        [InlineData("unprefixed")]
        public async Task Module_mutation_wrong_tenant_or_unprefixed_operation_throws_500_no_audit(string route)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");
            var response = await client.PostAsync($"/api/v1/tenants/{host.TenantId}/sample/{route}", null, Ct);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation LIKE 'sample.%' OR operation LIKE 'other.%'"));
        }

        [Fact]
        public async Task Module_idempotency_replay_and_concurrent_same_key_once()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("operator");
            HttpRequestMessage Post(string key, string name) => new(HttpMethod.Post, Items(host)) { Content = Json(name), Headers = { { "Idempotency-Key", key } } };

            var both = await Task.WhenAll(client.SendAsync(Post("k1", "a"), Ct), client.SendAsync(Post("k1", "a"), Ct));
            Assert.All(both, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
            Assert.Equal(await both[0].Content.ReadAsStringAsync(Ct), await both[1].Content.ReadAsStringAsync(Ct));
            Assert.Equal(1, both.Count(r => r.Headers.Contains("Idempotency-Replayed")));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation = 'sample.item.create'"));
            Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM sample.items"));
            Assert.Equal((HttpStatusCode)422, (await client.SendAsync(Post("k1", "other"), Ct)).StatusCode);
        }

        [Fact]
        public async Task Module_migrations_run_after_core_on_existing_database()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // A database a 2.0 core already migrated.
            await using (var core = await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'sample'"));
            var coreTables = await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'");

            await using (var host = await HostAsync(database))
            {
                Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'sample' AND table_name = 'items'"));
                Assert.Equal(coreTables, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
            }
            // A second start is a no-op.
            await using (var again = await HostAsync(database))
            {
                Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM sample.history"));
            }
        }

        [Fact]
        public async Task Two_hosts_starting_together_migrate_module_once()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var core = await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }
            var hosts = await Task.WhenAll(HostAsync(database), HostAsync(database));
            try
            {
                Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM sample.history"));
            }
            finally
            {
                foreach (var host in hosts)
                {
                    await host.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task Database_Migrate_false_skips_module_migrations()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var core = await ModuleTestHost.StartAsync(null, database, hostedServices: true))
            {
            }
            await using var host = await HostAsync(database, settings: new() { ["Database:Migrate"] = "false" });
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'sample'"));
        }

        [Fact]
        public async Task Module_migration_failure_stops_host()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<FailingModule>(), database, hostedServices: true);
            });
            // The core's schema is intact: its own migration ran first.
            Assert.True(await CountAsync(database, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'") > 5);
        }

        [Fact]
        public async Task Module_worker_runs()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            SampleWorker.Started = null;
            await using var host = await HostAsync(database);
            // After the runner: the worker found its table.
            Assert.Equal("table-present", SampleWorker.Started);
        }

        [Fact]
        public async Task Modules_listed_with_the_database_up()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            using var client = host.As("viewer");
            var page = await client.GetFromJsonAsync<ModulesDto>("/api/v1/modules", Ct);
            Assert.Equal(["core", "slos", "agents", "sample"], page!.Items.Select(i => i.Name));
        }
    }
}
