using System.Net;
using System.Text.Json;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 11 milestone 2: the workers' heartbeats, the platform's status, and <c>/health/platform</c> (plan R6, R7).</summary>
    public class PlatformStatusTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        private static readonly DiscoveryOptions Discovery = new();   // Lag 60 s, Interval 30 s
        private const string Self = "a";

        internal static WorkerHeartbeat Row(string worker, string replica, bool held = false, bool leased = true, double interval = 30, bool enabled = true,
            TimeSpan? endedAgo = null, TimeSpan? runningFor = null, TimeSpan? seenAgo = null, TimeSpan? registeredAgo = null, TimeSpan? heldFor = null,
            DateTimeOffset? now = null, TimeSpan? startedAgo = null)
        {
            var at = now ?? Now;
            return new()
            {
                Worker = worker, Replica = replica, Version = "1.3.0", Enabled = enabled, Leased = leased, Held = held, IntervalSeconds = interval,
                HeldSince = held ? at - (heldFor ?? TimeSpan.FromMinutes(10)) : null,
                LastEndedAt = endedAgo is { } e ? at - e : null, LastOutcome = endedAgo is null ? null : "success",
                RunningSince = runningFor is { } r ? at - r : null, RegisteredAt = enabled ? at - (registeredAgo ?? TimeSpan.FromHours(1)) : null,
                SeenAt = at - (seenAgo ?? TimeSpan.FromSeconds(2)), ReplicaStartedAt = at - (startedAgo ?? TimeSpan.FromHours(2)),
            };
        }

        private static PlatformSampler.Sample Sample(bool postgres = true, bool? clickHouse = true, bool? collector = true, double outbox = 0,
            double watermarkLag = 70, DateTimeOffset? at = null) => new((at ?? Now) - TimeSpan.FromSeconds(5),
            new PlatformSampler.PostgresSample(postgres, 0.01, 1e6, watermarkLag, 0, 0, outbox),
            clickHouse is { } ch ? new PlatformSampler.ClickHouseSample(ch, 1, 1e9, new Dictionary<string, long>(), [], [], []) : null,
            collector is { } co ? new PlatformSampler.CollectorSample(co, 0.01) : null);

        /// <summary>The rows always present: this replica's own (so its heartbeats are being written), and discovery running.</summary>
        private static List<WorkerHeartbeat> Base() => [Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3)),
            Row("discovery", Self, held: true, endedAgo: TimeSpan.FromSeconds(10))];

        private static PlatformStatus.Report Evaluate(IEnumerable<WorkerHeartbeat>? rows, PlatformSampler.Sample? sample = null)
            => PlatformStatus.Evaluate(rows?.ToList(), Now, sample ?? Sample(), Now, Discovery, Self);

        private static PlatformStatus.WorkerStatus Worker(PlatformStatus.Report report, string name) => Assert.Single(report.Workers, w => w.Name == name);

        [Fact]
        public void A_platform_whose_workers_pass_on_time_is_ok()
        {
            var report = Evaluate([.. Base(),
                Row("discovery", "b"),                                           // the standby: never ran, not stale
                Row("sampler", Self, leased: false, interval: 2, endedAgo: TimeSpan.FromSeconds(1)),
                Row("sampler", "b", leased: false, interval: 2, runningFor: TimeSpan.FromSeconds(3)),
            ]);
            Assert.Equal(PlatformStatus.Ok, report.Status);
            Assert.Equal("ok", Worker(report, "discovery").State);
            Assert.Equal(Self, Worker(report, "discovery").Holder);
            Assert.Equal("ok", Worker(report, "sampler").State);
            Assert.Equal([Self, "b"], report.Replicas.Select(r => r.Name));
            Assert.All(report.Dependencies, d => Assert.Equal("up", d.State));
        }

        [Fact]
        public void A_holder_without_a_pass_past_its_threshold_is_stale_and_one_stuck_in_a_pass_is_stuck()
        {
            var report = Evaluate([.. Base(),
                Row("health", Self, held: true, endedAgo: TimeSpan.FromSeconds(95)),         // 3 × 30 s = 90 s
                Row("slos", Self, held: true, endedAgo: TimeSpan.FromMinutes(5), runningFor: TimeSpan.FromSeconds(120)),
                Row("notifications", Self, held: true, interval: 2, endedAgo: TimeSpan.FromSeconds(50)),   // 3 × 2 s, but never under 60 s
            ]);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
            Assert.Equal("stale", Worker(report, "health").State);
            Assert.Equal("stuck", Worker(report, "slos").State);
            Assert.Equal(120, Worker(report, "slos").RunningForSeconds);
            Assert.Equal("ok", Worker(report, "notifications").State);
            Assert.Equal(60, Worker(report, "notifications").ThresholdSeconds);
        }

        /// <summary>Two replicas say they hold the lease (one lost it and hasn't noticed): the one that took it last is the holder.</summary>
        [Fact]
        public void The_latest_to_take_the_lease_is_the_holder()
        {
            var report = Evaluate([.. Base(),
                Row("health", "old", held: true, heldFor: TimeSpan.FromMinutes(30), runningFor: TimeSpan.FromMinutes(5)),
                Row("health", "new", held: true, heldFor: TimeSpan.FromSeconds(20), endedAgo: TimeSpan.FromSeconds(5)),
            ]);
            Assert.Equal("new", Worker(report, "health").Holder);
            Assert.Equal("ok", Worker(report, "health").State);
        }

        [Fact]
        public void Nobody_holding_a_lease_is_a_handover_for_a_while_then_unheld()
        {
            var handover = Evaluate([.. Base(), Row("rules", Self, interval: 5, endedAgo: TimeSpan.FromSeconds(20)), Row("rules", "b")]);
            Assert.Equal("handing_over", Worker(handover, "rules").State);
            Assert.Equal(PlatformStatus.Ok, handover.Status);

            var unheld = Evaluate([.. Base(), Row("rules", Self, interval: 5, endedAgo: TimeSpan.FromMinutes(3)), Row("rules", "b")]);
            Assert.Equal("unheld", Worker(unheld, "rules").State);
            Assert.Equal(PlatformStatus.Degraded, unheld.Status);

            // A rolling upgrade from 1.2.0: its holder writes no row, so the new replica reports the worker as starting until it takes it over.
            var starting = Evaluate([.. Base(), Row("rules", Self, interval: 5, registeredAgo: TimeSpan.FromSeconds(10))]);
            Assert.Equal("starting", Worker(starting, "rules").State);
            Assert.Equal(PlatformStatus.Ok, starting.Status);
        }

        /// <summary>
        /// Review finding: a worker whose replicas all stopped mustn't read as switched off. Enabled on a replica gone within the hour and on no live
        /// one, it is gone; disabled on every live replica, it is off.
        /// </summary>
        [Fact]
        public void A_worker_whose_replicas_stopped_is_gone_and_one_disabled_everywhere_is_off()
        {
            var report = Evaluate([.. Base(),
                Row("health", "worker-replica", held: true, endedAgo: TimeSpan.FromMinutes(10), seenAgo: TimeSpan.FromMinutes(10)),
                Row("health", Self, enabled: false),
                Row("action-requests", Self, enabled: false),
            ]);
            Assert.Equal("gone", Worker(report, "health").State);
            Assert.Equal("off", Worker(report, "action-requests").State);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
            Assert.Equal([Self], report.Replicas.Select(r => r.Name));

            var off = Evaluate([.. Base(), Row("action-requests", Self, enabled: false)]);
            Assert.Equal(PlatformStatus.Ok, off.Status);
        }

        [Fact]
        public void One_replica_of_a_worker_without_a_lease_stopping_degrades_it()
        {
            var report = Evaluate([.. Base(),
                Row("sampler", Self, leased: false, interval: 2, endedAgo: TimeSpan.FromSeconds(1)),
                Row("sampler", "b", leased: false, interval: 2, endedAgo: TimeSpan.FromMinutes(2)),
            ]);
            Assert.Equal("stale", Worker(report, "sampler").State);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
        }

        /// <summary>The replica answering has no live heartbeat of its own: its writer is failing, so nobody's status can be trusted.</summary>
        [Fact]
        public void A_replica_whose_own_heartbeats_arent_written_is_degraded()
        {
            var report = Evaluate([Row("discovery", "b", held: true, endedAgo: TimeSpan.FromSeconds(10))]);
            Assert.Equal("down", Assert.Single(report.Dependencies, d => d.Name == "heartbeats").State);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
        }

        [Theory]
        [InlineData(false, true, true, "postgres")]
        [InlineData(true, false, true, "clickhouse")]
        [InlineData(true, true, false, "collector")]
        public void A_dependency_down_degrades_the_status(bool postgres, bool clickHouse, bool collector, string down)
        {
            var report = Evaluate(Base(), Sample(postgres, clickHouse, collector));
            Assert.Equal(PlatformStatus.Degraded, report.Status);
            Assert.Equal("down", Assert.Single(report.Dependencies, d => d.Name == down).State);
        }

        [Fact]
        public void A_dependency_not_configured_or_without_a_fresh_sample_doesnt_degrade_it()
        {
            var notConfigured = Evaluate(Base(), Sample(clickHouse: null, collector: null));
            Assert.Equal(PlatformStatus.Ok, notConfigured.Status);
            Assert.Equal("not_configured", Assert.Single(notConfigured.Dependencies, d => d.Name == "clickhouse").State);

            var stale = PlatformStatus.Evaluate(Base(), Now, Sample(at: Now - TimeSpan.FromMinutes(5)), Now, Discovery, Self);
            Assert.Equal(PlatformStatus.Ok, stale.Status);
            Assert.All(stale.Dependencies.Where(d => d.Name != "heartbeats"), d => Assert.Equal("unknown", d.State));
        }

        /// <summary>The heartbeats are judged on the database's time; the sample on this replica's own clock.</summary>
        [Fact]
        public void Heartbeats_are_judged_on_the_databases_time()
        {
            var databaseNow = Now + TimeSpan.FromMinutes(10);   // this replica's clock 10 minutes behind the database's
            var rows = Base().Select(r => Row(r.Worker, r.Replica, r.Held, r.Leased, r.IntervalSeconds, endedAgo: TimeSpan.FromSeconds(5), now: databaseNow)).ToList();
            var report = PlatformStatus.Evaluate(rows, databaseNow, Sample(), Now, Discovery, Self);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        [Fact]
        public void Heartbeats_that_cant_be_read_are_a_database_down()
        {
            var report = Evaluate(null);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
            Assert.Equal("down", Assert.Single(report.Dependencies, d => d.Name == "postgres").State);
            Assert.Equal("unknown", Assert.Single(report.Backlogs, b => b.Name == "discovery").State);
        }

        [Fact]
        public void A_backlog_past_its_line_degrades_the_status()
        {
            var outbox = Evaluate(Base(), Sample(outbox: 75));
            Assert.Equal("behind", Assert.Single(outbox.Backlogs, b => b.Name == "outbox").State);
            Assert.Equal(PlatformStatus.Degraded, outbox.Status);
            // Discovery reads Lag (60 s) behind on purpose, a pass every Interval (30 s): its line is a minute past both, 150 s.
            Assert.Equal("ok", Assert.Single(Evaluate(Base(), Sample(watermarkLag: 140)).Backlogs, b => b.Name == "discovery").State);
            Assert.Equal("behind", Assert.Single(Evaluate(Base(), Sample(watermarkLag: 160)).Backlogs, b => b.Name == "discovery").State);
            // Discovery switched off: its watermark stays put, and isn't judged.
            var off = Evaluate([Base()[0], Row("discovery", Self, enabled: false)], Sample(watermarkLag: 9999));
            Assert.Equal("off", Assert.Single(off.Backlogs, b => b.Name == "discovery").State);
            Assert.Equal(PlatformStatus.Ok, off.Status);
        }

        /// <summary>Review finding: a holder that crashed; its standby hasn't taken the lease yet. The crashed holder's recent pass makes it a handover, not unheld.</summary>
        [Fact]
        public void A_crashed_holder_with_a_recent_pass_is_a_handover()
        {
            var report = Evaluate([.. Base(),
                Row("health", "crashed", held: true, endedAgo: TimeSpan.FromSeconds(40), seenAgo: TimeSpan.FromSeconds(35)),
                Row("health", Self),
            ]);
            Assert.Equal("handing_over", Worker(report, "health").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>A holder that has just taken the lease, its first pass not yet done, is starting, however long its replica has run.</summary>
        [Fact]
        public void A_holder_that_just_took_the_lease_is_starting()
        {
            var report = Evaluate([.. Base(), Row("rules", Self, held: true, interval: 5, heldFor: TimeSpan.FromSeconds(3))]);
            Assert.Equal("starting", Worker(report, "rules").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>
        /// Review finding: a redeploy that switches a worker off. The old replica's rows (enabled) linger until pruned, but every live replica started
        /// after it was last seen: off, not gone. A replica that was already running when the enabled one vanished: gone.
        /// </summary>
        [Fact]
        public void A_redeploy_that_switched_a_worker_off_is_off_and_a_replica_that_vanished_under_one_running_is_gone()
        {
            var redeploy = Evaluate([
                Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromMinutes(2)),
                Row("action-requests", Self, enabled: false, startedAgo: TimeSpan.FromMinutes(2)),
                Row("action-requests", "old", held: true, endedAgo: TimeSpan.FromMinutes(4), seenAgo: TimeSpan.FromMinutes(3)),
            ]);
            Assert.Equal("off", Worker(redeploy, "action-requests").State);

            var vanished = Evaluate([
                Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromHours(2)),
                Row("action-requests", Self, enabled: false, startedAgo: TimeSpan.FromHours(2)),
                Row("action-requests", "worker-replica", held: true, endedAgo: TimeSpan.FromMinutes(4), seenAgo: TimeSpan.FromMinutes(3)),
            ]);
            Assert.Equal("gone", Worker(vanished, "action-requests").State);
            Assert.Equal(PlatformStatus.Degraded, vanished.Status);
        }

        /// <summary>A rolling redeploy that starts the new replica first, then stops the old one: the worker it switched off is off, not gone.</summary>
        [Fact]
        public void A_rolling_redeploy_that_switched_a_worker_off_is_off()
        {
            var report = Evaluate([
                Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromMinutes(5)),
                Row("action-requests", Self, enabled: false, startedAgo: TimeSpan.FromMinutes(5)),
                Row("action-requests", "old", held: true, endedAgo: TimeSpan.FromMinutes(4), seenAgo: TimeSpan.FromMinutes(3), startedAgo: TimeSpan.FromDays(2)),
            ]);
            Assert.Equal("off", Worker(report, "action-requests").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>
        /// Review finding: a worker replica that crashed long before an unrelated replica restarted stays gone: the window is bounded both ways.
        /// </summary>
        [Fact]
        public void A_worker_replica_gone_long_before_another_restart_stays_gone()
        {
            var report = Evaluate([
                Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromMinutes(5)),
                Row("discovery", Self, enabled: false, startedAgo: TimeSpan.FromMinutes(5)),
                Row("discovery", "worker-replica", held: true, endedAgo: TimeSpan.FromMinutes(41), seenAgo: TimeSpan.FromMinutes(40)),
            ]);
            Assert.Equal("gone", Worker(report, "discovery").State);
            Assert.Equal(PlatformStatus.Degraded, report.Status);
        }

        /// <summary>Redeploying only the worker replicas with a worker switched off, the API replicas still running: off.</summary>
        [Fact]
        public void Redeploying_only_the_worker_replicas_with_a_worker_off_is_off()
        {
            var report = Evaluate([
                Row("sampler", Self, leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromDays(1)),
                Row("discovery", Self, enabled: false, startedAgo: TimeSpan.FromDays(1)),
                Row("discovery", "new-worker", enabled: false, startedAgo: TimeSpan.FromMinutes(3)),
                Row("sampler", "new-worker", leased: false, interval: 15, endedAgo: TimeSpan.FromSeconds(3), startedAgo: TimeSpan.FromMinutes(3)),
                Row("discovery", "old-worker", held: true, endedAgo: TimeSpan.FromMinutes(3), seenAgo: TimeSpan.FromMinutes(2), startedAgo: TimeSpan.FromHours(6)),
            ]);
            Assert.Equal("off", Worker(report, "discovery").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>A fresh replica whose own row isn't there yet doesn't call the workers of the replica it may have replaced gone.</summary>
        [Fact]
        public void Before_its_own_row_a_fresh_replica_doesnt_judge_gone()
        {
            var report = PlatformStatus.Evaluate([Row("health", "previous", held: true, endedAgo: TimeSpan.FromMinutes(2), seenAgo: TimeSpan.FromMinutes(1))],
                Now, Sample(), Now, Discovery, Self, selfWritten: false);
            Assert.Equal("starting", Worker(report, "health").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>A fresh replica before its first heartbeat write: its own row isn't expected yet.</summary>
        [Fact]
        public void A_replica_that_hasnt_written_yet_is_starting_not_down()
        {
            var report = PlatformStatus.Evaluate([Row("discovery", "b", held: true, endedAgo: TimeSpan.FromSeconds(10))], Now, Sample(), Now, Discovery, Self,
                selfWritten: false);
            Assert.Equal("starting", Assert.Single(report.Dependencies, d => d.Name == "heartbeats").State);
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>A row of a worker this build doesn't have (dropped or renamed by a release) isn't judged.</summary>
        [Fact]
        public void A_worker_this_build_doesnt_have_isnt_judged()
        {
            var report = Evaluate([.. Base(), Row("retired-worker", "old", held: true, endedAgo: TimeSpan.FromMinutes(20), seenAgo: TimeSpan.FromMinutes(20))]);
            Assert.DoesNotContain(report.Workers, w => w.Name == "retired-worker");
            Assert.Equal(PlatformStatus.Ok, report.Status);
        }

        /// <summary>A worker the host stops is no longer enabled here; one registered twice (two hosts in one process) stays until both stop.</summary>
        [Fact]
        public void A_worker_stopped_by_its_host_is_no_longer_enabled()
        {
            var worker = $"stop-{Guid.NewGuid():N}"[..20];
            using var first = new CancellationTokenSource();
            using var second = new CancellationTokenSource();
            WorkerPulse.Register(worker, TimeSpan.FromSeconds(30), stopping: first.Token);
            WorkerPulse.Register(worker, TimeSpan.FromSeconds(30), stopping: second.Token);
            first.Cancel();
            Assert.True(Assert.Single(WorkerPulse.Snapshot(), w => w.Worker == worker).Enabled);
            second.Cancel();
            Assert.DoesNotContain(WorkerPulse.Snapshot(), w => w.Worker == worker);   // not a known worker: dropped once nothing runs it
            Assert.All(WorkerPulse.KnownWorkers, known => Assert.Contains(WorkerPulse.Snapshot(), w => w.Worker == known));
        }

        /// <summary>The writer upserts one row per worker for its replica (enabled or not), in the database's time, and prunes replicas gone an hour.</summary>
        [Fact]
        public async Task The_heartbeat_writer_upserts_every_worker_and_prunes_gone_replicas_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var worker = $"hb-{Guid.NewGuid():N}"[..20];
            using var stopping = new CancellationTokenSource();
            WorkerPulse.Register(worker, TimeSpan.FromSeconds(30), stopping: stopping.Token);
            try
            {
                WorkerPulse.Lease(worker, true);
                await using (var db = database.Context())
                {
                    db.WorkerHeartbeats.Add(new WorkerHeartbeat
                    {
                        Worker = "old", Replica = "gone", Version = "1.2.0", IntervalSeconds = 30, SeenAt = DateTimeOffset.UtcNow.AddHours(-2),
                    });
                    await db.SaveChangesAsync(ct);
                }
                var writer = new HeartbeatWriter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HeartbeatWriter>.Instance);
                await writer.WriteOnceAsync(ct, "replica-a");
                using (var pass = WorkerPulse.Begin(worker))
                {
                    pass.Succeeded();
                }
                await writer.WriteOnceAsync(ct, "replica-a");

                await using (var db = database.Context())
                {
                    var serverNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(ct);
                    var row = await db.WorkerHeartbeats.SingleAsync(h => h.Worker == worker, ct);
                    Assert.Equal("replica-a", row.Replica);
                    Assert.True(row.Enabled);
                    Assert.True(row.Held);
                    Assert.NotNull(row.HeldSince);
                    Assert.Equal(30, row.IntervalSeconds);
                    Assert.Equal("success", row.LastOutcome);
                    Assert.Null(row.RunningSince);
                    Assert.True((serverNow - row.LastEndedAt!.Value).Duration() < TimeSpan.FromSeconds(30), "the database's time");
                    Assert.True((serverNow - row.SeenAt).Duration() < TimeSpan.FromSeconds(30));
                    // Every worker the platform has gets a row, enabled here or not.
                    var known = await db.WorkerHeartbeats.Where(h => h.Replica == "replica-a").Select(h => h.Worker).ToListAsync(ct);
                    Assert.All(WorkerPulse.KnownWorkers, k => Assert.Contains(k, known));
                    Assert.NotNull(row.ReplicaStartedAt);
                    Assert.False(await db.WorkerHeartbeats.AnyAsync(h => h.Replica == "gone", ct), "a replica gone for an hour is pruned");
                }
            }
            finally
            {
                stopping.Cancel();
            }
        }

        /// <summary>A Collector whose health check doesn't answer is down in the sample.</summary>
        [Fact]
        public async Task A_collector_that_doesnt_answer_is_down_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ClickHouseOptions()),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<PlatformSampler>.Instance,
                Options.Create(new TelemetryOptions { CollectorHealthUrl = "http://127.0.0.1:1/" }));
            var sample = await sampler.SampleOnceAsync(ct);
            Assert.False(sample.Collector!.Up);
            Assert.Null(sample.ClickHouse);
            Assert.True(sample.Postgres.Up);
        }
    }

    /// <summary>
    /// The endpoints on a real host, alone (the workers' registry is the process's, and other tests' hosts run workers): <c>/health/platform</c> answers
    /// anyone with the status alone, 200 when ok and 503 when degraded; <c>/api/v1/platform/status</c> answers this deployment's operators and admins.
    /// </summary>
    [Collection(typeof(SamplerAlone))]
    public class PlatformStatusEndpointTests
    {
        private static async Task<(HttpStatusCode Code, JsonElement Body)> HealthAsync(HttpClient client)
        {
            using var health = await client.GetAsync("/health/platform", TestContext.Current.CancellationToken);
            Assert.Equal("no-store", health.Headers.CacheControl?.ToString());
            return (health.StatusCode, JsonDocument.Parse(await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone());
        }

        [Fact]
        public async Task Health_platform_is_ok_with_200_and_degraded_with_503_and_the_status_is_for_operators_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["ClickHouse:MonitorConnectionString"] = LocalStack.ClickHouseMonitor,
            });
            using var client = factory.CreateClient();
            var services = factory.Services;

            // The host's heartbeats written and sampled once: ok.
            await new HeartbeatWriter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HeartbeatWriter>.Instance).WriteOnceAsync(ct);
            for (var i = 0; i < 100 && services.GetRequiredService<PlatformSampler>().Latest is null; i++) await Task.Delay(100, ct);
            var (okCode, okBody) = await HealthAsync(client);
            Assert.Equal(["status"], okBody.EnumerateObject().Select(p => p.Name));
            Assert.Equal((HttpStatusCode.OK, "ok"), (okCode, okBody.GetProperty("status").GetString()));

            // A worker another replica holds, stale: degraded, 503 (once the cached read expires).
            await using (var db = database.Context())
            {
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(ct);
                db.WorkerHeartbeats.Add(PlatformStatusTests.Row("notifications", "other", held: true, endedAgo: TimeSpan.FromMinutes(5), now: now));
                await db.SaveChangesAsync(ct);
            }
            await Task.Delay(PlatformStatusService.Cache + TimeSpan.FromSeconds(1), ct);
            var (degradedCode, degradedBody) = await HealthAsync(client);
            Assert.Equal((HttpStatusCode.ServiceUnavailable, "degraded"), (degradedCode, degradedBody.GetProperty("status").GetString()));

            using (var anonymous = await client.GetAsync("/api/v1/platform/status", ct))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }
            using (var viewer = await ApiTests.Get(client, "/api/v1/platform/status", await LocalStack.TokenAsync("victor")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
            }
            var status = await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/platform/status", await LocalStack.TokenAsync("erin")));
            Assert.Equal("degraded", status.GetProperty("status").GetString());
            var stale = status.GetProperty("workers").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "notifications");
            Assert.Equal("stale", stale.GetProperty("state").GetString());
            // Disabled in this host (ApiFactory turns discovery off): off, and its backlog isn't judged.
            Assert.Equal("off", status.GetProperty("workers").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "discovery").GetProperty("state").GetString());
            Assert.Equal("off", status.GetProperty("backlogs").EnumerateArray().Single(b => b.GetProperty("name").GetString() == "discovery").GetProperty("state").GetString());
            Assert.Equal("up", status.GetProperty("dependencies").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "clickhouse").GetProperty("state").GetString());
        }

        /// <summary>An operator whose tenant claim names another deployment's tenant: one IdP can serve several, and roles alone don't say which.</summary>
        [Fact]
        public async Task An_operator_of_another_deployment_cant_read_the_status_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync(provisionTenant: false);
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Platform:TenantKey"] = "0193f0aa-0000-7000-8000-0000000000e9" /* another deployment's tenant Id */ });
            using var client = factory.CreateClient();
            using var response = await ApiTests.Get(client, "/api/v1/platform/status", await LocalStack.TokenAsync("erin"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("Tenant not authorized", (await ApiTests.ProblemAsync(response)).GetProperty("title").GetString());
        }

        /// <summary>The API with the Collector's health check pointed at nothing: the status says the Collector is down, and degraded.</summary>
        [Fact]
        public async Task A_collector_that_doesnt_answer_shows_in_the_status_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Telemetry:CollectorHealthUrl"] = "http://127.0.0.1:1/" });
            using var client = factory.CreateClient();
            for (var i = 0; i < 150 && factory.Services.GetRequiredService<PlatformSampler>().Latest is null; i++) await Task.Delay(100, ct);
            var status = await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/platform/status", await LocalStack.TokenAsync("erin")));
            Assert.Equal("degraded", status.GetProperty("status").GetString());
            Assert.Equal("down", status.GetProperty("dependencies").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "collector").GetProperty("state").GetString());
        }

        /// <summary>The API with ClickHouse's sampler pointed at nothing: the status says ClickHouse is down, and degraded.</summary>
        [Fact]
        public async Task A_clickhouse_that_doesnt_answer_shows_in_the_status_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["ClickHouse:MonitorConnectionString"] = "Host=127.0.0.1;Port=1;Username=nobody;Password=x",
            });
            using var client = factory.CreateClient();
            for (var i = 0; i < 150 && factory.Services.GetRequiredService<PlatformSampler>().Latest is null; i++) await Task.Delay(100, ct);
            var status = await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/platform/status", await LocalStack.TokenAsync("erin")));
            Assert.Equal("degraded", status.GetProperty("status").GetString());
            Assert.Equal("down", status.GetProperty("dependencies").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "clickhouse").GetProperty("state").GetString());
        }
    }
}
