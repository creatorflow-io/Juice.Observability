using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>Requests of the Phase 6 API tests, as each demo user: <c>victor</c> views, <c>erin</c> operates, <c>alice</c> administers.</summary>
    internal sealed class Caller(HttpClient client, string token)
    {
        public static async Task<(Caller Viewer, Caller Operator, Caller Admin, Guid Tenant)> AllAsync(HttpClient client, ScratchDatabase database)
        {
            await using var db = database.Context();
            return (new(client, await LocalStack.TokenAsync("victor")), new(client, await LocalStack.TokenAsync("erin")), new(client, await LocalStack.TokenAsync("alice")), (await db.Tenants.SingleAsync(t => t.Key == LocalStack.TenantKey)).Id);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? ifMatch = null, string? key = null)
        {
            var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            return client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public Task<HttpResponseMessage> PostAsync(string path, object body, string? key = null) => SendAsync(HttpMethod.Post, path, body, key: key);

        public Task<HttpResponseMessage> PutAsync(string path, object body, string? ifMatch = null) => SendAsync(HttpMethod.Put, path, body, ifMatch);

        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

        public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }

    /// <summary>The Phase 6 configuration APIs: rules, endpoints and the allowlist can be changed through the API, by admins, with an audit, and never over what the deployment's file owns.</summary>
    public class NotificationApiTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly object ValidRule = new { key = "orders-errors", name = "Orders errors", template = "ErrorRate", severity = "Critical", service = "shop/orders", threshold = 0.05, forDuration = "00:01:00" };

        private static async Task AssertAuditAsync(ScratchDatabase database, string operation, AuditOutcome outcome, string? actor = null)
        {
            await using var db = database.Context();
            var audit = await db.AuditRecords.AsNoTracking().Where(a => a.Operation == operation && a.Outcome == outcome).OrderByDescending(a => a.OccurredAt).FirstOrDefaultAsync(Ct);
            Assert.True(audit is not null, $"no {outcome} audit for {operation}");
            if (actor is not null) Assert.Equal(actor, audit.Actor);
        }

        [Fact]
        public async Task An_admin_creates_and_changes_a_rule_and_nobody_else_can_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            var rules = $"/api/v1/tenants/{tenant}/rules";

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(rules, ValidRule)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(rules, ValidRule)).StatusCode);
            await AssertAuditAsync(database, "rule.create", AuditOutcome.Denied);

            var created = await admin.PostAsync(rules, ValidRule);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var rule = await Caller.JsonAsync(created);
            Assert.Equal((ManagedBy.Api, "Critical", 0.05), (rule.GetProperty("managedBy").GetString(), rule.GetProperty("severity").GetString(), rule.GetProperty("definition").GetProperty("threshold").GetDouble()));
            await AssertAuditAsync(database, "rule.create", AuditOutcome.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(rules, ValidRule)).StatusCode);

            var etag = rule.GetProperty("etag").GetString()!;
            var changed = new { name = "Orders errors", template = "ErrorRate", severity = "Warning", service = "shop/orders", threshold = 0.10 };
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PutAsync($"{rules}/orders-errors", changed)).StatusCode);
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.PutAsync($"{rules}/orders-errors", changed, "someone-elses")).StatusCode);
            var updated = await Caller.JsonAsync(await admin.PutAsync($"{rules}/orders-errors", changed, etag));
            Assert.NotEqual(etag, updated.GetProperty("etag").GetString());
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.PutAsync($"{rules}/orders-errors", changed, etag)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync($"{rules}/nope", changed)).StatusCode);

            await using var db = database.Context();
            var change = await db.ChangeEvents.AsNoTracking().OrderByDescending(c => c.Id).FirstAsync(Ct);
            Assert.Equal(("Rule orders-errors: update", ChangeKind.Configuration), (change.Summary, change.Kind));
        }

        [Theory]
        [InlineData("""{"name":"x","key":"bad key","template":"ErrorRate","threshold":0.1}""", "Key must match")]
        [InlineData("""{"name":"x","key":"ok-key","template":"ErrorRate","threshold":5}""", "ratios from 0 to 1")]
        [InlineData("""{"name":"x","key":"ok-key","threshold":0.1}""", "template and threshold are required")]
        [InlineData("""{"name":"x","key":"ok-key","template":"ErrorRate"}""", "template and threshold are required")]
        [InlineData("""{"name":"x","key":"ok-key","template":"Sql","threshold":1}""", "Unreadable definition")]
        [InlineData("""{"name":"x","key":"ok-key","template":"ErrorRate","threshold":0.1,"schedule":"00:00:01"}""", "Schedule must be")]
        public async Task An_invalid_rule_is_refused_with_the_reason_Async(string body, string reason)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var response = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", JsonDocument.Parse(body).RootElement);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(reason, await response.Content.ReadAsStringAsync(Ct));
            await using var db = database.Context();
            Assert.False(await db.Rules.AnyAsync(Ct));
        }

        [Fact]
        public async Task The_file_owns_what_it_lists_and_nothing_else_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var file = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "from-file", ["Rules:Definitions:0:Name"] = "From file", ["Rules:Definitions:0:Template"] = "ErrorRate", ["Rules:Definitions:0:Threshold"] = "0.05",
            };
            Guid tenant;
            await using (var first = new ApiFactory(database, settings: file))
            {
                using var client = first.CreateClient();
                var (_, _, admin, id) = await Caller.AllAsync(client, database);
                tenant = id;
                var refused = await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/from-file", new { name = "x", template = "ErrorRate", threshold = 0.5 });
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("the rules file", await refused.Content.ReadAsStringAsync(Ct));
                await AssertAuditAsync(database, "rule.update", AuditOutcome.Failed);
                Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", ValidRule)).StatusCode);
            }

            // A restart keeps the API's rule. Dropping the file's rule disables it and hands it over.
            await using (var second = new ApiFactory(database))
            {
                using var client = second.CreateClient();
                var (_, _, admin, _) = await Caller.AllAsync(client, database);
                await using var db = database.Context();
                Assert.True((await db.Rules.AsNoTracking().SingleAsync(r => r.Key == "orders-errors", Ct)).Enabled);
                var handed = await db.Rules.AsNoTracking().SingleAsync(r => r.Key == "from-file", Ct);
                Assert.Equal((false, ManagedBy.Api), (handed.Enabled, handed.ManagedBy));
                Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/from-file", new { name = "Mine now", template = "ErrorRate", threshold = 0.5 })).StatusCode);
            }

            // Listing a key the API created takes it over, once.
            var over = new Dictionary<string, string>
            {
                ["Rules:Definitions:0:Key"] = "orders-errors", ["Rules:Definitions:0:Name"] = "File wins", ["Rules:Definitions:0:Template"] = "ErrorRate", ["Rules:Definitions:0:Threshold"] = "0.2",
            };
            await using (var third = new ApiFactory(database, settings: over))
            {
                third.CreateClient().Dispose();
            }
            await using var check = database.Context();
            var taken = await check.Rules.AsNoTracking().SingleAsync(r => r.Key == "orders-errors", Ct);
            Assert.Equal(("File wins", ManagedBy.Configuration), (taken.Name, taken.ManagedBy));
            Assert.True((await check.Rules.AsNoTracking().SingleAsync(r => r.Key == "from-file", Ct)).Enabled, "still the API's");
        }

        private static async Task<(Guid Tenant, Rule Rule, Alert Alert)> FiringAsync(ScratchDatabase database, string key = "orders-errors")
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var now = DateTimeOffset.UtcNow;
            var definition = new Rules.RuleDefinition { Key = key, Name = "Orders errors", Template = RuleTemplate.ErrorRate, Service = "shop/orders", Threshold = 0.05, Severity = AlertSeverity.Critical };
            var rule = new Rule { TenantId = tenant, Key = key, Name = definition.Name, Template = definition.Template, Severity = definition.Severity, Definition = definition.ToJson(), DefinitionHash = definition.Hash(), LoadedAt = now, ManagedBy = ManagedBy.Api };
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = $"{key}|service:shop/orders@production", SubjectKey = "service:shop/orders@production", SubjectType = HealthSubject.Service,
                Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = now, FiredAt = now, LastEvaluatedAt = now, LastValue = 0.3,
            };
            db.AddRange(rule, alert);
            await db.SaveChangesAsync(Ct);
            return (tenant, rule, alert);
        }

        [Theory]
        [InlineData("disable", "rule_disabled")]
        [InlineData("rescope", "rule_changed")]
        public async Task A_rule_that_stops_watching_a_subject_closes_its_alert_Async(string change, string reason)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (tenant, _, alert) = await FiringAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, _) = await Caller.AllAsync(client, database);
            object body = change == "disable"
                ? new { name = "Orders errors", template = "ErrorRate", service = "shop/orders", threshold = 0.05, enabled = false, severity = "Critical" }
                : new { name = "Orders errors", template = "ErrorRate", service = "shop/billing", threshold = 0.05, severity = "Critical" };

            await Caller.JsonAsync(await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/orders-errors", body));

            await using var db = database.Context();
            var closed = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert.Id, Ct);
            Assert.Equal(AlertState.Resolved, closed.State);
            Assert.NotNull(closed.ResolvedAt);
            Assert.Equal(reason, (await db.AlertTransitions.AsNoTracking().OrderByDescending(t => t.At).FirstAsync(t => t.AlertId == alert.Id, Ct)).Reason);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_rule_dropped_from_the_file_closes_its_alerts_even_if_disabled_long_ago_Async(bool enabled)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (_, rule, alert) = await FiringAsync(database);
            await using (var db = database.Context())
            {
                await db.Rules.Where(r => r.Id == rule.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ManagedBy, ManagedBy.Configuration).SetProperty(r => r.Enabled, enabled), Ct);
            }
            await using var factory = new ApiFactory(database);
            factory.CreateClient().Dispose();
            await using var check = database.Context();
            Assert.Equal(AlertState.Resolved, (await check.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert.Id, Ct)).State);
            Assert.Equal(ManagedBy.Api, (await check.Rules.AsNoTracking().SingleAsync(r => r.Id == rule.Id, Ct)).ManagedBy);
        }

        [Fact]
        public async Task A_new_threshold_keeps_the_alert_open_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (tenant, _, alert) = await FiringAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, _) = await Caller.AllAsync(client, database);
            await Caller.JsonAsync(await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/orders-errors", new { name = "Orders errors", template = "ErrorRate", service = "shop/orders", threshold = 0.10, severity = "Critical" }));
            await using var db = database.Context();
            Assert.Equal(AlertState.Firing, (await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert.Id, Ct)).State);
        }

        [Fact]
        public async Task A_channel_never_shows_its_address_and_can_only_name_secrets_set_aside_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
            const string Token = "s3cr3t-path-token";
            var body = new { key = "ops", url = $"https://hooks.example.com/juice/{Token}", secretRef = "env:JUICE_WEBHOOK_OPS", minimumSeverity = "warning", environments = new[] { "production" } };

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(webhooks, body)).StatusCode);
            await AssertAuditAsync(database, "webhook.create", AuditOutcome.Denied);

            var refused = await admin.PostAsync(webhooks, new { key = "ops", url = "https://hooks.example.com/juice", secretRef = "env:POSTGRES_PASSWORD" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("JUICE_WEBHOOK_", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(webhooks, new { key = "ops", url = "https://hooks.example.com/juice", secretRef = "file:/etc/passwd" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(webhooks, new { key = "ops", url = "http://hooks.example.com/juice", secretRef = "env:JUICE_WEBHOOK_OPS" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(webhooks, new { key = "chat", kind = "slack", url = "https://hooks.slack.com/x", secretRef = "env:JUICE_WEBHOOK_S" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(webhooks, new { key = "mail", kind = "email", recipients = new[] { "a@example.com" } })).StatusCode);   // no SMTP here

            var created = await Caller.JsonAsync(await admin.PostAsync(webhooks, body));
            Assert.Equal("https://hooks.example.com", created.GetProperty("destination").GetString());
            var id = created.GetProperty("id").GetGuid();

            var seenByViewer = await viewer.GetAsync(webhooks);
            var text = await seenByViewer.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(Token, text);
            Assert.DoesNotContain("JUICE_WEBHOOK_OPS", text);
            var seenByAdmin = await Caller.JsonAsync(await admin.GetAsync(webhooks));
            Assert.Equal("env:JUICE_WEBHOOK_OPS", seenByAdmin.GetProperty("items")[0].GetProperty("secretRef").GetString());
            Assert.DoesNotContain(Token, seenByAdmin.ToString());

            // Left out, the URL stays: reads never return it, so an edit can't resend it.
            await Caller.JsonAsync(await admin.PutAsync($"{webhooks}/{id}", new { secretRef = "env:JUICE_WEBHOOK_OPS", enabled = false }));
            await using var db = database.Context();
            var stored = await db.WebhookEndpoints.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(($"https://hooks.example.com/juice/{Token}", false), (stored.Url, stored.Enabled));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"{webhooks}/{id}", new { kind = "slack", secretRef = "env:JUICE_WEBHOOK_OPS" })).StatusCode);

            var audited = string.Join(" ", (await db.AuditRecords.AsNoTracking().Where(a => a.TargetType == "webhook").ToListAsync(Ct)).SelectMany(a => a.Metadata.Values));
            var changes = string.Join(" ", await db.ChangeEvents.AsNoTracking().Select(c => c.Summary).ToListAsync(Ct));
            Assert.DoesNotContain(Token, audited);
            Assert.DoesNotContain(Token, changes);
            Assert.DoesNotContain("hooks.example.com", changes);
        }

        [Fact]
        public async Task The_allowlist_changes_only_through_an_admin_and_never_opens_what_is_always_blocked_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:Allowlist:0:Destination"] = "10.1.0.0/16" });
            using var client = factory.CreateClient();
            var (_, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            var allowlist = $"/api/v1/tenants/{tenant}/webhooks/allowlist";

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PutAsync(allowlist, new { destination = "10.2.0.0/16" })).StatusCode);
            await AssertAuditAsync(database, "webhook.allowlist.put", AuditOutcome.Denied);
            foreach (var blocked in new[] { "127.0.0.0/8", "169.254.169.254/32", "not a host!", "" })
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(allowlist, new { destination = blocked })).StatusCode);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(allowlist, new { destination = "10.2.0.0/16", ports = new[] { 70000 } })).StatusCode);

            var entry = await Caller.JsonAsync(await admin.PutAsync(allowlist, new { destination = "10.2.0.0/16", ports = new[] { 8443, 8443 } }));
            Assert.Equal([8443], entry.GetProperty("ports").EnumerateArray().Select(p => p.GetInt32()));
            await Caller.JsonAsync(await admin.PutAsync(allowlist, new { destination = "10.2.0.0/16", ports = new[] { 9443 } }));
            var list = await Caller.JsonAsync(await @operator.GetAsync(allowlist));
            Assert.Equal(2, list.GetProperty("items").GetArrayLength());

            Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync(allowlist, new { destination = "10.1.0.0/16", ports = new[] { 443 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Delete, $"{allowlist}?destination=10.1.0.0%2F16")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.SendAsync(HttpMethod.Delete, $"{allowlist}?destination=10.2.0.0%2F16")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"{allowlist}?destination=10.2.0.0%2F16")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Delete, $"{allowlist}?destination=10.2.0.0%2F16")).StatusCode);
            await AssertAuditAsync(database, "webhook.allowlist.remove", AuditOutcome.Succeeded);
        }

        [Fact]
        public async Task Silences_need_a_reason_a_scope_and_an_end_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var silences = $"/api/v1/tenants/{tenant}/silences";
            var now = DateTimeOffset.UtcNow;
            var good = new { comment = "Deploying --password hunter2", environment = "Production", endsAt = now.AddHours(1) };

            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(silences, good)).StatusCode);
            await AssertAuditAsync(database, "silence.create", AuditOutcome.Denied);

            foreach (var (bad, reason) in new (object, string)[]
            {
                (new { environment = "production", endsAt = now.AddHours(1) }, "comment is required"),
                (new { comment = "x", endsAt = now.AddHours(1) }, "Say what to silence"),
                (new { comment = "x", environment = "production" }, "endsAt is required"),
                (new { comment = "x", environment = "production", endsAt = now.AddDays(8) }, "at most 7 days"),
                (new { comment = "x", environment = "production", endsAt = now.AddHours(1), repeatWeekly = true }, "Only a maintenance window repeats"),
                (new { comment = "x", kind = "maintenance", environment = "production", startsAt = now.AddHours(1), endsAt = now.AddHours(100), repeatWeekly = true }, "at most 72 hours"),
                (new { comment = "x", allAlerts = true, environment = "production", endsAt = now.AddHours(1) }, "allAlerts takes no other scope"),
            })
            {
                var refused = await @operator.PostAsync(silences, bad);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains(reason, await refused.Content.ReadAsStringAsync(Ct));
            }

            var first = await @operator.PostAsync(silences, good, key: "retry-1");
            var created = await Caller.JsonAsync(first);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.DoesNotContain("hunter2", created.ToString());
            Assert.Equal("production", created.GetProperty("environment").GetString());
            Assert.True(created.GetProperty("active").GetBoolean());
            var replay = await @operator.PostAsync(silences, good, key: "retry-1");
            Assert.Equal((created.GetProperty("id").GetGuid(), "true"), ((await Caller.JsonAsync(replay)).GetProperty("id").GetGuid(), replay.Headers.GetValues("Idempotency-Replayed").Single()));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await @operator.PostAsync(silences, new { comment = "other", environment = "x", endsAt = now.AddHours(1) }, key: "retry-1")).StatusCode);

            var id = created.GetProperty("id").GetGuid();
            Assert.Single((await Caller.JsonAsync(await viewer.GetAsync(silences))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(HttpMethod.Delete, $"{silences}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{id}")).StatusCode);
            Assert.Empty((await Caller.JsonAsync(await viewer.GetAsync(silences))).GetProperty("items").EnumerateArray());
            var history = (await Caller.JsonAsync(await viewer.GetAsync($"{silences}?all=true"))).GetProperty("items")[0];
            Assert.Equal((false, "erin"), (history.GetProperty("active").GetBoolean(), history.GetProperty("cancelledBy").GetString()));
            await using var db = database.Context();
            Assert.Equal(1, await db.AuditRecords.CountAsync(a => a.Operation == "silence.cancel" && a.Outcome == AuditOutcome.Succeeded, Ct));
            Assert.Equal(1, await db.Silences.CountAsync(Ct));
        }

        [Fact]
        public async Task An_escalation_policy_is_an_admins_and_names_real_endpoints_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, @operator, admin, tenant) = await Caller.AllAsync(client, database);
            var policies = $"/api/v1/tenants/{tenant}/escalation-policies";
            await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/webhooks", new { key = "pager", url = "https://hooks.example.com/p", secretRef = "env:JUICE_WEBHOOK_P" }));
            var good = new { key = "critical", name = "Critical", steps = new[] { new { afterMinutes = 5, endpointKeys = new[] { "pager" } }, new { afterMinutes = 15, endpointKeys = new[] { "pager" } } } };

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(policies, good)).StatusCode);
            await AssertAuditAsync(database, "escalation.create", AuditOutcome.Denied);
            foreach (var (bad, reason) in new (object, string)[]
            {
                (new { key = "c", name = "x", steps = new[] { new { afterMinutes = 5, endpointKeys = new[] { "pager" } } } }, "key is lowercase"),
                (new { key = "critical", name = "x", steps = Array.Empty<object>() }, "1 to 10 steps"),
                (new { key = "critical", name = "x", steps = new[] { new { afterMinutes = 5, endpointKeys = new[] { "nobody" } } } }, "No endpoint 'nobody'"),
                (new { key = "critical", name = "x", steps = new[] { new { afterMinutes = 5, endpointKeys = Array.Empty<string>() } } }, "1 to 10 endpoints"),
                (new { key = "critical", name = "x", steps = new[] { new { afterMinutes = 15, endpointKeys = new[] { "pager" } }, new { afterMinutes = 5, endpointKeys = new[] { "pager" } } } }, "later than the one before"),
            })
            {
                var refused = await admin.PostAsync(policies, bad);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains(reason, await refused.Content.ReadAsStringAsync(Ct));
            }
            var created = await Caller.JsonAsync(await admin.PostAsync(policies, good));
            Assert.True(created.GetProperty("stopOnAcknowledge").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(policies, good)).StatusCode);
            var updated = await Caller.JsonAsync(await admin.PutAsync($"{policies}/critical", new { name = "Critical", stopOnAcknowledge = false, enabled = false, steps = good.steps }));
            Assert.Equal((false, false), (updated.GetProperty("stopOnAcknowledge").GetBoolean(), updated.GetProperty("enabled").GetBoolean()));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync($"{policies}/nope", new { name = "x", steps = good.steps })).StatusCode);
        }
    }
}
