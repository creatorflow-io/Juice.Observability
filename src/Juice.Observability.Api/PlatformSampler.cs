using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Samples, every <see cref="Interval"/>, what the platform can only read from its stores (Phase 11 R4, R5), and publishes the latest sample
    /// as gauges under the platform's meter. Never per request: a page or a probe reads the last sample.
    /// <list type="bullet">
    /// <item>PostgreSQL: the outbox backlog and its oldest age, the discovery watermark's lag behind now, the database's size, and
    /// whether it answered and how fast;</item>
    /// <item>ClickHouse, through <c>platform_monitor</c> (<see cref="ClickHouseOptions.MonitorConnectionString"/>; skipped when unset): its CPU,
    /// resident memory, running queries per user, the queries it finished per user and kind with their p95 (inserts are the Collector's
    /// <c>otel_writer</c>), and its disks and tables.</item>
    /// </list>
    /// A gauge shows the last sample only while it is fresh (<see cref="Fresh"/>); a store that stops answering drops out instead of showing old
    /// values, and its <c>up</c> gauge goes to 0. Each store has <see cref="Timeout"/> to answer, or it is down for that sample, so one store
    /// hanging neither hides the other nor leaves old values showing.
    /// <para>Every replica samples the same stores: the replica is the exporter's <c>service.instance.id</c>, and a figure is read from one
    /// replica (or the max across them), never summed across them.</para>
    /// </summary>
    public sealed class PlatformSampler(IServiceScopeFactory scopes, IOptions<ClickHouseOptions> clickHouse, IOptions<PlatformOptions> platform,
        TimeProvider clock, ILogger<PlatformSampler> logger, IOptions<TelemetryOptions>? telemetry = null) : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
        /// <summary>How long each store has to answer one sample.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        /// <summary>The longest stretch of ClickHouse's query log read in one sample: after an outage, older queries are left uncounted.</summary>
        public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(5);
        /// <summary>How old a sample may be and still show.</summary>
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

        /// <summary>The ClickHouse users named in the gauges; any other is <c>other</c>, so the set stays fixed.</summary>
        public static readonly IReadOnlySet<string> KnownUsers = new HashSet<string>(StringComparer.Ordinal)
            { "otel_writer", "platform_reader", "grafana_reader", "platform_monitor" };
        private static readonly IReadOnlySet<string> KnownKinds = new HashSet<string>(StringComparer.Ordinal) { "Select", "Insert", "AsyncInsertFlush" };

        private static readonly Meter Meter = new(WorkerPulse.MeterName);
        /// <summary>The sampler running in this process (one per host), which the gauges read; cleared when it stops.</summary>
        private static PlatformSampler? s_live;

        static PlatformSampler()
        {
            Gauge("juice.observability.postgres.up", "{up}", "1 when the platform database answered the last sample, else 0",
                s => s.Postgres is { } p ? [new(p.Up ? 1 : 0)] : []);
            Gauge("juice.observability.postgres.sample.duration", "s", "How long the last PostgreSQL sample took",
                s => s.Postgres is { Up: true } p ? [new(p.Duration)] : []);
            Gauge("juice.observability.postgres.size", "By", "The platform database's size",
                s => s.Postgres is { Up: true } p ? [new(p.Size)] : []);
            Gauge("juice.observability.discovery.watermark_lag", "s", "How far discovery's watermark is behind now (its configured Discovery:Lag included)",
                s => s.Postgres is { Up: true, WatermarkLag: { } lag } ? [new(lag)] : []);
            Gauge("juice.observability.outbox.backlog", "{message}", "Outbox messages still to deliver, by state (pending: not yet tried or in progress; retrying: failed, a retry due)",
                s => s.Postgres is { Up: true } p ? [new(p.OutboxPending, Tag("state", "pending")), new(p.OutboxRetrying, Tag("state", "retrying"))] : []);
            Gauge("juice.observability.outbox.oldest_age", "s", "How overdue the most overdue outbox message is (new: since written; failed: since its retry fell due); 0 when none",
                s => s.Postgres is { Up: true } p ? [new(p.OutboxOldest ?? 0)] : []);

            Gauge("juice.observability.collector.up", "{up}", "1 when the Collector's health check answered the last sample, else 0 (absent when it isn't checked)",
                s => s.Collector is { } c ? [new(c.Up ? 1 : 0)] : []);
            Gauge("juice.observability.clickhouse.up", "{up}", "1 when ClickHouse answered the last sample, else 0 (absent when it isn't sampled)",
                s => s.ClickHouse is { } c ? [new(c.Up ? 1 : 0)] : []);
            Gauge("juice.observability.clickhouse.cpu", "{cpu}", "CPU ClickHouse's query and background threads used since the previous sample, in cores (a lower bound: threads without profile events aren't counted)",
                s => s.ClickHouse is { Up: true, Cores: { } cores } ? [new(cores)] : []);
            Gauge("juice.observability.clickhouse.memory", "By", "ClickHouse's resident memory",
                s => s.ClickHouse is { Up: true } c ? [new(c.Memory)] : []);
            Gauge("juice.observability.clickhouse.running_queries", "{query}", "Queries running in ClickHouse, by user",
                s => s.ClickHouse is { Up: true } c ? c.Running.Select(r => new Measurement<double>(r.Value, Tag("user", r.Key))) : []);
            // A count since this process started, not per window: an export reads it whenever it runs, and nothing is lost or counted twice
            // whatever Telemetry:ExportInterval is.
            Meter.CreateObservableCounter("juice.observability.clickhouse.server.queries", () => s_live is { } live ? live.FinishedTotals() : [], "{query}",
                "Queries ClickHouse finished, by user, kind and outcome (success, failure), counted from its query log since this process started");
            Gauge("juice.observability.clickhouse.server.query.p95", "s", "p95 duration of the queries ClickHouse finished in the last sample's window (15 s; up to 5 min after a missed sample), by user and kind (inserts: otel_writer); for users or kinds merged into other, the highest of their p95s",
                s => s.ClickHouse is { Up: true } c ? c.Finished.Where(q => q.P95 is not null).Select(q => new Measurement<double>(q.P95!.Value, Tag("user", q.User), Tag("kind", q.Kind))) : []);
            Gauge("juice.observability.clickhouse.disk.free", "By", "Free space on each ClickHouse disk",
                s => s.ClickHouse is { Up: true } c ? c.Disks.Select(d => new Measurement<double>(d.Free, Tag("disk", d.Name))) : []);
            Gauge("juice.observability.clickhouse.disk.total", "By", "Size of each ClickHouse disk",
                s => s.ClickHouse is { Up: true } c ? c.Disks.Select(d => new Measurement<double>(d.Total, Tag("disk", d.Name))) : []);
            Gauge("juice.observability.clickhouse.table.size", "By", "Bytes on disk of each telemetry table (active parts)",
                s => s.ClickHouse is { Up: true } c ? c.Tables.Select(t => new Measurement<double>(t.Bytes, Tag("table", t.Name))) : []);
            Gauge("juice.observability.clickhouse.table.rows", "{row}", "Rows in each telemetry table (active parts)",
                s => s.ClickHouse is { Up: true } c ? c.Tables.Select(t => new Measurement<double>(t.Rows, Tag("table", t.Name))) : []);
        }

        private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

        private static void Gauge(string name, string unit, string description, Func<Sample, IEnumerable<Measurement<double>>> read)
            => Meter.CreateObservableGauge(name, () => s_live is { Latest: { } sample } live && live._clock.GetUtcNow() - sample.At <= Fresh ? read(sample) : [], unit, description);

        /// <summary>One PostgreSQL sample. Rules (enabled), Slos (not retired) and Services (null until first read) count the deployment's
        /// tenant: the mix the capacity view sets beside its calibration's (Phase 11 R9).</summary>
        public sealed record PostgresSample(bool Up, double Duration, double Size, double? WatermarkLag, long OutboxPending, long OutboxRetrying,
            double? OutboxOldest, long? Rules = null, long? Slos = null, long? Services = null);

        public sealed record FinishedQueries(string User, string Kind, long Succeeded, long Failed, double? P95);

        /// <summary>The platform's reads of one caller (<c>log_comment</c>, such as <c>juice:slos</c>) finished in the last minute (read once a minute).</summary>
        public sealed record CallerQueries(string Caller, long Count, double P95);

        /// <summary>
        /// A telemetry table's growth: <paramref name="BytesPerDay"/> on disk from the last whole UTC day's partition, or, with none yet, today's
        /// scaled to a day (<paramref name="Estimated"/>); and its retention, from its TTL.
        /// </summary>
        public sealed record TableGrowth(string Name, double? BytesPerDay, bool Estimated, int? RetentionDays);

        /// <summary>One ClickHouse sample. CoresAvailable: the cores ClickHouse may use (its automatic <c>max_threads</c>); MemoryTotal: the memory its machine has.
        /// InsertsInFlight: the Collector's inserts running at once, on average over the sample's window (their durations over its
        /// length). The 16 November rerun found the export saturating near 10, the ClickHouse driver's default pool, since raised to 32
        /// (docs/scale-tests.md).</summary>
        public sealed record ClickHouseSample(bool Up, double? Cores, double Memory, IReadOnlyDictionary<string, long> Running,
            IReadOnlyList<FinishedQueries> Finished, IReadOnlyList<(string Name, double Free, double Total)> Disks,
            IReadOnlyList<(string Name, double Bytes, double Rows)> Tables, double? CoresAvailable = null, double? InsertsInFlight = null,
            IReadOnlyList<CallerQueries>? Callers = null, IReadOnlyList<TableGrowth>? Growth = null, double? MemoryTotal = null);

        /// <summary>
        /// One signal through the Collector: accepted and refused by the tenant's receivers (not the platform's own pipeline; refused
        /// includes a full export queue turning them back), and dropped: the exporters' sends given up, and what the platform's own receivers
        /// refused (CollectorMetrics), as counts since the Collector started, and per second since the previous sample
        /// (null on the first, or after the Collector restarted).
        /// </summary>
        public sealed record SignalFlow(string Signal, double Accepted, double Refused, double Dropped, double? AcceptedPerSecond = null,
            double? RefusedPerSecond = null, double? DroppedPerSecond = null);

        /// <summary>An exporter's sending queue against its bound: beyond it, data is dropped.</summary>
        public sealed record ExportQueue(string Exporter, double Size, double Capacity);

        /// <summary>
        /// The Collector's health check: answered with a success status within the timeout. With its metrics page configured
        /// (<c>Telemetry:CollectorMetricsUrl</c>), also each signal's flow and each exporter's queue; null when it isn't read or didn't answer.
        /// </summary>
        public sealed record CollectorSample(bool Up, double Duration, IReadOnlyList<SignalFlow>? Signals = null, IReadOnlyList<ExportQueue>? Queues = null);

        /// <summary>One sample: <see cref="ClickHouse"/> is null when ClickHouse isn't sampled, <see cref="Collector"/> when the Collector isn't checked.</summary>
        public sealed record Sample(DateTimeOffset At, PostgresSample Postgres, ClickHouseSample? ClickHouse, CollectorSample? Collector = null);

        /// <summary>The last sample taken, or null before the first.</summary>
        public Sample? Latest { get; private set; }

        private readonly TimeProvider _clock = clock;
        private (long Micros, long At)? _cpu;
        // What seldom changes, read again at most every SlowRead (Phase 11 R9): ClickHouse's cores and its machine's memory (also while either is
        // missing, and a ClickHouse restarted on another machine shows within it), and the tables' retention.
        private (double? Cores, double? Memory, long At)? _machine;
        private (IReadOnlyDictionary<string, int?> Days, long At)? _retention;
        private static readonly TimeSpan SlowRead = TimeSpan.FromMinutes(10);
        // The platform's reads by caller, and the mix, change slowly and cost a scan each: read once a minute, the last answer kept between. A
        // failed read drops the callers' answer (it would be shown as the last minute's); the mix is kept, and stays unknown until first read.
        private (IReadOnlyList<CallerQueries> Callers, long At)? _callers;
        private ((long Rules, long Slos, long Services) Mix, long At)? _mix;
        private static readonly TimeSpan MinuteRead = TimeSpan.FromMinutes(1);
        private (IReadOnlyList<SignalFlow> Signals, long At)? _collector;
        private DateTime? _windowEnd;
        private readonly Dictionary<(string User, string Kind, string Outcome), long> _finished = [];
        private readonly Lock _finishedLock = new();

        private IEnumerable<Measurement<long>> FinishedTotals()
        {
            lock (_finishedLock)
            {
                return [.. _finished.Select(f => new Measurement<long>(f.Value, Tag("user", f.Key.User), Tag("kind", f.Key.Kind), Tag("outcome", f.Key.Outcome)))];
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            s_live = this;
            PlatformCaller.Set("sampler", stoppingToken);
            WorkerPulse.Register("sampler", Interval, leased: false, stopping: stoppingToken);
            using var timer = new PeriodicTimer(Interval, _clock);
            try
            {
                do
                {
                    try
                    {
                        using var pass = WorkerPulse.Begin("sampler", stoppingToken);
                        Latest = await SampleOnceAsync(stoppingToken);
                        var answered = (Latest.Postgres.Up ? 1 : 0) + (Latest.ClickHouse is { Up: true } ? 1 : 0) + (Latest.Collector is { Up: true } ? 1 : 0);
                        var sampled = 1 + (Latest.ClickHouse is null ? 0 : 1) + (Latest.Collector is null ? 0 : 1);
                        if (answered == sampled) pass.Succeeded(); else if (answered > 0) pass.Partial();   // none answered: a failure
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Platform sample failed; retrying in {Interval}", Interval);
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            finally
            {
                Interlocked.CompareExchange(ref s_live, null, this);
            }
        }

        /// <summary>Takes one sample of both stores, at once, each within <see cref="Timeout"/>. Exposed for tests.</summary>
        public async Task<Sample> SampleOnceAsync(CancellationToken cancellationToken)
        {
            var at = _clock.GetUtcNow();
            var postgres = WithinAsync(SamplePostgresAsync, cancellationToken);
            var clickHouseSample = string.IsNullOrWhiteSpace(clickHouse.Value.MonitorConnectionString) ? null : WithinAsync(SampleClickHouseAsync, cancellationToken);
            var collector = telemetry?.Value.CollectorHealthUrl is { Length: > 0 } url
                ? WithinAsync(ct => SampleCollectorAsync(url, telemetry.Value.CollectorMetricsUrl, ct), cancellationToken) : null;
            return new Sample(at, await postgres, clickHouseSample is null ? null : await clickHouseSample, collector is null ? null : await collector);
        }

        private static readonly HttpClient Http = new() { Timeout = Timeout };

        private async Task<CollectorSample> SampleCollectorAsync(string url, string? metricsUrl, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await Http.GetAsync(url, cancellationToken);
                var duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
                if (!response.IsSuccessStatusCode || metricsUrl is not { Length: > 0 })
                {
                    return new CollectorSample(response.IsSuccessStatusCode, duration);
                }
                try
                {
                    var (signals, queues) = CollectorMetrics.Parse(await Http.GetStringAsync(metricsUrl, cancellationToken));
                    var now = Stopwatch.GetTimestamp();
                    signals = CollectorMetrics.WithRates(signals, _collector?.Signals,
                        _collector is { } last ? Stopwatch.GetElapsedTime(last.At, now).TotalSeconds : null);
                    _collector = (signals, now);
                    return new CollectorSample(true, duration, signals, queues);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
                {
                    logger.LogWarning(ex, "The Collector's metrics page didn't answer the sample");
                    _collector = null;
                    return new CollectorSample(true, duration);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
            {
                logger.LogWarning(ex, "The Collector's health check didn't answer the sample");
                return new CollectorSample(false, Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }

        private static async Task<T> WithinAsync<T>(Func<CancellationToken, Task<T>> sample, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            return await sample(deadline.Token);
        }

        // Juice DeliveryState: 0 NotPublished, 1 InProgress, 2 Published, 3 Failed (a retry due while next_attempt_on is set), 4 Skipped.
        private const string PostgresSql = """
            SELECT pg_database_size(current_database())::float8,
              extract(epoch FROM now() - (SELECT c.watermark FROM reconciliation_checkpoints c JOIN platform_tenants t ON t.id = c.tenant_id
                WHERE c.source = 'telemetry' AND t.key = @tenant))::float8,
              (SELECT count(*) FROM outbox_deliveries WHERE state IN (0, 1)),
              (SELECT count(*) FROM outbox_deliveries WHERE state = 3 AND next_attempt_on IS NOT NULL),
              -- Overdue: a new message from when it was written (a deferred one from when it falls due), a failed one from when its retry fell due,
              -- a retry in progress from when it started; one waiting for a later retry or its deferred time isn't.
              extract(epoch FROM now() - (SELECT min(CASE WHEN state = 3 THEN next_attempt_on WHEN state = 1 AND retry_count > 0 THEN processed_on
                ELSE coalesce(next_attempt_on, creation_time) END) FROM outbox_deliveries
                WHERE (state = 0 AND (next_attempt_on IS NULL OR next_attempt_on <= now())) OR state = 1 OR (state = 3 AND next_attempt_on <= now())))::float8
            """;

        /// <summary>The mix (Phase 11 R9): what drives the workers' cost besides the load. Apart from <see cref="PostgresSql"/>, which says whether PostgreSQL is up.</summary>
        private const string MixSql = """
            SELECT (SELECT count(*) FROM rules r JOIN platform_tenants t ON t.id = r.tenant_id WHERE r.enabled AND t.key = @tenant),
              (SELECT count(*) FROM slos s JOIN platform_tenants t ON t.id = s.tenant_id WHERE NOT s.retired AND t.key = @tenant),
              (SELECT count(*) FROM services s JOIN platform_tenants t ON t.id = s.tenant_id WHERE t.key = @tenant)
            """;

        private async Task<PostgresSample> SamplePostgresAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = PostgresSql;
                    var tenant = command.CreateParameter();
                    tenant.ParameterName = "tenant";
                    tenant.Value = platform.Value.TenantKey;
                    command.Parameters.Add(tenant);
                    PostgresSample sample;
                    await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        await reader.ReadAsync(cancellationToken);
                        sample = new PostgresSample(true, Stopwatch.GetElapsedTime(started).TotalSeconds, reader.GetDouble(0), Maybe(reader, 1),
                            reader.GetInt64(2), reader.GetInt64(3), Maybe(reader, 4));
                    }
                    // Only the capacity view uses the mix: a failure there leaves PostgreSQL up, and the last mix read stands.
                    try
                    {
                        if (_mix is not { } known || Stopwatch.GetElapsedTime(known.At) > MinuteRead)
                        {
                            command.CommandText = MixSql;
                            await using var mix = await command.ExecuteReaderAsync(cancellationToken);
                            await mix.ReadAsync(cancellationToken);
                            _mix = ((mix.GetInt64(0), mix.GetInt64(1), mix.GetInt64(2)), Stopwatch.GetTimestamp());
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
                    {
                        logger.LogWarning(ex, "The platform database's rule, SLO and service counts couldn't be read this sample");
                    }
                    return _mix is { Mix: var counts } ? sample with { Rules = counts.Rules, Slos = counts.Slos, Services = counts.Services } : sample;
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
            {
                logger.LogWarning(ex, "The platform database didn't answer the sample");
                return new PostgresSample(false, Stopwatch.GetElapsedTime(started).TotalSeconds, 0, null, 0, 0, null);
            }
        }

        private static double? Maybe(DbDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i));

        private async Task<ClickHouseSample> SampleClickHouseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = new ClickHouseConnection(clickHouse.Value.MonitorConnectionString);
                await connection.OpenAsync(cancellationToken);

                // CPU: ClickHouse's own threads' CPU time (a cumulative profile event), over the wall time since the previous sample.
                var micros = Convert.ToInt64(await ScalarAsync(connection, "SELECT value FROM system.events WHERE event = 'OSCPUVirtualTimeMicroseconds'", cancellationToken) ?? 0L);
                var now = Stopwatch.GetTimestamp();
                double? cores = _cpu is { } previous && micros >= previous.Micros
                    ? (micros - previous.Micros) / Math.Max(1, Stopwatch.GetElapsedTime(previous.At, now).TotalMicroseconds)
                    : null;
                _cpu = (micros, now);

                var memory = Convert.ToDouble(await ScalarAsync(connection, "SELECT value FROM system.asynchronous_metrics WHERE metric = 'MemoryResident'", cancellationToken) ?? 0d);

                var running = new Dictionary<string, long>(StringComparer.Ordinal);
                await ReadAsync(connection, "SELECT user, count() FROM system.processes GROUP BY user", r =>
                {
                    var user = User(r.GetString(0));
                    running[user] = running.GetValueOrDefault(user) + Convert.ToInt64(r.GetValue(1));
                }, cancellationToken);

                // Queries finished in one window, each window starting where the last ended, so none is counted twice or missed. The window is
                // on ClickHouse's own clock (its event_time), and ends 10 s back, since ClickHouse flushes its query log every 7.5 s. It moves
                // on only once read: a failed read is read again next time, up to MaxWindow back.
                var windowEnd = Convert.ToDateTime(await ScalarAsync(connection, "SELECT now() - 10", cancellationToken));
                var windowStart = _windowEnd is { } last && last <= windowEnd ? last : windowEnd - Interval;
                if (windowEnd - windowStart > MaxWindow) windowStart = windowEnd - MaxWindow;
                var finished = new Dictionary<(string User, string Kind), (long Ok, long Failed, double? P95)>();
                var insertSeconds = 0d;
                await ReadAsync(connection, """
                    SELECT user, query_kind, countIf(type = 'QueryFinish'), countIf(type != 'QueryFinish'),
                      quantileIf(0.95)(query_duration_ms, type = 'QueryFinish') / 1000, toFloat64(sumIf(query_duration_ms, type = 'QueryFinish')) / 1000
                    FROM system.query_log
                    WHERE type != 'QueryStart' AND event_time >= {from:DateTime} AND event_time < {to:DateTime}
                    GROUP BY user, query_kind
                    """, r =>
                {
                    // The Collector's inserts' durations: over the window's length, how many ran at once (Phase 11 R9).
                    if (r.GetString(0) == "otel_writer" && r.GetString(1) == "Insert") insertSeconds += Convert.ToDouble(r.GetValue(5));
                    var key = (User(r.GetString(0)), KnownKinds.Contains(r.GetString(1)) ? r.GetString(1) : "other");
                    var ok = Convert.ToInt64(r.GetValue(2));
                    var p95 = ok > 0 ? Convert.ToDouble(r.GetValue(4)) : (double?)null;
                    var had = finished.GetValueOrDefault(key);
                    finished[key] = (had.Ok + ok, had.Failed + Convert.ToInt64(r.GetValue(3)), had.P95 is { } h && p95 is { } q ? Math.Max(h, q) : p95 ?? had.P95);
                }, cancellationToken, ("from", windowStart), ("to", windowEnd));
                _windowEnd = windowEnd;
                lock (_finishedLock)
                {
                    foreach (var ((user, kind), (ok, failed, _)) in finished)
                    {
                        _finished[(user, kind, "success")] = _finished.GetValueOrDefault((user, kind, "success")) + ok;
                        _finished[(user, kind, "failure")] = _finished.GetValueOrDefault((user, kind, "failure")) + failed;
                    }
                }

                var disks = new List<(string, double, double)>();
                await ReadAsync(connection, "SELECT name, free_space, total_space FROM system.disks",
                    r => disks.Add((r.GetString(0), Convert.ToDouble(r.GetValue(1)), Convert.ToDouble(r.GetValue(2)))), cancellationToken);
                var tables = new List<(string, double, double)>();
                var days = new List<(string Table, double Yesterday, double YesterdaySeconds, double Today, double TodaySeconds)>();
                // Each table's size, and the bytes of yesterday's UTC partition with the time its rows span, and of today's (the exporter's tables
                // are partitioned by day).
                await ReadAsync(connection, """
                    SELECT table, sum(bytes_on_disk), sum(rows), toFloat64(sumIf(bytes_on_disk, partition = toString(yesterday()))),
                      if(countIf(partition = toString(yesterday())) = 0, 0,
                        toFloat64(maxIf(max_time, partition = toString(yesterday())) - minIf(min_time, partition = toString(yesterday())))),
                      toFloat64(sumIf(bytes_on_disk, partition = toString(today()))), toFloat64(now() - toStartOfDay(now()))
                    FROM system.parts WHERE active AND database = 'otel' GROUP BY table
                    """, r =>
                {
                    tables.Add((r.GetString(0), Convert.ToDouble(r.GetValue(1)), Convert.ToDouble(r.GetValue(2))));
                    days.Add((r.GetString(0), Convert.ToDouble(r.GetValue(3)), Convert.ToDouble(r.GetValue(4)), Convert.ToDouble(r.GetValue(5)),
                        Convert.ToDouble(r.GetValue(6))));
                }, cancellationToken);

                // What only the Overview and the capacity view use (Phase 11 R8, R9), apart: if it fails, ClickHouse is still up and its figures
                // above still count; these are left out of this sample, and logged.
                var inserting = insertSeconds / Math.Max(1, (windowEnd - windowStart).TotalSeconds);
                var growth = days.Select(d => TableGrowthOf(d.Table, d.Yesterday, d.YesterdaySeconds, d.Today, d.TodaySeconds, null)).ToList();
                try
                {
                    if (_machine is not { Cores: not null, Memory: not null } machine || Stopwatch.GetElapsedTime(machine.At) > SlowRead)
                    {
                        _machine = (
                            Convert.ToDouble(await ScalarAsync(connection, "SELECT toFloat64(getSetting('max_threads'))", cancellationToken) ?? 0d) is var c and > 0 ? c : null,
                            Convert.ToDouble(await ScalarAsync(connection, "SELECT value FROM system.asynchronous_metrics WHERE metric = 'OSMemoryTotal'", cancellationToken) ?? 0d) is var m and > 0 ? m : null,
                            Stopwatch.GetTimestamp());
                    }
                    if (_retention is not { } known || Stopwatch.GetElapsedTime(known.At) > SlowRead)
                    {
                        var retention = new Dictionary<string, int?>(StringComparer.Ordinal);
                        await ReadAsync(connection, """SELECT name, toInt32OrNull(extract(engine_full, 'toIntervalDay\\((\\d+)\\)')) FROM system.tables WHERE database = 'otel'""",
                            r => retention[r.GetString(0)] = r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)), cancellationToken);
                        _retention = (retention, Stopwatch.GetTimestamp());
                    }
                    growth = [.. growth.Select(g => g with { RetentionDays = _retention!.Value.Days.GetValueOrDefault(g.Name) })];
                    if (_callers is not { } read || Stopwatch.GetElapsedTime(read.At) > MinuteRead)
                    {
                        // The last minute of the query log, to the same end as the window above.
                        var callers = new List<CallerQueries>();
                        await ReadAsync(connection, """
                            SELECT if(log_comment = '', '(none)', substring(log_comment, 1, 80)), count(), quantile(0.95)(query_duration_ms) / 1000
                            FROM system.query_log
                            WHERE type = 'QueryFinish' AND user = 'platform_reader' AND event_time >= {to:DateTime} - 60 AND event_time < {to:DateTime}
                            GROUP BY 1 ORDER BY 3 DESC LIMIT 30
                            """, r => callers.Add(new CallerQueries(r.GetString(0), Convert.ToInt64(r.GetValue(1)), Convert.ToDouble(r.GetValue(2)))),
                            cancellationToken, ("to", windowEnd));
                        _callers = (callers, Stopwatch.GetTimestamp());
                    }
                }
                // The sample's deadline too: the figures above were read, so ClickHouse answered. Only the host stopping ends the sample.
                catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
                {
                    logger.LogWarning(ex, "ClickHouse's figures for the Overview couldn't be read this sample");
                    if (_callers is { } stale && Stopwatch.GetElapsedTime(stale.At) > MinuteRead) _callers = null;
                }

                return new ClickHouseSample(true, cores, memory, running,
                    [.. finished.Select(f => new FinishedQueries(f.Key.User, f.Key.Kind, f.Value.Ok, f.Value.Failed, f.Value.P95))], disks, tables,
                    _machine?.Cores, inserting, _callers?.Callers, growth, _machine?.Memory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !StoppingNow(cancellationToken))
            {
                logger.LogWarning(ex, "ClickHouse didn't answer the platform sample");
                _cpu = null;
                _callers = null;   // read afresh once it answers, never the minute before it went down
                return new ClickHouseSample(false, null, 0, new Dictionary<string, long>(), [], [], []);
            }
        }

        private static string User(string user) => KnownUsers.Contains(user) ? user : "other";

        /// <summary>
        /// A table's bytes a day: yesterday's UTC partition when its rows span the day (<see cref="WholeDay"/>), or scaled to a day from the
        /// time they span (ingestion started, or stopped, partway); before an hour of yesterday's, today's scaled once an hour of it has passed.
        /// Scaled figures are estimates (and parts not merged yet are larger than they will be).
        /// </summary>
        internal static TableGrowth TableGrowthOf(string name, double yesterday, double yesterdaySeconds, double today, double todaySeconds, int? retentionDays)
            => yesterday > 0 && yesterdaySeconds >= WholeDay.TotalSeconds ? new TableGrowth(name, yesterday, false, retentionDays)
                : yesterday > 0 && yesterdaySeconds >= 3600 ? new TableGrowth(name, yesterday * 86400 / yesterdaySeconds, true, retentionDays)
                : today > 0 && todaySeconds >= 3600 ? new TableGrowth(name, today * 86400 / todaySeconds, true, retentionDays)
                : new TableGrowth(name, null, false, retentionDays);

        /// <summary>How much of yesterday its rows must span to count as a whole day.</summary>
        internal static readonly TimeSpan WholeDay = TimeSpan.FromHours(22);

        /// <summary>The token passed in is the host's, linked with the sample's own deadline: only the host stopping ends the sampler quietly.</summary>
        private bool StoppingNow(CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested && (PlatformCaller.Stopping?.IsCancellationRequested ?? false);

        private static async Task<object?> ScalarAsync(ClickHouseConnection connection, string sql, CancellationToken cancellationToken)
        {
            await using var command = Command(connection, sql);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        private static async Task ReadAsync(ClickHouseConnection connection, string sql, Action<DbDataReader> row, CancellationToken cancellationToken,
            params (string Name, object Value)[] parameters)
        {
            await using var command = Command(connection, sql);
            foreach (var (name, value) in parameters)
            {
                command.AddParameter(name, value);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                row(reader);
            }
        }

        private static ClickHouseCommand Command(ClickHouseConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CustomSettings["log_comment"] = ClickHouseTelemetrySource.LogComment("sampler");
            return command;
        }
    }
}
