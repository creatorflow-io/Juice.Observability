using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using static Juice.Observability.Tests.ApiTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Roadmap Phase 3 API (requirements §6.1): rules loaded from configuration and audited, read-only rules, alerts,
    /// alert detail for investigation, and acknowledgement with roles, audit and idempotency keys.
    /// </summary>
    public class AlertApiTests
    {
        private static Dictionary<string, string> Rules(params (string Key, string Threshold)[] rules)
        {
            var settings = new Dictionary<string, string>();
            for (var i = 0; i < rules.Length; i++)
            {
                settings[$"Rules:Definitions:{i}:Key"] = rules[i].Key;
                settings[$"Rules:Definitions:{i}:Name"] = $"Rule {rules[i].Key}";
                settings[$"Rules:Definitions:{i}:Template"] = "ErrorRate";
                settings[$"Rules:Definitions:{i}:Threshold"] = rules[i].Threshold;
            }
            return settings;
        }

        [Fact]
        public async Task Rules_load_from_configuration_with_an_audit_of_every_change_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            async Task<List<(string Operation, string? Target)>> AuditAsync()
            {
                await using var db = database.Context();
                return (await db.AuditRecords.Where(a => a.TargetType == "rule").OrderBy(a => a.OccurredAt).ToListAsync())
                    .Select(a => (a.Operation, a.TargetId)).ToList();
            }

            await using (var first = new ApiFactory(database, settings: Rules(("orders-errors", "0.05"), ("billing-errors", "0.05"))))
            {
                using var client = first.CreateClient();
                var token = await LocalStack.TokenAsync("victor");
                var tenant = (await JsonAsync(await Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
                var rules = await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/rules", token));
                Assert.Equal(2, rules.GetProperty("items").GetArrayLength());
                Assert.Equal(0.05, rules.GetProperty("items")[0].GetProperty("definition").GetProperty("threshold").GetDouble());
            }
            Assert.Equal([("rule.create", "orders-errors"), ("rule.create", "billing-errors")], await AuditAsync());

            // Restart with one rule changed and one removed: an update and a disable; a further identical restart: nothing.
            for (var restart = 0; restart < 2; restart++)
            {
                await using var next = new ApiFactory(database, settings: Rules(("orders-errors", "0.10")));
                next.CreateClient().Dispose();
            }
            var audit = await AuditAsync();
            Assert.Equal([("rule.update", "orders-errors"), ("rule.disable", "billing-errors")], audit.Skip(2));
            await using var check = database.Context();
            Assert.False((await check.Rules.SingleAsync(r => r.Key == "billing-errors")).Enabled);
        }

        [Fact]
        public async Task Invalid_rule_configuration_stops_the_host_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: Rules(("orders-errors", "5")));   // a ratio above 1
            var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains("Invalid rule configuration", error.ToString());
            Assert.Contains("ratios from 0 to 1", error.ToString());
        }

        [Fact]
        public async Task Alert_detail_and_acknowledgement_with_roles_audit_and_idempotency_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (tenant, firing, resolved) = await SeedAsync(database);
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["Integrations:Grafana:BaseUrl"] = "http://grafana.example:3000" });
            using var client = factory.CreateClient();
            var (viewer, operatorToken) = (await LocalStack.TokenAsync("victor"), await LocalStack.TokenAsync("erin"));
            var alerts = $"/api/v1/tenants/{tenant}/alerts";

            var list = await JsonAsync(await Get(client, $"{alerts}?state=firing", viewer));
            Assert.Equal(firing, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

            var detail = await JsonAsync(await Get(client, $"{alerts}/{firing}", viewer));
            Assert.Equal(["Pending", "Firing"], detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()));
            Assert.Equal("0.25", detail.GetProperty("evidence").GetProperty("value").GetString());
            Assert.Equal("Unhealthy", detail.GetProperty("subjectHealth")[0].GetProperty("state").GetString());
            var links = detail.GetProperty("links");
            Assert.Equal("grafana", links[0].GetProperty("integration").GetString());
            Assert.StartsWith("http://grafana.example:3000/d/juice-overview?var-service=orders&", links[0].GetProperty("url").GetString());

            // A viewer may read but not acknowledge; the attempt is audited.
            using (var denied = await Ack(client, $"{alerts}/{firing}/acknowledgement", viewer, "looking", key: null))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            using var first = await Ack(client, $"{alerts}/{firing}/acknowledgement", operatorToken, "on it", key: "ack-1", correlation: "incident-7");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var body = await first.Content.ReadAsStringAsync(Ct);
            Assert.Equal("erin", JsonDocument.Parse(body).RootElement.GetProperty("acknowledgedBy").GetString());

            using var replay = await Ack(client, $"{alerts}/{firing}/acknowledgement", operatorToken, "on it", key: "ack-1");
            Assert.Equal((HttpStatusCode.OK, body), (replay.StatusCode, await replay.Content.ReadAsStringAsync(Ct)));
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            using var reused = await Ack(client, $"{alerts}/{firing}/acknowledgement", operatorToken, "something else", key: "ack-1");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
            using var closed = await Ack(client, $"{alerts}/{resolved}/acknowledgement", operatorToken, null, key: null);
            Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);

            await using var db = database.Context();
            var audit = await db.AuditRecords.Where(a => a.Operation == "alert.acknowledge").OrderBy(a => a.OccurredAt).ToListAsync();
            Assert.Equal([(AuditOutcome.Denied, "victor"), (AuditOutcome.Succeeded, "erin"), (AuditOutcome.Failed, "erin")],
                audit.Select(a => (a.Outcome, a.Actor)));
            Assert.Equal("incident-7", audit[1].CorrelationId);
            Assert.Equal("on it", (await db.Alerts.SingleAsync(a => a.Id == firing)).AcknowledgeComment);
        }

        [Fact]
        public async Task Webhook_endpoints_and_delivery_history_hide_secrets_and_paths_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (tenant, firing, _) = await SeedAsync(database);
            Guid endpointId;
            await using (var db = database.Context())
            {
                var endpoint = new WebhookEndpoint
                {
                    TenantId = tenant, Key = "ops", Url = "https://hooks.example.com:8443/T0K3N/path", SecretRef = "env:OPS_SECRET",
                    Events = ["alert.firing"], DefinitionHash = "x", LoadedAt = DateTimeOffset.UtcNow,
                };
                var delivery = new WebhookDelivery
                {
                    TenantId = tenant, EndpointId = endpoint.Id, AlertId = firing, TransitionId = Guid.CreateVersion7(), Event = "alert.firing",
                    Payload = "{}", Status = DeliveryStatus.Retrying, Attempts = 1, NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(10),
                    LastStatusCode = 503, LastError = "status_503", CreatedAt = DateTimeOffset.UtcNow,
                };
                db.AddRange(endpoint, delivery);
                db.WebhookAttempts.Add(new WebhookAttempt { TenantId = tenant, DeliveryId = delivery.Id, Number = 1, At = DateTimeOffset.UtcNow, StatusCode = 503, Error = "status_503", DurationMs = 12 });
                await db.SaveChangesAsync();
                endpointId = endpoint.Id;
            }
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var token = await LocalStack.TokenAsync("victor");

            using var endpointsResponse = await Get(client, $"/api/v1/tenants/{tenant}/webhooks", token);
            var raw = await endpointsResponse.Content.ReadAsStringAsync(Ct);
            Assert.Contains("\"destination\":\"https://hooks.example.com:8443\"", raw);
            Assert.DoesNotContain("T0K3N", raw);
            Assert.DoesNotContain("OPS_SECRET", raw);

            var deliveries = await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/webhooks/{endpointId}/deliveries?status=retrying", token));
            var item = Assert.Single(deliveries.GetProperty("items").EnumerateArray());
            Assert.Equal("Retrying", item.GetProperty("status").GetString());
            Assert.Equal(503, item.GetProperty("history")[0].GetProperty("statusCode").GetInt32());
            Assert.Equal(HttpStatusCode.NotFound, (await Get(client, $"/api/v1/tenants/{tenant}/webhooks/{Guid.CreateVersion7()}/deliveries", token)).StatusCode);
        }

        private static async Task<HttpResponseMessage> Ack(HttpClient client, string path, string token, string? comment, string? key, string? correlation = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { comment }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (correlation is not null) request.Headers.Add("X-Correlation-Id", correlation);
            return await client.SendAsync(request, Ct);
        }

        [Fact]
        public async Task Alerts_are_filtered_by_environment_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (tenant, firing, resolved) = await SeedAsync(database);
            Guid staged, host;
            await using (var db = database.Context())
            {
                var now = DateTimeOffset.UtcNow;
                var rule = await db.Rules.SingleAsync();
                var staging = new DeploymentEnvironment { TenantId = tenant, Key = "staging", Type = EnvironmentType.Staging, FirstSeen = now, LastSeen = now };
                Alert NewAlert(string subject, HealthSubject type, Guid? environment) => new()
                {
                    TenantId = tenant, RuleId = rule.Id, DedupKey = $"orders-errors|{subject}", SubjectKey = subject, SubjectType = type,
                    EnvironmentId = environment, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, Evidence = [],
                };
                var (inStaging, onHost) = (NewAlert("service:shop/orders@staging", HealthSubject.Service, staging.Id), NewAlert("host:web-1", HealthSubject.Host, null));
                db.AddRange(staging, inStaging, onHost);
                await db.SaveChangesAsync();
                (staged, host) = (inStaging.Id, onHost.Id);
            }
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var viewer = await LocalStack.TokenAsync("victor");

            async Task<Guid[]> List(string query) => (await JsonAsync(await Get(client, $"/api/v1/tenants/{tenant}/alerts?{query}", viewer)))
                .GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray();

            Assert.Equal(new[] { firing, resolved }.Order(), (await List("environment=production")).Order());
            Assert.Equal([firing], await List("environment=production&state=firing"));
            Assert.Equal([staged], await List("environment=STAGING"));
            Assert.Empty(await List("environment=nowhere"));
            Assert.Equal(new[] { firing, resolved, staged, host }.Order(), (await List("environment=")).Order());
            Assert.Equal(new[] { firing, resolved, staged, host }.Order(), (await List("limit=10")).Order());
        }

        /// <summary>A firing alert on the orders service (with its health) and a resolved one.</summary>
        private static async Task<(Guid Tenant, Guid Firing, Guid Resolved)> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync()).Id;
            var now = DateTimeOffset.UtcNow;
            var production = new DeploymentEnvironment { TenantId = tenant, Key = "production", Type = EnvironmentType.Production, FirstSeen = now, LastSeen = now };
            var orders = new Service { TenantId = tenant, Namespace = "shop", Name = "orders", FirstSeen = now, LastSeen = now };
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Definition = "{}", DefinitionHash = "x", LoadedAt = now };
            Alert NewAlert(AlertState state) => new()
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = $"orders-errors|service:shop/orders@production|{state}", SubjectKey = "service:shop/orders@production",
                SubjectType = HealthSubject.Service, SubjectId = orders.Id, EnvironmentId = production.Id, State = state,
                BreachStartedAt = now.AddMinutes(-5), FiredAt = now.AddMinutes(-3), ResolvedAt = state == AlertState.Resolved ? now : null,
                LastEvaluatedAt = now, LastValue = 0.25, Evidence = new() { ["template"] = "ErrorRate", ["value"] = "0.25" },
            };
            var (firing, resolved) = (NewAlert(AlertState.Firing), NewAlert(AlertState.Resolved));
            db.AddRange(production, orders, rule, firing, resolved);
            db.AlertTransitions.AddRange(
                new AlertTransition { TenantId = tenant, AlertId = firing.Id, ToState = AlertState.Pending, At = now.AddMinutes(-5), Reason = "breach" },
                new AlertTransition { TenantId = tenant, AlertId = firing.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = now.AddMinutes(-3), Reason = "for_duration_met" });
            db.HealthSnapshots.Add(new HealthSnapshot
            {
                TenantId = tenant, SubjectType = HealthSubject.Service, SubjectId = orders.Id, EnvironmentId = production.Id, State = HealthState.Unhealthy,
                ReasonCodes = ["error_rate_high"], PolicyVersion = "test", EvaluatedAt = now, EvidenceFrom = now.AddMinutes(-5), EvidenceTo = now, Since = now,
            });
            await db.SaveChangesAsync();
            return (tenant, firing.Id, resolved.Id);
        }
    }
}
