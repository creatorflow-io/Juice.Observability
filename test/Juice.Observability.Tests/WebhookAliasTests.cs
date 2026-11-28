using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Notifications;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The webhooks alias at the service level (phase-5-plan I69, M46b; decisions on Q-A to Q-D), in each pipeline mode: in <c>legacy</c> and
    /// <c>shadow</c> the legacy row and the notifications rows are written in one platform transaction, so both commit or neither does; in
    /// <c>new</c> only notifications is written. Reads come from notifications, by the integration's id, which keeps the legacy endpoint's.
    /// (The API in shadow and new waits for M45-1, which lets the host run those modes.)
    /// </summary>
    public sealed class WebhookAliasTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class Stack(ScratchDatabase database, IHost host, Guid platformTenant) : IAsyncDisposable
        {
            public Guid PlatformTenant => platformTenant;

            public Guid Notifications => Guid.Parse(LocalStack.TenantKey);

            public ScratchDatabase Database => database;

            /// <summary>Work in one platform transaction, as an alias request runs: committed unless it returns false or throws.</summary>
            public async Task<T> InPlatformTransactionAsync<T>(Func<PlatformDbContext, WebhookAlias, Task<T>> work)
            {
                await using var scope = host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var result = await work(db, scope.ServiceProvider.GetRequiredService<WebhookAlias>());
                if (result is false)
                {
                    await transaction.RollbackAsync(Ct);
                    return result;
                }
                await transaction.CommitAsync(Ct);
                return result;
            }

            public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await work(scope.ServiceProvider);
            }

            public async ValueTask DisposeAsync()
            {
                await host.StopAsync();
                host.Dispose();
                await database.DisposeAsync();
            }
        }

        private static async Task<Stack> StartAsync(NotificationsPipeline mode)
        {
            LocalStack.RequireAvailable();
            var database = await LocalStack.CreateDatabaseAsync();
            Guid tenant;
            await using (var db = database.Context())
            {
                tenant = (await db.Tenants.SingleAsync()).Id;
            }
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Database:Migrate"] = "true",
                ["Notifications:Stages:Run"] = "false",
                ["Notifications:Delivery:AliasSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
            });
            builder.Services.AddDbContext<PlatformDbContext>((sp, o) =>
            {
                PlatformDbContext.Configure(o, database.ConnectionString);
                o.AddInterceptors(sp.GetRequiredService<PlatformCommitActions>());
            });
            builder.Services.AddSingleton<PlatformCommitActions>();
            builder.Services.AddScoped<Juice.Notifications.INotificationTransaction, PlatformNotificationTransaction>();
            builder.Services.AddJuiceNotificationsEmbedded(builder.Configuration.GetSection("Notifications"), o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.TenantId = Guid.Parse(LocalStack.TenantKey);
                o.ApplicationKey = "observability";
                o.ApplicationName = "Juice Observability";
            });
            builder.Services.AddChatProviders();
            builder.Services.Configure<PlatformNotificationsOptions>(o => o.Pipeline = mode);
            builder.Services.Configure<WebhookOptions>(_ => { });
            builder.Services.Configure<NotificationsMigrationOptions>(o => (o.SmtpServer, o.SmtpFrom) = ("observability", "alerts@example.com"));
            builder.Services.AddScoped<MigrationPlanner>();
            builder.Services.AddScoped<WebhookAlias>();
            var host = builder.Build();
            await host.StartAsync(Ct);
            return new Stack(database, host, tenant);
        }

        private static LegacyEndpoint Definition(string key, string? url = null, ChannelKind kind = ChannelKind.Webhook, string[]? recipients = null)
            => EndpointMappingTests.Endpoint(key, kind, url: url, recipients: recipients);

        /// <summary>The legacy row a legacy write adds, as the webhooks API makes it.</summary>
        private static WebhookEndpoint Legacy(Guid tenant, LegacyEndpoint d) => new()
        {
            TenantId = tenant, Key = d.Key, Kind = d.Kind, Url = d.Url, SecretRef = d.SecretRef, Events = [.. d.Events], RuleKeys = [.. d.RuleKeys],
            Environments = [.. d.Environments], Recipients = [.. d.Recipients], GroupWindowSeconds = d.GroupWindowSeconds, MinimumSeverity = d.MinimumSeverity,
            Enabled = d.Enabled, DefinitionHash = "h", ManagedBy = ManagedBy.Api,
        };

        [Theory]
        [InlineData(NotificationsPipeline.Legacy)]
        [InlineData(NotificationsPipeline.Shadow)]
        public async Task Before_cutover_a_create_writes_both_sides_in_one_transaction_with_one_id(NotificationsPipeline mode)
        {
            await using var stack = await StartAsync(mode);
            var definition = Definition("ops");
            var id = await stack.InPlatformTransactionAsync(async (db, alias) =>
            {
                Assert.True(alias.WritesLegacy);
                var row = Legacy(stack.PlatformTenant, definition);
                db.WebhookEndpoints.Add(row);
                await db.SaveChangesAsync(Ct);
                Assert.Empty(await alias.PutAsync(definition, row.Id, Ct));
                return row.Id;
            });

            await stack.InScopeAsync(async sp =>
            {
                var found = await sp.GetRequiredService<WebhookAlias>().FindAsync(id, null, Ct);
                Assert.Equal((id, definition), (found!.Id, found.Definition));
                return 0;
            });
            await using var platform = stack.Database.Context();
            Assert.Equal(id, (await platform.WebhookEndpoints.SingleAsync(e => e.Key == "ops", Ct)).Id);
        }

        [Fact]
        public async Task In_shadow_a_notifications_refusal_leaves_neither_side()
        {
            await using var stack = await StartAsync(NotificationsPipeline.Shadow);
            // An integration the notifications API made holds the key: the alias's write is refused.
            await stack.InScopeAsync(async sp =>
            {
                using var context = NotificationsMessageContext.Begin("test");
                return await sp.GetRequiredService<Juice.MediatR.IMediator>().Send(new PutIntegration(stack.Notifications, "ops", "webhook", "webhook",
                    System.Text.Json.JsonDocument.Parse("{}").RootElement, "env:NOTIFICATIONS_SECRET_MINE", 0, true), Ct);
            });

            var refused = await stack.InPlatformTransactionAsync(async (db, alias) =>
            {
                var definition = Definition("ops");
                var row = Legacy(stack.PlatformTenant, definition);
                db.WebhookEndpoints.Add(row);
                await db.SaveChangesAsync(Ct);
                var problems = await alias.PutAsync(definition, row.Id, Ct);
                return problems.Count == 0;
            });

            Assert.False(refused);
            await using var platform = stack.Database.Context();
            Assert.False(await platform.WebhookEndpoints.AnyAsync(e => e.Key == "ops", Ct));
            await stack.InScopeAsync(async sp =>
            {
                var integration = await sp.GetRequiredService<NotificationsDbContext>().Integrations.AsNoTracking().SingleAsync(i => i.Key == "ops", Ct);
                Assert.Equal(Juice.Notifications.ManagedBy.Api, integration.ManagedBy);
                Assert.False(await sp.GetRequiredService<NotificationsDbContext>().Routes.AnyAsync(r => r.Key == "ops", Ct));
                return 0;
            });
        }

        [Fact]
        public async Task In_new_only_notifications_is_written_and_a_change_keeps_the_id_and_drops_a_target()
        {
            await using var stack = await StartAsync(NotificationsPipeline.New);
            var id = Guid.CreateVersion7();
            var mail = Definition("mail", kind: ChannelKind.Email, recipients: ["oncall@example.com", "lead@example.com"]);
            await stack.InPlatformTransactionAsync(async (_, alias) =>
            {
                Assert.False(alias.WritesLegacy);
                Assert.Empty(await alias.PutAsync(mail, id, Ct));
                return true;
            });
            var changed = mail with { Recipients = ["oncall@example.com"], Enabled = false };
            await stack.InPlatformTransactionAsync(async (_, alias) =>
            {
                Assert.Empty(await alias.PutAsync(changed, id, Ct)); // a change names the endpoint's id: an integration's id never changes
                return true;
            });

            await stack.InScopeAsync(async sp =>
            {
                var alias = sp.GetRequiredService<WebhookAlias>();
                var found = Assert.Single(await alias.ListAsync(null, Ct));
                Assert.Equal((id, changed, ManagedBy.Api), (found.Id, found.Definition, found.ManagedBy));
                Assert.False(await sp.GetRequiredService<NotificationsDbContext>().IntegrationTargets.AnyAsync(t => t.Key == "mail-2", Ct));
                return 0;
            });
            await using var platform = stack.Database.Context();
            Assert.False(await platform.WebhookEndpoints.AnyAsync(Ct));
        }

        [Fact]
        public async Task What_the_mapping_refuses_comes_back_and_nothing_is_written()
        {
            await using var stack = await StartAsync(NotificationsPipeline.New);
            var problems = await stack.InPlatformTransactionAsync(async (_, alias) => await alias.PutAsync(Definition("1-ops"), Guid.CreateVersion7(), Ct));
            Assert.Equal(ProblemCodes.KeyStartsWithDigit, Assert.Single(problems).Code);
            Assert.False(await stack.InScopeAsync(sp => sp.GetRequiredService<NotificationsDbContext>().Integrations.AnyAsync(i => i.Key == "1-ops", Ct)));
        }
    }
}
