using System.Net;
using System.Text.Json;
using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Rules;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// SLO burn-rate alerts (Phase 9, milestone 2): multiwindow conditions, never firing or resolving on missing data, and alerts that go through the
    /// same lifecycle, transitions and outbox as any rule's, under the platform's three burn rules.
    /// </summary>
    public class SloBurnTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly BurnAlert Fast = SloBurnAlerts.All[0];

        /// <summary>A window of a 99% SLO: 1% of requests may fail, so 20% failing is a rate of 20.</summary>
        private static WindowStatus W(string name, long total, long bad, long? minutes = null)
        {
            var length = SloBudget.BurnWindows.Single(w => w.Name == name).Length;
            return SloBudget.Window(name, length, [new VersionSums(1, 99, total, total - bad, minutes ?? (long)length.TotalMinutes)], 10, 0.10);
        }

        [Fact]
        public void Both_windows_must_burn_to_fire_and_the_short_one_cooling_resolves()
        {
            Assert.Equal(BurnCondition.Burning, SloBurnAlerts.Classify(Fast, W("1h", 1000, 200), W("5m", 100, 20)));
            // The short window has stopped burning: the long one still remembers, but the burn is over.
            Assert.Equal(BurnCondition.Cooled, SloBurnAlerts.Classify(Fast, W("1h", 1000, 200), W("5m", 100, 1)));
            // A spike the long window hasn't seen enough of: not yet, and not over either.
            Assert.Equal(BurnCondition.Between, SloBurnAlerts.Classify(Fast, W("1h", 1000, 1), W("5m", 100, 50)));
            Assert.Equal(BurnCondition.Cooled, SloBurnAlerts.Classify(Fast, W("1h", 1000, 10), W("5m", 100, 1)));
            Assert.Equal([(14.4, "1h", "5m", AlertSeverity.Critical), (6, "6h", "30m", AlertSeverity.Warning), (1, "3d", "6h", AlertSeverity.Warning)],
                SloBurnAlerts.All.Select(a => (a.Rate, a.Long, a.Short, a.Severity)));
        }

        [Fact]
        public void An_older_version_this_build_cant_read_is_left_out_not_holding_the_slo()
        {
            SloVersion V(int number, string json) => new() { Version = number, Definition = json, Hash = "", CreatedBy = "" };
            SloDefinition D(string service) => new()
            {
                Key = "k", Name = "K", Sli = SliKinds.Availability, Service = service, Environment = "production", Target = 99,
            };
            var current = D("shop/orders");
            // Version 2 was written by a build with a field this one can't read; versions 1 and 3 are on the same service, version 4 on another.
            var versions = new[] { V(1, current.ToJson()), V(2, """{"Sli":7}"""), V(3, current.ToJson()), V(4, D("shop/other").ToJson()) };
            Assert.Equal([1, 3], SloBurnAlerts.SameServiceVersions(versions, current).Order());
        }

        [Fact]
        public void Missing_data_neither_fires_nor_resolves()
        {
            // Every request failing, but only five of them in the short window: no rate.
            Assert.Equal(BurnCondition.Unknown, SloBurnAlerts.Classify(Fast, W("1h", 1000, 500), W("5m", 5, 5)));
            // All good, but a third of the hour unread: no rate either, so a firing alert isn't resolved by it.
            Assert.Equal(BurnCondition.Unknown, SloBurnAlerts.Classify(Fast, W("1h", 1000, 0, minutes: 40), W("5m", 100, 0)));
        }

        [Fact]
        public void The_burn_rules_are_the_platforms_and_their_keys_are_reserved()
        {
            var mine = new RuleDefinition { Key = "mine", Name = "Mine", Template = RuleTemplate.SloBurn, Threshold = 1 };
            Assert.Contains(mine.Validate(), e => e.Contains("platform's own", StringComparison.Ordinal));
            // The keys are reserved when a rule is made, not on every change: a rule that had one before keeps it, and can still be changed.
            Assert.All(SloBurnRules.Keys, k => Assert.True(RuleDefinition.IsReservedKey(k)));
            Assert.False(RuleDefinition.IsReservedKey("slo-fast-burn-2"));
            var held = new RuleDefinition { Key = SloBurnRules.FastBurn, Name = "Someone's", Template = RuleTemplate.ErrorRate, Threshold = 0.1 };
            Assert.Empty(held.Validate());
        }

        /// <summary>A burn alert's notification says which SLO is burning: several SLOs can watch one service, and the subject alone doesn't tell.</summary>
        [Fact]
        public void A_burn_alerts_notification_names_its_slo()
        {
            var rule = new Rule { Key = SloBurnRules.FastBurn, Name = "SLO fast burn", Template = RuleTemplate.SloBurn, Definition = "{}", DefinitionHash = "" };
            var alert = new Alert
            {
                DedupKey = SloBurnRules.DedupKey(SloBurnRules.FastBurn, "orders-latency"), SubjectKey = "service:shop/orders@production", Severity = AlertSeverity.Critical,
                State = AlertState.Firing, Evidence = new() { ["slo"] = "orders-latency", ["sloName"] = "Orders latency" },
            };
            var transition = new AlertTransition { AlertId = alert.Id, ToState = AlertState.Firing, Reason = EvaluateSloBurnsHandler.Fired, Value = 20 };
            var payload = Webhooks.NotificationPlan.Payload(Guid.NewGuid(), Webhooks.NotificationPlan.Firing, "tenant", rule, alert, transition, null);
            using (var json = JsonDocument.Parse(payload))
            {
                var slo = json.RootElement.GetProperty("alert").GetProperty("slo");
                Assert.Equal(("orders-latency", "Orders latency"), (slo.GetProperty("key").GetString(), slo.GetProperty("name").GetString()));
            }
            var view = Webhooks.NotificationView.Parse(payload);
            Assert.Equal("orders-latency", view.Alerts[0].Slo);
            Assert.Contains("SLO orders-latency", Webhooks.ChannelFormatter.SlackBody(view, null), StringComparison.Ordinal);
            Assert.Contains("SLO orders-latency", Webhooks.ChannelFormatter.TeamsBody(view, null), StringComparison.Ordinal);
            Assert.Contains("SLO: orders-latency", Webhooks.ChannelFormatter.Email(view, null).Body, StringComparison.Ordinal);
            // A rule's alert has none.
            var plain = new Alert { DedupKey = "error-rate|x", SubjectKey = "service:shop/orders@production", Severity = AlertSeverity.Warning, State = AlertState.Firing };
            var ruleView = Webhooks.NotificationView.Parse(Webhooks.NotificationPlan.Payload(Guid.NewGuid(), Webhooks.NotificationPlan.Firing, "tenant", rule, plain, transition, null));
            Assert.Null(ruleView.Alerts[0].Slo);
        }

        /// <summary>
        /// A rule made before the burn rules' keys were reserved keeps its key: starting doesn't fail on it (rules can't be deleted or renamed), that
        /// kind of burn alert is left out and the problem reported, and the other two burn rules are made.
        /// </summary>
        [Fact]
        public async Task A_rule_already_holding_a_burn_rule_key_doesnt_stop_starting_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var db = database.Context();
            var tenantId = await db.Tenants.Where(t => t.Key == LocalStack.TenantKey).Select(t => t.Id).SingleAsync(Ct);
            db.Rules.Add(new Rule
            {
                TenantId = tenantId, Key = SloBurnRules.SlowBurn, Name = "Someone's", Template = RuleTemplate.ErrorRate, Definition = "{}", DefinitionHash = "x",
                ManagedBy = ManagedBy.Api,
            });
            await db.SaveChangesAsync(Ct);
            var warnings = new List<string>();
            var definition = new SloDefinition
            {
                Key = "orders-availability", Name = "Orders availability", Sli = SliKinds.Availability, Service = "shop/orders", Environment = "production", Target = 99,
            };
            await SloProvisioner.ProvisionAsync(db, [definition], LocalStack.TenantKey, DateTimeOffset.UtcNow, Ct, warnings);
            await db.SaveChangesAsync(Ct);
            Assert.Contains(warnings, w => w.Contains(SloBurnRules.SlowBurn, StringComparison.Ordinal));
            var rules = await db.Rules.AsNoTracking().ToDictionaryAsync(r => r.Key, Ct);
            Assert.Equal((ManagedBy.Platform, ManagedBy.Api, ManagedBy.Platform),
                (rules[SloBurnRules.FastBurn].ManagedBy, rules[SloBurnRules.SlowBurn].ManagedBy, rules[SloBurnRules.SlowBurn3d].ManagedBy));
        }

        private sealed class MovableClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private const string OrdersKey = "orders-availability";

        private static SloDefinition Orders(string service = "shop/orders") => new()
        {
            Key = OrdersKey, Name = "Orders availability", Sli = SliKinds.Availability, Service = service, Environment = "production", Target = 99,
        };

        /// <summary>The orders SLO, in effect from four days before <paramref name="now"/>.</summary>
        private static async Task<(Guid TenantId, Guid SloId)> ProvisionAsync(ScratchDatabase database, DateTimeOffset now)
        {
            await using var db = database.Context();
            await SloProvisioner.ProvisionAsync(db, [Orders()], LocalStack.TenantKey, now - TimeSpan.FromDays(4), Ct);
            await db.SaveChangesAsync(Ct);
            var slo = await db.Slos.SingleAsync(Ct);
            return (slo.TenantId, slo.Id);
        }

        /// <summary>Every minute in [from, to) counted with 100 requests, <paramref name="good"/> of them good, under <paramref name="version"/>.</summary>
        private static async Task FillAsync(ScratchDatabase database, (Guid TenantId, Guid SloId) slo, DateTimeOffset from, DateTimeOffset to, long good, int version = 1)
        {
            await using var db = database.Context();
            await db.SloBuckets.Where(b => b.SloId == slo.SloId && b.Minute >= from && b.Minute < to).ExecuteDeleteAsync(Ct);
            var last = to - TimeSpan.FromMinutes(1);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO slo_buckets (tenant_id, slo_id, minute, version, total, good, eligible, bad, excluded_health, excluded_cancelled,
                                         excluded_client_errors, histogram, counted_at)
                SELECT {slo.TenantId}, {slo.SloId}, m, {version}, 100, {good}, 100, {100 - good}, 0, 0, 0, ARRAY[]::bigint[], {to}
                FROM generate_series({from}, {last}, interval '1 minute') AS m
                """, Ct);
            // Their daily totals, as the counter keeps them with what it writes.
            await SloDays.RecomputeAsync(db, slo.SloId, from, to, Ct);
        }

        /// <summary>A command through the mediator and its TransactionBehavior, as the counter sends it.</summary>
        private static async Task<T> SendAsync<T>(ScratchDatabase database, TimeProvider clock, IRequest<T> command)
        {
            await using var services = PlatformServices.Build(database, clock);
            using var context = PlatformMessageContext.Begin("test");
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, Ct);
        }

        private static async Task<Dictionary<string, Alert>> OpenAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.Alerts.AsNoTracking().Where(a => a.State != AlertState.Resolved).ToDictionaryAsync(a => a.DedupKey, Ct);
        }

        private static async Task<List<AlertTransition>> ResolutionsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.AlertTransitions.AsNoTracking().Where(t => t.ToState == AlertState.Resolved).OrderBy(t => t.Id).ToListAsync(Ct);
        }

        /// <summary>Where the windows end at the clock's time.</summary>
        private static DateTimeOffset End(TimeProvider clock) => SloCounter.Floor(clock.GetUtcNow()) - SloReader.Settle;

        /// <summary>
        /// A fast burn that stops: the last five minutes are good while the hour still remembers the burn. It resolves on the short window, and the
        /// resolve reports that window's rate, not the hour's; the slow burns, whose short windows still burn, stay firing.
        /// </summary>
        [Fact]
        public async Task A_burn_that_stops_resolves_on_its_short_window_and_reports_its_rate_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var clock = new MovableClock(DateTimeOffset.UtcNow);
            var slo = await ProvisionAsync(database, clock.Now);
            var end = End(clock);
            await FillAsync(database, slo, end - TimeSpan.FromDays(3), end, good: 50);
            Assert.Equal(3, (await SendAsync(database, clock, new EvaluateSloBurns())).Fired);
            await FillAsync(database, slo, end - TimeSpan.FromMinutes(5), end, good: 100);
            Assert.Equal(1, (await SendAsync(database, clock, new EvaluateSloBurns())).Resolved);
            var resolution = Assert.Single(await ResolutionsAsync(database));
            Assert.Equal((EvaluateSloBurnsHandler.Cooled, 0.0), (resolution.Reason, resolution.Value));
            var fast = (await SloBurnTests.OpenAsync(database)).Keys;
            Assert.Equal(new[] { SloBurnRules.DedupKey(SloBurnRules.SlowBurn, OrdersKey), SloBurnRules.DedupKey(SloBurnRules.SlowBurn3d, OrdersKey) }.Order(), fast.Order());
        }

        /// <summary>With counting off, open burn alerts are closed, notifying: nothing would ever judge them again.</summary>
        [Fact]
        public async Task With_counting_off_open_burn_alerts_are_closed_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var clock = new MovableClock(DateTimeOffset.UtcNow);
            var slo = await ProvisionAsync(database, clock.Now);
            var end = End(clock);
            await FillAsync(database, slo, end - TimeSpan.FromDays(3), end, good: 50);
            Assert.Equal(3, (await SendAsync(database, clock, new EvaluateSloBurns())).Fired);
            // Another replica is counting (it holds the counter's lease): this one, with counting off, leaves the alerts it still judges.
            await using (var counting = new WorkerLease(database.ConnectionString, CloseSloBurnAlertsHandler.CounterLease,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
            {
                Assert.True(await counting.TryHoldAsync(Ct));
                Assert.Equal(0, await SendAsync(database, clock, new CloseSloBurnAlerts(CloseSloBurnAlertsHandler.CountingDisabled)));
                Assert.Equal(3, (await OpenAsync(database)).Count);
            }
            // Nobody counting: they're closed.
            Assert.Equal(3, await SendAsync(database, clock, new CloseSloBurnAlerts(CloseSloBurnAlertsHandler.CountingDisabled)));
            Assert.Empty(await OpenAsync(database));
            Assert.All(await ResolutionsAsync(database), t => Assert.Equal(CloseSloBurnAlertsHandler.CountingDisabled, t.Reason));
            // Each fired and resolved: both in the outbox, for notifications.
            Assert.Equal(6, (await PlatformServices.OutboxAsync(database, Ct)).Count(o => o.Type.Contains("AlertTransitioned", StringComparison.Ordinal)));
        }

        /// <summary>
        /// The whole life of the three burn alerts on one SLO, with minute buckets written directly and a fixed clock: they fire through the alert
        /// lifecycle and the outbox; hold, not resolve, while a window is unread; resolve when both windows cool; and resolve when the SLO is retired.
        /// </summary>
        [Fact]
        public async Task Burn_alerts_fire_hold_on_missing_data_and_resolve_through_the_alert_lifecycle_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var clock = new MovableClock(DateTimeOffset.UtcNow);
            const string Key = OrdersKey;
            var slo = await ProvisionAsync(database, clock.Now);
            await using (var db = database.Context())
            {
                var rules = await db.Rules.Where(r => r.Template == RuleTemplate.SloBurn).ToListAsync(Ct);
                Assert.Equal(SloBurnRules.Keys.Order(), rules.Select(r => r.Key).Order());
                Assert.All(rules, r => Assert.Equal(ManagedBy.Platform, r.ManagedBy));
            }
            var end = End(clock);
            Task FillAsync(DateTimeOffset from, DateTimeOffset to, long good, int version = 1) => SloBurnTests.FillAsync(database, slo, from, to, good, version);
            Task<SloBurnEvaluation> EvaluateAsync() => SendAsync(database, clock, new EvaluateSloBurns());
            Task<Dictionary<string, Alert>> OpenAsync() => SloBurnTests.OpenAsync(database);
            async Task<List<string>> ResolvedReasonsAsync() => [.. (await ResolutionsAsync(database)).Select(t => t.Reason)];
            string Dedup(string rule) => SloBurnRules.DedupKey(rule, Key);
            // Past every cooldown (the 3-day burn's is its 6-hour window): the windows move on, and the minutes are written again.
            void Wait()
            {
                clock.Now += TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1);
                end = End(clock);
            }

            // Half of every request failing for three days: 50 times the budget's pace in every window. All three fire.
            await FillAsync(end - TimeSpan.FromDays(3), end, good: 50);
            Assert.Equal(3, (await EvaluateAsync()).Fired);
            var open = await OpenAsync();
            Assert.Equal(SloBurnRules.Keys.Select(Dedup).Order(), open.Keys.Order());
            Assert.All(open.Values, a =>
            {
                Assert.Equal(AlertState.Firing, a.State);
                Assert.Equal("service:shop/orders@production", a.SubjectKey);
                Assert.Equal((Key, "1", "Technical", "50", "50"), (a.Evidence["slo"], a.Evidence["version"], a.Evidence["category"], a.Evidence["longBurnRate"], a.Evidence["shortBurnRate"]));
                Assert.False(a.NoData);
            });
            Assert.Equal(AlertSeverity.Critical, open[Dedup(SloBurnRules.FastBurn)].Severity);
            Assert.Equal(AlertSeverity.Warning, open[Dedup(SloBurnRules.SlowBurn3d)].Severity);
            await using (var db = database.Context())
            {
                // Through the lifecycle, as a rule's alert: Pending, then Firing, and the firing is in the outbox for notifications.
                var states = await db.AlertTransitions.AsNoTracking().OrderBy(t => t.Id).Select(t => t.ToState).ToListAsync(Ct);
                Assert.Equal(3, states.Count(s => s == AlertState.Pending));
                Assert.Equal(3, states.Count(s => s == AlertState.Firing));
            }
            Assert.Equal(3, (await PlatformServices.OutboxAsync(database, Ct)).Count(o => o.Type.Contains("AlertTransitioned", StringComparison.Ordinal)));
            // Still burning: nothing new.
            var again = await EvaluateAsync();
            Assert.Equal((0, 0), (again.Fired, again.Resolved));

            // Every request good again, but the last ten minutes unread. The fast and 6-hour burns lose a window's rate, so they hold, still firing;
            // the 3-day burn's 6-hour window is still known and cool, so it resolves.
            await FillAsync(end - TimeSpan.FromDays(3), end, good: 100);
            await using (var db = database.Context())
            {
                await db.SloBuckets.Where(b => b.Minute >= end - TimeSpan.FromMinutes(10)).ExecuteDeleteAsync(Ct);
                // As the counter would: their hour and day summed again, or just after an hour began, the hour before would still hold them.
                await SloDays.RecomputeAsync(db, slo.SloId, end - TimeSpan.FromMinutes(10), end, Ct);
            }
            Assert.Equal(1, (await EvaluateAsync()).Resolved);
            open = await OpenAsync();
            Assert.Equal([Dedup(SloBurnRules.FastBurn), Dedup(SloBurnRules.SlowBurn)], open.Keys.Order());
            Assert.All(open.Values, a => Assert.True(a.NoData));
            Assert.Equal("Unknown", open[Dedup(SloBurnRules.FastBurn)].Evidence["condition"]);
            // Held with no long-window rate, the fast burn keeps the rate that fired it, not an empty one. (The 6-hour burn's long window still
            // has a rate, now 0, and shows it.)
            Assert.Equal(50, open[Dedup(SloBurnRules.FastBurn)].LastValue ?? 0, 3);
            Assert.Equal(0, open[Dedup(SloBurnRules.SlowBurn)].LastValue ?? -1, 3);

            // The unread minutes arrive, good: both windows cool, and the other two resolve.
            await FillAsync(end - TimeSpan.FromMinutes(10), end, good: 100);
            Assert.Equal(2, (await EvaluateAsync()).Resolved);
            Assert.Empty(await OpenAsync());
            Assert.Equal(Enumerable.Repeat(EvaluateSloBurnsHandler.Cooled, 3), await ResolvedReasonsAsync());
            // Each resolved by its short window, whose rate the resolve reports: 0, not the long window's memory of the burn.
            Assert.All(await ResolutionsAsync(database), t => Assert.Equal(0, t.Value ?? -1, 6));

            // Burning again at once: the cooldown holds them back, so a burn hovering at the rate doesn't fire and resolve on every pass.
            await FillAsync(end - TimeSpan.FromDays(3), end, good: 50);
            Assert.Equal(0, (await EvaluateAsync()).Fired);
            // Past it, they fire again.
            Wait();
            await FillAsync(end - TimeSpan.FromDays(3), end, good: 50);
            Assert.Equal(3, (await EvaluateAsync()).Fired);

            // A new version moves the SLO to another service: the alerts about the old service resolve as moved. The old service's minutes say
            // nothing about the new one, so its windows read Unknown and nothing fires on it for the old one's errors.
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Orders("shop/orders-v2")], LocalStack.TenantKey, clock.Now - TimeSpan.FromSeconds(30), Ct);
                await db.SaveChangesAsync(Ct);
            }
            var afterMove = await EvaluateAsync();
            Assert.Equal((3, 0), (afterMove.Resolved, afterMove.Fired));
            Assert.Empty(await OpenAsync());
            // Once the new service's own minutes burn, its alerts fire, about it, at once: closing an alert for a move starts no cooldown.
            await FillAsync(end - TimeSpan.FromDays(3), end, good: 50, version: 2);
            Assert.Equal(3, (await EvaluateAsync()).Fired);
            Assert.All((await OpenAsync()).Values, a => Assert.Equal(("service:shop/orders-v2@production", "2"), (a.SubjectKey, a.Evidence["version"])));

            // Then the SLO retired: nothing would ever resolve its alerts, so they resolve as retired, and notify.
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [], LocalStack.TenantKey, clock.Now, Ct);
                await db.SaveChangesAsync(Ct);
            }
            Assert.Equal(3, (await EvaluateAsync()).Resolved);
            Assert.Empty(await OpenAsync());
            Assert.Equal([.. Enumerable.Repeat(EvaluateSloBurnsHandler.SloMoved, 3), .. Enumerable.Repeat(EvaluateSloBurnsHandler.SloRetired, 3)],
                (await ResolvedReasonsAsync()).Skip(3));
        }

        /// <summary>The rules API lists the burn rules as the platform's, and refuses to change them, create one, or take their keys.</summary>
        [Fact]
        public async Task The_api_lists_the_burn_rules_and_refuses_to_change_or_take_them_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = new Dictionary<string, string>
            {
                ["Slos:Enabled"] = "false",
                ["Slos:Definitions:0:Key"] = "orders-availability", ["Slos:Definitions:0:Name"] = "Orders availability", ["Slos:Definitions:0:Sli"] = "Availability",
                ["Slos:Definitions:0:Service"] = "shop/orders", ["Slos:Definitions:0:Environment"] = "production", ["Slos:Definitions:0:Target"] = "99.5",
            };
            // A rule someone made with one of the keys before they were reserved: the API still starts, and the rule can still be changed.
            await using (var db = database.Context())
            {
                var tenantId = await db.Tenants.Where(t => t.Key == LocalStack.TenantKey).Select(t => t.Id).SingleAsync(Ct);
                var legacy = new RuleDefinition { Key = SloBurnRules.SlowBurn3d, Name = "Someone's", Template = RuleTemplate.ErrorRate, Threshold = 0.1 };
                db.Rules.Add(new Rule
                {
                    TenantId = tenantId, Key = legacy.Key, Name = legacy.Name, Template = legacy.Template, Definition = legacy.ToJson(), DefinitionHash = legacy.Hash(),
                    ManagedBy = ManagedBy.Api,
                });
                await db.SaveChangesAsync(Ct);
            }
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);
            using var client = factory.CreateClient();
            var (viewer, _, admin, tenant) = await Caller.AllAsync(client, database);

            var rules = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/rules"))).GetProperty("items").EnumerateArray().ToList();
            var fast = rules.Single(r => r.GetProperty("key").GetString() == SloBurnRules.FastBurn);
            Assert.Equal(("platform", "SloBurn", "Critical"),
                (fast.GetProperty("managedBy").GetString(), fast.GetProperty("template").GetString(), fast.GetProperty("severity").GetString()));
            Assert.Equal(2, rules.Count(r => r.GetProperty("template").GetString() == "SloBurn"));
            Assert.Equal("api", rules.Single(r => r.GetProperty("key").GetString() == SloBurnRules.SlowBurn3d).GetProperty("managedBy").GetString());
            using (var disabled = await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/{SloBurnRules.SlowBurn3d}",
                new { name = "Someone's", template = "ErrorRate", threshold = 0.1, enabled = false }))
            {
                Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            }

            using (var changed = await admin.PutAsync($"/api/v1/tenants/{tenant}/rules/{SloBurnRules.FastBurn}", new { name = "Mine", template = "ErrorRate", threshold = 0.1 }))
            {
                Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            }
            await using (var db = database.Context())
            {
                // Refused, and audited as a refusal.
                var refused = await db.AuditRecords.AsNoTracking().SingleAsync(r => r.Operation == "rule.update" && r.TargetId == SloBurnRules.FastBurn, Ct);
                Assert.Equal((AuditOutcome.Failed, "managed_by_platform"), (refused.Outcome, refused.Metadata["reason"]));
            }
            using (var taken = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", new { key = SloBurnRules.SlowBurn, name = "Mine", template = "ErrorRate", threshold = 0.1 }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
            }
            using (var burn = await admin.PostAsync($"/api/v1/tenants/{tenant}/rules", new { key = "my-burn", name = "Mine", template = "SloBurn", threshold = 1 }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, burn.StatusCode);
            }
            var detail = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos/orders-availability"));
            Assert.Equal(JsonValueKind.Array, detail.GetProperty("alerts").ValueKind);
            Assert.Equal(0, detail.GetProperty("alerts").GetArrayLength());
        }
    }
}
