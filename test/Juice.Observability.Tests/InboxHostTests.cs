using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Juice.Notifications.Hosting;
using Juice.Notifications.Inbox;
using Juice.Observability.Api;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The embedded inbox in the platform's host (M54, slice B): who is a person, who is let in, where the hub's token is read, and that off is
    /// the host it was.
    /// </summary>
    [Trait("Milestone", "M54")]
    public sealed class InboxHostTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string ConsoleUrl = "https://console.example.com";

        internal static readonly Guid Tenant = PlatformOptions.TenantId(LocalStack.TenantKey);

        /// <summary>A host with the inbox on and the pipeline's stages quiet: what the API answers, nothing delivered.</summary>
        internal static Dictionary<string, string> On(Dictionary<string, string>? more = null)
        {
            var settings = new Dictionary<string, string>(ProducerAdapterTests.Quiet())
            {
                ["Notifications:Inbox:Enabled"] = "true", ["Notifications:ConsoleUrl"] = ConsoleUrl,
            };
            foreach (var (key, value) in more ?? [])
            {
                settings[key] = value;
            }
            return settings;
        }

        internal static string Me(string path = "") => $"/notifications/api/v1/tenants/{Tenant}/me/inbox{path}";

        // ─── The person: the host's own judgement of a token ──────────────────

        private static ObservabilityInboxAccess Access(string tenantKey = LocalStack.TenantKey)
            => new(new NotificationsHosting(NotificationsMode.Embedded, Tenant, true), Options.Create(new AuthOptions()),
                Options.Create(new PlatformOptions { TenantKey = tenantKey }));

        private static ClaimsPrincipal Token(params (string Type, string Value)[] claims)
            => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test", "preferred_username", "roles"));

        private static async Task<InboxAccessResult> JudgeAsync(ClaimsPrincipal user, Guid? tenant = null)
            => await Access().PersonAsync(user, tenant ?? Tenant, Ct);

        [Fact]
        public async Task Inbox_ForeignTenantClaim_UppercaseGuid_NoTenantClaim_AreNotMembers()
        {
            // The same judgement as /api/v1/platform/status and the admin access: the claim is this deployment's tenant key, byte for byte.
            foreach (var claims in new (string, string)[][]
            {
                [("sub", "s-1"), ("roles", "operator")],
                [("sub", "s-1"), ("roles", "operator"), ("platform_tenant", Guid.CreateVersion7().ToString())],
                [("sub", "s-1"), ("roles", "operator"), ("platform_tenant", LocalStack.TenantKey.ToUpperInvariant())],
                [("sub", "s-1"), ("roles", "operator"), ("platform_tenant", " " + LocalStack.TenantKey)],
            })
            {
                var result = await JudgeAsync(Token(claims));
                Assert.Null(result.Person);
                Assert.Equal(InboxAccessResult.NotAMember.Refusal, result.Refusal);
            }
            // And another tenant's id in the path is refused before the claims are read.
            Assert.Equal(InboxAccessResult.NotAMember, await JudgeAsync(Token(("sub", "s-1"), ("roles", "operator"), ("platform_tenant", LocalStack.TenantKey)), Guid.NewGuid()));
        }

        [Fact]
        public async Task Inbox_ClientToken_IsNotAPerson_NoSub_NoRole_AreNotMembers()
        {
            var tenant = ("platform_tenant", LocalStack.TenantKey);
            // A service account's token: its client_id is its azp.
            Assert.Equal(InboxAccessResult.NotAPerson, await JudgeAsync(Token(tenant, ("sub", "service-account-x"), ("roles", "admin"), ("client_id", "ops-bot"), ("azp", "ops-bot"))));
            // A user's token has an azp (the client that asked for it) and no client_id, or a different one.
            Assert.NotNull((await JudgeAsync(Token(tenant, ("sub", "s-1"), ("roles", "admin"), ("azp", "juice-console")))).Person);
            Assert.NotNull((await JudgeAsync(Token(tenant, ("sub", "s-1"), ("roles", "admin"), ("client_id", "a"), ("azp", "b")))).Person);
            // No subject, or no Console role: not a member.
            Assert.Equal(InboxAccessResult.NotAMember, await JudgeAsync(Token(tenant, ("roles", "admin"))));
            Assert.Equal(InboxAccessResult.NotAMember, await JudgeAsync(Token(tenant, ("sub", ""), ("roles", "admin"))));
            Assert.Equal(InboxAccessResult.NotAMember, await JudgeAsync(Token(tenant, ("sub", "s-1"), ("roles", "offline_access"))));
            Assert.Equal(InboxAccessResult.NotAMember, await JudgeAsync(Token(tenant, ("sub", "s-1"))));
        }

        [Fact]
        public async Task Inbox_Person_CarriesTheConsoleRolesOnly_AndTheirName()
        {
            var tenant = ("platform_tenant", LocalStack.TenantKey);
            var person = (await JudgeAsync(Token(tenant, ("sub", "s-1"), ("preferred_username", "erin"), ("roles", "offline_access"), ("roles", "Operator"),
                ("roles", "operator"), ("roles", "uma_authorization"), ("roles", "viewer")))).Person!;

            Assert.Equal(("s-1", "erin"), (person.Subject, person.Name));
            // Only the Console's own three, as the platform reads them (ordinal): never offline_access, never Operator.
            Assert.Equal(["viewer", "operator"], person.Roles);
        }

        // ─── The host: mapped when on, nothing when off ───────────────────────

        [Fact]
        public async Task InboxOff_NothingMapped_404_NoRecipientsAdded_ParityUnchanged()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string>(ProducerAdapterTests.Quiet()) { ["Notifications:ConsoleUrl"] = ConsoleUrl });
            using var client = factory.CreateClient();
            var erin = new Caller(client, await LocalStack.TokenAsync("erin"));

            Assert.Equal(HttpStatusCode.NotFound, (await erin.GetAsync(Me("/counts"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await erin.GetAsync(Me())).StatusCode);
            // The hub isn't there: the Console's fallback route takes the path, and answers a POST with 405, a GET with its file (none here: 404).
            Assert.Contains((await erin.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}", new { })).StatusCode,
                new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            // The admin API is where it was.
            Assert.Equal(HttpStatusCode.OK, (await erin.GetAsync($"/notifications/api/v1/tenants/{Tenant}/routes")).StatusCode);
            // The hub's token isn't read anywhere: the query is no way in.
            using var query = await client.GetAsync($"/api/v1/platform/status?access_token={await LocalStack.TokenAsync("erin")}", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
            using var hubQuery = await client.PostAsync($"/notifications/hubs/inbox/negotiate?access_token={await LocalStack.TokenAsync("erin")}", null, Ct);
            Assert.NotEqual(HttpStatusCode.OK, hubQuery.StatusCode);

            // The firing carries no recipient, and nothing is recorded of any person.
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            Assert.All(await RecipientsAsync(database), r => Assert.Equal(["route"], InboxProducerTests.Named(r)));
            Assert.Equal(0L, await ScalarAsync(database, "SELECT count(*) FROM notifications.subscribers"));
        }

        [Fact]
        public async Task Inbox_ViewerReadsOwnInbox_200_WhileAdminPages403()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            using var client = factory.CreateClient();
            var viewer = new Caller(client, await LocalStack.TokenAsync("victor"));

            var counts = await Caller.JsonAsync(await viewer.GetAsync(Me("/counts")));
            Assert.Equal(0, counts.GetProperty("unread").GetInt32());
            Assert.Empty((await Caller.JsonAsync(await viewer.GetAsync(Me()))).EnumerateArray());
            // The admin pages are still the operators' and admins'.
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/notifications/api/v1/tenants/{Tenant}/routes")).StatusCode);
            // The viewer is recorded with the viewer role, from their own token.
            Assert.Equal(1L, await ScalarAsync(database, "SELECT count(*) FROM notifications.subscribers WHERE source = 'oidc'"));
            Assert.Equal("viewer", await ScalarAsync(database, "SELECT string_agg(role, ',') FROM notifications.subscriber_roles"));
            // Without a token: the host's fallback policy.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me("/counts"), Ct)).StatusCode);
        }

        [Fact]
        public async Task Inbox_ForeignDeploymentsToken_403_NothingRecorded()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            using var client = factory.CreateClient();
            // The stack's own deployment signs its users in through the platform CLI client: their tenant claim names that deployment, not this one.
            using var keycloak = new HttpClient();
            using var response = await keycloak.PostAsync($"{LocalStack.Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "juice-platform-cli", ["username"] = "alice",
                    ["password"] = LocalStack.Secret("KEYCLOAK_DEMO_PASSWORD") }), Ct);
            var other = new Caller(client, (await Caller.JsonAsync(response)).GetProperty("access_token").GetString()!);

            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Me("/counts"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Me())).StatusCode);
            Assert.Equal(0L, await ScalarAsync(database, "SELECT count(*) FROM notifications.subscribers"));
        }

        // ─── The hub's token ──────────────────────────────────────────────────

        private static MessageReceivedContext Received(string path, string query)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            http.Request.QueryString = new QueryString(query);
            return new MessageReceivedContext(http, new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), new JwtBearerOptions());
        }

        [Theory]
        [InlineData("/notifications/hubs/inbox", "?tenant=t&access_token=abc", "abc")]
        [InlineData("/notifications/hubs/inbox/negotiate", "?access_token=abc", "abc")]
        [InlineData("/NOTIFICATIONS/hubs/inbox", "?access_token=abc", "abc")]
        [InlineData("/api/v1/alerts", "?access_token=abc", null)]
        [InlineData("/notifications/api/v1/tenants/x/me/inbox", "?access_token=abc", null)]
        [InlineData("/notifications/hubs/inboxes", "?access_token=abc", null)]
        [InlineData("/notifications/hubs", "?access_token=abc", null)]
        [InlineData("/hubs/inbox", "?access_token=abc", null)]
        [InlineData("/notifications/hubs/inbox", "?access_token=a&access_token=b", null)]
        [InlineData("/notifications/hubs/inbox", "?access_token=", null)]
        [InlineData("/notifications/hubs/inbox", "?tenant=t", null)]
        public async Task HubToken_OnlyOnHubPath_OneValue(string path, string query, string? expected)
        {
            var context = Received(path, query);

            await NotificationsInboxExtensions.ReadHubToken(context);

            Assert.Equal(expected, context.Token);
        }

        [Fact]
        public async Task HubToken_OnlyOnHubPath_InTheHost()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("erin");

            // Ignored off the hub's path: the query is no way to sign in to the API, the admin API or the inbox's own endpoints.
            foreach (var path in new[] { "/api/v1/platform/status", "/api/v1/alerts", $"/notifications/api/v1/tenants/{Tenant}/routes", Me("/counts") })
            {
                using var response = await client.GetAsync($"{path}{(path.Contains('?') ? "&" : "?")}access_token={token}", Ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            // Honoured on the hub's: once.
            using var negotiated = await client.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}&access_token={token}", null, Ct);
            Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);
            using var twice = await client.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}&access_token={token}&access_token={token}", null, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, twice.StatusCode);
            using var forged = await client.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}&access_token={token}x", null, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        [Fact]
        public async Task HubToken_NotInLogs()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var capture = new LogCapture();
            await using var factory = new ApiFactory(database, settings: On(), services: services => services.AddSingleton<ILoggerProvider>(capture));
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("erin");

            using var negotiated = await client.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}&access_token={token}", null, Ct);
            Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);
            using var refused = await client.PostAsync($"/notifications/hubs/inbox/negotiate?negotiateVersion=1&tenant={Tenant}&access_token={token}x", null, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            using var elsewhere = await client.GetAsync($"/api/v1/alerts?access_token={token}", Ct);

            // The host logged (its start at least), and none of its lines holds the query's value: the request log writes the query string, so the
            // inbox's registration quiets it (Microsoft.AspNetCore.Hosting.Diagnostics, below Warning).
            Assert.NotEmpty(capture.Entries);
            var signature = token.Split('.')[2];
            Assert.DoesNotContain(capture.Entries, e => e.Message.Contains(token, StringComparison.Ordinal) || e.Message.Contains(signature, StringComparison.Ordinal)
                || e.Message.Contains("access_token", StringComparison.OrdinalIgnoreCase));
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        /// <summary>The recipients of each request the pipeline accepted, in order: the JSON array each was sent with.</summary>
        internal static async Task<List<JsonElement[]>> RecipientsAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT recipients::text FROM notifications.requests ORDER BY accepted_at, id", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var all = new List<JsonElement[]>();
            while (await reader.ReadAsync(Ct))
            {
                all.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray());
            }
            return all;
        }

        internal static async Task<object?> ScalarAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(Ct);
            return value is DBNull ? null : value;
        }
    }
}
