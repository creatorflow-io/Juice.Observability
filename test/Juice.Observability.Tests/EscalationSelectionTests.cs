using System.Net;
using System.Text.Json;
using NotificationsDbContext = Juice.Notifications.Data.NotificationsDbContext;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Escalation after 2.0 (M50): the platform keeps <i>which</i> policy an alert escalates by (severity, rules, environments), and the notification
    /// pipeline keeps <i>what</i> it does, under the same key.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class EscalationSelectionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static EscalationPolicy Policy(string key, AlertSeverity minimum = AlertSeverity.Info, string[]? rules = null, string[]? environments = null, bool enabled = true)
            => new() { TenantId = Guid.NewGuid(), Key = key, Name = key, Enabled = enabled, MinimumSeverity = minimum, RuleKeys = rules ?? [], Environments = environments ?? [] };

        private static readonly AlertContext Service = new("orders-errors", "service:shop/orders@production", AlertSeverity.Critical);
        private static readonly AlertContext Host = new("host-down", "host:web-1", AlertSeverity.Critical);

        // ─── The selection ─────────────────────────────────────────────────────

        [Fact]
        public void Selection_BySeverity_Rule_Environment_DisabledNever_ByKeyOrdinal()
        {
            EscalationPolicy[] policies =
            [
                Policy("b-any"), Policy("a-warning", AlertSeverity.Warning), Policy("c-critical-only", AlertSeverity.Critical), Policy("d-other-rule", rules: ["other"]),
                Policy("e-this-rule", rules: ["orders-errors"]), Policy("f-staging", environments: ["staging"]), Policy("g-production", environments: ["production"]),
                Policy("h-disabled", enabled: false), Policy("B-upper"),
            ];

            var matched = EscalationSelection.Match(policies, Service, AlertSeverity.Warning);

            // A warning is below c-critical-only; ordinal: capitals first.
            Assert.Equal(["B-upper", "a-warning", "b-any", "e-this-rule", "g-production"], matched.Select(p => p.Key));
            Assert.Equal(["B-upper", "a-warning", "b-any", "c-critical-only", "e-this-rule", "g-production"],
                EscalationSelection.Match(policies, Service, AlertSeverity.Critical).Select(p => p.Key));
        }

        [Fact]
        public void Selection_AHostAlertHasNoEnvironment_SoOnlyPoliciesWithoutOneMatchIt()
        {
            EscalationPolicy[] policies = [Policy("any"), Policy("production", environments: ["production"]), Policy("host-rule", rules: ["host-down"])];

            Assert.Equal(["any", "host-rule"], EscalationSelection.Match(policies, Host, AlertSeverity.Critical).Select(p => p.Key));
            Assert.Empty(EscalationSelection.Match([], Host, AlertSeverity.Critical));
        }

        // ─── In the pipeline's request ─────────────────────────────────────────

        private static async Task<Guid> FireAsync(ScratchDatabase database, string subject, string ruleKey, AlertSeverity severity = AlertSeverity.Critical)
        {
            await using var db = database.Context();
            var tenant = await ProducerAdapterTests.TenantAsync(database);
            var now = DateTimeOffset.UtcNow;
            var rule = new Rule { TenantId = tenant, Key = ruleKey, Name = ruleKey, Template = RuleTemplate.ErrorRate, Severity = severity, Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = Data.ManagedBy.Api };
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = $"{ruleKey}|{subject}", SubjectKey = subject,
                SubjectType = subject.StartsWith("host:", StringComparison.Ordinal) ? HealthSubject.Host : HealthSubject.Service, Severity = severity, State = AlertState.Firing,
                BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, LastValue = 0.4,
            };
            var transition = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now, Value = 0.4, Reason = "for_duration_met" };
            db.AddRange(rule, alert, transition);
            await db.SaveChangesAsync(Ct);
            return transition.Id;
        }

        private static async Task SelectAsync(ScratchDatabase database, string key, AlertSeverity minimum = AlertSeverity.Info, string[]? rules = null, string[]? environments = null)
        {
            await using var db = database.Context();
            db.EscalationPolicies.Add(new EscalationPolicy
            {
                TenantId = await ProducerAdapterTests.TenantAsync(database), Key = key, Name = key, MinimumSeverity = minimum, RuleKeys = rules ?? [], Environments = environments ?? [],
            });
            await db.SaveChangesAsync(Ct);
        }

        private static string? PolicyLabel(ProducerAdapterTests.Request request) => request.Labels.GetValueOrDefault(ObservabilityTypes.EscalationPolicyLabel);

        [Fact]
        public async Task Escalation_SelectionBySeverityRuleEnv_NamesPolicyLabel()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            _ = factory.Server;
            foreach (var key in new[] { "pager", "staging-only", "other-rule", "critical" }) await ProducerAdapterTests.PipelinePolicyAsync(factory, key);
            await SelectAsync(database, "pager", AlertSeverity.Warning, rules: ["orders-errors"], environments: ["production"]);
            await SelectAsync(database, "staging-only", environments: ["staging"]);
            await SelectAsync(database, "other-rule", rules: ["other"]);

            await ProducerAdapterTests.PlanAsync(factory, database, await FireAsync(database, "service:shop/orders@production", "orders-errors"));

            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            Assert.Equal("pager", PolicyLabel(request));
            Assert.Equal("production", request.Labels["environment"]);
        }

        [Fact]
        public async Task Escalation_SelectionBySeverityRuleEnv_NamesPolicyLabel_HostAlertWithNoEnvironment()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            _ = factory.Server;
            foreach (var key in new[] { "hosts", "production-only" }) await ProducerAdapterTests.PipelinePolicyAsync(factory, key);
            await SelectAsync(database, "production-only", environments: ["production"]);
            await SelectAsync(database, "hosts", rules: ["host-down"]);

            await ProducerAdapterTests.PlanAsync(factory, database, await FireAsync(database, "host:web-1", "host-down"));

            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            // No environment on the alert: a policy that names one doesn't select it, and no label says one.
            Assert.Equal("hosts", PolicyLabel(request));
            Assert.False(request.Labels.ContainsKey("environment"));
        }

        [Fact]
        public async Task Escalation_TwoMatch_FirstByKeyOrdinal_Warns()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var logs = new LogCapture();
            await using var factory = ProducerAdapterTests.Factory(database, s => s.AddSingleton<ILoggerProvider>(logs));
            _ = factory.Server;
            foreach (var key in new[] { "pager-b", "pager-a" }) await ProducerAdapterTests.PipelinePolicyAsync(factory, key);
            await SelectAsync(database, "pager-b");
            await SelectAsync(database, "pager-a");

            await ProducerAdapterTests.PlanAsync(factory, database, await FireAsync(database, "service:shop/orders@production", "orders-errors"));

            Assert.Equal("pager-a", PolicyLabel(Assert.Single(await ProducerAdapterTests.RequestsAsync(database))));
            var warning = Assert.Single(logs.At(LogLevel.Warning), e => e.Message.Contains("escalation policies", StringComparison.Ordinal));
            Assert.Contains("pager-a", warning.Message);
            Assert.Contains("pager-b", warning.Message);
        }

        [Fact]
        public async Task Escalation_PipelinePolicyDeletedLater_NotificationStillDelivered_NoEscalation()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await TestReceiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var logs = new LogCapture();
            await using var file = ProvisionedHost.Write(receiver);
            var settings = ProvisionedHost.Settings(file.Path);
            await using var factory = new ApiTests.ApiFactory(database, settings: settings, services: s => s.AddSingleton<ILoggerProvider>(logs));
            _ = factory.Server;
            await ProducerAdapterTests.PipelinePolicyAsync(factory, "pager");
            await SelectAsync(database, "pager");
            // The pipeline's policy goes (its admin page, an API call): the platform's selection of it stays.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                Assert.Equal(1, await notifications.EscalationPolicies.Where(p => p.Key == "pager").ExecuteDeleteAsync(Ct));
            }

            await ProducerAdapterTests.PlanAsync(factory, database, await FireAsync(database, "service:shop/orders@production", "orders-errors"));

            // Accepted without the label, and delivered.
            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            Assert.Null(PolicyLabel(request));
            await ProvisionedHost.WaitAsync(() => !receiver.Requests.IsEmpty, "the notification to reach the receiver");
            Assert.Single(receiver.Requests);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Escalations.CountAsync(Ct));
            }
            var warning = Assert.Single(logs.At(LogLevel.Warning), e => e.Category.EndsWith("ProducerAdapter", StringComparison.Ordinal) && e.Message.Contains("doesn't have", StringComparison.Ordinal));
            Assert.Contains("pager", warning.Message);
        }

        // ─── The API ───────────────────────────────────────────────────────────

        private static async Task<(string Code, string Detail)> ProblemAsync(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var root = document.RootElement;
            return (root.TryGetProperty("code", out var code) ? code.GetString()! : "", root.TryGetProperty("detail", out var detail) ? detail.GetString()! : "");
        }

        [Fact]
        public async Task EscalationApi_StepsInBody_422()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            await ProducerAdapterTests.PipelinePolicyAsync(factory, "pager");
            var policies = $"/api/v1/tenants/{tenant}/escalation-policies";

            foreach (var body in new object[]
            {
                new { key = "pager", name = "Pager", steps = new[] { new { afterMinutes = 5, endpointKeys = new[] { "ops" } } } },
                new { key = "pager", name = "Pager", stopOnAcknowledge = false },
                new { key = "pager", name = "Pager", StopOnAcknowledge = true },
            })
            {
                var created = await admin.PostAsync(policies, body);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
                var (code, detail) = await ProblemAsync(created);
                Assert.Equal("steps_moved", code);
                Assert.Contains("Escalation Policies page", detail);
            }
            // The same for a change.
            await Caller.JsonAsync(await admin.PostAsync(policies, new { key = "pager", name = "Pager" }));
            var updated = await admin.PutAsync($"{policies}/pager", new { name = "Pager", steps = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, updated.StatusCode);
            Assert.Equal("steps_moved", (await ProblemAsync(updated)).Code);
            await using var db = database.Context();
            Assert.Equal("Pager", (await db.EscalationPolicies.AsNoTracking().SingleAsync(Ct)).Name);
        }

        [Fact]
        public async Task EscalationApi_NoPipelinePolicy_422()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var policies = $"/api/v1/tenants/{tenant}/escalation-policies";

            var created = await admin.PostAsync(policies, new { key = "pager", name = "Pager" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
            var (code, detail) = await ProblemAsync(created);
            Assert.Equal("no_pipeline_policy", code);
            Assert.Contains("Escalation Policies page", detail);
            await using (var db = database.Context())
            {
                Assert.Empty(await db.EscalationPolicies.AsNoTracking().ToListAsync(Ct));
            }

            // With the pipeline's policy, the selection is made, read back as having one, and changed.
            await ProducerAdapterTests.PipelinePolicyAsync(factory, "pager");
            var made = await Caller.JsonAsync(await admin.PostAsync(policies, new { key = "pager", name = "Pager", minimumSeverity = "warning", ruleKeys = new[] { "orders-errors" }, environments = new[] { "production" } }));
            Assert.Equal(("pager", "Warning", true), (made.GetProperty("key").GetString(), made.GetProperty("minimumSeverity").GetString(), made.GetProperty("pipelinePolicyExists").GetBoolean()));
            Assert.False(made.TryGetProperty("steps", out _));
            Assert.False(made.TryGetProperty("stopOnAcknowledge", out _));
            var changed = await Caller.JsonAsync(await admin.PutAsync($"{policies}/pager", new { name = "Pager (on call)", enabled = false }));
            Assert.Equal(("Pager (on call)", false), (changed.GetProperty("name").GetString(), changed.GetProperty("enabled").GetBoolean()));

            // Deleted from the pipeline since: listed, with the badge's flag false, and a change is refused.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().EscalationPolicies.Where(p => p.Key == "pager").ExecuteDeleteAsync(Ct);
            }
            var listed = (await Caller.JsonAsync(await admin.GetAsync(policies))).GetProperty("items").EnumerateArray().Single();
            Assert.False(listed.GetProperty("pipelinePolicyExists").GetBoolean());
            var refused = await admin.PutAsync($"{policies}/pager", new { name = "Pager" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("no_pipeline_policy", (await ProblemAsync(refused)).Code);

            // But it can still be switched off, and deleted, with its policy gone.
            var off = await Caller.JsonAsync(await admin.PutAsync($"{policies}/pager", new { name = "Pager", enabled = false }));
            Assert.Equal((false, false), (off.GetProperty("enabled").GetBoolean(), off.GetProperty("pipelinePolicyExists").GetBoolean()));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"{policies}/pager")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Delete, $"{policies}/pager")).StatusCode);
            await using var after = database.Context();
            Assert.Empty(await after.EscalationPolicies.AsNoTracking().ToListAsync(Ct));
        }

        [Fact]
        public async Task EscalationApi_Delete_AdminOnly()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            await ProducerAdapterTests.PipelinePolicyAsync(factory, "pager");
            var policies = $"/api/v1/tenants/{tenant}/escalation-policies";
            await Caller.JsonAsync(await admin.PostAsync(policies, new { key = "pager", name = "Pager" }));

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.SendAsync(HttpMethod.Delete, $"{policies}/pager")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(HttpMethod.Delete, $"{policies}/pager")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"{policies}/pager")).StatusCode);
        }

        [Fact]
        public async Task EscalationApi_OperatorForbidden()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            await ProducerAdapterTests.PipelinePolicyAsync(factory, "pager");
            var policies = $"/api/v1/tenants/{tenant}/escalation-policies";

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(policies, new { key = "pager", name = "Pager" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PutAsync($"{policies}/pager", new { name = "Pager" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(policies, new { key = "pager", name = "Pager" })).StatusCode);
            await using var db = database.Context();
            Assert.Empty(await db.EscalationPolicies.AsNoTracking().ToListAsync(Ct));
        }

        [Fact]
        public async Task EscalationApi_OtherTenantClaim_Forbidden()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = ProducerAdapterTests.Factory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, _) = await Caller.AllAsync(client, database);
            var other = $"/api/v1/tenants/{Guid.NewGuid()}/escalation-policies";

            // The caller's tenant claim isn't the path's: refused before the roles are asked, whatever they are, and the embedded tenant's policies are never read.
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(other)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync(other, new { key = "pager", name = "Pager" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsync($"{other}/pager", new { name = "Pager" })).StatusCode);
            // A caller with no platform role at all.
            var nora = new Caller(client, await LocalStack.TokenAsync("nora"));
            Assert.Equal(HttpStatusCode.Forbidden, (await nora.PostAsync($"/api/v1/tenants/{PlatformOptionsTenant(database)}/escalation-policies", new { key = "pager", name = "Pager" })).StatusCode);
        }

        private static Guid PlatformOptionsTenant(ScratchDatabase database)
        {
            using var db = database.Context();
            return db.Tenants.AsNoTracking().Single(t => t.Key == LocalStack.TenantKey).Id;
        }
    }
}
