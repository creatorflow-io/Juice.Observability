using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Tests.ModuleData;
using Juice.Observability.Extensibility;
using Juice.Observability.Slos;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Observability.Tests.ProducerAdapterTests;

namespace Juice.Observability.Tests.AlertSources
{
    /// <summary>
    /// The core's <see cref="IAlertSource"/> (the <c>SloBurn</c> template, the slos module's): it acts only on the platform-managed rules of its template, in the
    /// platform tenant, and commits an alert, its transitions and the outbox together (W-23). Local stack.
    /// </summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class AlertSourceTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string Template = "SloBurn", RuleKey = SloBurnRules.FastBurn;

        private static readonly string Dedup = SloBurnRules.DedupKey(RuleKey, "orders");

        private static readonly DateTimeOffset At = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        private static AlertSignal Fire(string? dedup = null, string? rule = null, AlertSubject? subject = null, double value = 20)
            => new(rule ?? RuleKey, dedup ?? Dedup, AlertSignalKind.Fire, At, "burn_rate_over", subject ?? new AlertSubject(AlertSubjectKind.Service, "service:shop/orders@production"),
                value, 100, NoData: false, Evidence: new Dictionary<string, string> { ["slo"] = "orders" });

        private static AlertSignal Resolve(string reason = "burn_rate_under") => new(RuleKey, Dedup, AlertSignalKind.Resolve, At.AddMinutes(10), reason);

        /// <summary>The platform's burn rules, a service and an environment of the platform tenant.</summary>
        private static async Task<(Guid Service, Guid Environment)> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(database);
            await SloBurnAlerts.EnsureRulesAsync(db, tenant, DateTimeOffset.UtcNow, [], Ct);
            var service = new Service { TenantId = tenant, Namespace = "shop", Name = "orders", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow };
            var environment = new DeploymentEnvironment { TenantId = tenant, Key = "production", Type = EnvironmentType.Production, FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow };
            db.Services.Add(service);
            db.Environments.Add(environment);
            await db.SaveChangesAsync(Ct);
            return (service.Id, environment.Id);
        }

        private static Task<ModuleTestHost> HostAsync(ScratchDatabase database) => ModuleTestHost.StartAsync(null, database, hostedServices: false);

        private static IAlertSource Source(IServiceScope scope) => scope.ServiceProvider.GetRequiredKeyedService<IAlertSource>(AlertSourceKeys.Key("slos", Template));

        private sealed record Counts(int Alerts, int Transitions, int Outbox);

        private static async Task<Counts> CountsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return new(await db.Alerts.CountAsync(Ct), await db.AlertTransitions.CountAsync(Ct), await db.Outbox.CountAsync(Ct));
        }

        [Fact]
        public async Task Another_module_cannot_resolve_the_slos_source_by_its_template()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<SourceModule>(), database, hostedServices: false);
            await using var scope = host.App.Services.CreateAsyncScope();

            // Sources are keyed by the module that declared them: the template alone names none, whoever asks.
            Assert.Null(scope.ServiceProvider.GetKeyedService<IAlertSource>(Template));
            Assert.Null(scope.ServiceProvider.GetKeyedService<IAlertSource>("source-ok.burn"));
            Assert.NotNull(scope.ServiceProvider.GetKeyedService<IAlertSource>(AlertSourceKeys.Key("source-ok", "source-ok.burn")));
            Assert.NotNull(scope.ServiceProvider.GetKeyedService<IAlertSource>(AlertSourceKeys.Key("slos", Template)));
        }

        [Fact]
        public void Schema_denylist_covers_the_embedded_notifications_schema()
            => Assert.True(Api.Modules.ModuleData.CoreSchemas.IsCoreSchema(Juice.Notifications.Data.NotificationsDbContext.Schema));

        [Fact]
        public async Task Apply_foreign_rule_or_template_refused_nothing_written()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using (var db = database.Context())
            {
                var tenant = await TenantAsync(database);
                // A user's rule of the same template, and a platform-managed rule of another one: neither is the source's.
                db.Rules.Add(new Rule { TenantId = tenant, Key = "user-burn", Name = "u", Template = RuleTemplate.SloBurn, Definition = "{}", DefinitionHash = "h", ManagedBy = ManagedBy.Api });
                db.Rules.Add(new Rule { TenantId = tenant, Key = "platform-errors", Name = "p", Template = RuleTemplate.ErrorRate, Definition = "{}", DefinitionHash = "h", ManagedBy = ManagedBy.Platform });
                await db.SaveChangesAsync(Ct);
            }
            var before = await CountsAsync(database);
            await using var host = await HostAsync(database);

            foreach (var foreign in new[] { "user-burn", "platform-errors", "no-such-rule" })
            {
                await using var scope = host.App.Services.CreateAsyncScope();
                // A valid signal first: the whole call is refused, so it isn't applied either.
                await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([Fire(), Fire(dedup: foreign + "|x", rule: foreign)], Ct));
            }

            Assert.Equal(before, await CountsAsync(database));
        }

        [Fact]
        public async Task Apply_subject_id_of_other_tenant_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (service, environment) = await SeedAsync(database);
            // The host reads the deployment's tenant as the only one at start; the other tenant comes after.
            await using var host = await HostAsync(database);
            Guid otherService, otherEnvironment;
            await using (var db = database.Context())
            {
                var other = new PlatformTenant { Key = Guid.NewGuid().ToString(), DisplayName = "other" };
                db.Tenants.Add(other);
                var s = new Service { TenantId = other.Id, Namespace = "shop", Name = "orders", FirstSeen = At, LastSeen = At };
                var e = new DeploymentEnvironment { TenantId = other.Id, Key = "production", Type = EnvironmentType.Production, FirstSeen = At, LastSeen = At };
                db.Services.Add(s);
                db.Environments.Add(e);
                await db.SaveChangesAsync(Ct);
                (otherService, otherEnvironment) = (s.Id, e.Id);
            }
            var before = await CountsAsync(database);

            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([Fire(subject: new(AlertSubjectKind.Service, "service:shop/orders@production", otherService, environment))], Ct));
                await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([Fire(subject: new(AlertSubjectKind.Service, "service:shop/orders@production", service, otherEnvironment))], Ct));
                await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([Fire(subject: new(AlertSubjectKind.Service, "service:shop/orders@production", Guid.NewGuid()))], Ct));
                // A fire with no subject is refused too.
                await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([new AlertSignal(RuleKey, Dedup, AlertSignalKind.Fire, At, "burn_rate_over")], Ct));
            }
            Assert.Equal(before, await CountsAsync(database));

            // The platform tenant's own ids are accepted.
            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                Assert.Equal(new AlertSourceResult(1, 0, 0), await Source(scope).ApplyAsync([Fire(subject: new(AlertSubjectKind.Service, "service:shop/orders@production", service, environment))], Ct));
            }
        }

        [Fact]
        public async Task Apply_outside_command_commits_alert_transitions_outbox_together()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var host = await HostAsync(database);
            var before = await CountsAsync(database);

            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                Assert.Equal(new AlertSourceResult(1, 0, 0), await Source(scope).ApplyAsync([Fire()], Ct));
            }

            // One transaction: the alert (firing), its two transitions and the integration event of the firing.
            var after = await CountsAsync(database);
            Assert.Equal((1, 2, 1), (after.Alerts - before.Alerts, after.Transitions - before.Transitions, after.Outbox - before.Outbox));
            await using (var db = database.Context())
            {
                var alert = await db.Alerts.AsNoTracking().SingleAsync(Ct);
                Assert.Equal((AlertState.Firing, Dedup, At, 20, 100L, false), (alert.State, alert.DedupKey, alert.FiredAt, alert.LastValue, alert.LastSamples, alert.NoData));
                Assert.Equal("orders", alert.Evidence["slo"]);
                Assert.Equal(["Firing", "Pending"], (await db.AlertTransitions.AsNoTracking().ToListAsync(Ct)).Select(t => t.ToState.ToString()).Order());
            }

            // A fire with one open only updates it; a resolve resolves it, and notifies since it had fired.
            await using (var scope = host.App.Services.CreateAsyncScope())
            {
                Assert.Equal(new AlertSourceResult(0, 0, 0), await Source(scope).ApplyAsync([Fire(value: 30)], Ct));
                Assert.Equal(new AlertSourceResult(0, 1, 0), await Source(scope).ApplyAsync([Resolve()], Ct));
            }
            var resolved = await CountsAsync(database);
            Assert.Equal((1, 3, 2), (resolved.Alerts - before.Alerts, resolved.Transitions - before.Transitions, resolved.Outbox - before.Outbox));
            await using (var db = database.Context())
            {
                var alert = await db.Alerts.AsNoTracking().SingleAsync(Ct);
                Assert.Equal((AlertState.Resolved, 30.0), (alert.State, alert.LastValue));
            }
        }

        [Fact]
        public async Task Apply_inside_command_joins_transaction()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var host = await HostAsync(database);
            var before = await CountsAsync(database);
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // As a command's handler calls it: managed by the TransactionBehavior, which saves, dispatches the domain events and commits afterwards.
            db.BeginManage();
            Assert.Equal(new AlertSourceResult(1, 0, 0), await Source(scope).ApplyAsync([Fire()], Ct));

            // It changed entities and nothing else: no save, no commit, no command of its own, and the domain event is on the alert for the behavior.
            var alert = Assert.Single(db.ChangeTracker.Entries<Alert>(), e => e.State == EntityState.Added).Entity;
            Assert.Single(alert.DomainEvents);
            Assert.Equal(before, await CountsAsync(database));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(before, await CountsAsync(database));
            await db.Database.CurrentTransaction!.CommitAsync(Ct);
            db.EndManage();

            var after = await CountsAsync(database);
            Assert.Equal((1, 2), (after.Alerts - before.Alerts, after.Transitions - before.Transitions));
        }

        [Fact]
        public async Task Apply_in_a_transaction_no_command_manages_refused_never_committed_without_its_event()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var host = await HostAsync(database);
            var before = await CountsAsync(database);
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Source(scope).ApplyAsync([Fire()], Ct));
                Assert.Empty(db.ChangeTracker.Entries<Alert>());
            }

            Assert.Equal(before, await CountsAsync(database));
        }

        [Fact]
        public async Task Refused_apply_in_a_command_leaves_no_transaction_on_the_context()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var host = await HostAsync(database);
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            db.BeginManage();
            await Assert.ThrowsAsync<ArgumentException>(() => Source(scope).ApplyAsync([Fire(rule: "no-such-rule", dedup: "x|y")], Ct));

            // The transaction the source began for its lock is gone with the failure: the next command in the scope isn't taken for a nested one.
            Assert.Null(db.Database.CurrentTransaction);
            db.EndManage();
        }

        [Fact]
        public async Task Last_resolved_reads_only_the_templates_platform_rules()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            var tenant = await TenantAsync(database);
            // An alert of a user's rule, fired and then resolved for the same reason, under a dedup key the source is asked about.
            await using (var db = database.Context())
            {
                var rule = new Rule { TenantId = tenant, Key = "user-burn", Name = "u", Template = RuleTemplate.SloBurn, Definition = "{}", DefinitionHash = "h", ManagedBy = ManagedBy.Api };
                db.Rules.Add(rule);
                var alert = new Alert { TenantId = tenant, RuleId = rule.Id, DedupKey = "user-burn|x", SubjectKey = "s", State = AlertState.Resolved, BreachStartedAt = At, FiredAt = At, LastEvaluatedAt = At };
                db.Alerts.Add(alert);
                db.AlertTransitions.Add(new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Firing, ToState = AlertState.Resolved, At = At, Reason = "burn_rate_under" });
                await db.SaveChangesAsync(Ct);
            }
            await using var host = await HostAsync(database);
            await using var scope = host.App.Services.CreateAsyncScope();

            Assert.Empty(await Source(scope).LastResolvedAsync(["user-burn|x"], "burn_rate_under", Ct));
        }

        [Fact]
        public async Task Concurrent_apply_same_dedup_one_open_alert()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var host = await HostAsync(database);

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            {
                await using var scope = host.App.Services.CreateAsyncScope();
                return await Source(scope).ApplyAsync([Fire()], Ct);
            }));

            Assert.Equal(1, results.Sum(r => r.Fired));
            await using var db = database.Context();
            Assert.Equal(1, await db.Alerts.CountAsync(a => a.DedupKey == Dedup && a.State != AlertState.Resolved, Ct));
            Assert.Equal(1, await db.Alerts.CountAsync(Ct));
            Assert.Equal(2, await db.AlertTransitions.CountAsync(Ct));
        }

        [Fact]
        public async Task Silenced_subject_holds_source_alert_notification()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await SeedAsync(database);
            await using var factory = Factory(database);
            _ = factory.Server;
            await SilenceAsync(database);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.Equal(1, (await Source(scope).ApplyAsync([Fire()], Ct)).Fired);
            }
            await using var db = database.Context();
            var alert = await db.Alerts.AsNoTracking().SingleAsync(Ct);
            var firing = await db.AlertTransitions.AsNoTracking().SingleAsync(t => t.AlertId == alert.Id && t.ToState == AlertState.Firing, Ct);

            // The host's outbox delivery hands the firing to the planner, as it does for the alert of any rule; wait for it.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (!await db.NotificationHolds.AnyAsync(Ct) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, Ct);
            }

            // Held, not sent.
            Assert.Empty(await RequestsAsync(database));
            var hold = await db.NotificationHolds.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((alert.Id, firing.Id), (hold.AlertId, hold.TransitionId));
        }
    }
}
