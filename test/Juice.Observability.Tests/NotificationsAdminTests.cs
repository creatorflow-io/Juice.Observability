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
    }
}
