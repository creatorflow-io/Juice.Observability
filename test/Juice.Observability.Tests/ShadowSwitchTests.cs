using System.Diagnostics.Metrics;
using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Observability.Data;
using Juice.Observability.Health;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Juice.Observability.Tests.ProducerAdapterTests;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Across the switch from shadow to new (architecture §6.4, M45-3): what was kept while the pipeline was down in shadow was sent by
    /// legacy then, so the pipeline sends it dry, while its escalation's later steps are real; a silence's hold (legacy held it too) and
    /// what was kept in new are sent for real; the adapter never sends again what shadow planned dry (<c>dry_duplicates</c> stays 0).
    /// </summary>
    public sealed class ShadowSwitchTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        // Within the notifications API's own limits (M46-3): the integration is the API's, not the alias's.
        private const string SecretVariable = "NOTIFICATIONS_SECRET_SHADOW_SWITCH";

        /// <summary>Two receivers: the firing's own recipients (route <c>alerts</c>) and its escalation's step (route <c>pager</c>).</summary>
        private sealed class Receivers : IAsyncDisposable
        {
            public required WebhookTests.Receiver Own { get; init; }
            public required WebhookTests.Receiver Pager { get; init; }
            public static async Task<Receivers> StartAsync() => new() { Own = await WebhookTests.Receiver.StartAsync(), Pager = await WebhookTests.Receiver.StartAsync() };
            public async ValueTask DisposeAsync()
            {
                await Own.DisposeAsync();
                await Pager.DisposeAsync();
            }
        }

        /// <summary>A host in <paramref name="mode"/>, its stages quick; its start fails when <paramref name="down"/> (a broken provisioning file).</summary>
        private static async Task<(ApiTests.ApiFactory Factory, string? File)> HostAsync(ScratchDatabase database, string mode, bool down = false)
        {
            var settings = new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = mode,
                ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
                ["Notifications:Stages:Planner:Poll"] = "00:00:00.200",
                ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.200",
                ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.200",
            };
            string? file = null;
            if (down)
            {
                file = Path.Combine(Path.GetTempPath(), $"juice-obs-switch-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(file, $$"""{ "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }""", Ct);
                settings["Notifications:Provisioning:Path"] = file;
            }
            var factory = new ApiTests.ApiFactory(database, settings: settings);
            _ = factory.Server;
            Assert.Equal(down, ((PipelineStart)factory.Services.GetService(typeof(PipelineStart))!).Failure is not null);
            return (factory, file);
        }

        private static async Task<T> PipelineAsync<T>(WebApplicationFactory<Program> factory, Juice.MediatR.IRequest<T> command)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            using var context = Juice.Notifications.Data.NotificationsMessageContext.Begin("test");
            return await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
        }

        /// <summary>
        /// The pipeline's configuration (kept in its tables, so every later host has it) and the platform's policy: a firing's own recipients
        /// are the <c>alerts</c> route's receiver; its escalation's one step, a minute after it fired, the <c>pager</c> route's.
        /// </summary>
        private static async Task ConfigureAsync(WebApplicationFactory<Program> factory, ScratchDatabase database, Receivers receivers)
        {
            Environment.SetEnvironmentVariable(SecretVariable, "shadow-switch-signing-secret");
            var tenant = Guid.Parse(LocalStack.TenantKey);
            Assert.Null((await PipelineAsync(factory, new PutAllowlistEntry(tenant, receivers.Own.Address.ToString(), [receivers.Own.Port, receivers.Pager.Port]))).Code);
            var integration = await PipelineAsync(factory, new PutIntegration(tenant, "hooks", "webhook", "webhook", JsonDocument.Parse("{}").RootElement, $"env:{SecretVariable}", 0, true));
            Assert.True(integration.Code is null, $"{integration.Code} {integration.Message}");
            foreach (var (key, receiver) in new[] { ("own", receivers.Own), ("pager", receivers.Pager) })
            {
                var target = await PipelineAsync(factory, new PutTarget(tenant, "hooks", key, receiver.Url, null, true));
                Assert.True(target.Code is null, $"{target.Code} {target.Message}");
            }
            var alerts = await PipelineAsync(factory, new PutRoute(tenant, "alerts", JsonDocument.Parse("{}").RootElement, ["own"], true));
            Assert.True(alerts.Code is null, $"{alerts.Code} {alerts.Message}");
            // Only an escalation step names it: no notification's own labels match it.
            var pager = await PipelineAsync(factory, new PutRoute(tenant, "pager", JsonDocument.Parse("""{ "type": "nothing.matches" }""").RootElement, ["pager"], true));
            Assert.True(pager.Code is null, $"{pager.Code} {pager.Message} {string.Join("; ", pager.Errors?.Select(e => e.Pointer + " " + e.Message) ?? [])}");
            var policy = await PipelineAsync(factory, new PutEscalationPolicy(tenant, "pager",
                JsonSerializer.SerializeToElement(new[] { new { afterMinutes = 1, recipients = new[] { new { kind = "route", key = "pager" } } } }), true));
            Assert.True(policy.Kind is Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created,
                policy.Message + " " + string.Join("; ", policy.Errors?.Select(e => $"{e.Pointer} {e.Message}") ?? []));
            await using var db = database.Context();
            db.EscalationPolicies.Add(new EscalationPolicy
            {
                TenantId = await TenantAsync(database), Key = "pager", Name = "pager", MinimumSeverity = AlertSeverity.Info,
                Steps = [new EscalationStep { AfterMinutes = 1, EndpointKeys = ["ops"] }],
            });
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>A firing, committed and not planned yet, that fired <paramref name="ago"/>.</summary>
        private static async Task<(Guid Alert, Guid Transition)> FireAsync(ScratchDatabase database, TimeSpan ago)
        {
            await using var db = database.Context();
            var tenant = await TenantAsync(database);
            var fired = DateTimeOffset.UtcNow - ago;
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                Definition = "{}", DefinitionHash = "h", LoadedAt = fired, ManagedBy = Data.ManagedBy.Api };
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = "orders-errors|service:shop/orders@production", SubjectKey = "service:shop/orders@production",
                SubjectType = HealthSubject.Service, Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = fired, FiredAt = fired, LastEvaluatedAt = fired,
            };
            var transition = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = fired, Reason = "for_duration_met" };
            db.AddRange(rule, alert, transition);
            await db.SaveChangesAsync(Ct);
            return (alert.Id, transition.Id);
        }

        private static NotificationsReleasePass Release(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new Webhooks.WebhookOptions()), TimeProvider.System,
                NullLogger<NotificationsReleasePass>.Instance);

        private static async Task<Dictionary<string, long>> DeliveriesAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT status, count(*) FROM notifications.deliveries GROUP BY status", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var counts = new Dictionary<string, long>();
            while (await reader.ReadAsync(Ct)) counts[reader.GetString(0)] = reader.GetInt64(1);
            return counts;
        }

        private static async Task<string?> EscalationStateAsync(ScratchDatabase database)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT state FROM notifications.escalations LIMIT 1", connection);
            return (string?)await command.ExecuteScalarAsync(Ct);
        }

        private static async Task WaitAsync(Func<Task<bool>> done, string what, ScratchDatabase database)
        {
            for (var i = 0; i < 150; i++)
            {
                if (await done()) return;
                await Task.Delay(200, Ct);
            }
            Assert.Fail($"timed out waiting for {what}: deliveries {string.Join(", ", (await DeliveriesAsync(database)).Select(p => $"{p.Key}={p.Value}"))}");
        }

        private static async Task<NotificationHold> HoldAsync(ScratchDatabase database, Guid transition)
        {
            await using var db = database.Context();
            return await db.NotificationHolds.AsNoTracking().SingleAsync(h => h.TransitionId == transition, Ct);
        }

        private static async Task<(string Reason, string? Pipeline)> KeptAsync(ScratchDatabase database, Guid transition)
            => await HoldAsync(database, transition) is var hold ? (hold.Reason, hold.Pipeline) : default;

        /// <summary>Counts <c>juice.observability.notifications.dry_duplicates</c> while it lives.</summary>
        private sealed class DryDuplicates : IDisposable
        {
            private readonly MeterListener _listener = new();
            private long _count;
            public long Count => Interlocked.Read(ref _count);

            public DryDuplicates()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "Juice.Observability" && instrument.Name == "juice.observability.notifications.dry_duplicates")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
                _listener.Start();
            }

            public void Dispose() => _listener.Dispose();
        }

        [Fact]
        public async Task A_firing_kept_while_down_in_shadow_is_dry_for_its_own_recipients_and_its_step_after_the_switch_is_sent_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await ConfigureAsync(up, database, receivers);
            }
            // In shadow, the pipeline down: legacy sends the firing, the platform keeps it for the pipeline.
            var (_, firing) = await FireAsync(database, TimeSpan.FromMinutes(3));
            var (down, file) = await HostAsync(database, "shadow", down: true);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, firing);
                }
            }
            finally
            {
                File.Delete(file!);
            }
            Assert.Equal((NotificationHoldReasons.PipelineUnavailable, "shadow"), await KeptAsync(database, firing));

            // Switched to new, the pipeline up: the kept firing goes to the pipeline dry, and its escalation (a minute after it fired, so due) is real.
            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await Release(now).ReleaseOnceAsync(Ct);
                await WaitAsync(async () => receivers.Pager.Requests.Count >= 1, "the escalation's step to reach the pager", database);
                await WaitAsync(async () => (await DeliveriesAsync(database)).ContainsKey("WouldSend"), "the firing's own recipient to be recorded dry", database);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            Assert.Empty(receivers.Own.Requests);
            Assert.Single(receivers.Pager.Requests);
            Assert.Equal(NotificationHoldOutcomes.Sent, (await HoldAsync(database, firing)).Outcome);
        }

        [Fact]
        public async Task A_resolve_kept_while_down_in_shadow_stops_the_escalation_once_replayed_in_new_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            // In shadow, the pipeline up: the firing is planned dry, and its escalation starts (its step a minute away).
            var (alert, firing) = await FireAsync(database, TimeSpan.Zero);
            var (shadow, _) = await HostAsync(database, "shadow");
            await using (shadow)
            {
                await ConfigureAsync(shadow, database, receivers);
                await PlanAsync(shadow, database, firing);
                await WaitAsync(async () => await EscalationStateAsync(database) == "Running", "the escalation to start", database);
            }
            // Still in shadow, the pipeline down: the alert resolves, and the platform keeps the resolve.
            var resolved = await ResolveAsync(database, alert);
            var (down, file) = await HostAsync(database, "shadow", down: true);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, resolved);
                }
            }
            finally
            {
                File.Delete(file!);
            }

            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await Release(now).ReleaseOnceAsync(Ct);
                Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
                await WaitAsync(async () => await EscalationStateAsync(database) == "Stopped", "the escalation to stop", database);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            // Nobody was sent anything: the firing and the resolve were legacy's, and the step never came.
            Assert.Empty(receivers.Own.Requests);
            Assert.Empty(receivers.Pager.Requests);
        }

        [Fact]
        public async Task A_firing_kept_while_down_in_new_is_sent_for_real_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await ConfigureAsync(up, database, receivers);
            }
            var (_, firing) = await FireAsync(database, TimeSpan.Zero);
            var (down, file) = await HostAsync(database, "new", down: true);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, firing);
                }
            }
            finally
            {
                File.Delete(file!);
            }
            Assert.Equal((NotificationHoldReasons.PipelineUnavailable, "new"), await KeptAsync(database, firing));

            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await Release(now).ReleaseOnceAsync(Ct);
                await WaitAsync(async () => receivers.Own.Requests.Count >= 1, "the firing to reach its recipient", database);
            }
            Assert.Single(receivers.Own.Requests);
            Assert.False((await DeliveriesAsync(database)).ContainsKey("WouldSend"));
        }

        [Fact]
        public async Task A_firing_held_by_a_silence_in_shadow_and_released_after_the_switch_is_sent_once_for_real_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var dry = new DryDuplicates();
            var silence = await SilenceAsync(database);
            var (_, firing) = await FireAsync(database, TimeSpan.Zero);
            var (shadow, _) = await HostAsync(database, "shadow");
            await using (shadow)
            {
                await ConfigureAsync(shadow, database, receivers);
                await PlanAsync(shadow, database, firing);
            }
            // Held by the silence, as legacy holds it: nothing was planned, dry or real.
            Assert.Equal((NotificationHoldReasons.Silence, "shadow"), await KeptAsync(database, firing));
            Assert.Empty(await RequestsAsync(database));

            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await using (var db = database.Context())
                {
                    await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow), Ct);
                }
                await Release(now).ReleaseOnceAsync(Ct);
                await WaitAsync(async () => receivers.Own.Requests.Count >= 1, "the released firing to reach its recipient", database);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            Assert.Single(receivers.Own.Requests);
            Assert.Equal(0, dry.Count);
        }

        [Fact]
        public async Task A_firing_planned_dry_in_shadow_and_sent_again_in_new_is_counted_as_a_dry_duplicate_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var dry = new DryDuplicates();
            var (alert, firing) = await FireAsync(database, TimeSpan.Zero);
            var (shadow, _) = await HostAsync(database, "shadow");
            await using (shadow)
            {
                await ConfigureAsync(shadow, database, receivers);
                await PlanAsync(shadow, database, firing);
                await WaitAsync(async () => (await DeliveriesAsync(database)).ContainsKey("WouldSend"), "the firing to be recorded dry", database);
            }
            Assert.Equal(0, dry.Count);

            // The breach: after the switch, the adapter sends the same transition again.
            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await using var scope = now.Services.CreateAsyncScope();
                using var context = PlatformMessageContext.Begin("test");
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                await scope.ServiceProvider.GetRequiredService<ProducerAdapter>().TransitionAsync(await TenantAsync(database), alert, firing, Ct);
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }
            Assert.Equal(1, dry.Count);
            Assert.Empty(receivers.Own.Requests);
        }

        /// <summary>
        /// Both apply (architecture §6.4 A): kept while the pipeline was down in shadow, under a silence. The silence wins: legacy held it too,
        /// so once the silence ends after the switch it is sent for real.
        /// </summary>
        [Fact]
        public async Task A_firing_kept_under_a_silence_while_down_in_shadow_is_the_silences_and_sent_for_real_after_the_switch_Async()
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await ConfigureAsync(up, database, receivers);
            }
            var silence = await SilenceAsync(database);
            var (_, firing) = await FireAsync(database, TimeSpan.Zero);
            var (down, file) = await HostAsync(database, "shadow", down: true);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, firing);
                }
            }
            finally
            {
                File.Delete(file!);
            }
            Assert.Equal((NotificationHoldReasons.Silence, "shadow"), await KeptAsync(database, firing));

            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await using (var db = database.Context())
                {
                    await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow), Ct);
                }
                await Release(now).ReleaseOnceAsync(Ct);
                await WaitAsync(async () => receivers.Own.Requests.Count >= 1, "the released firing to reach its recipient", database);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            Assert.Single(receivers.Own.Requests);
            Assert.False((await DeliveriesAsync(database)).ContainsKey("WouldSend"));
        }

        /// <summary>
        /// A send parked (an action request's notice, say, kept on a concurrent change or while the pipeline was down) replays dry when it was kept
        /// in shadow, legacy having sent it, and for real when kept in new.
        /// </summary>
        [Theory]
        [InlineData("shadow")]
        [InlineData("new")]
        public async Task A_parked_send_replays_dry_when_kept_in_shadow_and_real_when_kept_in_new_Async(string keptIn)
        {
            LocalStack.RequireAvailable();
            await using var receivers = await Receivers.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (now, _) = await HostAsync(database, "new");
            await using (now)
            {
                await ConfigureAsync(now, database, receivers);
                // What the adapter would have sent (a firing's request, as a parked send carries any).
                var (alertId, transitionId) = await FireAsync(database, TimeSpan.Zero);
                Juice.Notifications.NotificationToSend notice;
                await using (var scope = now.Services.CreateAsyncScope())
                {
                    var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                    var alert = await platform.Alerts.SingleAsync(a => a.Id == alertId, Ct);
                    var rule = await platform.Rules.SingleAsync(r => r.Id == alert.RuleId, Ct);
                    var transition = await platform.AlertTransitions.SingleAsync(t => t.Id == transitionId, Ct);
                    notice = scope.ServiceProvider.GetRequiredService<ProducerAdapter>().Firing(rule, alert, transition,
                        new Rules.Notifications.AlertContext(rule.Key, alert.SubjectKey, alert.Severity), null);
                }
                await using (var db = database.Context())
                {
                    db.NotificationParkedCalls.Add(new NotificationParkedCall
                    {
                        TenantId = await TenantAsync(database), Kind = NotificationParkedCalls.Send, CreatedAt = DateTimeOffset.UtcNow, Pipeline = keptIn,
                        Request = JsonSerializer.Serialize(notice, JsonSerializerOptions.Web),
                    });
                    await db.SaveChangesAsync(Ct);
                }

                await Release(now).ReleaseOnceAsync(Ct);
                var status = keptIn == "shadow" ? "WouldSend" : "Delivered";
                await WaitAsync(async () => (await DeliveriesAsync(database)).ContainsKey(status), $"the notice's delivery to be {status}", database);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            await using (var db = database.Context())
            {
                Assert.NotNull(Assert.Single(await db.NotificationParkedCalls.AsNoTracking().ToListAsync(Ct)).ReplayedAt);
            }
            Assert.Equal(keptIn == "shadow" ? 0 : 1, receivers.Own.Requests.Count);
        }

        // ─── A held resolve at release (review #13): resolved then only if it wasn't at plan, and never once the alert fired again ────────

        /// <summary>The alert fires again: a new firing transition, committed and not planned yet.</summary>
        private static async Task<Guid> RefireAsync(ScratchDatabase database, Guid alertId)
        {
            await using var db = database.Context();
            var alert = await db.Alerts.SingleAsync(a => a.Id == alertId, Ct);
            var now = DateTimeOffset.UtcNow;
            var transition = new AlertTransition { TenantId = alert.TenantId, AlertId = alert.Id, FromState = AlertState.Resolved, ToState = AlertState.Firing, At = now, Reason = "for_duration_met" };
            db.AlertTransitions.Add(transition);
            (alert.State, alert.ResolvedAt, alert.FiredAt) = (AlertState.Firing, null, now);
            await db.SaveChangesAsync(Ct);
            return transition.Id;
        }

        private static async Task EndAsync(ScratchDatabase database, Guid silence)
        {
            await using var db = database.Context();
            await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow), Ct);
        }

        private static async Task<(ApiTests.ApiFactory Factory, string? File)> DownAsync(ScratchDatabase database) => await HostAsync(database, "new", down: true);

        /// <summary>
        /// Reviewer-2's case: a resolve held under a silence (resolved at plan), the silence ends and the alert fires again before the release
        /// pass gets to it. Its escalation is the recurrence's: the release pass leaves it open, so a recurrence right after maintenance is paged.
        /// </summary>
        [Fact]
        public async Task A_held_resolve_released_after_the_alert_fired_again_leaves_the_recurrence_open_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, firing) = await FireAsync(database, TimeSpan.Zero);
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await PlanAsync(up, database, firing);
                var silence = await SilenceAsync(database);
                var resolved = await ResolveAsync(database, alert);
                await PlanAsync(up, database, resolved);
                Assert.Equal((NotificationHoldReasons.Silence, true), await HoldAsync(database, resolved) is var h ? (h.Reason, h.ResolveAsked) : default);
                Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));

                await EndAsync(database, silence);
                var again = await RefireAsync(database, alert);
                await PlanAsync(up, database, again);
                Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));

                await Release(up).ReleaseOnceAsync(Ct);

                Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));
                Assert.NotNull((await HoldAsync(database, resolved)).DecidedAt);
            }
        }

        /// <summary>Both apply: a resolve kept under a silence while the pipeline was down wasn't resolved at plan, so the release pass resolves it.</summary>
        [Fact]
        public async Task A_resolve_kept_under_a_silence_while_down_is_resolved_at_release_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, firing) = await FireAsync(database, TimeSpan.Zero);
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await PlanAsync(up, database, firing);
            }
            var silence = await SilenceAsync(database);
            var resolved = await ResolveAsync(database, alert);
            var (down, file) = await DownAsync(database);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, resolved);
                }
            }
            finally
            {
                File.Delete(file!);
            }
            Assert.Equal((NotificationHoldReasons.Silence, false), await HoldAsync(database, resolved) is var h ? (h.Reason, h.ResolveAsked) : default);
            Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));

            (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await EndAsync(database, silence);
                await Release(up).ReleaseOnceAsync(Ct);
            }
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
        }

        /// <summary>A resolve kept while the pipeline was down, then held by a silence made later: still not resolved at plan, so resolved at release.</summary>
        [Fact]
        public async Task A_resolve_kept_while_down_and_moved_under_a_later_silence_is_resolved_at_release_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, firing) = await FireAsync(database, TimeSpan.Zero);
            var (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await PlanAsync(up, database, firing);
            }
            var resolved = await ResolveAsync(database, alert);
            var (down, file) = await DownAsync(database);
            try
            {
                await using (down)
                {
                    await PlanAsync(down, database, resolved);
                }
            }
            finally
            {
                File.Delete(file!);
            }

            // A silence covers it by the time the pipeline is back: the release pass moves it under that silence.
            var silence = await SilenceAsync(database);
            (up, _) = await HostAsync(database, "new");
            await using (up)
            {
                await Release(up).ReleaseOnceAsync(Ct);
                Assert.Equal(silence, (await HoldAsync(database, resolved)).SilenceId);
                Assert.Equal("Open", await CorrelationStateAsync(database, $"alert:{alert}"));

                await EndAsync(database, silence);
                await Release(up).ReleaseOnceAsync(Ct);
            }
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
        }
    }
}
