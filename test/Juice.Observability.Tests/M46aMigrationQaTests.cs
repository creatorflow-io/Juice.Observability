using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// QA (M46a, waves §5.3): the mapping on the local stack's real legacy rows; report and apply twice over a copy of them with no change the
    /// second time (row versions untouched); every kind of conflict through the planner, with the rows in the way left as they were; the tenant
    /// guard, including another notifications tenant's alias rows.
    /// </summary>
    public class M46aMigrationQaTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly Guid OtherNotificationsTenant = Guid.Parse("0193f0bb-0000-7000-8000-0000000000b2");

        private sealed class Stack(ScratchDatabase database, IHost host, Guid tenantId) : IAsyncDisposable
        {
            public Guid TenantId => tenantId;
            public Guid Notifications => Guid.Parse(LocalStack.TenantKey);
            public ScratchDatabase Database => database;

            public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await work(scope.ServiceProvider);
            }

            public async Task<LegacySetup> LegacyAsync()
            {
                await using var db = database.Context();
                return await LegacySetup.ReadAsync(db, tenantId, Ct);
            }

            public Task<MigrationReport> ReportAsync(LegacySetup? legacy = null)
                => InScopeAsync(async sp => await sp.GetRequiredService<MigrationPlanner>().ReportAsync(legacy ?? await LegacyAsync(), Ct));

            public Task<MigrationReport> ApplyAsync() => InScopeAsync(async sp => await sp.GetRequiredService<MigrationPlanner>().ApplyAsync(await LegacyAsync(), Ct));

            public Task<T> SendAsync<T>(Juice.MediatR.IRequest<T> command) => InScopeAsync(async sp =>
            {
                using var context = NotificationsMessageContext.Begin("qa");
                return await sp.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
            });

            public async ValueTask DisposeAsync()
            {
                await host.StopAsync();
                host.Dispose();
                await database.DisposeAsync();
            }
        }

        private static async Task<Stack> StartAsync()
        {
            LocalStack.RequireAvailable();
            var database = await LocalStack.CreateDatabaseAsync();
            Guid tenantId;
            await using (var db = database.Context())
            {
                tenantId = (await db.Tenants.SingleAsync(Ct)).Id;
            }
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Database:Migrate"] = "true",
                ["Notifications:Stages:Run"] = "false",
                ["Notifications:Delivery:AliasSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                ["Notifications:Delivery:AliasSecretDirectory"] = "/run/secrets/juice",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            });
            builder.Services.AddJuiceNotificationsEmbedded(builder.Configuration.GetSection("Notifications"), o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.TenantId = Guid.Parse(LocalStack.TenantKey);
                o.ApplicationKey = "observability";
                o.ApplicationName = "Juice Observability";
            });
            builder.Services.AddChatProviders();
            builder.Services.Configure<NotificationsMigrationOptions>(o => (o.SmtpServer, o.SmtpFrom) = ("observability", "alerts@example.com"));
            builder.Services.AddScoped<MigrationPlanner>();
            var host = builder.Build();
            await host.StartAsync(Ct);
            return new Stack(database, host, tenantId);
        }

        private static WebhookEndpoint Endpoint(Guid tenantId, string key, ChannelKind kind = ChannelKind.Webhook, string[]? recipients = null) => new()
        {
            TenantId = tenantId, Key = key, Kind = kind,
            Url = kind == ChannelKind.Webhook ? $"https://receiver.example/hooks/{key}" : "",
            SecretRef = kind == ChannelKind.Email ? "" : "env:JUICE_WEBHOOK_QA",
            Events = [NotificationPlan.Firing, NotificationPlan.Resolved], Recipients = recipients ?? [], DefinitionHash = "h",
        };

        private static async Task SeedAsync(Stack stack, Action<PlatformDbContext> seed)
        {
            await using var db = stack.Database.Context();
            seed(db);
            await db.SaveChangesAsync(Ct);
        }

        // ─── The local stack's real legacy rows ────────

        private static async Task<(List<WebhookEndpoint> Endpoints, List<Data.EscalationPolicy> Policies, List<WebhookAllowlistEntry> Allowlist)> RealLegacyAsync()
        {
            LocalStack.RequireAvailable();
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, LocalStack.PostgresConnection("juice_observability"));
            await using var live = new PlatformDbContext(options.Options);
            // Read only: the running platform's own database.
            var tenant = await live.Tenants.AsNoTracking().Select(t => t.Id).FirstOrDefaultAsync(Ct); // the deployment's one tenant
            return (await live.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenant).OrderBy(e => e.Key).ToListAsync(Ct),
                await live.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == tenant).OrderBy(p => p.Key).ToListAsync(Ct),
                await live.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenant).ToListAsync(Ct));
        }

        [Fact]
        public async Task The_local_stacks_real_legacy_rows_map_and_map_back()
        {
            LocalStack.RequireAvailable();
            var (endpoints, policies, allowlist) = await RealLegacyAsync();
            Assert.SkipWhen(endpoints.Count == 0, "the local platform has no legacy endpoints");
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            var refused = new List<string>();
            foreach (var row in endpoints)
            {
                var legacy = LegacyEndpoint.From(row);
                var map = EndpointMapping.Map(legacy, EndpointMappingTests.Options);
                if (!map.Succeeded)
                {
                    refused.Add($"{row.Key}: {string.Join(",", map.Problems.Select(p => p.Code))}");
                    continue;
                }
                var back = EndpointMapping.Unmap(map.Value!);
                Assert.True(back.Succeeded, $"{row.Key}: {string.Join("; ", back.Problems.Select(p => p.Message))}");
                Assert.Equal(legacy, back.Value);
                mapped.Add(row.Key);
            }
            foreach (var row in policies)
            {
                var legacy = LegacyPolicy.From(row);
                var map = PolicyMapping.Map(legacy, mapped);
                if (!map.Succeeded)
                {
                    refused.Add($"policy {row.Key}: {string.Join(",", map.Problems.Select(p => p.Code))}");
                    continue;
                }
                Assert.Equal(legacy, PolicyMapping.Unmap(map.Value!).Value);
            }
            foreach (var row in allowlist)
            {
                var legacy = LegacyAllowlistEntry.From(row);
                var map = AllowlistMapping.Map(legacy);
                Assert.True(map.Succeeded, row.Destination);
                Assert.Equal(legacy with { Ports = legacy.Ports.Order().ToList() }, AllowlistMapping.Unmap(map.Value!));
            }
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m46a.txt"),
                $"{DateTime.UtcNow:O} real rows: {endpoints.Count} endpoints ({mapped.Count} mapped), {policies.Count} policies, {allowlist.Count} allowlist; refused: {string.Join(" | ", refused)}{Environment.NewLine}");
            Assert.NotEmpty(mapped);
        }

        /// <summary>
        /// The real endpoints and allowlist (without the policies, see below): report, apply, report and apply again, nothing changed.
        /// </summary>
        [Fact]
        public Task A_copy_of_the_real_endpoints_reports_applies_and_a_second_run_changes_nothing() => RealCopyAsync(withPolicies: false);

        /// <summary>
        /// With the real policies too. Before notifications M46-0 a step can't name a route: the report says each policy waits (pending_feature, QA
        /// defect D2), and the apply does what the report said.
        /// </summary>
        [Fact]
        public Task A_copy_of_the_real_rows_with_policies_reports_what_the_apply_does() => RealCopyAsync(withPolicies: true);

        private async Task RealCopyAsync(bool withPolicies)
        {
            LocalStack.RequireAvailable();
            var (endpoints, policies, allowlist) = await RealLegacyAsync();
            if (!withPolicies)
            {
                policies = [];
            }
            Assert.SkipWhen(endpoints.Count == 0, "the local platform has no legacy endpoints");
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                foreach (var e in endpoints)
                {
                    db.WebhookEndpoints.Add(new WebhookEndpoint
                    {
                        TenantId = stack.TenantId, Key = e.Key, Kind = e.Kind, Url = e.Url, SecretRef = e.SecretRef, Events = e.Events, RuleKeys = e.RuleKeys,
                        Environments = e.Environments, Recipients = e.Recipients, GroupWindowSeconds = e.GroupWindowSeconds, MinimumSeverity = e.MinimumSeverity,
                        Enabled = e.Enabled, DefinitionHash = e.DefinitionHash, ManagedBy = e.ManagedBy,
                    });
                }
                foreach (var p in policies)
                {
                    db.EscalationPolicies.Add(new Data.EscalationPolicy
                    {
                        TenantId = stack.TenantId, Key = p.Key, Name = p.Name, StopOnAcknowledge = p.StopOnAcknowledge, Enabled = p.Enabled, RuleKeys = p.RuleKeys,
                        Environments = p.Environments, MinimumSeverity = p.MinimumSeverity, Steps = [.. p.Steps.Select(s => new EscalationStep { AfterMinutes = s.AfterMinutes, EndpointKeys = s.EndpointKeys })],
                    });
                }
                foreach (var a in allowlist)
                {
                    db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = stack.TenantId, Destination = a.Destination, Ports = a.Ports });
                }
            });

            var report = await stack.ReportAsync();
            Assert.Equal(endpoints.Count + policies.Count + allowlist.Count, report.Items.Count);
            var first = await stack.ApplyAsync();
            Assert.True(first.Applied, string.Join("; ", first.Items.SelectMany(i => i.Problems).Select(p => p.Message)));
            // What the report said is what the apply did.
            Assert.Equal(report.Items.Select(i => (i.Key, i.Action)), first.Items.Select(i => (i.Key, i.Action)));
            var versions = await VersionsAsync(stack);

            var again = await stack.ReportAsync();
            Assert.All(again.Items, i => Assert.True(i.Action is MigrationAction.Unchanged or MigrationAction.Conflict or MigrationAction.PendingFeature, $"{i.Key}: {i.Action}"));
            Assert.Equal(first.Items.Where(i => i.Action is MigrationAction.Conflict or MigrationAction.PendingFeature).Select(i => i.Key),
                again.Items.Where(i => i.Action is MigrationAction.Conflict or MigrationAction.PendingFeature).Select(i => i.Key));
            var second = await stack.ApplyAsync();
            Assert.True(second.Applied);
            Assert.False(second.HasChanges);
            Assert.Equal(versions, await VersionsAsync(stack));

            // What was written reads back as the legacy endpoint.
            var legacy = await stack.LegacyAsync();
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                foreach (var key in first.Items.Where(i => i.Subject == MigrationSubject.Endpoint && i.Action == MigrationAction.Create).Select(i => i.Key))
                {
                    var setup = await MigrationPlanner.ReadSetupAsync(db, stack.Notifications, key, Ct);
                    Assert.NotNull(setup);
                    Assert.Equal(LegacyEndpoint.From(legacy.Endpoints.Single(e => e.Key == key)), EndpointMapping.Unmap(setup).Value);
                }
                return 0;
            });
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m46a.txt"),
                $"{DateTime.UtcNow:O} real copy: {string.Join(", ", first.Items.Select(i => $"{i.Key}={i.Action}{(i.Problems.Count > 0 ? "(" + string.Join(",", i.Problems.Select(p => p.Code)) + ")" : "")}"))}{Environment.NewLine}");
        }

        /// <summary>Every alias-reachable row's xmin, by table and key: a no-op apply rewrites none.</summary>
        private static async Task<List<string>> VersionsAsync(Stack stack)
        {
            await using var connection = new NpgsqlConnection(stack.Database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("""
                SELECT 'i:' || key || ':' || xmin FROM notifications.integrations UNION ALL
                SELECT 't:' || key || ':' || xmin FROM notifications.integration_targets UNION ALL
                SELECT 'r:' || key || ':' || xmin FROM notifications.routes UNION ALL
                SELECT 'p:' || key || ':' || xmin FROM notifications.escalation_policies UNION ALL
                SELECT 'a:' || destination || ':' || xmin FROM notifications.destination_allowlist
                ORDER BY 1
                """, connection);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }
            return rows;
        }

        // ─── Templates against what legacy really sent ────────

        /// <summary>
        /// Every firing and resolved body the local platform actually sent (webhook_deliveries.payload, step 0), rendered by the ported webhook
        /// template from what the adapter will send (the payload without its id): the same bytes but the id, which M46-0's delivery.id fills.
        /// </summary>
        [Fact]
        public async Task The_webhook_template_renders_every_body_legacy_really_sent()
        {
            LocalStack.RequireAvailable();
            var payloads = new List<string>();
            await using (var connection = new NpgsqlConnection(LocalStack.PostgresConnection("juice_observability")))
            {
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand("""
                    SELECT DISTINCT payload FROM webhook_deliveries
                    WHERE escalation_step = 0 AND action_request_id IS NULL AND event IN ('alert.firing', 'alert.resolved') AND payload IS NOT NULL
                    """, connection);
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    payloads.Add(reader.GetString(0));
                }
            }
            Assert.SkipWhen(payloads.Count == 0, "the local platform has sent nothing");
            await TemplateWarmUp.EnsureAsync();
            var mismatches = new List<string>();
            var older = 0;
            foreach (var payload in payloads)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
                var id = node["id"]!.GetValue<string>();
                node.Remove("id");
                var body = await Juice.Notifications.Rendering.TemplateRenderer.RenderAsync(DefaultTemplates.Webhook,
                    new Juice.Notifications.Rendering.RenderModel(AliasVocabulary.FiringType, 1, "Alert", Juice.Notifications.NotificationPriority.High,
                        JsonSerializer.SerializeToElement(node), new Dictionary<string, string>(), new Dictionary<string, string>()), Ct);
                var expected = payload.Replace($"\"id\":\"{id}\",\"type\"", "\"id\":null,\"type\"", StringComparison.Ordinal);
                // Bodies sent before the SLO fields (Phase 9) have no "slo": legacy today writes "slo":null there, as the template does.
                if (!node["alert"]!.AsObject().ContainsKey("slo"))
                {
                    older++;
                    body = body.Replace(",\"slo\":null}", "}", StringComparison.Ordinal);
                }
                if (body != expected)
                {
                    mismatches.Add($"expected {expected}\n  actual {body}");
                }
            }
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m46a.txt"),
                $"{DateTime.UtcNow:O} real bodies: {payloads.Count} distinct, {older} from before the slo field, {mismatches.Count} differ{Environment.NewLine}{string.Join(Environment.NewLine, mismatches.Take(3))}{Environment.NewLine}");
            Assert.Empty(mismatches);
        }

        // ─── Conflicts ────────

        [Fact]
        public async Task Digit_led_keys_email_suffix_collisions_and_long_suffixes_are_conflicts_and_the_rest_applies()
        {
            await using var stack = await StartAsync();
            var longKey = "m" + new string('a', 61); // 62 characters: its "-2" target would be 64
            await SeedAsync(stack, db => db.WebhookEndpoints.AddRange(
                Endpoint(stack.TenantId, "1ops"),
                Endpoint(stack.TenantId, "oncall", ChannelKind.Email, ["a@example.com", "b@example.com"]),
                Endpoint(stack.TenantId, "oncall-2"),
                Endpoint(stack.TenantId, longKey, ChannelKind.Email, ["a@example.com", "b@example.com"]),
                Endpoint(stack.TenantId, "fine")));

            var report = await stack.ApplyAsync();

            Assert.True(report.Applied);
            Assert.Contains(ProblemCodes.KeyStartsWithDigit, Item(report, "1ops").Problems.Select(p => p.Code));
            Assert.Equal(MigrationAction.Conflict, Item(report, "1ops").Action);
            Assert.Equal((MigrationAction.Conflict, ProblemCodes.KeyTaken), (Item(report, "oncall").Action, Item(report, "oncall").Problems.Single().Code));
            Assert.Equal((MigrationAction.Conflict, ProblemCodes.KeyTaken), (Item(report, "oncall-2").Action, Item(report, "oncall-2").Problems.Single().Code));
            Assert.Equal(MigrationAction.Conflict, Item(report, longKey).Action);
            Assert.Equal(MigrationAction.Create, Item(report, "fine").Action);
            var keys = await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().IntegrationTargets.Where(t => t.TenantId == stack.Notifications).Select(t => t.Key).ToListAsync(Ct));
            Assert.Equal(["fine"], keys);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m46a.txt"),
                $"{DateTime.UtcNow:O} long key problems: {string.Join(",", Item(report, longKey).Problems.Select(p => p.Code + ": " + p.Message))}{Environment.NewLine}");
        }

        [Fact]
        public async Task Foreign_rows_in_the_way_are_conflicts_and_stay_as_they_were()
        {
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                db.WebhookEndpoints.AddRange(Endpoint(stack.TenantId, "routed"), Endpoint(stack.TenantId, "targeted"), Endpoint(stack.TenantId, "fine"));
                db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = stack.TenantId, Destination = "10.30.0.0/16", Ports = [8443] });
                db.EscalationPolicies.Add(new Data.EscalationPolicy { TenantId = stack.TenantId, Key = "paging", Name = "Paging", Steps = [new() { AfterMinutes = 5, EndpointKeys = ["fine"] }] });
            });
            // An admin's rows through the notifications API: a route "routed", a target "targeted" under its own integration, the same allowlist
            // destination and a policy "paging".
            Assert.NotNull((await stack.SendAsync(new PutIntegration(stack.Notifications, "admins", "webhook", "webhook", JsonDocument.Parse("{}").RootElement,
                "env:NOTIFICATIONS_SECRET_ADMIN", 0, true))).Value);
            Assert.NotNull((await stack.SendAsync(new PutTarget(stack.Notifications, "admins", "targeted", "https://admin.example/hook", null, true))).Value);
            Assert.NotNull((await stack.SendAsync(new PutTarget(stack.Notifications, "admins", "admin-target", "https://admin.example/other", null, true))).Value);
            Assert.NotNull((await stack.SendAsync(new PutRoute(stack.Notifications, "routed", JsonDocument.Parse("{}").RootElement, ["admin-target"], true))).Value);
            Assert.NotNull((await stack.SendAsync(new PutAllowlistEntry(stack.Notifications, "10.30.0.0/16", [9443]))).Value);
            // The admin's policy, as the API stores it (written directly: its recipients' kinds don't matter to the key check).
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                db.EscalationPolicies.Add(new Juice.Notifications.Data.EscalationPolicy
                {
                    TenantId = stack.Notifications, Key = "paging", Steps = """[{"afterMinutes":1,"recipients":[]}]""", StopOnAcknowledge = true,
                    ManagedBy = "api", DefinitionHash = "qa", UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
                return 0;
            });
            var before = await VersionsAsync(stack);

            var report = await stack.ApplyAsync();

            Assert.True(report.Applied, string.Join("; ", report.Items.Select(i => $"{i.Key}={i.Action} {string.Join(",", i.Problems.Select(p => p.Code + ": " + p.Message))}")));
            foreach (var key in new[] { "routed", "targeted", "10.30.0.0/16", "paging" })
            {
                Assert.Equal((MigrationAction.Conflict, ProblemCodes.KeyTaken), (Item(report, key).Action, Item(report, key).Problems.Single().Code));
            }
            Assert.Equal(MigrationAction.Create, Item(report, "fine").Action);
            // The admin's rows are as they were: only the endpoint that applied added rows.
            var after = await VersionsAsync(stack);
            Assert.All(before, row => Assert.Contains(row, after));
            Assert.All(after.Except(before), row => Assert.Contains(":fine:", row));
        }

        // ─── The tenant guard ────────

        [Fact]
        public async Task The_tenant_guard_refuses_another_key_and_another_notifications_tenants_rows_are_never_read_or_touched()
        {
            await using var stack = await StartAsync();
            await SeedAsync(stack, db => db.WebhookEndpoints.Add(Endpoint(stack.TenantId, "ops")));
            var legacy = await stack.LegacyAsync();
            // The platform row with another key, and the right key in another form: both refused.
            foreach (var key in new[] { "0193f0aa-0000-7000-8000-0000000000a2", LocalStack.TenantKey.ToUpperInvariant(), "", "{" + LocalStack.TenantKey + "}" })
            {
                await Assert.ThrowsAsync<ArgumentException>(() => stack.ReportAsync(legacy with { TenantKey = key }));
            }

            // Alias rows with the same keys in another notifications tenant.
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.destination_allowlist (tenant_id, destination, ports, managed_by, updated_at)
                    VALUES ({OtherNotificationsTenant}, '10.40.0.0/16', ARRAY[443], 'webhooks-alias', now())
                    """, Ct);
                return 0;
            });
            var report = await stack.ApplyAsync();
            Assert.Equal(MigrationAction.Create, Item(report, "ops").Action);
            var other = await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Allowlist.AsNoTracking()
                .Where(a => a.TenantId == OtherNotificationsTenant).Select(a => a.Destination).ToListAsync(Ct));
            Assert.Equal(["10.40.0.0/16"], other);
            Assert.All(await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Integrations.AsNoTracking()
                .Where(i => i.ManagedBy == AliasVocabulary.WebhooksAlias).Select(i => i.TenantId).ToListAsync(Ct)), t => Assert.Equal(stack.Notifications, t));
        }

        private static MigrationItem Item(MigrationReport report, string key) => Assert.Single(report.Items, i => i.Key == key);
    }
}
