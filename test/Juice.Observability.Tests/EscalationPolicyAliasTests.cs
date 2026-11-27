using System.Net;
using Juice.Notifications.Data;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The legacy escalation-policy API through the alias (M46c, waves §6.4 C): a policy is written twice, in one platform transaction through the
    /// host scope. Its legacy row holds what selects it (severity, rules, environments, enabled), which the producer adapter reads in every mode;
    /// its notifications policy (relative waits naming the endpoints' routes by key) is what runs in new. A step names endpoints legacy has while
    /// it sends, and the alias's in new; a key must also pass notifications' key rule.
    /// </summary>
    [Trait("Milestone", "M46c")]
    public sealed class EscalationPolicyAliasTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string Secret = "JUICE_WEBHOOK_POLICY_ALIAS";

        static EscalationPolicyAliasTests() => Environment.SetEnvironmentVariable(Secret, "policy-alias-secret");

        private static Dictionary<string, string> Settings(string pipeline) => new()
        {
            ["Notifications:Pipeline"] = pipeline,
            ["Webhooks:AllowHttpToAllowlisted"] = "true",
            ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
            ["Notifications:Stages:Planner:Poll"] = "00:00:00.200",
            ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.200",
            ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.200",
            ["Notifications:Stages:Escalation:Poll"] = "00:00:00.200",
        };

        private static object Policy(string key, params string[] endpoints)
            => new { key, name = "Paging", steps = new[] { new { afterMinutes = 5, endpointKeys = endpoints } } };

        /// <summary>
        /// In new, a policy made through /escalation-policies is the producer adapter's choice for a matching alert, and its step runs from the
        /// notifications policy: fired 6 minutes ago, step 1 pages the endpoint at once.
        /// </summary>
        [Fact]
        public async Task In_new_a_policy_made_through_the_api_pages_its_endpoint_from_the_notifications_policy()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings("new"));
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var api = $"/api/v1/tenants/{tenant}";
            await Caller.JsonAsync(await admin.SendAsync(HttpMethod.Put, $"{api}/webhooks/allowlist", new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } }));
            // The pager hears only resolves on its own: the firing reaches it only through the policy's step.
            await Caller.JsonAsync(await admin.PostAsync($"{api}/webhooks", new { key = "pager", url = receiver.Url, secretRef = $"env:{Secret}", events = new[] { "alert.resolved" } }));
            await Caller.JsonAsync(await admin.PostAsync($"{api}/escalation-policies", Policy("paging", "pager")));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var policy = await notifications.EscalationPolicies.AsNoTracking().SingleAsync(p => p.Key == "paging", Ct);
                Assert.Equal(Juice.Notifications.ManagedBy.WebhooksAlias, policy.ManagedBy);
            }
            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);
            await using (var db = database.Context())
            {
                var fired = DateTimeOffset.UtcNow.AddMinutes(-6);
                await db.Alerts.Where(a => a.Id == alert).ExecuteUpdateAsync(s => s.SetProperty(a => a.FiredAt, fired), Ct);
                await db.AlertTransitions.Where(t => t.Id == transition).ExecuteUpdateAsync(s => s.SetProperty(t => t.At, fired), Ct);
            }
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            for (var i = 0; i < 150 && receiver.Requests.IsEmpty; i++)
            {
                await Task.Delay(200, Ct);
            }

            // The pager hears the firing only as the policy's step 1. (Its body is the pipeline's default template until the observability types
            // carry the ported ones.)
            var paged = Assert.Single(receiver.Requests);
            Assert.Contains(alert.ToString(), paged.Body);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                Assert.True(await notifications.Recipients.AsNoTracking().AnyAsync(r => r.RouteKey == "pager" && r.EscalationStep == 1, Ct));
            }
            await using var check = database.Context();
            Assert.Empty(await check.WebhookDeliveries.AsNoTracking().ToListAsync(Ct));   // legacy sent nothing
        }

        /// <summary>A step naming an endpoint the alias doesn't hold is refused in new; a key notifications can't take (a leading digit) everywhere.</summary>
        [Theory]
        [InlineData("new")]
        [InlineData("legacy")]
        public async Task A_step_naming_an_unknown_endpoint_or_a_digit_led_key_is_refused(string pipeline)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings(pipeline));
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var api = $"/api/v1/tenants/{tenant}";
            await Caller.JsonAsync(await admin.PostAsync($"{api}/webhooks", new { key = "pager", url = "https://hooks.example.com/p", secretRef = $"env:{Secret}" }));

            using var unknown = await admin.PostAsync($"{api}/escalation-policies", Policy("paging", "nobody"));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains("No endpoint 'nobody'", await unknown.Content.ReadAsStringAsync(Ct));
            using var digit = await admin.PostAsync($"{api}/escalation-policies", Policy("1paging", "pager"));
            Assert.Equal(HttpStatusCode.BadRequest, digit.StatusCode);
            Assert.Contains("starting with a letter", await digit.Content.ReadAsStringAsync(Ct));

            await Caller.JsonAsync(await admin.PostAsync($"{api}/escalation-policies", Policy("paging", "pager")));
            await using var check = database.Context();
            Assert.True(await check.EscalationPolicies.AsNoTracking().AnyAsync(p => p.Key == "paging", Ct));   // the selection inputs, in every mode
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().EscalationPolicies.AsNoTracking().AnyAsync(p => p.Key == "paging", Ct));
        }

        /// <summary>The pipeline's host scope with its database failing mid-write: a real error that aborts the transaction.</summary>
        private sealed class FailingHostScope(NotificationsDbContext notifications) : Juice.Notifications.Embedded.INotificationsHostScope
        {
            public async Task<T> RunAsync<T>(Func<Juice.Notifications.Embedded.NotificationsHostWork, Task<T>> work, CancellationToken cancellationToken)
            {
                await notifications.Database.ExecuteSqlRawAsync("SELECT 1/0", cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        /// <summary>In new, a notifications failure while a policy is written leaves neither its legacy row nor a notifications policy.</summary>
        [Fact]
        public async Task In_new_a_notifications_failure_leaves_neither_row()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: Settings("new"));
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var api = $"/api/v1/tenants/{tenant}";
            await Caller.JsonAsync(await admin.PostAsync($"{api}/webhooks", new { key = "pager", url = "https://hooks.example.com/p", secretRef = $"env:{Secret}" }));
            await using var failing = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<Juice.Notifications.Embedded.INotificationsHostScope, FailingHostScope>()));
            using var failingClient = failing.CreateClient();
            var (_, _, failingAdmin, _) = await Caller.AllAsync(failingClient, database);

            using var refused = await failingAdmin.PostAsync($"{api}/escalation-policies", Policy("paging", "pager"));

            Assert.False(refused.IsSuccessStatusCode);
            await using var check = database.Context();
            Assert.False(await check.EscalationPolicies.AsNoTracking().AnyAsync(p => p.Key == "paging", Ct));
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().EscalationPolicies.AsNoTracking().AnyAsync(p => p.Key == "paging", Ct));
        }
    }
}
