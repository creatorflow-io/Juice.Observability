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

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The migration tool's core (phase-5-plan I70, waves §5.3 M46a) against a real notifications schema, without the platform host: notifications
    /// embedded on a scratch database (its stages off), the legacy rows in the same database's platform schema. Proof point 5's tool half: report,
    /// apply, report again unchanged; conflicts reported and never overwritten; a failure part-way leaves nothing applied.
    /// </summary>
    public class NotificationsMigrationTests
    {
        private sealed class Stack(ScratchDatabase database, IHost host, Guid tenantId) : IAsyncDisposable
        {
            public Guid TenantId => tenantId;

            /// <summary>The notifications tenant: the platform tenant's key, which can differ from its row's Id.</summary>
            public Guid Notifications => Guid.Parse(LocalStack.TenantKey);

            public ScratchDatabase Database => database;

            public IServiceProvider Services => host.Services;

            public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await work(scope.ServiceProvider);
            }

            public async Task<LegacySetup> LegacyAsync()
            {
                await using var db = database.Context();
                return await LegacySetup.ReadAsync(db, tenantId, TestContext.Current.CancellationToken);
            }

            public async Task<MigrationReport> ReportAsync() => await InScopeAsync(async sp => await sp.GetRequiredService<MigrationPlanner>().ReportAsync(await LegacyAsync(), TestContext.Current.CancellationToken));

            public async Task<MigrationReport> ApplyAsync(int? failAfter = null) => await InScopeAsync(async sp =>
            {
                var planner = sp.GetRequiredService<MigrationPlanner>();
                planner.FailAfterWrites = failAfter;
                return await planner.ApplyAsync(await LegacyAsync(), TestContext.Current.CancellationToken);
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
                tenantId = (await db.Tenants.SingleAsync()).Id;
            }
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Database:Migrate"] = "true",
                ["Notifications:Stages:Run"] = "false",
                ["Notifications:Delivery:ApiSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                ["Notifications:Delivery:ApiSecretDirectory"] = "/run/secrets/juice",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            });
            builder.Services.AddJuiceNotificationsEmbedded(builder.Configuration.GetSection("Notifications"), o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.TenantId = Guid.Parse(LocalStack.TenantKey); // the notifications tenant is the platform tenant's key, its Id
                o.ApplicationKey = "observability";
                o.ApplicationName = "Juice Observability";
            });
            builder.Services.AddChatProviders();
            builder.Services.Configure<NotificationsMigrationOptions>(o => (o.SmtpServer, o.SmtpFrom) = ("observability", "alerts@example.com"));
            builder.Services.AddScoped<MigrationPlanner>();
            var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            return new Stack(database, host, tenantId);
        }


        private static WebhookEndpoint Endpoint(Guid tenantId, string key, ChannelKind kind = ChannelKind.Webhook, string[]? recipients = null,
            AlertSeverity minimum = AlertSeverity.Info, string[]? rules = null, string? secret = null) => new()
            {
                TenantId = tenantId, Key = key, Kind = kind,
                Url = kind == ChannelKind.Webhook ? $"https://receiver.example/hooks/{key}" : "",
                SecretRef = secret ?? (kind == ChannelKind.Email ? "" : $"env:JUICE_WEBHOOK_{key.ToUpperInvariant().Replace('-', '_')}"),
                Events = [NotificationPlan.Firing, NotificationPlan.Resolved], RuleKeys = rules ?? [], Recipients = recipients ?? [],
                MinimumSeverity = minimum, DefinitionHash = "h",
            };

        private static async Task SeedAsync(Stack stack, Action<PlatformDbContext> seed)
        {
            await using var db = stack.Database.Context();
            seed(db);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>A legacy setup of every channel, with an allowlist entry.</summary>
        private static Task SeedEveryChannelAsync(Stack stack) => SeedAsync(stack, db =>
        {
            db.WebhookEndpoints.AddRange(
                Endpoint(stack.TenantId, "ops", minimum: AlertSeverity.Warning, rules: ["orders-errors"]),
                Endpoint(stack.TenantId, "sre-slack", ChannelKind.Slack),
                Endpoint(stack.TenantId, "sre-teams", ChannelKind.Teams, secret: "file:/run/secrets/juice/teams"),
                Endpoint(stack.TenantId, "oncall-mail", ChannelKind.Email, recipients: ["oncall@example.com", "lead@example.com"]));
            db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = stack.TenantId, Destination = "10.20.0.0/16", Ports = [8443] });
        });

        private static void All(MigrationReport report, MigrationAction action)
            => Assert.All(report.Items, i => Assert.True(i.Action == action, $"{i.Subject} {i.Key}: {i.Action} {string.Join("; ", i.Problems.Select(p => p.Message))}"));

        [Fact]
        public async Task Report_then_apply_then_report_again_finds_everything_unchanged()
        {
            await using var stack = await StartAsync();
            await SeedEveryChannelAsync(stack);

            var report = await stack.ReportAsync();
            Assert.Equal(5, report.Items.Count);
            All(report, MigrationAction.Create);
            Assert.False(report.Applied);
            // A report writes nothing.
            Assert.Equal(0, await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Integrations.CountAsync(i => i.TenantId == stack.Notifications)));

            var applied = await stack.ApplyAsync();
            Assert.True(applied.Applied);
            All(applied, MigrationAction.Create);

            All(await stack.ReportAsync(), MigrationAction.Unchanged);
            var again = await stack.ApplyAsync();
            Assert.True(again.Applied);
            All(again, MigrationAction.Unchanged);
        }

        [Fact]
        public async Task What_was_applied_maps_back_to_every_legacy_endpoint_and_is_the_aliases()
        {
            await using var stack = await StartAsync();
            await SeedEveryChannelAsync(stack);
            await stack.ApplyAsync();

            var legacy = await stack.LegacyAsync();
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                foreach (var endpoint in legacy.Endpoints)
                {
                    var setup = await MigrationPlanner.ReadSetupAsync(db, stack.Notifications, endpoint.Key, TestContext.Current.CancellationToken);
                    Assert.NotNull(setup);
                    Assert.Equal(LegacyEndpoint.From(endpoint), EndpointMapping.Unmap(setup).Value);
                }
                Assert.All(await db.Integrations.Where(i => i.TenantId == stack.Notifications && i.Channel != "inbox").ToListAsync(), i => Assert.Equal(AliasVocabulary.WebhooksAlias, i.ManagedBy));
                Assert.All(await db.IntegrationTargets.Where(t => t.TenantId == stack.Notifications).ToListAsync(), t => Assert.Equal(AliasVocabulary.WebhooksAlias, t.ManagedBy));
                Assert.All(await db.Routes.Where(r => r.TenantId == stack.Notifications).ToListAsync(), r => Assert.Equal(AliasVocabulary.WebhooksAlias, r.ManagedBy));
                var entry = await db.Allowlist.SingleAsync(a => a.TenantId == stack.Notifications);
                Assert.Equal(("10.20.0.0/16", "8443", AliasVocabulary.WebhooksAlias), (entry.Destination, string.Join(',', entry.Ports), entry.ManagedBy));
                return 0;
            });
        }

        [Fact]
        public async Task A_legacy_change_is_an_update_and_a_recipient_taken_out_loses_its_target()
        {
            await using var stack = await StartAsync();
            await SeedEveryChannelAsync(stack);
            await stack.ApplyAsync();
            await using (var db = stack.Database.Context())
            {
                var mail = await db.WebhookEndpoints.SingleAsync(e => e.Key == "oncall-mail");
                mail.Recipients = ["oncall@example.com"];
                (await db.WebhookEndpoints.SingleAsync(e => e.Key == "ops")).Enabled = false;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var report = await stack.ApplyAsync();

            Assert.Equal([("oncall-mail", MigrationAction.Update), ("ops", MigrationAction.Update)],
                report.Items.Where(i => i.Action != MigrationAction.Unchanged).Select(i => (i.Key, i.Action)).Order());
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                Assert.False(await db.IntegrationTargets.AnyAsync(t => t.TenantId == stack.Notifications && t.Key == "oncall-mail-2"));
                Assert.False((await db.Routes.SingleAsync(r => r.TenantId == stack.Notifications && r.Key == "ops")).Enabled);
                return 0;
            });
            All(await stack.ReportAsync(), MigrationAction.Unchanged);
        }

        [Fact]
        public async Task A_key_an_api_row_holds_is_reported_and_not_overwritten()
        {
            await using var stack = await StartAsync();
            await SeedEveryChannelAsync(stack);
            // An admin made an integration "ops" through the notifications API.
            var made = await stack.InScopeAsync(async sp =>
            {
                using var context = NotificationsMessageContext.Begin("test");
                return await sp.GetRequiredService<Juice.MediatR.IMediator>().Send(new PutIntegration(stack.Notifications, "ops", "webhook", "webhook",
                    JsonDocument.Parse("{}").RootElement, "env:JUICE_WEBHOOK_MINE", 0, true), TestContext.Current.CancellationToken);
            });
            Assert.NotNull(made.Value);

            var report = await stack.ApplyAsync();

            var ops = Assert.Single(report.Items, i => i.Key == "ops");
            Assert.Equal(MigrationAction.Conflict, ops.Action);
            Assert.Equal(ProblemCodes.KeyTaken, Assert.Single(ops.Problems).Code);
            Assert.True(report.Applied);
            Assert.All(report.Items.Where(i => i.Key != "ops"), i => Assert.Equal(MigrationAction.Create, i.Action));
            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                var integration = await db.Integrations.SingleAsync(i => i.TenantId == stack.Notifications && i.Key == "ops");
                Assert.Equal(("api", "env:JUICE_WEBHOOK_MINE"), (integration.ManagedBy, integration.SecretRef));
                Assert.False(await db.Routes.AnyAsync(r => r.TenantId == stack.Notifications && r.Key == "ops"));
                return 0;
            });
        }

        [Fact]
        public async Task A_failure_part_way_leaves_nothing_applied()
        {
            await using var stack = await StartAsync();
            await SeedEveryChannelAsync(stack);

            await Assert.ThrowsAsync<InvalidOperationException>(() => stack.ApplyAsync(failAfter: 3));

            await stack.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<NotificationsDbContext>();
                Assert.False(await db.Integrations.AnyAsync(i => i.TenantId == stack.Notifications && i.ManagedBy == AliasVocabulary.WebhooksAlias));
                Assert.False(await db.Allowlist.AnyAsync(a => a.TenantId == stack.Notifications));
                return 0;
            });
            All(await stack.ReportAsync(), MigrationAction.Create);
            All(await stack.ApplyAsync(), MigrationAction.Create);
        }

        [Fact]
        public async Task A_group_window_waits_for_route_digests_and_the_rest_applies()
        {
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                var grouped = Endpoint(stack.TenantId, "grouped");
                grouped.GroupWindowSeconds = 60;
                db.WebhookEndpoints.AddRange(grouped, Endpoint(stack.TenantId, "ops"));
            });

            var report = await stack.ApplyAsync();

            Assert.True(report.Applied);
            Assert.False(report.HasConflicts);
            var item = Assert.Single(report.Items, i => i.Key == "grouped");
            Assert.Equal((MigrationAction.PendingFeature, ProblemCodes.PendingFeature), (item.Action, Assert.Single(item.Problems).Code));
            Assert.Equal(MigrationAction.Create, Assert.Single(report.Items, i => i.Key == "ops").Action);
            Assert.False(await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Integrations.AnyAsync(i => i.TenantId == stack.Notifications && i.Key == "grouped")));
            Assert.Equal(MigrationAction.PendingFeature, Assert.Single((await stack.ReportAsync()).Items, i => i.Key == "grouped").Action);
        }

        [Fact]
        public async Task Overlapping_policies_and_steps_out_of_order_are_reported()
        {
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                db.WebhookEndpoints.Add(Endpoint(stack.TenantId, "ops"));
                db.EscalationPolicies.AddRange(
                    new Data.EscalationPolicy { TenantId = stack.TenantId, Key = "paging", Name = "Paging", Steps = [new() { AfterMinutes = 5, EndpointKeys = ["ops"] }] },
                    new Data.EscalationPolicy { TenantId = stack.TenantId, Key = "also-paging", Name = "Also", RuleKeys = ["orders-errors"], Steps = [new() { AfterMinutes = 5, EndpointKeys = ["ops"] }] },
                    new Data.EscalationPolicy { TenantId = stack.TenantId, Key = "backwards", Name = "Backwards", Enabled = false,
                        Steps = [new() { AfterMinutes = 30, EndpointKeys = ["ops"] }, new() { AfterMinutes = 10, EndpointKeys = ["ops"] }] });
            });

            var report = await stack.ReportAsync();

            Assert.Contains(Assert.Single(report.Items, i => i.Key == "paging").Problems, p => p.Code == ProblemCodes.OverlappingPolicies);
            Assert.Contains(Assert.Single(report.Items, i => i.Key == "also-paging").Problems, p => p.Code == ProblemCodes.OverlappingPolicies);
            Assert.Contains(Assert.Single(report.Items, i => i.Key == "backwards").Problems, p => p.Code == ProblemCodes.NonIncreasingSteps);
            Assert.Equal(MigrationAction.Create, Assert.Single(report.Items, i => i.Key == "ops").Action);
        }

        /// <summary>Whether the notifications alpha takes a route recipient by key in a step (M46-0); policies can't be applied before.</summary>
        private static bool StepsNameRoutes()
        {
            using var steps = JsonDocument.Parse("""[{"afterMinutes":5,"recipients":[{"kind":"route","key":"ops"}]}]""");
            return EscalationRules.Parse(steps.RootElement, []).Errors.Count == 0;
        }

        [Fact]
        public async Task A_policy_is_applied_with_relative_steps_naming_routes()
        {
            Assert.SkipUnless(StepsNameRoutes(), "needs M46-0: an escalation step naming a route by its key");
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                db.WebhookEndpoints.AddRange(Endpoint(stack.TenantId, "ops"), Endpoint(stack.TenantId, "sre"));
                db.EscalationPolicies.Add(new Data.EscalationPolicy
                {
                    TenantId = stack.TenantId, Key = "paging", Name = "Paging", StopOnAcknowledge = false,
                    Steps = [new() { AfterMinutes = 5, EndpointKeys = ["ops"] }, new() { AfterMinutes = 20, EndpointKeys = ["ops", "sre"] }],
                });
            });

            All(await stack.ApplyAsync(), MigrationAction.Create);
            All(await stack.ReportAsync(), MigrationAction.Unchanged);
            var legacy = await stack.LegacyAsync();
            await stack.InScopeAsync(async sp =>
            {
                var row = await sp.GetRequiredService<NotificationsDbContext>().EscalationPolicies.SingleAsync(p => p.TenantId == stack.Notifications && p.Key == "paging");
                Assert.Equal(AliasVocabulary.WebhooksAlias, row.ManagedBy);
                Assert.Equal(LegacyPolicy.From(legacy.Policies[0]), PolicyMapping.Unmap(new PolicyDefinition(row.Key, row.Steps, row.StopOnAcknowledge)).Value);
                return 0;
            });
        }

        [Fact]
        public async Task A_policy_the_notifications_alpha_refuses_rolls_the_whole_apply_back()
        {
            // Before M46-0 a step can't name a route by key: notifications refuses the policy, and the endpoints written before it go too.
            Assert.SkipWhen(StepsNameRoutes(), "M46-0 is in: the policy is taken (A_policy_is_applied_with_relative_steps_naming_routes)");
            await using var stack = await StartAsync();
            await SeedAsync(stack, db =>
            {
                db.WebhookEndpoints.Add(Endpoint(stack.TenantId, "ops"));
                db.EscalationPolicies.Add(new Data.EscalationPolicy { TenantId = stack.TenantId, Key = "paging", Name = "Paging", Steps = [new() { AfterMinutes = 5, EndpointKeys = ["ops"] }] });
            });

            var report = await stack.ApplyAsync();

            Assert.False(report.Applied);
            Assert.Equal(MigrationAction.Conflict, Assert.Single(report.Items, i => i.Key == "paging").Action);
            Assert.False(await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Integrations.AnyAsync(i => i.TenantId == stack.Notifications && i.Key == "ops")));
        }

        [Fact]
        public async Task Another_tenant_is_refused()
        {
            await using var stack = await StartAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => stack.InScopeAsync(sp =>
                sp.GetRequiredService<MigrationPlanner>().ReportAsync(new LegacySetup(Guid.NewGuid(), "another-tenant", [], [], []), TestContext.Current.CancellationToken)));
        }
    }
}
