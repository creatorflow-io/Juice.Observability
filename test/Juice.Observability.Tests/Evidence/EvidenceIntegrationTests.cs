using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests.EvidenceContributors
{
    /// <summary>The evidence endpoints on the local stack: tenancy, authorization, the read-only transaction, savepoints, timeouts and what the response never says.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed partial class EvidenceIntegrationTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed record Seeded(Guid Tenant, Guid Alert, Guid Service, Guid Environment, Guid OtherAlert, Guid Host, DateTimeOffset Breach, Guid SecondAlert);

        private static Task<ModuleTestHost> HostAsync(ScratchDatabase database, Dictionary<string, string?>? settings = null)
        {
            EvidenceBehaviors.Reset();
            return ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EvidenceSampleModule>(), database, hostedServices: true, settings: settings);
        }

        private static async Task<Seeded> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var now = DateTimeOffset.UtcNow;
            var breach = now.AddMinutes(-10);
            var production = new DeploymentEnvironment { TenantId = tenant, Key = "production", Type = EnvironmentType.Production, FirstSeen = now, LastSeen = now };
            var orders = new Service { TenantId = tenant, Namespace = "shop", Name = "orders", FirstSeen = now, LastSeen = now };
            var host = new Resource { TenantId = tenant, Type = ResourceType.Host, Key = "web-1", DisplayName = "web-1", FirstSeen = now, LastSeen = now };
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical, Definition = "{}", DefinitionHash = "h", LoadedAt = now };
            Alert NewAlert(Guid t, Guid ruleId, Guid? subject, Guid? environment, string key) => new()
            {
                TenantId = t, RuleId = ruleId, DedupKey = key, SubjectKey = "service:shop/orders@production", SubjectType = HealthSubject.Service, SubjectId = subject, EnvironmentId = environment,
                Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = breach, FiredAt = breach.AddMinutes(2), LastEvaluatedAt = now, LastValue = 0.25,
            };
            var alert = NewAlert(tenant, rule.Id, orders.Id, production.Id, "orders-errors|one");
            var second = NewAlert(tenant, rule.Id, orders.Id, production.Id, "orders-errors|two");
            db.AddRange(production, orders, host, rule, alert, second);
            db.ChangeEvents.AddRange(
                new ChangeEvent { TenantId = tenant, Kind = ChangeKind.Deployment, Source = "pipeline", Summary = "Deployed orders 1.4.2", ServiceId = orders.Id, Environment = "production", OccurredAt = breach.AddMinutes(-3), RecordedAt = now },
                new ChangeEvent { TenantId = tenant, Kind = ChangeKind.Configuration, Source = "pipeline", Summary = "Raised the pool size", ServiceId = orders.Id, Environment = "production", OccurredAt = breach.AddMinutes(-1), RecordedAt = now });
            var other = new PlatformTenant { Key = $"other-{Guid.NewGuid():N}", DisplayName = "Other" };
            db.Tenants.Add(other);
            var foreignRule = new Rule { TenantId = other.Id, Key = "foreign", Name = "Foreign", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical, Definition = "{}", DefinitionHash = "h", LoadedAt = now };
            var foreign = NewAlert(other.Id, foreignRule.Id, null, null, "foreign|one");
            db.AddRange(foreignRule, foreign);
            await db.SaveChangesAsync(Ct);
            return new Seeded(tenant, alert.Id, orders.Id, production.Id, foreign.Id, host.Id, breach, second.Id);
        }

        private static string Alerts(Seeded s, Guid? alert = null) => $"/api/v1/tenants/{s.Tenant}/alerts/{alert ?? s.Alert}/evidence-sections";

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return document.RootElement.Clone();
        }

        private static IReadOnlyDictionary<string, JsonElement> ByKey(JsonElement body)
            => body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!, i => i);

        private static string Normalize(string problem) => IdentityFields().Replace(problem, "");

        [GeneratedRegex("\"(traceId|correlationId)\":\"[^\"]*\"")]
        private static partial Regex IdentityFields();

        private static async Task<long> CountAsync(ScratchDatabase database, string sql)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
        }

        [Fact]
        public async Task Other_tenant_403_same_body_as_unknown()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            using var client = host.As("viewer");

            var foreign = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/alerts/{seed.OtherAlert}/evidence-sections", Ct);
            var unknown = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/alerts/{Guid.NewGuid()}/evidence-sections", Ct);
            var resource = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}/resources/{seed.Service}/evidence-sections", Ct);

            Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (foreign.StatusCode, unknown.StatusCode, resource.StatusCode));
            Assert.Equal(Normalize(await foreign.Content.ReadAsStringAsync(Ct)), Normalize(await unknown.Content.ReadAsStringAsync(Ct)));
            Assert.Empty(EvidenceBehaviors.Seen);
            // A token with no tenant claim is the same refusal.
            using var noTenant = host.As("viewer", tenant: null);
            Assert.Equal(HttpStatusCode.Forbidden, (await noTenant.GetAsync(Alerts(seed), Ct)).StatusCode);
        }

        [Fact]
        public async Task Alert_not_in_tenant_404_contributors_not_called()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            using var client = host.As("viewer");

            // The caller's own tenant, another tenant's alert, and an unknown one: all the same 404, and no contributor ran.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Alerts(seed, seed.OtherAlert), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Alerts(seed, Guid.NewGuid()), Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{Guid.NewGuid()}/evidence-sections", Ct)).StatusCode);
            Assert.Empty(EvidenceBehaviors.Seen);
        }

        [Fact]
        public async Task Context_has_core_tenant_and_verified_subject()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            using var client = host.As("viewer");

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Alerts(seed), Ct)).StatusCode);
            var (_, alert) = EvidenceBehaviors.Seen.First(s => s.Key == "evidence-sample.one");
            Assert.Equal((seed.Tenant, EvidenceTarget.Alert, seed.Alert, seed.Service, seed.Environment), (alert.TenantId, alert.Target, alert.SubjectId, alert.ServiceId, alert.EnvironmentId));
            Assert.Equal(seed.Breach.AddMinutes(2).AddHours(-2).ToUnixTimeSeconds(), alert.From.ToUnixTimeSeconds());
            Assert.InRange(alert.To, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(5));

            EvidenceBehaviors.Seen.Clear();
            var from = DateTimeOffset.UtcNow.AddHours(-3);
            var response = await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{seed.Service}/evidence-sections?environmentId={seed.Environment}&from={Uri.EscapeDataString(from.ToString("O"))}", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var (key, service) = Assert.Single(EvidenceBehaviors.Seen);
            Assert.Equal("evidence-sample.service", key);
            Assert.Equal((seed.Tenant, EvidenceTarget.Service, seed.Service, seed.Service, seed.Environment), (service.TenantId, service.Target, service.SubjectId, service.ServiceId, service.EnvironmentId));
            Assert.Equal(from.ToUnixTimeSeconds(), service.From.ToUnixTimeSeconds());

            // The window is at most 7 days, and from is before to.
            var tooWide = await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{seed.Service}/evidence-sections?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-8).ToString("O"))}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{seed.Service}/evidence-sections?environmentId={Guid.NewGuid()}", Ct)).StatusCode);
        }

        [Fact]
        public async Task Module_context_write_refused_read_only_row_absent()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            EvidenceBehaviors.ByKey["evidence-sample.one"] = async (_, services, ct) =>
            {
                services.Module.Notes.Add(new EvidenceNote { Id = Guid.CreateVersion7(), Text = "written" });
                await services.Module.SaveChangesAsync(ct);
                return new EvidenceSection("Wrote", []);
            };
            using var client = host.As("viewer");

            var body = await Json(await client.GetAsync(Alerts(seed), Ct));

            var sections = ByKey(body);
            Assert.Equal("failed", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM evidence_sample.notes"));
        }

        [Fact]
        public async Task Core_context_write_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            var before = await CountAsync(database, "SELECT count(*) FROM change_events");
            var auditBefore = await CountAsync(database, "SELECT count(*) FROM audit_records");
            EvidenceBehaviors.ByKey["evidence-sample.one"] = async (context, services, ct) =>
            {
                services.Core.ChangeEvents.Add(new ChangeEvent { TenantId = context.TenantId, Kind = ChangeKind.Other, Source = "evil", Summary = "written", OccurredAt = DateTimeOffset.UtcNow, RecordedAt = DateTimeOffset.UtcNow });
                await services.Core.SaveChangesAsync(ct);
                return new EvidenceSection("Wrote", []);
            };
            using var client = host.As("viewer");

            var sections = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)));

            Assert.Equal("failed", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal(before, await CountAsync(database, "SELECT count(*) FROM change_events"));
            Assert.Equal(auditBefore, await CountAsync(database, "SELECT count(*) FROM audit_records"));
        }

        [Fact]
        public async Task Failing_contributor_savepoint_next_runs()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            // A failed statement aborts the transaction until its savepoint is rolled back; the next contributor reads as if nothing happened.
            EvidenceBehaviors.ByKey["evidence-sample.one"] = async (_, services, ct) =>
            {
                await services.Core.Database.ExecuteSqlRawAsync("SELECT 1/0", ct);
                return null;
            };
            // One that swallows its own database error and returns normally leaves the transaction aborted too.
            EvidenceBehaviors.ByKey["evidence-sample.two"] = async (_, services, ct) =>
            {
                try
                {
                    await services.Module.Database.ExecuteSqlRawAsync("SELECT 1/0", ct);
                }
                catch (PostgresException)
                {
                }
                return new EvidenceSection("Swallowed", []);
            };
            using var client = host.As("viewer");

            var sections = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)));

            Assert.Equal("failed", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["evidence-sample.three"].GetProperty("state").GetString());
            Assert.Equal("Sample evidence-sample.three", sections["evidence-sample.three"].GetProperty("title").GetString());
        }

        [Fact]
        public async Task Timeout_marked_and_budget_respected()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // One contributor may take up to 10 s but the whole request has 1 s: the first slow one uses what is left, and the rest are never called.
            await using var host = await HostAsync(database, new() { ["Evidence:ContributorTimeout"] = "00:00:10", ["Evidence:Budget"] = "00:00:01" });
            var seed = await SeedAsync(database);
            EvidenceBehaviors.ByKey["evidence-sample.one"] = Slow;
            using var client = host.As("viewer");

            var clock = Stopwatch.StartNew();
            var sections = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)));
            clock.Stop();

            Assert.Equal("timeout", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("timeout", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.Equal("timeout", sections["evidence-sample.three"].GetProperty("state").GetString());
            Assert.DoesNotContain(EvidenceBehaviors.Seen, s => s.Key is "evidence-sample.two" or "evidence-sample.three");
            Assert.All(new[] { "one", "two", "three" }, k => Assert.Equal(JsonValueKind.Null, sections["evidence-sample." + k].GetProperty("title").ValueKind));
            Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        }

        [Fact]
        public async Task Contributor_timeout_marks_only_that_section()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database, new() { ["Evidence:ContributorTimeout"] = "00:00:00.5" });
            var seed = await SeedAsync(database);
            EvidenceBehaviors.ByKey["evidence-sample.one"] = Slow;
            using var client = host.As("viewer");

            var sections = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)));

            Assert.Equal("timeout", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["core.changes"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.Equal("ok", sections["evidence-sample.three"].GetProperty("state").GetString());
        }

        [Fact]
        public async Task Contributor_ignoring_its_token_does_not_hold_the_request()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database, new() { ["Evidence:ContributorTimeout"] = "00:00:00.5" });
            var seed = await SeedAsync(database);
            // Neither honors the token nor yields the thread: the core stops waiting, and nothing else touches the shared connection after it.
            EvidenceBehaviors.ByKey["evidence-sample.one"] = (_, _, _) =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return Task.FromResult<EvidenceSection?>(new EvidenceSection("Late", []));
            };
            using var client = host.As("viewer");

            var clock = Stopwatch.StartNew();
            var sections = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)));
            clock.Stop();

            Assert.Equal("timeout", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("timeout", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.DoesNotContain(EvidenceBehaviors.Seen, s => s.Key == "evidence-sample.two");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.8), $"took {clock.Elapsed}");
        }

        private static readonly EvidenceBehaviors.Behavior Slow = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new EvidenceSection("Late", []);
        };

        [Fact]
        public async Task Exception_text_never_in_response()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            const string Secret = "connection string Password=hunter2";
            EvidenceBehaviors.ByKey["evidence-sample.one"] = (_, _, _) => throw new InvalidOperationException(Secret);
            EvidenceBehaviors.ByKey["evidence-sample.two"] = (_, _, _) => Task.FromResult<EvidenceSection?>(new EvidenceSection("Bad\ntitle", []));
            using var client = host.As("viewer");

            var response = await client.GetAsync(Alerts(seed), Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain("InvalidOperationException", text);
            Assert.DoesNotContain("Bad", text);
            var sections = ByKey(JsonDocument.Parse(text).RootElement);
            Assert.Equal("failed", sections["evidence-sample.one"].GetProperty("state").GetString());
            Assert.Equal("invalid", sections["evidence-sample.two"].GetProperty("state").GetString());
            Assert.All(new[] { "evidence-sample.one", "evidence-sample.two" }, k =>
            {
                Assert.Equal(JsonValueKind.Null, sections[k].GetProperty("title").ValueKind);
                Assert.Equal(0, sections[k].GetProperty("items").GetArrayLength());
            });
        }

        [Fact]
        public async Task Denied_policy_section_omitted_no_hint()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);

            using var viewer = host.As("viewer");
            var viewerText = await (await viewer.GetAsync(Alerts(seed), Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("evidence-sample.admin", viewerText);
            Assert.DoesNotContain("admin", viewerText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, ByKey(JsonDocument.Parse(viewerText).RootElement).Count);
            Assert.DoesNotContain(EvidenceBehaviors.Seen, s => s.Key == "evidence-sample.admin");

            using var admin = host.As("admin");
            var adminSections = ByKey(await Json(await admin.GetAsync(Alerts(seed), Ct)));
            Assert.Equal("ok", adminSections["evidence-sample.admin"].GetProperty("state").GetString());
            Assert.Equal(5, adminSections.Count);
        }

        [Fact]
        public async Task IModuleMutations_in_contributor_throws()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            string? thrown = null;
            EvidenceBehaviors.ByKey["evidence-sample.one"] = (context, services, _) =>
            {
                try
                {
                    services.Mutations.Begin(HttpContexts.Bare(), context.TenantId, "evidence-sample.write", "note");
                    thrown = "no";
                }
                catch (InvalidOperationException)
                {
                    thrown = "yes";
                }
                return Task.FromResult<EvidenceSection?>(new EvidenceSection("Tried", []));
            };
            using var client = host.As("viewer");

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Alerts(seed), Ct)).StatusCode);

            Assert.Equal("yes", thrown);
            Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM audit_records WHERE operation LIKE 'evidence-sample.%'"));
        }

        [Fact]
        public async Task Core_changes_equals_related_changes()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            using var client = host.As("viewer");

            var related = await Json(await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/alerts/{seed.Alert}/related", Ct));
            var changes = related.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "change").ToList();
            var section = ByKey(await Json(await client.GetAsync(Alerts(seed), Ct)))["core.changes"];

            Assert.Equal(2, changes.Count);
            Assert.Equal("ok", section.GetProperty("state").GetString());
            Assert.Equal("core", section.GetProperty("module").GetString());
            var items = section.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(changes.Select(c => c.GetProperty("title").GetString()), items.Select(i => i.GetProperty("label").GetString()));
            Assert.Equal(changes.Select(c => c.GetProperty("at").GetDateTimeOffset()), items.Select(i => i.GetProperty("at").GetDateTimeOffset()));
            Assert.Equal(changes.Select(c => c.GetProperty("detail").GetString()), items.Select(i => i.GetProperty("value").GetString()));
            Assert.Equal(related.GetProperty("note").GetString(), section.GetProperty("note").GetString());
        }

        [Fact]
        public async Task Non_service_resource_empty()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            using var client = host.As("viewer");

            var hostBody = await Json(await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{seed.Host}/evidence-sections", Ct));
            Assert.Equal(0, hostBody.GetProperty("items").GetArrayLength());
            Assert.Empty(EvidenceBehaviors.Seen);

            var serviceBody = await Json(await client.GetAsync($"/api/v1/tenants/{seed.Tenant}/resources/{seed.Service}/evidence-sections", Ct));
            Assert.Equal(["evidence-sample.service"], ByKey(serviceBody).Keys);
        }

        [Fact]
        public async Task Parallel_requests_isolated()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await HostAsync(database);
            var seed = await SeedAsync(database);
            EvidenceBehaviors.ByKey["evidence-sample.one"] = async (context, _, ct) =>
            {
                await Task.Delay(300, ct);
                return new EvidenceSection($"For {context.SubjectId:N}", [new EvidenceItem("subject", context.SubjectId.ToString("N"))]);
            };
            using var first = host.As("viewer");
            using var second = host.As("viewer");

            var results = await Task.WhenAll(first.GetAsync(Alerts(seed), Ct), second.GetAsync(Alerts(seed, seed.SecondAlert), Ct));
            var (one, two) = (await results[0].Content.ReadAsStringAsync(Ct), await results[1].Content.ReadAsStringAsync(Ct));

            Assert.Contains(seed.Alert.ToString("N"), one);
            Assert.DoesNotContain(seed.SecondAlert.ToString("N"), one);
            Assert.Contains(seed.SecondAlert.ToString("N"), two);
            Assert.DoesNotContain(seed.Alert.ToString("N"), two);
            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }
    }
}
