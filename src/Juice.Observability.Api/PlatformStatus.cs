using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Writes this replica's heartbeats (Phase 11 R6): every <see cref="Interval"/>, one row per worker the platform has (<see cref="WorkerPulse.Snapshot"/>,
    /// enabled here or not) in <c>worker_heartbeats</c>, in one upsert; and prunes rows of replicas not seen for <see cref="Prune"/>. Every time in
    /// a row is the database's: this replica's ages are subtracted from the database's <c>now()</c>, so replicas' clocks needn't agree.
    /// </summary>
    /// <para>Once the host starts stopping it writes no more: the last row of a worker leaves it enabled, so a replica that stopped reads as gone
    /// wherever it ran a worker no live replica runs, whatever order the host stops its services in.</para>
    public sealed class HeartbeatWriter(IServiceScopeFactory scopes, ILogger<HeartbeatWriter> logger, IHostApplicationLifetime? lifetime = null) : BackgroundService
    {
        /// <summary>When this process started, on its monotonic clock: now, less the process's own age (read once, from its start time).</summary>
        private static readonly long ProcessStarted = System.Diagnostics.Stopwatch.GetTimestamp()
            - (long)(Math.Max(0, (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds) * System.Diagnostics.Stopwatch.Frequency);

        /// <summary>This replica has written its heartbeats at least once: until then its own row can't be expected.</summary>
        public bool Written { get; private set; }

        /// <summary>How long this process has run.</summary>
        public static TimeSpan Uptime => System.Diagnostics.Stopwatch.GetElapsedTime(ProcessStarted);

        /// <summary>
        /// Whether this replica's own row is expected yet: once it has written, or once <see cref="PlatformStatus.Live"/> has passed since this
        /// writer started (a writer that never succeeds is down, not starting forever; a slow startup before it, such as migrations, doesn't count).
        /// </summary>
        public bool Expected => Written || (_writerStarted != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_writerStarted) > PlatformStatus.Live);

        private long _writerStarted;

        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan Prune = TimeSpan.FromHours(1);
        /// <summary>A failure is logged once, then at most this often while it lasts.</summary>
        private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(5);

        /// <summary>This replica, as its heartbeats and its metrics (<c>service.instance.id</c>) name it.</summary>
        public static string Replica { get; } = Environment.MachineName;

        private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _writerStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // The workers register as their loops start, alongside this one: a moment for them, so the first row isn't a false "disabled".
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                if (lifetime?.ApplicationStopping.IsCancellationRequested == true)
                {
                    return;
                }
                try
                {
                    await WriteOnceAsync(stoppingToken);
                    _lastWarning = DateTimeOffset.MinValue;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow - _lastWarning >= WarnEvery)
                    {
                        _lastWarning = DateTimeOffset.UtcNow;
                        logger.LogWarning(ex, "Writing the workers' heartbeats failed; retrying every {Interval} (logged again in {Every} if it goes on)", Interval, WarnEvery);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private static double? Secs(TimeSpan? age) => age?.TotalSeconds;

        /// <summary>Writes every worker's row for <paramref name="replica"/> (this one by default), and prunes replicas gone for an hour. Exposed for tests.</summary>
        public async Task WriteOnceAsync(CancellationToken cancellationToken, string? replica = null)
        {
            var workers = WorkerPulse.Snapshot();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO worker_heartbeats (worker, replica, version, enabled, leased, held, interval_seconds, registered_at, held_since,
                      running_since, last_ended_at, last_outcome, replica_started_at, seen_at)
                    SELECT w.worker, @replica, @version, w.enabled, w.leased, w.held, w.interval,
                      now() - make_interval(secs => w.registered), now() - make_interval(secs => w.held_for), now() - make_interval(secs => w.running),
                      now() - make_interval(secs => w.since_end), w.outcome, now() - make_interval(secs => @uptime), now()
                    FROM unnest(@worker, @enabled, @leased, @held, @interval, @registered, @held_for, @running, @since_end, @outcome)
                      AS w(worker, enabled, leased, held, interval, registered, held_for, running, since_end, outcome)
                    ON CONFLICT (worker, replica) DO UPDATE SET version = excluded.version, enabled = excluded.enabled, leased = excluded.leased,
                      held = excluded.held, interval_seconds = excluded.interval_seconds, registered_at = excluded.registered_at,
                      held_since = excluded.held_since, running_since = excluded.running_since, last_ended_at = excluded.last_ended_at,
                      last_outcome = excluded.last_outcome, replica_started_at = excluded.replica_started_at, seen_at = excluded.seen_at;
                    DELETE FROM worker_heartbeats WHERE seen_at < now() - @prune;
                    """, connection);
                command.Parameters.AddWithValue("replica", replica ?? Replica);
                // The column holds 63 characters; a longer prerelease version is cut, never a failed write.
                command.Parameters.AddWithValue("version", WorkerLease.CurrentVersion.Length <= 63 ? WorkerLease.CurrentVersion : WorkerLease.CurrentVersion[..63]);
                command.Parameters.AddWithValue("prune", Prune);
                command.Parameters.AddWithValue("uptime", System.Diagnostics.Stopwatch.GetElapsedTime(ProcessStarted).TotalSeconds);
                command.Parameters.Add(new NpgsqlParameter("worker", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = workers.Select(w => w.Worker).ToArray() });
                command.Parameters.Add(new NpgsqlParameter("enabled", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = workers.Select(w => w.Enabled).ToArray() });
                command.Parameters.Add(new NpgsqlParameter("leased", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = workers.Select(w => w.Leased).ToArray() });
                command.Parameters.Add(new NpgsqlParameter("held", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = workers.Select(w => w.Held).ToArray() });
                command.Parameters.Add(new NpgsqlParameter("interval", NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = workers.Select(w => w.Interval.TotalSeconds).ToArray() });
                foreach (var (name, values) in new (string, double?[])[]
                {
                    ("registered", [.. workers.Select(w => Secs(w.RegisteredFor))]), ("held_for", [.. workers.Select(w => Secs(w.HeldFor))]),
                    ("running", [.. workers.Select(w => Secs(w.RunningFor))]), ("since_end", [.. workers.Select(w => Secs(w.SinceLastEnd))]),
                })
                {
                    command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Double) { Value = values });
                }
                command.Parameters.Add(new NpgsqlParameter("outcome", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = workers.Select(w => w.LastOutcome).ToArray() });
                await command.ExecuteNonQueryAsync(cancellationToken);
                Written = true;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>The platform's own status (Phase 11 R6, R7): each worker across replicas, the stores it depends on, and its backlogs.</summary>
    public static class PlatformStatus
    {
        /// <summary>A replica whose heartbeat is older than this is gone.</summary>
        public static readonly TimeSpan Live = TimeSpan.FromSeconds(30);
        /// <summary>The least a worker may go without a pass before it is stale, whatever its interval.</summary>
        public static readonly TimeSpan MinimumThreshold = TimeSpan.FromSeconds(60);
        /// <summary>How far past when it was due a backlog's oldest item may get (the scale tests' queue-lag line).</summary>
        public static readonly TimeSpan BacklogLine = TimeSpan.FromSeconds(60);

        public const string Ok = "ok";
        public const string Degraded = "degraded";

        /// <summary>The worker states that make the platform degraded; <c>ok</c>, <c>starting</c>, <c>handing_over</c> and <c>off</c> don't.</summary>
        public static readonly IReadOnlySet<string> BadStates = new HashSet<string> { "stale", "stuck", "unheld", "gone" };

        public sealed record ReplicaWorker(string Replica, string Version, bool Enabled, bool Held, DateTimeOffset? HeldSince, DateTimeOffset? RunningSince,
            DateTimeOffset? LastEndedAt, string? LastOutcome, DateTimeOffset SeenAt);

        public sealed record WorkerStatus(string Name, string State, bool Leased, double IntervalSeconds, double ThresholdSeconds, string? Holder,
            DateTimeOffset? LastPassEndedAt, double? RunningForSeconds, string? LastOutcome, IReadOnlyList<ReplicaWorker> Replicas);

        /// <summary><c>up</c>, <c>down</c>, <c>unknown</c> (no fresh sample) or <c>not_configured</c>.</summary>
        public sealed record DependencyStatus(string Name, string State);

        /// <summary><c>ok</c>, <c>behind</c> (past its line), <c>unknown</c>, or <c>off</c> (its worker isn't running anywhere).</summary>
        public sealed record BacklogStatus(string Name, string State, double? AgeSeconds, double LineSeconds);

        public sealed record ReplicaStatus(string Name, string Version, DateTimeOffset SeenAt);

        public sealed record Report(string Status, DateTimeOffset At, IReadOnlyList<WorkerStatus> Workers, IReadOnlyList<DependencyStatus> Dependencies,
            IReadOnlyList<BacklogStatus> Backlogs, IReadOnlyList<ReplicaStatus> Replicas);

        /// <summary>A worker's stale line: three intervals, and never under <see cref="MinimumThreshold"/>.</summary>
        public static TimeSpan Threshold(TimeSpan interval) => interval * 3 > MinimumThreshold ? interval * 3 : MinimumThreshold;

        /// <summary>
        /// Judges the platform from its heartbeats (<paramref name="heartbeats"/>; null when the database couldn't be read), at the database's time
        /// <paramref name="now"/>, and this replica's last sample, judged fresh on this replica's own clock (<paramref name="localNow"/>). A worker is
        /// judged across the live replicas (a heartbeat within <see cref="Live"/>) that have it enabled:
        /// <list type="bullet">
        /// <item>under a lease: <c>stuck</c> when the holder (the latest to take the lease) has a pass running past the threshold, <c>stale</c> when its
        /// last pass ended longer ago, <c>handing_over</c> when nobody holds it but some replica passed within the threshold, <c>starting</c> while
        /// every replica with it enabled registered it within the threshold, <c>unheld</c> otherwise;</item>
        /// <item>in every replica (no lease): the worst of its replicas.</item>
        /// </list>
        /// A worker every live replica has disabled is <c>off</c>. One that a replica gone within the hour had enabled, and no live replica has,
        /// is <c>gone</c>: its replicas stopped. The replica answering must have a live heartbeat of its own (<c>heartbeats</c>), or nobody's is
        /// being written. A dependency without a fresh sample is <c>unknown</c>, which alone doesn't degrade the status: the sampler's own
        /// heartbeat says whether it stopped.
        /// </summary>
        public static Report Evaluate(IReadOnlyList<WorkerHeartbeat>? heartbeats, DateTimeOffset now, PlatformSampler.Sample? sample, DateTimeOffset localNow,
            DiscoveryOptions discovery, string self, bool selfWritten = true)
        {
            // Only the workers this build has: a row of a worker a later release dropped or renamed is left to the hourly prune.
            var rows = (heartbeats ?? []).Where(h => WorkerPulse.KnownWorkers.Contains(h.Worker)).ToList();
            var live = rows.Where(h => now - h.SeenAt <= Live).ToList();
            var workers = rows.GroupBy(h => h.Worker).OrderBy(g => g.Key).Select(g => Worker(g.Key, [.. g], [.. live.Where(h => h.Worker == g.Key)], now, selfWritten))
                .Where(w => w is not null).Select(w => w!).ToList();
            var fresh = sample is not null && localNow - sample.At <= PlatformSampler.Fresh ? sample : null;

            var dependencies = new List<DependencyStatus>
            {
                new("postgres", heartbeats is null ? "down" : fresh is null ? "unknown" : fresh.Postgres.Up ? "up" : "down"),
                new("clickhouse", fresh is null ? "unknown" : fresh.ClickHouse is null ? "not_configured" : fresh.ClickHouse.Up ? "up" : "down"),
                new("collector", fresh is null ? "unknown" : fresh.Collector is null ? "not_configured" : fresh.Collector.Up ? "up" : "down"),
                // Before this replica's first write its own row can't be there yet (a fresh replica): starting, not down.
                new("heartbeats", heartbeats is null ? "unknown" : live.Any(h => h.Replica == self) ? "up" : selfWritten ? "down" : "starting"),
            };
            var postgres = fresh?.Postgres is { Up: true } p ? p : null;
            var discoveryOff = heartbeats is not null && workers.FirstOrDefault(w => w.Name == "discovery")?.State is null or "off";
            var backlogs = new List<BacklogStatus>
            {
                Backlog("outbox", postgres is null ? null : postgres.OutboxOldest ?? 0, BacklogLine),
                Backlog("webhooks", postgres is null ? null : postgres.WebhookOldest ?? 0, BacklogLine),
                // Discovery reads Discovery:Lag behind now on purpose, a pass every Discovery:Interval: behind is a minute past both.
                heartbeats is null ? new BacklogStatus("discovery", "unknown", null, (discovery.Lag + discovery.Interval + BacklogLine).TotalSeconds)
                    : discoveryOff ? new BacklogStatus("discovery", "off", postgres?.WatermarkLag, (discovery.Lag + discovery.Interval + BacklogLine).TotalSeconds)
                    : Backlog("discovery", postgres?.WatermarkLag, discovery.Lag + discovery.Interval + BacklogLine),
            };
            var replicas = live.GroupBy(h => h.Replica).OrderBy(g => g.Key)
                .Select(g => new ReplicaStatus(g.Key, g.OrderByDescending(h => h.SeenAt).First().Version, g.Max(h => h.SeenAt))).ToList();
            var degraded = heartbeats is null || workers.Any(w => BadStates.Contains(w.State)) || dependencies.Any(d => d.State == "down")
                || backlogs.Any(b => b.State == "behind");
            return new Report(degraded ? Degraded : Ok, now, workers, dependencies, backlogs, replicas);
        }

        /// <summary>How long a rollout may take: replicas gone this soon after a newer generation started were replaced by it.</summary>
        public static readonly TimeSpan RolloutWindow = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The replicas that had a worker enabled were replaced (a redeploy that switched it off) rather than stopping: some live replica, with it
        /// disabled, started after the last of them did, and within <see cref="RolloutWindow"/> of when they were last seen, either side (a
        /// stop-then-start redeploy starts the new one just after, a rolling one just before). That covers redeploying only the worker replicas
        /// while the others keep running. The cost: a worker replica that crashes within the window of some other replica's start reads as
        /// off, not gone. Rows of a replica that is gone for good can also be deleted by hand (runbook).
        /// </summary>
        private static bool Replaced(List<WorkerHeartbeat> gone, List<WorkerHeartbeat> live)
        {
            if (gone.Any(r => r.ReplicaStartedAt is null))
            {
                return false;
            }
            var newest = gone.Max(r => r.ReplicaStartedAt!.Value);
            var lastSeen = gone.Max(r => r.SeenAt);
            return live.Any(r => !r.Enabled && r.ReplicaStartedAt is { } started && started > newest && (started - lastSeen).Duration() <= RolloutWindow);
        }

        private static BacklogStatus Backlog(string name, double? age, TimeSpan line)
            => new(name, age is null ? "unknown" : age > line.TotalSeconds ? "behind" : "ok", age, line.TotalSeconds);

        private static readonly Dictionary<string, int> Worst = new() { ["stuck"] = 4, ["stale"] = 3, ["starting"] = 1, ["ok"] = 0 };

        private static WorkerStatus? Worker(string name, List<WorkerHeartbeat> all, List<WorkerHeartbeat> live, DateTimeOffset now, bool selfExpected)
        {
            var rows = live.Where(r => r.Enabled).ToList();
            ReplicaWorker Replica(WorkerHeartbeat r) => new(r.Replica, r.Version, r.Enabled, r.Held, r.HeldSince, r.RunningSince, r.LastEndedAt, r.LastOutcome, r.SeenAt);
            if (rows.Count == 0)
            {
                // Enabled on a replica gone within the hour, and on no live one: its replicas stopped (gone), unless they were replaced by a
                // redeploy that switched it off (Replaced, RolloutWindow). Disabled on every live replica that was there: off.
                var enabledBefore = all.Where(r => r.Enabled && now - r.SeenAt > Live).ToList();
                if (enabledBefore.Count > 0 && !selfExpected)
                {
                    // This replica hasn't written yet: it may be the one that replaced them. Judged once its own row is there.
                    var first = enabledBefore.OrderByDescending(r => r.SeenAt).First();
                    return new WorkerStatus(name, "starting", first.Leased, first.IntervalSeconds, Threshold(TimeSpan.FromSeconds(first.IntervalSeconds)).TotalSeconds,
                        null, enabledBefore.Max(r => r.LastEndedAt), null, first.LastOutcome, [.. enabledBefore.Select(Replica)]);
                }
                if (enabledBefore.Count > 0 && !Replaced(enabledBefore, live))
                {
                    var last = enabledBefore.OrderByDescending(r => r.SeenAt).First();
                    return new WorkerStatus(name, "gone", last.Leased, last.IntervalSeconds, Threshold(TimeSpan.FromSeconds(last.IntervalSeconds)).TotalSeconds, null,
                        enabledBefore.Max(r => r.LastEndedAt), null, last.LastOutcome, [.. enabledBefore.Select(Replica)]);
                }
                return live.Count == 0 ? null : new WorkerStatus(name, "off", live[0].Leased, 0, 0, null, null, null, null, [.. live.Select(Replica)]);
            }

            var interval = TimeSpan.FromSeconds(rows.Max(r => r.IntervalSeconds));
            var threshold = Threshold(interval);
            var leased = rows.Any(r => r.Leased);

            string State(WorkerHeartbeat r)
            {
                if (r.RunningSince is { } since)
                {
                    return now - since > threshold ? "stuck" : "ok";
                }
                if (r.LastEndedAt is { } last)
                {
                    return now - last <= threshold ? "ok" : "stale";
                }
                // No pass yet: since it registered, or, for a holder, since it took the lease (a standby that just took over).
                var from = r.Held && r.HeldSince is { } took ? took : r.RegisteredAt;
                return from is { } since2 && now - since2 <= threshold ? "starting" : "stale";
            }

            WorkerHeartbeat? holder = null;
            string state;
            if (!leased)
            {
                state = rows.Select(State).MaxBy(s => Worst[s])!;
            }
            else if ((holder = rows.Where(r => r.Held).MaxBy(r => r.HeldSince ?? DateTimeOffset.MinValue)) is not null)
            {
                state = State(holder);
            }
            else if (all.Where(r => r.Enabled).Max(r => r.LastEndedAt) is { } lastAny && now - lastAny <= threshold)
            {
                state = "handing_over";
            }
            else
            {
                state = rows.All(r => r.RegisteredAt is { } registered && now - registered <= threshold) ? "starting" : "unheld";
            }

            var reference = holder ?? rows.OrderByDescending(r => r.LastEndedAt).First();
            return new WorkerStatus(name, state, leased, interval.TotalSeconds, threshold.TotalSeconds, holder?.Replica,
                rows.Max(r => r.LastEndedAt), reference.RunningSince is { } running ? (now - running).TotalSeconds : null, reference.LastOutcome,
                [.. live.OrderBy(r => r.Replica).Select(Replica)]);
        }
    }

    /// <summary>
    /// The platform's status, for <c>/health/platform</c> and the operators' status endpoint. The heartbeats are read at most every <see cref="Cache"/>
    /// (many probes cost one read); the dependencies come from this replica's last sample, in memory, on every request, so a store going down shows
    /// within one sample (<see cref="PlatformSampler.Interval"/> plus its timeout). A probe never reaches ClickHouse or the Collector.
    /// </summary>
    public sealed class PlatformStatusService(IServiceScopeFactory scopes, PlatformSampler sampler, IOptions<DiscoveryOptions> discovery,
        ILogger<PlatformStatusService> logger, HeartbeatWriter? writer = null)
    {
        public static readonly TimeSpan Cache = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _one = new(1, 1);
        /// <summary>One read: swapped whole, so a request never pairs one read's time with another's. ReadAt is a monotonic timestamp.</summary>
        /// <para>Expected: this replica's own row was expected when the read was taken; only such a read is cached, and it is judged as it was.</para>
        private sealed record Read(IReadOnlyList<WorkerHeartbeat>? Rows, DateTimeOffset DatabaseNow, long ReadAt, bool Expected);

        private volatile Read? _read;

        private static TimeSpan Since(long at) => System.Diagnostics.Stopwatch.GetElapsedTime(at);

        public async Task<PlatformStatus.Report> GetAsync(CancellationToken cancellationToken)
        {
            var read = await HeartbeatsAsync(cancellationToken);
            // The database's time now: its time at the read, plus what has passed here since.
            return PlatformStatus.Evaluate(read.Rows, read.DatabaseNow + Since(read.ReadAt), sampler.Latest, DateTimeOffset.UtcNow, discovery.Value,
                HeartbeatWriter.Replica, read.Expected);
        }

        private async Task<Read> HeartbeatsAsync(CancellationToken cancellationToken)
        {
            // A read taken before this replica's own row was expected (its first seconds) isn't reused: the next request reads again.
            if (_read is { Expected: true } cached && Since(cached.ReadAt) < Cache)
            {
                return cached;
            }
            await _one.WaitAsync(cancellationToken);
            try
            {
                if (_read is { Expected: true } again && Since(again.ReadAt) < Cache)
                {
                    return again;
                }
                var read = await ReadAsync();
                _read = read;
                return read;
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>The heartbeats and the database's time, or null rows when the database doesn't answer within <see cref="ReadTimeout"/>.</summary>
        private async Task<Read> ReadAsync()
        {
            // Taken before the query: a write landing during the read must not make a read without its row count as expected.
            var expected = writer?.Expected ?? true;
            try
            {
                using var deadline = new CancellationTokenSource(ReadTimeout);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(deadline.Token);
                var rows = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(deadline.Token);
                return new Read(rows, databaseNow, System.Diagnostics.Stopwatch.GetTimestamp(), expected);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The platform's status couldn't read the workers' heartbeats");
                return new Read(null, DateTimeOffset.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp(), expected);
            }
        }
    }

    public static class PlatformStatusEndpoints
    {
        /// <summary>
        /// <c>/health/platform</c> (anonymous): only <c>ok</c> or <c>degraded</c>, 503 when degraded, for alerting probes. Not for a liveness or
        /// readiness probe: every replica gives the same answer, so it would restart or remove them all at once. And <c>/api/v1/platform/status</c>:
        /// every worker, dependency, backlog and replica, for an operator or admin of this deployment's tenant (its tenant claim).
        /// </summary>
        public static IEndpointRouteBuilder MapPlatformStatus(this IEndpointRouteBuilder app)
        {
            app.MapGet("/health/platform", async (HttpContext http, PlatformStatusService status, CancellationToken ct) =>
            {
                var report = await status.GetAsync(ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { status = report.Status }, statusCode: report.Status == PlatformStatus.Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            }).AllowAnonymous();
            app.MapGet("/api/v1/platform/status", async (HttpContext http, PlatformStatusService status, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await status.GetAsync(ct));
            }).RequireAuthorization(Policies.Operate).AddEndpointFilter(ThisDeploymentAsync);
            return app;
        }

        /// <summary>An operator of this deployment: one IdP may serve several, and roles alone don't say which (the tenant claim does).</summary>
        private static async ValueTask<object?> ThisDeploymentAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var claim = http.User.FindFirst(http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.TenantClaim)?.Value;
            var key = http.RequestServices.GetRequiredService<IOptions<PlatformOptions>>().Value.TenantKey;
            if (claim is null || claim != key)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Tenant not authorized",
                    detail: "The caller is not authorized for this deployment.");
            }
            return await next(context);
        }
    }
}
