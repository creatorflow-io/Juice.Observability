using System.Net;
using Juice.Observability.Data;
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
    /// Silences as holds in the pipeline (p5-d3, proof point 3 with notifications' CorrelationHoldTests): a silence covering an open alert whose
    /// firing was sent holds its correlation, in the silence's own transaction; once no silence covers it, it is released. The platform produces
    /// requests as in <c>new</c>; the pipeline's stages don't run here, so what a held or released step does is notifications' to test.
    /// </summary>
    public class SilenceHoldTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed record Hold(DateTimeOffset? HeldAt, string? Reason);

        private static async Task<Hold?> HoldAsync(ScratchDatabase database, Guid alert)
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT held_at, hold_reason FROM notifications.correlations WHERE key = @key", connection);
            command.Parameters.AddWithValue("key", $"alert:{alert}");
            await using var reader = await command.ExecuteReaderAsync(Ct);
            return await reader.ReadAsync(Ct) ? new Hold(reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
        }

        private static async Task<List<AlertHold>> RowsAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.AlertHolds.AsNoTracking().ToListAsync(Ct);
        }

        /// <summary>A firing planned and sent: the pipeline has its correlation.</summary>
        private static async Task<Guid> FiringSentAsync(WebApplicationFactory<Program> factory, ScratchDatabase database)
        {
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            Assert.NotNull(await HoldAsync(database, alert));
            return alert;
        }

        private static async Task<(Caller Operator, string Silences)> OperatorAsync(WebApplicationFactory<Program> factory, ScratchDatabase database, HttpClient client)
        {
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            return (@operator, $"/api/v1/tenants/{tenant}/silences");
        }

        private static async Task<Guid> CreateAsync(Caller @operator, string silences, string service = "shop/orders")
        {
            var created = await @operator.PostAsync(silences, new { comment = "maintenance", service, endsAt = DateTimeOffset.UtcNow.AddHours(1) });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await Caller.JsonAsync(created)).GetProperty("id").GetGuid();
        }

        private static SilenceHoldPass Pass(WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()), NullLogger<SilenceHoldPass>.Instance);

        [Fact]
        public async Task Creating_a_silence_holds_the_alert_and_cancelling_it_releases_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);

            var silence = await CreateAsync(@operator, silences);

            // In the create's own transaction: held when the response comes back.
            var hold = await HoldAsync(database, alert);
            Assert.Equal(ProducerAdapter.SilenceReason(silence), hold!.Reason);
            Assert.NotNull(hold.HeldAt);
            Assert.Equal((alert, silence), Assert.Single(await RowsAsync(database)) is var row ? (row.AlertId, row.SilenceId) : default);

            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{silence}")).StatusCode);

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task Overlapping_silences_release_only_after_both_end_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var first = await CreateAsync(@operator, silences);
            var second = await CreateAsync(@operator, silences, service: "orders");

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{first}");

            // Still covered: the hold moves to the silence still covering it.
            Assert.Equal(ProducerAdapter.SilenceReason(second), (await HoldAsync(database, alert))!.Reason);
            Assert.Equal(second, Assert.Single(await RowsAsync(database)).SilenceId);

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{second}");
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_silence_that_starts_later_is_held_by_the_pass_once_it_starts_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var alert = await FiringSentAsync(factory, database);
            var starts = DateTimeOffset.UtcNow.AddSeconds(2);
            await using (var db = database.Context())
            {
                db.Silences.Add(new Silence
                {
                    TenantId = await TenantAsync(database), Kind = SilenceKind.Maintenance, Comment = "window", CreatedBy = "erin", CreatedAt = DateTimeOffset.UtcNow,
                    StartsAt = starts, EndsAt = starts.AddHours(1), Service = "shop/orders",
                });
                await db.SaveChangesAsync(Ct);
            }

            Assert.Equal(0, await Pass(factory).RunOnceAsync(Ct));
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            await Task.Delay(starts - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300), Ct);
            Assert.Equal(1, await Pass(factory).RunOnceAsync(Ct));

            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
            Assert.Single(await RowsAsync(database));
        }

        [Fact]
        public async Task A_silence_ending_by_time_is_released_by_the_pass_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var silence = await CreateAsync(@operator, silences);
            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
            await using (var db = database.Context())
            {
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }

            Assert.Equal(1, await Pass(factory).RunOnceAsync(Ct));

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_hold_survives_the_resolve_and_is_released_when_the_silence_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var silence = await CreateAsync(@operator, silences);

            await PlanAsync(factory, database, await ResolveAsync(database, alert));
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"alert:{alert}"));
            // Q6: the resolve leaves the hold; alert_holds still says what to release.
            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);

            await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{silence}");
            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task A_rolled_back_change_leaves_the_correlation_unheld_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            _ = factory.Server;
            var alert = await FiringSentAsync(factory, database);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var now = DateTimeOffset.UtcNow;
                db.Silences.Add(new Silence
                {
                    TenantId = await TenantAsync(database), Kind = SilenceKind.Silence, Comment = "x", CreatedBy = "erin", CreatedAt = now, StartsAt = now.AddMinutes(-1),
                    EndsAt = now.AddHours(1), Service = "shop/orders",
                });
                await db.SaveChangesAsync(Ct);
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SilenceHolds>().ReconcileAsync(null, Ct));
                await transaction.RollbackAsync(Ct);
            }

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
            Assert.Empty(await RowsAsync(database));
        }

        [Fact]
        public async Task An_alert_whose_firing_a_silence_held_has_nothing_to_hold_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            await CreateAsync(@operator, silences);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            // Its firing is a notification hold (I65), never sent: no correlation, so no hold, and no failure.
            Assert.Equal(0, await Pass(factory).RunOnceAsync(Ct));
            Assert.Null(await HoldAsync(database, alert));
            Assert.Empty(await RowsAsync(database));
        }

        /// <summary>A host on the same database in new; its provisioning file broken when <paramref name="broken"/> (its start fails).</summary>
        private static async Task<(ApiTests.ApiFactory Factory, string? File)> NewHostAsync(ScratchDatabase database, bool broken)
        {
            var settings = new Dictionary<string, string> { ["Notifications:Pipeline"] = "new" };
            string? file = null;
            if (broken)
            {
                file = Path.Combine(Path.GetTempPath(), $"juice-obs-broken-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(file, $$"""{ "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }""", Ct);
                settings["Notifications:Provisioning:Path"] = file;
            }
            return (new ApiTests.ApiFactory(database, settings: settings), file);
        }

        [Fact]
        public async Task A_silence_cancelled_while_the_pipeline_is_down_is_released_once_it_is_up_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid alert, silence;
            var (up, _) = await NewHostAsync(database, broken: false);
            await using (up)
            {
                using var client = up.CreateClient();
                alert = await FiringSentAsync(up, database);
                var (@operator, silences) = await OperatorAsync(up, database, client);
                silence = await CreateAsync(@operator, silences);
                Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
            }
            var (down, file) = await NewHostAsync(database, broken: true);
            try
            {
                await using (down)
                {
                    using var client = down.CreateClient();
                    var (@operator, silences) = await OperatorAsync(down, database, client);
                    Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{silences}/{silence}")).StatusCode);
                    // Nothing was asked of the pipeline, and the platform's record of the hold stays: it is what says to release it later.
                    Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);
                    Assert.Single(await RowsAsync(database));
                }
            }
            finally
            {
                File.Delete(file!);
            }
            (up, _) = await NewHostAsync(database, broken: false);
            await using (up)
            {
                _ = up.Server;
                await Pass(up).RunOnceAsync(Ct);
                Assert.Null((await HoldAsync(database, alert))!.HeldAt);
                Assert.Empty(await RowsAsync(database));
            }
        }

        [Fact]
        public async Task A_release_and_a_new_silence_at_once_leave_the_hold_under_the_new_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var first = await CreateAsync(@operator, silences);
            // The first silence ends by time, and someone makes a second just as the pass runs.
            await using (var db = database.Context())
            {
                await db.Silences.Where(s => s.Id == first).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndsAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }
            var pass = Pass(factory).RunOnceAsync(Ct);
            var second = CreateAsync(@operator, silences, service: "orders");
            await Task.WhenAll(pass, second);

            Assert.Equal(ProducerAdapter.SilenceReason(await second), (await HoldAsync(database, alert))!.Reason);
            Assert.Equal(await second, Assert.Single(await RowsAsync(database)).SilenceId);
        }

        [Fact]
        public async Task A_hold_with_no_record_is_released_by_the_pass_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = Factory(database);
            using var client = factory.CreateClient();
            var alert = await FiringSentAsync(factory, database);
            var (@operator, silences) = await OperatorAsync(factory, database, client);
            var silence = await CreateAsync(@operator, silences);
            // The record is lost (as an older release lost it) and the silence ends: the pipeline's hold alone remains.
            await using (var db = database.Context())
            {
                await db.AlertHolds.ExecuteDeleteAsync(Ct);
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow), Ct);
            }
            Assert.NotNull((await HoldAsync(database, alert))!.HeldAt);

            Assert.Equal(1, await Pass(factory).RunOnceAsync(Ct));

            Assert.Null((await HoldAsync(database, alert))!.HeldAt);
        }

        /// <summary>
        /// Proof point 3's timing: a firing held under a silence past its policy's first step gets that step at release, as legacy sends it then,
        /// not a[0] after the release. The request carries when the alert fired (OccurredAt), which the pipeline times the first step from.
        /// </summary>
        [Fact]
        public async Task A_firing_held_past_its_first_step_gets_it_at_release_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Stages:Planner:Poll"] = "00:00:00.2", ["Notifications:Stages:Escalation:Poll"] = "00:00:00.2",
            });
            _ = factory.Server;
            var tenant = await TenantAsync(database);
            // The policy: the platform's (so the firing names it) and the pipeline's, its first step a minute after the firing.
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = tenant, Key = "pager", Name = "pager", MinimumSeverity = AlertSeverity.Info,
                });
                await db.SaveChangesAsync(Ct);
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                using var context = PlatformMessageContext.Begin("test");
                var put = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(new Juice.Notifications.Configuration.PutEscalationPolicy(
                    Guid.Parse(LocalStack.TenantKey), "pager", System.Text.Json.JsonSerializer.SerializeToElement(new[]
                    {
                        new { afterMinutes = 1, recipients = new[] { new { kind = "route", key = "pager" } } },
                    }), true), Ct);
                Assert.True(put.Kind is Juice.Notifications.OutcomeKind.Ok or Juice.Notifications.OutcomeKind.Created,
                    put.Message + " " + string.Join("; ", put.Errors?.Select(e => $"{e.Pointer} {e.Message}") ?? []));
            }
            var silence = await SilenceAsync(database);
            // Fired three minutes ago, under the silence: its firing is held.
            Guid transition;
            await using (var db = database.Context())
            {
                var fired = DateTimeOffset.UtcNow.AddMinutes(-3);
                var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical,
                    Definition = "{}", DefinitionHash = "h", LoadedAt = fired, ManagedBy = Data.ManagedBy.Api };
                var alert = new Alert
                {
                    TenantId = tenant, RuleId = rule.Id, DedupKey = "orders-errors|service:shop/orders@production", SubjectKey = "service:shop/orders@production",
                    SubjectType = HealthSubject.Service, Severity = AlertSeverity.Critical, State = AlertState.Firing, BreachStartedAt = fired, FiredAt = fired, LastEvaluatedAt = fired,
                };
                var t = new AlertTransition { TenantId = tenant, AlertId = alert.Id, FromState = AlertState.Pending, ToState = AlertState.Firing, At = fired, Reason = "for_duration_met" };
                db.AddRange(rule, alert, t);
                await db.SaveChangesAsync(Ct);
                transition = t.Id;
            }
            await PlanAsync(factory, database, transition);
            await using (var db = database.Context())
            {
                Assert.Single(await db.NotificationHolds.ToListAsync(Ct));
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow), Ct);
            }

            var released = DateTimeOffset.UtcNow;
            await new NotificationsReleasePass(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()),
                TimeProvider.System, NullLogger<NotificationsReleasePass>.Instance).ReleaseOnceAsync(Ct);

            // The pipeline's escalation of it: its first step due at once (two minutes late already), not a minute after the release.
            (int Step, DateTimeOffset? NextAt)? escalation = null;
            for (var i = 0; i < 100 && escalation is null; i++)
            {
                await using var connection = new NpgsqlConnection(database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var read = new NpgsqlCommand("SELECT next_step, next_at FROM notifications.escalations LIMIT 1", connection);
                await using var reader = await read.ExecuteReaderAsync(Ct);
                if (await reader.ReadAsync(Ct)) escalation = (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
                else await Task.Delay(100, Ct);
            }
            Assert.NotNull(escalation);
            Assert.True(escalation.Value.Step >= 1 || escalation.Value.NextAt <= released.AddSeconds(10),
                $"step {escalation.Value.Step}, due {escalation.Value.NextAt:O}, released {released:O}: the first step should be due at the release");
        }
    }
}
