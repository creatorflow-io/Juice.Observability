using System.Net;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>The Phase 6 gate for incidents: every transition is on the incident's record and in the audit, and incidents never touch alerts.</summary>
    public class IncidentApiTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<(Guid Firing, Guid Warning, Guid OtherTenants)> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var other = new PlatformTenant { Key = "other-tenant", DisplayName = "Other" };
            var now = DateTimeOffset.UtcNow;
            Rule NewRule(Guid owner, string key) => new() { TenantId = owner, Key = key, Name = key, Template = RuleTemplate.ErrorRate, Definition = "{}", DefinitionHash = "h", LoadedAt = now, ManagedBy = ManagedBy.Api };
            Alert NewAlert(Rule rule, string subject, AlertSeverity severity) => new()
            {
                TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = $"{rule.Key}|{subject}", SubjectKey = subject, SubjectType = HealthSubject.Service, Severity = severity,
                State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now,
            };
            var (mine, theirs) = (NewRule(tenant, "errors"), NewRule(other.Id, "errors"));
            var (firing, warning, foreign) = (NewAlert(mine, "service:a@production", AlertSeverity.Critical), NewAlert(mine, "service:b@production", AlertSeverity.Warning), NewAlert(theirs, "service:c@production", AlertSeverity.Critical));
            db.AddRange(other, mine, theirs, firing, warning, foreign);
            await db.SaveChangesAsync(Ct);
            return (firing.Id, warning.Id, foreign.Id);
        }

        [Fact]
        public async Task Every_transition_is_recorded_and_audited_and_changes_no_alert_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (firing, warning, _) = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var incidents = $"/api/v1/tenants/{tenant}/incidents";
            var open = new { title = "Checkout is failing", alertIds = new[] { firing, warning }, note = "paged by billing" };

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(incidents, open)).StatusCode);
            var first = await @operator.PostAsync(incidents, open, key: "open-1");
            var created = await Caller.JsonAsync(first);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var id = created.GetProperty("id").GetGuid();
            Assert.Equal(("Open", "Critical", "erin"), (created.GetProperty("state").GetString(), created.GetProperty("severity").GetString(), created.GetProperty("createdBy").GetString()));   // its worst alert
            Assert.Equal(id, (await Caller.JsonAsync(await @operator.PostAsync(incidents, open, key: "open-1"))).GetProperty("id").GetGuid());

            var path = $"{incidents}/{id}";
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{path}/transitions", new { to = "investigating" })).StatusCode);
            await Caller.JsonAsync(await @operator.PostAsync($"{path}/transitions", new { to = "investigating" }));
            await Caller.JsonAsync(await @operator.PostAsync($"{path}/transitions", new { to = "mitigated", comment = "rolled back" }));
            await Caller.JsonAsync(await @operator.PostAsync($"{path}/notes", new { text = "ran psql --password hunter2 to check" }));

            var noComment = await @operator.PostAsync($"{path}/transitions", new { to = "resolved" });
            Assert.Equal(HttpStatusCode.BadRequest, noComment.StatusCode);
            Assert.Contains("comment is required", await noComment.Content.ReadAsStringAsync(Ct));
            var resolved = await Caller.JsonAsync(await @operator.PostAsync($"{path}/transitions", new { to = "resolved", comment = "fixed" }));
            Assert.Equal(System.Text.Json.JsonValueKind.String, JsonValueKindOf(resolved, "resolvedAt"));
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{path}/transitions", new { to = "mitigated", comment = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"{path}/transitions", new { to = "investigating" })).StatusCode);
            var reopened = await Caller.JsonAsync(await @operator.PostAsync($"{path}/transitions", new { to = "investigating", comment = "it came back" }));
            Assert.Equal(System.Text.Json.JsonValueKind.Null, JsonValueKindOf(reopened, "resolvedAt"));

            var detail = await Caller.JsonAsync(await viewer.GetAsync(path));
            Assert.Equal(2, detail.GetProperty("openAlerts").GetInt32());
            Assert.Equal(2, detail.GetProperty("alerts").GetArrayLength());
            var events = detail.GetProperty("events").EnumerateArray().ToList();
            Assert.Equal(
                [("created", "Open"), ("alert_added", null), ("alert_added", null), ("state", "Investigating"), ("state", "Mitigated"), ("note", null), ("state", "Resolved"), ("state", "Investigating")],
                events.Select(e => (e.GetProperty("type").GetString()!, e.GetProperty("to").GetString())));
            Assert.All(events, e => Assert.Equal("erin", e.GetProperty("actor").GetString()));
            Assert.DoesNotContain("hunter2", detail.ToString());

            await using var db = database.Context();
            var transitions = await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "incident.transition").OrderBy(a => a.OccurredAt).ToListAsync(Ct);
            Assert.Equal(
                [(AuditOutcome.Denied, null), (AuditOutcome.Succeeded, "Investigating"), (AuditOutcome.Succeeded, "Mitigated"), (AuditOutcome.Succeeded, "Resolved"), (AuditOutcome.Failed, "Mitigated"), (AuditOutcome.Succeeded, "Investigating")],
                transitions.Select(a => (a.Outcome, a.Metadata.GetValueOrDefault("to"))));
            Assert.All(transitions, a => Assert.False(string.IsNullOrEmpty(a.CorrelationId)));
            Assert.DoesNotContain(await db.AuditRecords.AsNoTracking().Where(a => a.TargetType == "incident").ToListAsync(Ct), a => a.Metadata.Values.Any(v => v.Contains("hunter2")));
            Assert.Equal(2, await db.Alerts.CountAsync(a => a.State == AlertState.Firing && (a.Id == firing || a.Id == warning), Ct));
        }

        private static System.Text.Json.JsonValueKind JsonValueKindOf(System.Text.Json.JsonElement element, string property) => element.GetProperty(property).ValueKind;

        [Fact]
        public async Task An_alert_is_in_one_unresolved_incident_and_only_this_tenants_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (firing, warning, foreign) = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var incidents = $"/api/v1/tenants/{tenant}/incidents";

            Assert.Equal(HttpStatusCode.NotFound, (await @operator.PostAsync(incidents, new { title = "x", alertIds = new[] { foreign } })).StatusCode);
            var a = (await Caller.JsonAsync(await @operator.PostAsync(incidents, new { title = "A", alertIds = new[] { firing } }))).GetProperty("id").GetGuid();
            var b = (await Caller.JsonAsync(await @operator.PostAsync(incidents, new { title = "B", severity = "warning" }))).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync(incidents, new { title = "C", alertIds = new[] { firing } })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{incidents}/{b}/alerts", new { alertId = firing })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await @operator.PostAsync($"{incidents}/{b}/alerts", new { alertId = foreign })).StatusCode);

            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{b}/alerts", new { alertId = warning }));
            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{b}/alerts", new { alertId = warning }));   // twice: once
            Assert.Equal(1, (await Caller.JsonAsync(await viewer.GetAsync($"{incidents}/{b}"))).GetProperty("alerts").GetArrayLength());
            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{incidents}/{b}/alerts/{warning}")).StatusCode);

            // Resolved, an incident gives its alerts back.
            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{a}/transitions", new { to = "resolved", comment = "done" }));
            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{b}/alerts", new { alertId = firing }));

            var open = await Caller.JsonAsync(await viewer.GetAsync($"{incidents}?state=open"));
            Assert.Equal(b, Assert.Single(open.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync($"{incidents}?state=nope")).StatusCode);

            var notifications = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/alerts/{firing}/notifications"));
            Assert.Equal(2, notifications.GetProperty("incidents").GetArrayLength());
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/tenants/{tenant}/alerts/{foreign}/notifications")).StatusCode);
        }

        [Fact]
        public async Task Another_tenants_incident_stays_out_of_reach_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            Guid foreign;
            await using (var db = database.Context())
            {
                var other = await db.Tenants.SingleAsync(t => t.Key == "other-tenant", Ct);
                var incident = new Incident { TenantId = other.Id, Title = "Theirs", State = IncidentState.Open, CreatedBy = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                db.Incidents.Add(incident);
                await db.SaveChangesAsync(Ct);
                foreign = incident.Id;
            }
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var incidents = $"/api/v1/tenants/{tenant}/incidents";
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"{incidents}/{foreign}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await @operator.PostAsync($"{incidents}/{foreign}/transitions", new { to = "resolved", comment = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await @operator.PostAsync($"{incidents}/{foreign}/notes", new { text = "x" })).StatusCode);
            Assert.Empty((await Caller.JsonAsync(await viewer.GetAsync(incidents))).GetProperty("items").EnumerateArray());
        }
    }
}
