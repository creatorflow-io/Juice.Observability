using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The /api/v1 contracts (requirements §6.1) against the real host, a scratch database and Keycloak tokens:
    /// OIDC authentication, role checks, tenant scope, problem details, correlation ids, cursor pagination.
    /// </summary>
    public class ApiTests
    {
        [Fact]
        public async Task Health_is_anonymous_and_the_api_is_not_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Ct)).StatusCode);
            using var response = await client.GetAsync("/api/v1/tenants", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await ProblemAsync(response);
            Assert.True(problem.TryGetProperty("correlationId", out _));
            Assert.True(problem.TryGetProperty("traceId", out _));
        }

        [Fact]
        public async Task Tampered_token_and_wrong_audience_are_rejected_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var token = await LocalStack.TokenAsync("victor");

            await using (var factory = new ApiFactory(database))
            {
                using var client = factory.CreateClient();
                var tampered = token[..^4] + (token[^4..] == "AAAA" ? "BBBB" : "AAAA");
                Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, "/api/v1/tenants", tampered)).StatusCode);
            }
            await using (var other = new ApiFactory(database, audience: "another-api"))
            {
                using var client = other.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, "/api/v1/tenants", token)).StatusCode);
            }
        }

        [Fact]
        public async Task Users_without_a_platform_role_are_forbidden_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();

            using var response = await Get(client, "/api/v1/tenants", await LocalStack.TokenAsync("nora"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task Path_tenant_must_be_the_callers_tenant_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");

            var tenants = await JsonAsync(await Get(client, "/api/v1/tenants", token));
            var tenant = Assert.Single(tenants.GetProperty("items").EnumerateArray());
            Assert.Equal(LocalStack.TenantKey, tenant.GetProperty("key").GetString());
            var id = tenant.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await Get(client, $"/api/v1/tenants/{id}/applications", token)).StatusCode);

            // An unknown id and another tenant's id get the same answer.
            Guid otherTenant;
            await using (var db = database.Context())
            {
                var other = new PlatformTenant { Key = "other-tenant", DisplayName = "Other" };
                db.Tenants.Add(other);
                await db.SaveChangesAsync();
                otherTenant = other.Id;
            }
            foreach (var tenantId in new[] { otherTenant, Guid.CreateVersion7() })
            {
                using var response = await Get(client, $"/api/v1/tenants/{tenantId}/resources", token);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("Tenant not authorized", (await ProblemAsync(response)).GetProperty("title").GetString());
            }
        }

        [Fact]
        public async Task Resources_page_through_services_then_infrastructure_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var path = $"/api/v1/tenants/{seed.Tenant}/resources";

            var seen = new List<(string Type, Guid Id)>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = await JsonAsync(await Get(client, $"{path}?limit=2{(cursor is null ? "" : $"&cursor={cursor}")}", token));
                seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("type").GetString()!, i.GetProperty("id").GetGuid())));
                cursor = page.GetProperty("nextCursor").GetString();
                pages++;
            }
            while (cursor is not null && pages < 10);

            Assert.Equal(3, pages);
            Assert.Equal(5, seen.Select(s => s.Id).Distinct().Count());
            Assert.Equal(["service", "service", "service"], seen.Take(3).Select(s => s.Type));
            // ids made in the same millisecond have no defined order between them
            Assert.Equal(["database", "host"], seen.Skip(3).Select(s => s.Type).Order());

            var hosts = await JsonAsync(await Get(client, $"{path}?type=host", token));
            Assert.Single(hosts.GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, $"{path}?type=planet", token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, $"{path}?cursor=garbage", token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, $"{path}?limit=500", token)).StatusCode);
        }

        [Fact]
        public async Task Resource_detail_and_topology_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");
            var tenant = $"/api/v1/tenants/{seed.Tenant}";

            var orders = await JsonAsync(await Get(client, $"{tenant}/resources/{seed.Orders}", token));
            Assert.Equal("service", orders.GetProperty("type").GetString());
            Assert.Equal("shop/orders", orders.GetProperty("key").GetString());
            var instances = orders.GetProperty("instances").EnumerateArray().ToList();
            Assert.Equal(2, instances.Count);
            Assert.Equal("Active", instances[0].GetProperty("state").GetString());
            Assert.All(instances, i => Assert.Equal("production", i.GetProperty("environment").GetString()));

            var host = await JsonAsync(await Get(client, $"{tenant}/resources/{seed.Host}", token));
            Assert.Equal(2, host.GetProperty("instances").GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"{tenant}/resources/{Guid.CreateVersion7()}", token)).StatusCode);

            var topology = await JsonAsync(await Get(client, $"{tenant}/topology?environment=Production", token));
            var edges = topology.GetProperty("edges").EnumerateArray().ToList();
            Assert.Equal(2, edges.Count);
            Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "calls" && e.GetProperty("confidence").GetString() == "observed");
            Assert.Contains(edges, e => e.GetProperty("relation").GetString() == "usesDatabase" && e.GetProperty("confidence").GetString() == "inferred");
            Assert.Equal(3, topology.GetProperty("nodes").GetArrayLength());
            var staging = await JsonAsync(await Get(client, $"{tenant}/topology?environment=staging", token));
            Assert.Equal(0, staging.GetProperty("edges").GetArrayLength());
        }

        [Fact]
        public async Task Correlation_id_is_echoed_when_safe_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();

            using var safe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
            safe.Headers.Add("X-Correlation-Id", "order-42.retry_1");
            using var safeResponse = await client.SendAsync(safe, Ct);
            Assert.Equal("order-42.retry_1", safeResponse.Headers.GetValues("X-Correlation-Id").Single());
            Assert.Equal("order-42.retry_1", (await ProblemAsync(safeResponse)).GetProperty("correlationId").GetString());

            using var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
            unsafeRequest.Headers.Add("X-Correlation-Id", "<script>");
            using var unsafeResponse = await client.SendAsync(unsafeRequest, Ct);
            Assert.NotEqual("<script>", unsafeResponse.Headers.GetValues("X-Correlation-Id").Single());
        }

        [Fact]
        public async Task Startup_provisions_the_tenant_and_refuses_a_database_of_another_tenant_Async()
        {
            LocalStack.RequireAvailable();
            await using (var empty = await LocalStack.CreateDatabaseAsync(provisionTenant: false))
            {
                await using (var factory = new ApiFactory(empty))
                {
                    factory.CreateClient().Dispose();
                }
                await using var db = empty.Context();
                var tenant = await db.Tenants.SingleAsync();
                Assert.Equal(LocalStack.TenantKey, tenant.Key);
                var audit = await db.AuditRecords.SingleAsync();
                Assert.Equal(("system", "tenant.provision", AuditOutcome.Succeeded), (audit.Actor, audit.Operation, audit.Outcome));
            }

            await using var foreign = await LocalStack.CreateDatabaseAsync(provisionTenant: false);
            await using (var db = foreign.Context())
            {
                db.Tenants.Add(new PlatformTenant { Key = "other-tenant", DisplayName = "Other" });
                await db.SaveChangesAsync();
            }
            await using var refused = new ApiFactory(foreign);
            var error = Assert.ThrowsAny<Exception>(() => refused.CreateClient());
            Assert.Contains("belongs to tenant 'other-tenant'", error.ToString());
        }

        // ─── helpers ──────────────────────────────────────────────────────────

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed record Seed(Guid Tenant, Guid Orders, Guid Host);

        /// <summary>Three services, a host and a database; two instances of orders on the host; orders calls billing and uses the database.</summary>
        private static async Task<Seed> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = await db.Tenants.SingleAsync();
            var now = DateTimeOffset.UtcNow;
            var production = new DeploymentEnvironment { TenantId = tenant.Id, Key = "production", Type = EnvironmentType.Production, FirstSeen = now, LastSeen = now };
            var app = new Application { TenantId = tenant.Id, Key = "shop", DisplayName = "shop", FirstSeen = now, LastSeen = now };
            Service NewService(string name) => new() { TenantId = tenant.Id, Namespace = "shop", Name = name, ApplicationId = app.Id, FirstSeen = now, LastSeen = now };
            var (orders, billing, catalog) = (NewService("orders"), NewService("billing"), NewService("catalog"));
            var host = new Resource { TenantId = tenant.Id, Type = ResourceType.Host, Key = "web-1", DisplayName = "web-1", FirstSeen = now, LastSeen = now };
            var sql = new Resource { TenantId = tenant.Id, Type = ResourceType.Database, Key = "mssql:sql:orders", DisplayName = "orders", FirstSeen = now, LastSeen = now };
            db.AddRange(production, app, orders, billing, catalog, host, sql);
            db.AddRange(
                new ServiceInstance { TenantId = tenant.Id, ServiceId = orders.Id, EnvironmentId = production.Id, InstanceKey = "a", HostResourceId = host.Id, FirstSeen = now, LastSeen = now },
                new ServiceInstance { TenantId = tenant.Id, ServiceId = orders.Id, EnvironmentId = production.Id, InstanceKey = "b", HostResourceId = host.Id, State = InstanceState.Inactive, FirstSeen = now.AddHours(-1), LastSeen = now.AddHours(-1) },
                new Dependency { TenantId = tenant.Id, EnvironmentId = production.Id, SourceServiceId = orders.Id, Relation = DependencyRelation.Calls, TargetServiceId = billing.Id, Confidence = DependencyConfidence.Observed, SpanCount = 3, FirstSeen = now, LastSeen = now },
                new Dependency { TenantId = tenant.Id, EnvironmentId = production.Id, SourceServiceId = orders.Id, Relation = DependencyRelation.UsesDatabase, TargetResourceId = sql.Id, Confidence = DependencyConfidence.Inferred, SpanCount = 5, FirstSeen = now, LastSeen = now });
            await db.SaveChangesAsync();
            return new Seed(tenant.Id, orders.Id, host.Id);
        }

        private static async Task<HttpResponseMessage> Get(HttpClient client, string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, Ct);
        }

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
                return JsonDocument.Parse(body).RootElement.Clone();
            }
        }

        private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
            => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        private sealed class ApiFactory(ScratchDatabase database, string audience = "juice-platform") : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("ConnectionStrings:Platform", database.ConnectionString);
                builder.UseSetting("Platform:TenantKey", LocalStack.TenantKey);
                builder.UseSetting("ClickHouse:ConnectionString", LocalStack.ClickHouseReader);
                builder.UseSetting("Auth:Authority", LocalStack.Authority);
                builder.UseSetting("Auth:RequireHttpsMetadata", "false");
                builder.UseSetting("Auth:Audience", audience);
                builder.UseSetting("Discovery:Enabled", "false");
                builder.UseSetting("Database:Migrate", "true");
            }
        }
    }
}
