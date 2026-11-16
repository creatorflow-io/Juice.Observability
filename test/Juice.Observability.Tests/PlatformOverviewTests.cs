using System.Net;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Api.PlatformSampler;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 11 R8, R9: the Overview's platform panel and the capacity view, from the status, the last sample and the calibration.</summary>
    public class PlatformOverviewTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        /// <summary>The reference host's memory, as ClickHouse saw it in the final rerun (the Docker VM's).</summary>
        private const double ReferenceMemory = 33427488768;

        private static string Repository()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "deploy", "local", "capacity", "calibration.json"))) return dir.FullName;
            }
            throw new InvalidOperationException("deploy/local/capacity/calibration.json not found above " + AppContext.BaseDirectory);
        }

        private static CapacityCalibration Shipped()
            => CapacityCalibration.Parse(File.ReadAllText(Path.Combine(Repository(), "deploy", "local", "capacity", "calibration.json")));

        private static PlatformStatus.Report Status(params PlatformStatus.WorkerStatus[] workers)
            => new(PlatformStatus.Ok, Now, workers, [], [new("discovery", "ok", 80, 150)], []);

        private static PlatformStatus.WorkerStatus Worker(string name, double interval, double? pass, string state = "ok")
            => new(name, state, true, interval, 90, "a", Now, null, "success", [], pass);

        private static Sample SampleOf(double cores = 1, double available = 24, double memory = ReferenceMemory, double? inFlight = 0.5,
            double pointsPerSecond = 10000, double dropped = 0, double queue = 5000, double insertP95 = 0.05,
            IReadOnlyList<TableGrowth>? growth = null, double diskFree = 900e9, double diskTotal = 1000e9)
            => new(Now - TimeSpan.FromSeconds(5),
                new PostgresSample(true, 0.01, 1e6, 70, 0, 0, null, 0, 0, null, Rules: 50, Slos: 50, Services: 10),
                new ClickHouseSample(true, cores, 2e9, new Dictionary<string, long> { ["platform_reader"] = 2 },
                    [new FinishedQueries("otel_writer", "Insert", 100, 0, insertP95)], [("default", diskFree, diskTotal)], [("otel_traces", 40e9, 1e9)],
                    available, inFlight, [new CallerQueries("juice:api:/telemetry/logs", 20, 0.8), new CallerQueries("juice:slos", 30, 3)],
                    growth ?? [new TableGrowth("otel_traces", 4e9, false, 30)], memory),
                new CollectorSample(true, 0.01,
                    [new SignalFlow("spans", 1e6, 0, 0, 1000, 0, 0), new SignalFlow("points", 1e7, 0, dropped, pointsPerSecond, 0, dropped),
                        new SignalFlow("logs", 1e6, 0, 0, 1000, 0, 0)],
                    [new ExportQueue("clickhouse/metrics", queue, 200000), new ExportQueue("clickhouse/traces", 0, 200000)]));

        [Fact]
        public void The_collector_page_counts_the_tenants_receivers_and_every_exporters_drops_and_queues()
        {
            var (signals, queues) = CollectorMetrics.Parse("""
                # HELP otelcol_receiver_accepted_spans Number of spans successfully pushed into the pipeline.
                otelcol_receiver_accepted_spans_total{receiver="otlp",transport="http"} 1500
                otelcol_receiver_accepted_spans_total{receiver="otlp",transport="grpc"} 500
                otelcol_receiver_accepted_metric_points{receiver="otlp/platform",transport="http"} 9999
                otelcol_receiver_accepted_metric_points{receiver="prometheus/self",transport="http"} 7777
                otelcol_receiver_accepted_metric_points{receiver="host_metrics",transport=""} 300
                otelcol_receiver_refused_log_records{receiver="otlp",transport="http"} 4
                otelcol_exporter_enqueue_failed_metric_points{exporter="clickhouse/metrics"} 12948
                otelcol_exporter_send_failed_metric_points{exporter="clickhouse/metrics"} 52
                otelcol_exporter_queue_size{data_type="metrics",exporter="clickhouse/metrics"} 196052
                otelcol_exporter_queue_capacity{data_type="metrics",exporter="clickhouse/metrics"} 200000
                otelcol_exporter_queue_capacity{data_type="logs",exporter="clickhouse/logs"} 200000
                """);

            var by = signals.ToDictionary(s => s.Signal);
            Assert.Equal(2000, by["spans"].Accepted);
            Assert.Equal(300, by["points"].Accepted);   // the platform's own pipeline isn't the tenant's load
            Assert.Equal(13000, by["points"].Dropped);
            Assert.Equal(4, by["logs"].Refused);
            Assert.Equal([new ExportQueue("clickhouse/logs", 0, 200000), new ExportQueue("clickhouse/metrics", 196052, 200000)], queues);
        }

        [Fact]
        public void Rates_are_since_the_previous_sample_and_none_across_a_collector_restart()
        {
            IReadOnlyList<SignalFlow> before = [new SignalFlow("points", 1000, 0, 10)];
            var rated = Assert.Single(CollectorMetrics.WithRates([new SignalFlow("points", 4000, 0, 40)], before, 15));
            Assert.Equal((200d, 0d, 2d), (rated.AcceptedPerSecond!.Value, rated.RefusedPerSecond!.Value, rated.DroppedPerSecond!.Value));

            var restarted = Assert.Single(CollectorMetrics.WithRates([new SignalFlow("points", 50, 0, 0)], before, 15));
            Assert.Null(restarted.AcceptedPerSecond);
            Assert.Null(Assert.Single(CollectorMetrics.WithRates([new SignalFlow("points", 50, 0, 0)], null, null)).AcceptedPerSecond);
        }

        [Fact]
        public void The_shipped_calibration_is_the_final_reruns_bracket_with_the_held_out_steps_kept_apart()
        {
            var calibration = Shipped();
            Assert.Equal((5d, 10d), (calibration.Knee.HeldTo!.Value, calibration.Knee.CrossedAt!.Value));
            Assert.Equal([1d, 3d, 5d, 10d], calibration.Steps.Where(s => !s.HeldOut).Select(s => s.Multiplier));
            // R10's check: the held-out steps fall in the bracket. 4× held below it, 6× crossed inside it: the knee is at least the lower bound, 5×.
            Assert.True(calibration.Steps.Single(s => s.Multiplier == 4).Held);
            Assert.False(calibration.Steps.Single(s => s.Multiplier == 6).Held);
            Assert.InRange(calibration.Knee.HeldTo!.Value / 6 - 1, -0.25, 0.25);
            Assert.True(calibration.Matches(24, ReferenceMemory));
            Assert.False(calibration.Matches(8, ReferenceMemory));
            Assert.False(calibration.Matches(24, ReferenceMemory / 2));
            Assert.All(calibration.Steps, s => Assert.Contains("clickhouse.inserts_in_flight", s.Resources.Keys));
        }

        [Fact]
        public void Calibrated_on_this_machine_the_view_says_so_and_judges_each_resource_against_the_steps()
        {
            var calibration = Shipped();
            var overview = PlatformOverview.Build(Status(Worker("slos", 30, 3), Worker("notifications", 2, 0.02)), SampleOf(inFlight: 3), Now, calibration);
            var capacity = overview.Capacity;

            Assert.True(capacity.Calibrated);
            Assert.StartsWith("Calibrated on 24 cores", capacity.Label);
            Assert.Equal((5d, 10d), (capacity.HeldTo!.Value, capacity.CrossedAt!.Value));
            Assert.Equal(1, capacity.Load.Single(l => l.Signal == "points").Multiple);
            Assert.Equal(5, capacity.Headroom);
            Assert.Equal((50L, 50L, 50L, 50L), (capacity.Mix.Rules!.Value, capacity.Mix.CalibratedRules!.Value, capacity.Mix.Slos!.Value, capacity.Mix.CalibratedSlos!.Value));
            // Inserts in flight: 2.09 where every line held (5×), 10.3 where one crossed (10×). 3 is past the first.
            var inserts = capacity.Resources.Single(r => r.Name == "clickhouse.inserts_in_flight");
            Assert.Equal(PlatformOverview.Warn, inserts.Level);
            Assert.InRange(inserts.AtHeld!.Value, 2, 2.2);
            Assert.InRange(inserts.AtCrossed!.Value, 10, 10.5);
            Assert.Equal(PlatformOverview.Ok, capacity.Resources.Single(r => r.Name == "worker.slos").Level);
            // A light worker measured a few milliseconds a pass in the steps: 1 % busy is still green, its line is its interval.
            var notifications = capacity.Resources.Single(r => r.Name == "worker.notifications");
            Assert.Equal(PlatformOverview.Ok, notifications.Level);
            Assert.True(notifications.AtCrossed < 0.01);

            Assert.Equal(PlatformOverview.Bad,
                PlatformOverview.Build(Status(), SampleOf(inFlight: 11), Now, calibration).Capacity.Resources.Single(r => r.Name == "clickhouse.inserts_in_flight").Level);
            Assert.Equal(PlatformOverview.Ok,
                PlatformOverview.Build(Status(), SampleOf(inFlight: 1), Now, calibration).Capacity.Resources.Single(r => r.Name == "clickhouse.inserts_in_flight").Level);
        }

        [Fact]
        public void On_another_machine_or_without_a_file_it_shows_utilisation_only_and_says_so()
        {
            var elsewhere = PlatformOverview.Build(Status(), SampleOf(available: 8, memory: 16e9), Now, Shipped()).Capacity;
            Assert.False(elsewhere.Calibrated);
            Assert.StartsWith("Not calibrated for this machine: the calibration is for 24 cores", elsewhere.Label);
            Assert.EndsWith("Utilisation only.", elsewhere.Label);
            Assert.Null(elsewhere.HeldTo);
            Assert.Null(elsewhere.Headroom);
            Assert.All(elsewhere.Resources, r => Assert.Null(r.AtHeld));

            var none = PlatformOverview.Build(Status(), SampleOf(), Now, null).Capacity;
            Assert.StartsWith("Not calibrated: no calibration file", none.Label);
            Assert.Equal(1, none.Load.Single(l => l.Signal == "spans").Multiple);   // §9's reference still gives the load
        }

        [Fact]
        public void Each_figure_has_its_level()
        {
            var overview = PlatformOverview.Build(
                Status(Worker("discovery", 30, 27), Worker("health", 30, 31), Worker("rules", 60, 1, state: "stale")),
                SampleOf(cores: 20, dropped: 5, queue: 190000, insertP95: 0.4), Now, null);

            Assert.Equal([PlatformOverview.Warn, PlatformOverview.Bad, PlatformOverview.Bad], overview.Workers.Select(w => w.Level));
            Assert.Equal(0.9, overview.Workers[0].Utilisation!.Value, 3);
            Assert.Equal(PlatformOverview.Bad, overview.Ingestion!.Signals.Single(s => s.Name == "points").Level);
            Assert.Equal(PlatformOverview.Ok, overview.Ingestion.Signals.Single(s => s.Name == "spans").Level);
            Assert.Equal(PlatformOverview.Bad, overview.Ingestion.Queues.Single(q => q.Exporter == "clickhouse/metrics").Level);
            Assert.Equal(PlatformOverview.Bad, overview.ClickHouse!.CpuLevel);        // 20 of 24 cores
            Assert.Equal(PlatformOverview.Bad, overview.ClickHouse.InsertLevel);      // past the 10× p95
            Assert.Equal(PlatformOverview.Warn, overview.ClickHouse.Reads.Single(r => r.Name == "juice:api:/telemetry/logs").Level);
            Assert.Equal(PlatformOverview.Ok, overview.ClickHouse.Reads.Single(r => r.Name == "juice:slos").Level);   // a worker: no search line
        }

        [Fact]
        public void The_disk_says_what_the_tables_need_at_their_retention_and_when_it_is_full()
        {
            var disk = PlatformOverview.Build(Status(), SampleOf(growth: [new TableGrowth("otel_traces", 4e9, false, 30), new TableGrowth("otel_metrics_sum", 8e9, true, 90)],
                diskFree: 120e9), Now, null).Disk!;
            Assert.Equal(12e9, disk.BytesPerDay);
            Assert.Equal(4e9 * 30 + 8e9 * 90, disk.NeedAtRetention);
            Assert.Equal(10, disk.DaysUntilFull);
            Assert.True(disk.Estimated);
            Assert.Equal(PlatformOverview.Warn, disk.Level);   // under a month
            Assert.Equal(PlatformOverview.Bad, PlatformOverview.Build(Status(), SampleOf(diskFree: 20e9), Now, null).Disk!.Level);

            Assert.Equal(new TableGrowth("t", 5, false, 30), PlatformSampler.TableGrowthOf("t", 5, 86000, 9, 7200, 30));
            // Yesterday's rows span two hours (ingestion started at 22:00): scaled to a day, and an estimate.
            Assert.Equal(new TableGrowth("t", 43200, true, 30), PlatformSampler.TableGrowthOf("t", 3600, 7200, 9, 7200, 30));
            Assert.Equal(new TableGrowth("t", 86400, true, 30), PlatformSampler.TableGrowthOf("t", 0, 0, 7200, 7200, 30));
            Assert.Null(PlatformSampler.TableGrowthOf("t", 0, 0, 100, 600, 30).BytesPerDay);   // under an hour of today: no estimate yet
        }

        [Fact]
        public void A_full_queue_is_red_even_where_the_calibrations_queues_sum_went_past_one()
        {
            // Review of milestone 6: the calibration's queue is the three queues' sum (1.05 of one bound at 10×), so one queue against it never reached red.
            var overview = PlatformOverview.Build(Status(), SampleOf(queue: 200000), Now, Shipped());
            Assert.Equal(PlatformOverview.Bad, overview.Ingestion!.Queues.Single(q => q.Exporter == "clickhouse/metrics").Level);
        }

        [Fact]
        public void A_queue_without_a_bound_on_the_page_is_unknown_not_a_failed_answer()
        {
            var sample = SampleOf();
            sample = sample with { Collector = sample.Collector! with { Queues = [new ExportQueue("clickhouse/metrics", 50, 0)] } };
            var overview = PlatformOverview.Build(Status(), sample, Now, null);

            Assert.Equal(PlatformOverview.Unknown, overview.Ingestion!.Queues.Single().Level);
            Assert.Null(overview.Capacity.Resources.Single(r => r.Name == "collector.queue").Now);
            System.Text.Json.JsonSerializer.Serialize(overview);   // no NaN or infinity to refuse
        }

        [Fact]
        public void Tables_that_reach_their_retention_before_the_disk_is_full_dont_count_down_to_full()
        {
            // 40 GB of traces on disk, 4 GB a day, 30 days: 80 GB still to grow, and 100 GB free. The disk never fills.
            var disk = PlatformOverview.Build(Status(), SampleOf(diskFree: 100e9), Now, null).Disk!;
            Assert.Null(disk.DaysUntilFull);
            Assert.Equal(PlatformOverview.Ok, disk.Level);
        }

        [Fact]
        public void A_calibration_without_cpu_figures_falls_back_to_shares_of_the_cores()
        {
            var shipped = Shipped();
            var calibration = shipped with { Steps = [.. shipped.Steps.Select(s => s with { Resources = s.Resources.Where(r => r.Key != "clickhouse.cpu").ToDictionary() })] };
            Assert.Equal(PlatformOverview.Bad, PlatformOverview.Build(Status(), SampleOf(cores: 22), Now, calibration).ClickHouse!.CpuLevel);
        }

        [Fact]
        public void A_calibration_missing_a_section_is_a_bad_file_and_is_read_again_only_once_it_changes()
        {
            Assert.Throws<InvalidDataException>(() => CapacityCalibration.Parse("""{ "version": 1, "steps": [] }"""));

            var file = Path.Combine(Path.GetTempPath(), $"calibration-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(file, "{ not json");
                var logger = new CountingLogger();
                var store = new CapacityCalibrationStore(Options.Create(new CapacityOptions { CalibrationFile = file }), logger);
                Assert.Null(store.Get());
                Assert.Null(store.Get());
                Assert.Equal(1, logger.Warnings);   // logged once a change, not on every poll

                File.Copy(Path.Combine(Repository(), "deploy", "local", "capacity", "calibration.json"), file, overwrite: true);
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
                Assert.Equal(5, store.Get()!.Knee.HeldTo);
            }
            finally
            {
                File.Delete(file);
            }
        }

        private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger<CapacityCalibrationStore>
        {
            public int Warnings { get; private set; }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings++;
            }
        }

        [Fact]
        public void An_old_sample_shows_no_figures_but_the_workers()
        {
            var overview = PlatformOverview.Build(Status(Worker("slos", 30, 3)), SampleOf(), Now + TimeSpan.FromMinutes(5), Shipped());
            Assert.Null(overview.ClickHouse);
            Assert.Null(overview.Ingestion);
            Assert.Null(overview.Disk);
            Assert.False(overview.Capacity.Calibrated);
            Assert.StartsWith("ClickHouse isn't sampled now", overview.Capacity.Label);   // not "for another machine"
            Assert.Single(overview.Workers);
        }

        [Fact]
        public void A_workers_pass_time_is_a_moving_mean_of_its_recent_passes()
        {
            var worker = $"pulse-{Guid.NewGuid():N}";
            using var stopping = new CancellationTokenSource();
            WorkerPulse.Register(worker, TimeSpan.FromSeconds(30), stopping: stopping.Token);
            using (var pass = WorkerPulse.Begin(worker)) { pass.Succeeded(); }
            var first = WorkerPulse.Snapshot().Single(w => w.Worker == worker).PassSeconds;
            Assert.NotNull(first);
            using (var pass = WorkerPulse.Begin(worker)) { Thread.Sleep(200); pass.Succeeded(); }
            var second = WorkerPulse.Snapshot().Single(w => w.Worker == worker).PassSeconds!.Value;
            // One 0.2 s pass after a near-zero one: a fifth of the way there.
            Assert.InRange(second, first!.Value + 0.03, first.Value + 0.1);
            stopping.Cancel();
            Assert.Null(WorkerPulse.Snapshot().SingleOrDefault(w => w.Worker == worker)?.PassSeconds);
        }

        /// <summary>The sampler reads the Collector's metrics page and ClickHouse's figures for the panel, on the running stack.</summary>
        [Fact]
        public async Task The_sampler_reads_the_collectors_flow_and_clickhouses_growth_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ClickHouseOptions { MonitorConnectionString = LocalStack.ClickHouseMonitor }),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<PlatformSampler>.Instance,
                Options.Create(new TelemetryOptions { CollectorHealthUrl = "http://127.0.0.1:13133/", CollectorMetricsUrl = "http://127.0.0.1:8888/metrics" }));

            var first = await sampler.SampleOnceAsync(ct);
            await Task.Delay(1000, ct);
            var second = await sampler.SampleOnceAsync(ct);

            var collector = Assert.IsType<CollectorSample>(second.Collector);
            Assert.True(collector.Up);
            Assert.Equal(["logs", "points", "spans"], collector.Signals!.Select(s => s.Signal).Order());
            Assert.All(collector.Signals!, s => Assert.NotNull(s.AcceptedPerSecond));
            Assert.Contains(collector.Queues!, q => q.Exporter == "clickhouse/metrics" && q.Capacity == 200000);
            Assert.Null(first.Collector!.Signals!.First().AcceptedPerSecond);

            var clickHouse = Assert.IsType<ClickHouseSample>(second.ClickHouse);
            Assert.True(clickHouse.CoresAvailable > 0);
            Assert.True(clickHouse.MemoryTotal > 0);
            Assert.True(clickHouse.InsertsInFlight >= 0);
            Assert.NotNull(clickHouse.Callers);
            Assert.Contains(clickHouse.Growth!, g => g.Name == "otel_traces" && g.RetentionDays is > 0);
            // The exporter's DateTime64 time columns give its parts a time range, so a day's span is known (not 0) once a day has data.
            Assert.True(await LocalStack.ClickHouseAsync(
                "SELECT count() FROM system.parts WHERE active AND database = 'otel' AND table = 'otel_traces' AND toUnixTimestamp(max_time) > 0") != "0");
            Assert.True(second.Postgres.Services >= 0);
        }

        [Fact]
        public async Task The_overview_is_for_operators_of_this_deployment_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["ClickHouse:MonitorConnectionString"] = LocalStack.ClickHouseMonitor,
                ["Capacity:CalibrationFile"] = Path.Combine(Repository(), "deploy", "local", "capacity", "calibration.json"),
            });
            using var client = factory.CreateClient();
            for (var i = 0; i < 100 && factory.Services.GetRequiredService<PlatformSampler>().Latest is null; i++) await Task.Delay(100, ct);

            using (var viewer = await ApiTests.Get(client, "/api/v1/platform/overview", await LocalStack.TokenAsync("victor")))
            {
                Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
            }
            // The tenant tells the Console whether to ask at all, so a viewer's Overview never gets the 403.
            static bool CanOperate(System.Text.Json.JsonElement tenants) => tenants.GetProperty("items")[0].GetProperty("canOperate").GetBoolean();
            Assert.False(CanOperate(await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/tenants", await LocalStack.TokenAsync("victor")))));
            Assert.True(CanOperate(await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/tenants", await LocalStack.TokenAsync("erin")))));
            var overview = await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/platform/overview", await LocalStack.TokenAsync("erin")));
            var capacity = overview.GetProperty("capacity");
            // The local stack runs on the machine the shipped calibration is for (the reference host); elsewhere it says it isn't calibrated.
            if (capacity.GetProperty("calibrated").GetBoolean())
            {
                Assert.Equal(5, capacity.GetProperty("heldTo").GetDouble());
                Assert.StartsWith("Calibrated on", capacity.GetProperty("label").GetString());
            }
            else
            {
                Assert.StartsWith("Not calibrated for this machine", capacity.GetProperty("label").GetString());
            }
            Assert.True(overview.GetProperty("clickHouse").GetProperty("coresAvailable").GetDouble() > 0);
            Assert.True(overview.GetProperty("disk").GetProperty("total").GetDouble() > 0);
        }
    }
}
