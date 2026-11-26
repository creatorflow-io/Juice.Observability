using System.Net;
using System.Text.Json;
using Juice.Observability.Discovery;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The notifications admin API in the platform's host (phase 5, M48): Juice.Notifications.Admin under <c>/notifications</c>, behind the
    /// platform's authentication, with its roles mapped (p5-d6): <c>admin</c> everything, <c>operator</c> reads and retries, <c>viewer</c> nothing.
    /// </summary>
    public class NotificationsAdminTests
    {
        private const string Api = "/notifications/api/v1";

        private static readonly object Route = new { match = new { }, targets = Array.Empty<string>(), enabled = false };

        private static async Task<string?> CodeAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (string.IsNullOrEmpty(text)) return null;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }

        [Fact]
        public async Task The_platform_roles_map_to_the_admin_rights_for_the_platform_tenant_only_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, _) = await Caller.AllAsync(client, database);
            var tenant = PlatformOptions.TenantId(LocalStack.TenantKey);
            var routes = $"{Api}/tenants/{tenant}/routes";

            // Signed in or nothing: the host's fallback policy.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Api}/access", TestContext.Current.CancellationToken)).StatusCode);

            // The access read: embedded, the platform tenant with each role's rights; a viewer has no tenant.
            var access = await Caller.JsonAsync(await admin.GetAsync($"{Api}/access"));
            Assert.Equal("embedded", access.GetProperty("mode").GetString());
            var only = Assert.Single(access.GetProperty("tenants").EnumerateArray());
            Assert.Equal(tenant, only.GetProperty("id").GetGuid());
            Assert.Equal(["configure", "operate", "read"], only.GetProperty("rights").EnumerateArray().Select(r => r.GetString()!).Order());
            var operating = Assert.Single((await Caller.JsonAsync(await @operator.GetAsync($"{Api}/access"))).GetProperty("tenants").EnumerateArray());
            Assert.Equal(["operate", "read"], operating.GetProperty("rights").EnumerateArray().Select(r => r.GetString()!).Order());
            Assert.Empty((await Caller.JsonAsync(await viewer.GetAsync($"{Api}/access"))).GetProperty("tenants").EnumerateArray());

            // A viewer reads nothing.
            var viewed = await viewer.GetAsync(routes);
            Assert.Equal(HttpStatusCode.Forbidden, viewed.StatusCode);

            // An operator reads, but doesn't change the setup.
            Assert.Equal(HttpStatusCode.OK, (await @operator.GetAsync(routes)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await @operator.GetAsync($"{Api}/tenants/{tenant}/deliveries")).StatusCode);
            var refused = await @operator.PutAsync($"{routes}/m48-route", Route);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("not_allowed", await CodeAsync(refused));

            // An admin passes the rights check: the same body reaches the setup's own validation (a route needs a target, none exists here).
            var put = await admin.PutAsync($"{routes}/m48-route", Route);
            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            Assert.Equal("invalid_configuration", await CodeAsync(put));

            // Another tenant is refused before the roles are asked, even for an admin.
            var other = await admin.GetAsync($"{Api}/tenants/{Guid.NewGuid()}/routes");
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
            Assert.Equal("tenant_forbidden", await CodeAsync(other));

            // The Console's fallback doesn't answer the admin API's unknown paths with index.html.
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Api}/no-such-thing")).StatusCode);
        }

        /// <summary>
        /// One IdP may serve several deployments, and roles alone don't say which (#11 review): an admin of another deployment (their tenant
        /// claim isn't this deployment's tenant key) administers nothing here. The access read lists no tenant, and every route refuses
        /// (<c>not_tenant_admin</c>), as <c>/api/v1/platform/status</c> refuses them.
        /// </summary>
        [Fact]
        public async Task An_admin_of_another_deployment_administers_nothing_here_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            // The stack's own deployment signs its users in through the platform CLI client: their tenant claim names that deployment, not
            // this one (the tests' tenant). Same IdP, same roles.
            async Task<Caller> OfAnotherDeploymentAsync(string user)
            {
                using var keycloak = new HttpClient();
                using var response = await keycloak.PostAsync($"{LocalStack.Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "juice-platform-cli", ["username"] = user,
                        ["password"] = LocalStack.Secret("KEYCLOAK_DEMO_PASSWORD") }), TestContext.Current.CancellationToken);
                var token = (await Caller.JsonAsync(response)).GetProperty("access_token").GetString()!;
                var claim = JsonDocument.Parse(Convert.FromBase64String(Pad(token.Split('.')[1]))).RootElement.GetProperty("platform_tenant").GetString();
                Assert.NotEqual(LocalStack.TenantKey, claim);
                return new Caller(client, token);
            }
            static string Pad(string part) => part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            var (admin, @operator) = (await OfAnotherDeploymentAsync("alice"), await OfAnotherDeploymentAsync("erin"));
            var tenant = PlatformOptions.TenantId(LocalStack.TenantKey);

            // As the platform's own status refuses them.
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/platform/status")).StatusCode);
            Assert.Empty((await Caller.JsonAsync(await admin.GetAsync($"{Api}/access"))).GetProperty("tenants").EnumerateArray());
            foreach (var caller in new[] { admin, @operator })
            {
                foreach (var path in new[] { "routes", "deliveries", "integrations" })
                {
                    var refused = await caller.GetAsync($"{Api}/tenants/{tenant}/{path}");
                    Assert.Equal((HttpStatusCode.Forbidden, "not_tenant_admin"), (refused.StatusCode, await CodeAsync(refused)));
                }
            }
            var change = await admin.PutAsync($"{Api}/tenants/{tenant}/routes/m48-route", Route);
            Assert.Equal((HttpStatusCode.Forbidden, "not_tenant_admin"), (change.StatusCode, await CodeAsync(change)));
        }

        /// <summary>The roles count only with this deployment's tenant claim: none, or another's, is no right; this one's maps the roles.</summary>
        [Theory]
        [InlineData(null, "admin", false)]
        [InlineData("other", "admin", false)]
        [InlineData("this", "admin", true)]
        [InlineData("this", "operator", true)]
        [InlineData("this", "viewer", false)]
        public async Task The_tenant_claim_gates_the_roles(string? claim, string role, bool rights)
        {
            const string key = "0193f0aa-0000-7000-8000-0000000000f1";
            var tenant = PlatformOptions.TenantId(key);
            Juice.Notifications.Admin.INotificationsAdminAccess access = new Api.ObservabilityNotificationsAccess(
                new Juice.Notifications.Hosting.NotificationsHosting(Juice.Notifications.Hosting.NotificationsMode.Embedded, tenant),
                Microsoft.Extensions.Options.Options.Create(new Api.AuthOptions()), Microsoft.Extensions.Options.Options.Create(new PlatformOptions { TenantKey = key }));
            var claims = new List<System.Security.Claims.Claim> { new("roles", role) };
            if (claim is not null) claims.Add(new("platform_tenant", claim == "this" ? key : Guid.CreateVersion7().ToString()));
            var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test", "preferred_username", "roles"));
            var granted = await access.RightsAsync(user, tenant, TestContext.Current.CancellationToken);
            Assert.Equal(rights, granted != Juice.Notifications.Admin.AdminRights.None);
            Assert.Equal(rights, (await access.TenantsAsync(user, TestContext.Current.CancellationToken)).Ids.Count == 1);
        }
    }
}
