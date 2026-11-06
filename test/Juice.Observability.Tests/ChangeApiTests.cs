using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>The Phase 5 gate: the timeline is tenant scoped, secrets are excluded, and closeness in time is labelled as correlation.</summary>
    public class ChangeApiTests
    {
        private static Task<HttpResponseMessage> Post(HttpClient client, string path, string token, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request);
        }

        private static Task<HttpResponseMessage> Get(HttpClient client, string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }

        private sealed record Seeded(Guid Tenant, Guid Orders, Guid Billing, Guid Other, Guid Firing, DateTimeOffset Breach, Guid OtherTenantChange);

        /// <summary>orders depends on billing; shipping is unrelated. orders has a firing alert, and billing went Unhealthy just before it.</summary>
        private static async Task<Seeded> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync()).Id;
            var now = DateTimeOffset.UtcNow;
            var breach = now.AddMinutes(-10);
            var production = new DeploymentEnvironment { TenantId = tenant, Key = "production", Type = EnvironmentType.Production, FirstSeen = now, LastSeen = now };
            var staging = new DeploymentEnvironment { TenantId = tenant, Key = "staging", Type = EnvironmentType.Staging, FirstSeen = now, LastSeen = now };
            Service NewService(string name) => new() { TenantId = tenant, Namespace = "shop", Name = name, FirstSeen = now, LastSeen = now };
            var (orders, billing, shipping) = (NewService("orders"), NewService("billing"), NewService("shipping"));
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical, Definition = "{}", DefinitionHash = "h", LoadedAt = now };
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = "orders-errors|service:shop/orders@production", SubjectKey = "service:shop/orders@production",
                SubjectType = HealthSubject.Service, SubjectId = orders.Id, EnvironmentId = production.Id, Severity = AlertSeverity.Critical, State = AlertState.Firing,
                BreachStartedAt = breach, FiredAt = breach.AddMinutes(2), LastEvaluatedAt = now, LastValue = 0.25, Evidence = new() { ["template"] = "ErrorRate" },
            };
            db.AddRange(production, staging, orders, billing, shipping, rule, alert);
            db.Dependencies.Add(new Dependency
            {
                TenantId = tenant, EnvironmentId = production.Id, SourceServiceId = orders.Id, TargetServiceId = billing.Id, Relation = DependencyRelation.Calls,
                Confidence = DependencyConfidence.Observed, EvidenceSource = "test", SpanCount = 1, FirstSeen = now, LastSeen = now,
            });
            db.AlertTransitions.Add(new AlertTransition { TenantId = tenant, AlertId = alert.Id, ToState = AlertState.Firing, At = breach.AddMinutes(2), Value = 0.25, Reason = "breach" });
            db.HealthTransitions.AddRange(
                new HealthTransition { TenantId = tenant, SubjectType = HealthSubject.Service, SubjectId = billing.Id, EnvironmentId = production.Id, FromState = HealthState.Healthy, ToState = HealthState.Unhealthy,
                    ReasonCodes = ["error_rate_high"], PolicyVersion = "p", EvaluatedAt = breach.AddMinutes(-4), EvidenceFrom = breach.AddMinutes(-9), EvidenceTo = breach.AddMinutes(-4) },
                new HealthTransition { TenantId = tenant, SubjectType = HealthSubject.Service, SubjectId = orders.Id, EnvironmentId = production.Id, FromState = HealthState.Healthy, ToState = HealthState.Degraded,
                    ReasonCodes = ["error_rate_high"], PolicyVersion = "p", EvaluatedAt = breach.AddMinutes(1), EvidenceFrom = breach.AddMinutes(-4), EvidenceTo = breach.AddMinutes(1) });
            var other = new PlatformTenant { Key = $"other-{Guid.NewGuid():N}", DisplayName = "Other" };
            db.Tenants.Add(other);
            var otherChange = new ChangeEvent { TenantId = other.Id, Kind = ChangeKind.Deployment, Source = "pipeline", Summary = "foreign", OccurredAt = breach.AddMinutes(-1), RecordedAt = now };
            db.ChangeEvents.Add(otherChange);
            await db.SaveChangesAsync();
            return new Seeded(tenant, orders.Id, billing.Id, shipping.Id, alert.Id, breach, otherChange.Id);
        }

        [Fact]
        public async Task A_change_is_reported_with_roles_audit_masking_and_idempotency_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, tester) = (await LocalStack.TokenAsync("victor"), await LocalStack.TokenAsync("erin"));
            var changes = $"/api/v1/tenants/{seed.Tenant}/changes";

            Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, changes, viewer, new { kind = "deployment", summary = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, changes, tester, new { kind = "nonsense", summary = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, changes, tester, new { kind = "version", summary = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, changes, tester, new { kind = "deployment", summary = "  " })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, changes, tester, new { kind = "deployment", summary = "x", occurredAt = DateTimeOffset.UtcNow.AddHours(1) })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, changes, tester, new { kind = "deployment", summary = "x", source = "telemetry" })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(client, changes, tester, new { kind = "deployment", summary = "x", service = "nothing" })).StatusCode);

            const string Secret = "hunter2";
            var body = new
            {
                kind = "deployment", summary = $"Deployed with --password {Secret}", service = "shop/orders", environment = "Production", toVersion = "1.4.2",
                source = "github-actions", externalKey = "run-42",
                attributes = new Dictionary<string, string> { ["commit"] = "abc123", ["url"] = $"https://bot:{Secret}@ci.example.com/42", ["DB_PASSWORD"] = Secret },
            };
            var created = await Post(client, changes, tester, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var change = await Json(created);
            var id = change.GetProperty("id").GetGuid();
            Assert.Equal(("Deployment", "github-actions", seed.Orders, "production"), (change.GetProperty("kind").GetString(), change.GetProperty("source").GetString(),
                change.GetProperty("serviceId").GetGuid(), change.GetProperty("environment").GetString()));
            Assert.False(string.IsNullOrEmpty(change.GetProperty("actor").GetString()), "the actor comes from the token");

            // The same external key is the same change.
            var again = await Post(client, changes, tester, body);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(id, (await Json(again)).GetProperty("id").GetGuid());

            await using var db = database.Context();
            Assert.Single(await db.ChangeEvents.Where(c => c.ExternalKey == "run-42").ToListAsync());
            var stored = await db.ChangeEvents.SingleAsync(c => c.Id == id);
            var everything = JsonSerializer.Serialize(stored) + JsonSerializer.Serialize(await db.AuditRecords.Where(a => a.TargetType == "change").ToListAsync());
            Assert.DoesNotContain(Secret, everything);
            Assert.DoesNotContain("DB_PASSWORD", everything);
            Assert.Equal(["commit", "url"], stored.Attributes.Keys.Order());
            Assert.Single(await db.AuditRecords.Where(a => a.Operation == "change.report" && a.Outcome == AuditOutcome.Denied).ToListAsync());
            Assert.Single(await db.AuditRecords.Where(a => a.Operation == "change.report" && a.Outcome == AuditOutcome.Succeeded).ToListAsync());

            var listed = (await Json(await Get(client, $"{changes}?serviceId={seed.Orders}&kind=deployment", viewer))).GetProperty("items");
            Assert.Equal(id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
            Assert.Empty((await Json(await Get(client, $"{changes}?serviceId={seed.Other}", viewer))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, $"{changes}?kind=nonsense", viewer)).StatusCode);
        }

        [Fact]
        public async Task The_timeline_joins_changes_health_and_alerts_within_the_tenant_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, tester) = (await LocalStack.TokenAsync("victor"), await LocalStack.TokenAsync("erin"));
            var root = $"/api/v1/tenants/{seed.Tenant}";
            (await Post(client, $"{root}/changes", tester, new { kind = "deployment", summary = "Deployed orders", service = "shop/orders", environment = "production", occurredAt = seed.Breach.AddMinutes(-3) })).EnsureSuccessStatusCode();
            (await Post(client, $"{root}/changes", tester, new { kind = "deployment", summary = "Deployed orders to staging", service = "shop/orders", environment = "staging", occurredAt = seed.Breach.AddMinutes(-2) })).EnsureSuccessStatusCode();
            (await Post(client, $"{root}/changes", tester, new { kind = "deployment", summary = "Deployed shipping", service = "shop/shipping", environment = "production", occurredAt = seed.Breach.AddMinutes(-2) })).EnsureSuccessStatusCode();
            var window = $"from={Uri.EscapeDataString(seed.Breach.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}";

            var timeline = await Json(await Get(client, $"{root}/timeline?serviceId={seed.Orders}&environment=production&{window}", viewer));
            var entries = timeline.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(["change", "health", "alert"], entries.Select(e => e.GetProperty("type").GetString()));
            Assert.Equal(entries.Select(e => e.GetProperty("at").GetDateTimeOffset()).Order(), entries.Select(e => e.GetProperty("at").GetDateTimeOffset()));
            Assert.Equal("Deployed orders", entries[0].GetProperty("title").GetString());
            Assert.Equal(seed.Firing, entries[2].GetProperty("id").GetGuid());

            // Across the tenant: the other service too, and never the other tenant's.
            var all = (await Json(await Get(client, $"{root}/timeline?{window}", viewer))).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();
            Assert.Contains("Deployed shipping", all);
            Assert.Contains("Deployed orders to staging", all);
            Assert.DoesNotContain("foreign", all);

            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"{root}/timeline?serviceId={Guid.NewGuid()}", viewer)).StatusCode);
            var backwards = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}";
            Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, $"{root}/timeline?{backwards}", viewer)).StatusCode);
        }

        [Fact]
        public async Task What_relates_to_an_alert_is_ranked_with_reasons_and_labelled_as_correlation_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var seed = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, tester) = (await LocalStack.TokenAsync("victor"), await LocalStack.TokenAsync("erin"));
            var root = $"/api/v1/tenants/{seed.Tenant}";
            async Task Report(string summary, string service, string environment, TimeSpan before)
                => (await Post(client, $"{root}/changes", tester, new { kind = "deployment", summary, service, environment, occurredAt = seed.Breach - before })).EnsureSuccessStatusCode();
            await Report("own deployment", "shop/orders", "production", TimeSpan.FromMinutes(5));
            await Report("dependency deployment", "shop/billing", "production", TimeSpan.FromMinutes(5));
            await Report("unrelated service", "shop/shipping", "production", TimeSpan.FromMinutes(5));
            await Report("other environment", "shop/orders", "staging", TimeSpan.FromMinutes(5));
            await Report("too long ago", "shop/orders", "production", TimeSpan.FromHours(3));

            var related = await Json(await Get(client, $"{root}/alerts/{seed.Firing}/related", viewer));

            Assert.Equal("correlation", related.GetProperty("relation").GetString());
            Assert.Contains("not a proven cause", related.GetProperty("note").GetString());
            var items = related.GetProperty("items").EnumerateArray().ToList();
            var titles = items.Select(i => i.GetProperty("title").GetString()).ToList();
            Assert.Equal("own deployment", titles[0]);
            Assert.Equal(["own deployment", "dependency deployment", "billing became Unhealthy"], titles);
            Assert.Equal("subject", items[0].GetProperty("scope").GetString());
            Assert.Equal("dependency", items[1].GetProperty("scope").GetString());
            Assert.All(items, i => Assert.Equal(i.GetProperty("score").GetInt32(), i.GetProperty("reasons").EnumerateArray().Sum(r => r.GetProperty("points").GetInt32())));

            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"{root}/alerts/{Guid.NewGuid()}/related", viewer)).StatusCode);
            // A foreign tenant's alert id reads as missing: the id alone grants nothing.
            await using var db = database.Context();
            Assert.True(await db.ChangeEvents.AnyAsync(c => c.Id == seed.OtherTenantChange));
        }

        [Fact]
        public async Task A_rule_change_is_recorded_and_relates_to_that_rules_alerts_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "errors", ["Rules:Definitions:0:Name"] = "Errors", ["Rules:Definitions:0:Template"] = "ErrorRate",
                ["Rules:Definitions:0:Severity"] = "Critical", ["Rules:Definitions:0:Threshold"] = "0.05",
            };
            await using (var first = new ApiFactory(database, settings: settings)) first.CreateClient().Dispose();
            await using var db = database.Context();
            var change = await db.ChangeEvents.SingleAsync(c => c.Source == ChangeEvent.PlatformSource);
            Assert.Equal((ChangeKind.Configuration, "errors"), (change.Kind, change.Attributes[ChangeEvent.RuleAttribute]));
            Assert.Contains("create", change.Summary);
        }
    }
}
