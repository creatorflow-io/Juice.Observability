using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Juice.Observability.Slos;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 9 milestone 4: does scheduled evaluation meet its targets with 50 rules and 50 SLOs at the reference load? Hosts the whole platform on a
    /// scratch database, reading the ClickHouse tenant <c>tests/load.sh</c> fills, and measures what <c>docs/phase-9-plan.md</c> fixed before the run.
    /// Runs only with <c>JUICE_BENCHMARK=1</c> (about 50 minutes): <c>deploy/local/tests/streaming-benchmark.sh</c> starts it beside the load.
    /// It reports the numbers; it fails only when the measurement itself is incomplete, not on a missed target.
    /// </summary>
    public class StreamingBenchmark
    {
        private const string Environment = "bench";
        /// <summary>Its own per run: the services share the ClickHouse tenant with earlier runs, whose failures would otherwise count as this run's history.</summary>
        private static readonly string Namespace = $"bench-{Guid.NewGuid():N}"[..14];
        private const int Services = 50, PerTick = 20;
        /// <summary>20 in the benchmark; <c>JUICE_BENCHMARK_BREAKS</c> fewer for a smoke run of the harness itself, which also skips the 200-SLO read.</summary>
        private static readonly int Breaks = int.TryParse(System.Environment.GetEnvironmentVariable("JUICE_BENCHMARK_BREAKS"), out var b) ? Math.Clamp(b, 1, 20) : 20;
        private const double Target = 99.5, FastRate = 14.4;
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ForDuration = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FirstBreak = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan BreakEvery = TimeSpan.FromSeconds(75);
        /// <summary>How long a break may take to be seen before it counts as not seen.</summary>
        private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(8);
        private const string SecretVariable = "JUICE_WEBHOOK_BENCH";

        private static string Service(int i) => $"svc-{i:00}";
        private static string ErrorRule(int i) => $"bench-errors-{i:00}";
        private static string AvailabilitySlo(int i) => $"bench-avail-{i:00}";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Scheduled_evaluation_at_the_reference_load_Async()
        {
            Assert.SkipUnless(System.Environment.GetEnvironmentVariable("JUICE_BENCHMARK") == "1", "a 50-minute benchmark: deploy/local/tests/streaming-benchmark.sh runs it");
            LocalStack.RequireAvailable();
            System.Environment.SetEnvironmentVariable(SecretVariable, "bench-secret");
            var report = new Report();
            using var metrics = Metrics(report);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await Receiver.StartAsync();
            await EndpointAsync(database, receiver);

            var traffic = new Traffic();
            var t0 = DateTimeOffset.UtcNow;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var sending = traffic.RunAsync(t0, stop.Token);
            var watching = Task.CompletedTask;
            try
            {
                await using (var factory = new ApiFactory(database, settings: Settings(slos: 50)))
                {
                    using var client = factory.CreateClient();
                    var viewer = new Caller(client, await FreshTokenAsync("victor"));
                    var tenant = (await Caller.JsonAsync(await viewer.GetAsync("/api/v1/tenants"))).GetProperty("items")[0].GetProperty("id").GetGuid();

                    Log($"platform up; waiting for the 50 SLOs to count their 30 days");
                    await CaughtUpAsync(database, 50, TimeSpan.FromMinutes(7));
                    var seeded = await SeedAsync(database, t0, onlyAfter: null);
                    Log($"seeded {seeded:N0} buckets of history");
                    report.Reads50 = await ReadAsync(client, tenant, "under the reference load, 50 SLOs");

                    var seen = new ConcurrentDictionary<string, DateTimeOffset>();
                    watching = WatchAsync(database, seen, stop.Token);
                    await WaitAsync(t0 + FirstBreak + BreakEvery * (Breaks - 1) + TimeSpan.FromSeconds(30));
                    // Every break seen, or the last given up on.
                    var deadline = t0 + FirstBreak + BreakEvery * (Breaks - 1) + GiveUp;
                    while (DateTimeOffset.UtcNow < deadline && Enumerable.Range(0, Breaks).Any(i => !seen.ContainsKey(RuleDedup(i)) || !seen.ContainsKey(BurnDedup(i))))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
                    }
                    Measure(report, traffic, seen, t0);
                    await ExplainBurnsAsync(database, traffic, seen, t0);
                    report.Dispatch = await DispatchAsync(database);
                }
                report.EvaluationRules = report.TakeRuleLag();
                report.EvaluationSlos = report.TakeBurnLag();

                // The same read at the 200-SLO limit: 150 more SLOs on the same services, counted and seeded, then read.
                if (Breaks == 20)
                await using (var factory = new ApiFactory(database, settings: Settings(slos: 200)))
                {
                    using var client = factory.CreateClient();
                    var viewer = new Caller(client, await FreshTokenAsync("victor"));
                    var tenant = (await Caller.JsonAsync(await viewer.GetAsync("/api/v1/tenants"))).GetProperty("items")[0].GetProperty("id").GetGuid();
                    Log("200 SLOs: waiting for the new 150 to count their 30 days");
                    await CaughtUpAsync(database, 200, TimeSpan.FromMinutes(12));
                    var seeded = await SeedAsync(database, t0, onlyAfter: 50);
                    Log($"seeded {seeded:N0} more buckets");
                    report.Reads200 = await ReadAsync(client, tenant, "200 SLOs");
                }
            }
            finally
            {
                await stop.CancelAsync();
                await Task.WhenAll(sending.ContinueWith(_ => { }), watching.ContinueWith(_ => { }));
            }

            report.Write();
            Assert.True(report.RuleBreaches.Count == Breaks && report.FastBurns.Count == Breaks,
                $"every break should be measured: {report.RuleBreaches.Count} rule breaches and {report.FastBurns.Count} burns seen of {Breaks}");
        }

        /// <summary>
        /// The benchmark's reference point, checked by hand: 240 requests a minute, seeded until 10 minutes before the break and sent live since. From
        /// the break all fail, so the 1-hour window holds 14,420 requests and reaches 7.2% bad (14.4 × 0.5%) at the 52nd failing tick, 255 s in.
        /// </summary>
        [Fact]
        public void A_burn_crosses_when_both_windows_reach_the_rate_on_what_was_sent()
        {
            var broke = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            var sent = Enumerable.Range(-120, 200).Select(n => (broke + Tick * n, n < 0 ? PerTick : 0, n < 0 ? 0 : PerTick)).ToList();
            Assert.Equal(broke + TimeSpan.FromSeconds(255), Crossing(sent, broke, broke - TimeSpan.FromMinutes(10), broke - TimeSpan.FromDays(31)));
            // Without enough failing yet, no crossing.
            Assert.Null(Crossing(sent.Take(170).ToList(), broke, broke - TimeSpan.FromMinutes(10), broke - TimeSpan.FromDays(31)));
        }

        // ─── The platform under test ─────────────────────────────────────────

        private static Dictionary<string, string> Settings(int slos)
        {
            var s = new Dictionary<string, string>
            {
                ["Discovery:Enabled"] = "true", ["Health:Enabled"] = "true", ["Rules:Enabled"] = "true", ["Slos:Enabled"] = "true",
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true",
                // As the local stack: a new SLO's 30 days count in five passes, not sixty.
                ["Slos:MaxMinutesPerPass"] = "10080",
            };
            var r = 0;
            void Rule(string key, string template, string service, params (string, string)[] values)
            {
                s[$"Rules:Definitions:{r}:Key"] = key;
                s[$"Rules:Definitions:{r}:Name"] = key;
                s[$"Rules:Definitions:{r}:Template"] = template;
                s[$"Rules:Definitions:{r}:Service"] = $"{Namespace}/{service}";
                s[$"Rules:Definitions:{r}:Environment"] = Environment;
                foreach (var (k, v) in values) s[$"Rules:Definitions:{r}:{k}"] = v;
                r++;
            }
            for (var i = 0; i < 20; i++) Rule(ErrorRule(i), "ErrorRate", Service(i), ("Severity", "Critical"), ("Threshold", "0.05"), ("RecoveryThreshold", "0.02"), ("MinimumSamples", "20"), ("ForDuration", "00:01:00"));
            for (var i = 20; i < 35; i++) Rule($"bench-latency-{i:00}", "LatencyP95", Service(i), ("Threshold", "1000"), ("RecoveryThreshold", "800"), ("MinimumSamples", "20"), ("ForDuration", "00:05:00"));
            for (var i = 35; i < 50; i++) Rule($"bench-traffic-{i:00}", "ThroughputAbsent", Service(i), ("Window", "00:10:00"), ("Threshold", "0"), ("ForDuration", "00:05:00"));

            for (var i = 0; i < slos; i++)
            {
                var service = Service(i % Services);
                var latency = i % Services >= 35;
                var key = i < Services ? (latency ? $"bench-latency-{i:00}" : AvailabilitySlo(i)) : $"bench-extra-{i:000}";
                s[$"Slos:Definitions:{i}:Key"] = key;
                s[$"Slos:Definitions:{i}:Name"] = key;
                s[$"Slos:Definitions:{i}:Sli"] = latency ? "Latency" : "Availability";
                s[$"Slos:Definitions:{i}:Service"] = $"{Namespace}/{service}";
                s[$"Slos:Definitions:{i}:Environment"] = Environment;
                s[$"Slos:Definitions:{i}:Target"] = latency ? "99" : Target.ToString(CultureInfo.InvariantCulture);
                if (latency) s[$"Slos:Definitions:{i}:ThresholdMs"] = "300";
            }
            return s;
        }

        private static async Task EndpointAsync(ScratchDatabase database, Receiver receiver)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            db.WebhookEndpoints.Add(new WebhookEndpoint
            {
                TenantId = tenant, Key = "bench", Url = receiver.Url, SecretRef = $"env:{SecretVariable}", Events = [NotificationPlan.Firing],
                DefinitionHash = "h", LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api,
            });
            db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenant, Destination = $"{receiver.Address}/32", Ports = [receiver.Port], LoadedAt = DateTimeOffset.UtcNow, ManagedBy = ManagedBy.Api });
            await db.SaveChangesAsync(Ct);
        }

        // ─── Traffic: 50 services, 4 requests a second each; a broken one fails every request ───

        private sealed class Traffic
        {
            /// <summary>What each service sent: per tick, when and how many good and bad.</summary>
            public ConcurrentDictionary<int, List<(DateTimeOffset At, int Good, int Bad)>> Sent { get; } = new();
            public ConcurrentDictionary<int, DateTimeOffset> BrokeAt { get; } = new();

            public async Task RunAsync(DateTimeOffset t0, CancellationToken ct)
            {
                using var timer = new PeriodicTimer(Tick);
                do
                {
                    var now = DateTimeOffset.UtcNow;
                    var resources = new JsonArray();
                    for (var i = 0; i < Services; i++)
                    {
                        var broken = i < Breaks && now >= t0 + FirstBreak + BreakEvery * i;
                        if (broken) BrokeAt.TryAdd(i, now);
                        var spans = new JsonArray();
                        for (var k = 0; k < PerTick; k++)
                        {
                            var span = Otlp.Lasting(Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, "GET /work", Otlp.Server, now,
                                ("http.route", "/work"), ("http.response.status_code", broken ? "500" : "200")), TimeSpan.FromMilliseconds(20));
                            spans.Add(broken ? Otlp.Failed(span) : span);
                        }
                        resources.Add(new JsonObject
                        {
                            ["resource"] = new JsonObject
                            {
                                ["attributes"] = new JsonArray(
                                    Attribute("service.name", Service(i)), Attribute("service.namespace", Namespace), Attribute("deployment.environment.name", Environment),
                                    Attribute("service.instance.id", $"{Service(i)}-1"), Attribute("host.name", $"bench-host-{i % 5}")),
                            },
                            ["scopeSpans"] = new JsonArray(new JsonObject { ["scope"] = new JsonObject { ["name"] = "bench" }, ["spans"] = spans }),
                        });
                        Sent.GetOrAdd(i, _ => []).Add((now, broken ? 0 : PerTick, broken ? PerTick : 0));
                    }
                    try
                    {
                        await LocalStack.SendAsync("traces", new JsonObject { ["resourceSpans"] = resources });
                    }
                    catch (HttpRequestException e)
                    {
                        Log($"traffic: a send failed ({e.Message}); the next tick carries on");
                    }
                }
                while (await timer.WaitForNextTickAsync(ct));
            }

            private static JsonObject Attribute(string key, string value) => new() { ["key"] = key, ["value"] = new JsonObject { ["stringValue"] = value } };
        }

        // ─── Seeding 30 days of history ─────────────────────────────────────

        private static async Task CaughtUpAsync(ScratchDatabase database, int count, TimeSpan within)
        {
            var until = DateTimeOffset.UtcNow + within;
            while (true)
            {
                await using var db = database.Context();
                var behind = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4);
                var slos = await db.Slos.AsNoTracking().Where(s => !s.Retired).Select(s => s.CountedThrough).ToListAsync(Ct);
                if (slos.Count == count && slos.All(c => c >= behind)) return;
                if (DateTimeOffset.UtcNow > until) Assert.Fail($"SLO counting didn't catch up within {within}: {slos.Count(c => c >= behind)} of {count}");
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            }
        }

        /// <summary>
        /// A minute bucket per SLO for each minute of the 30 days before the traffic began, as if each service had sent its 4 requests a second all
        /// along, all good. Only minutes with no bucket (the counter stores none for minutes without telemetry). Those minutes are sealed, so counting
        /// never rewrites them.
        /// </summary>
        private static async Task<long> SeedAsync(ScratchDatabase database, DateTimeOffset t0, int? onlyAfter)
        {
            await using var db = database.Context();
            var slos = (await db.Slos.AsNoTracking().Where(s => !s.Retired).OrderBy(s => s.Key).ToListAsync(Ct))
                .Where(s => onlyAfter is null || s.Key.StartsWith("bench-extra-", StringComparison.Ordinal)).ToList();
            var tenant = slos[0].TenantId;
            var end = SloCounter.Floor(t0);
            var start = end - SloOptions.Window - TimeSpan.FromMinutes(5);
            var perMinute = PerTick * 12L;
            var histogram = new long[SloHistogram.Slots];
            histogram[SloHistogram.Boundaries.ToList().IndexOf(25)] = perMinute;   // 20 ms
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using (var create = new NpgsqlCommand("CREATE TEMP TABLE seed (LIKE slo_buckets INCLUDING DEFAULTS)", connection)) await create.ExecuteNonQueryAsync(Ct);
            await using (var copy = await connection.BeginBinaryImportAsync(
                "COPY seed (tenant_id, slo_id, minute, version, total, good, eligible, bad, excluded_health, excluded_cancelled, excluded_client_errors, histogram, counted_at) FROM STDIN (FORMAT BINARY)", Ct))
            {
                foreach (var slo in slos)
                {
                    for (var minute = start; minute < end; minute += TimeSpan.FromMinutes(1))
                    {
                        await copy.StartRowAsync(Ct);
                        await copy.WriteAsync(tenant, NpgsqlDbType.Uuid, Ct);
                        await copy.WriteAsync(slo.Id, NpgsqlDbType.Uuid, Ct);
                        await copy.WriteAsync(minute, NpgsqlDbType.TimestampTz, Ct);
                        await copy.WriteAsync(1, NpgsqlDbType.Integer, Ct);
                        foreach (var n in new[] { perMinute, perMinute, perMinute, 0L, 0L, 0L, 0L }) await copy.WriteAsync(n, NpgsqlDbType.Bigint, Ct);
                        await copy.WriteAsync(histogram, NpgsqlDbType.Array | NpgsqlDbType.Bigint, Ct);
                        await copy.WriteAsync(DateTimeOffset.UtcNow, NpgsqlDbType.TimestampTz, Ct);
                    }
                }
                await copy.CompleteAsync(Ct);
            }
            await using var insert = new NpgsqlCommand("INSERT INTO slo_buckets SELECT * FROM seed ON CONFLICT DO NOTHING", connection) { CommandTimeout = 600 };
            var inserted = await insert.ExecuteNonQueryAsync(Ct);
            // Their daily totals, as the counter keeps them for what it writes; the day the traffic began too, which holds counted minutes.
            foreach (var slo in slos) await SloDays.RecomputeAsync(db, slo.Id, start, end + TimeSpan.FromMinutes(1), Ct);
            await using (var analyze = new NpgsqlCommand("ANALYZE slo_buckets; ANALYZE slo_hours; ANALYZE slo_days", connection)) await analyze.ExecuteNonQueryAsync(Ct);
            return inserted;
        }

        // ─── Measuring ──────────────────────────────────────────────────────

        private static string RuleDedup(int i) => $"{ErrorRule(i)}|service:{Namespace}/{Service(i)}@{Environment}";
        private static string BurnDedup(int i) => SloBurnRules.DedupKey(SloBurnRules.FastBurn, AvailabilitySlo(i));

        /// <summary>When each firing alert was first seen committed, polling every half second: burn alerts' own times are their windows' end.</summary>
        private static async Task WatchAsync(ScratchDatabase database, ConcurrentDictionary<string, DateTimeOffset> seen, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var db = database.Context();
                    var firing = await db.Alerts.AsNoTracking().Where(a => a.State == AlertState.Firing).Select(a => a.DedupKey).ToListAsync(ct);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var key in firing) seen.TryAdd(key, now);
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static void Measure(Report report, Traffic traffic, ConcurrentDictionary<string, DateTimeOffset> seen, DateTimeOffset t0)
        {
            var seededFrom = SloCounter.Floor(t0) - SloOptions.Window - TimeSpan.FromMinutes(5);
            for (var i = 0; i < Breaks; i++)
            {
                if (!traffic.BrokeAt.TryGetValue(i, out var broke)) continue;
                if (seen.TryGetValue(RuleDedup(i), out var fired)) report.RuleBreaches.Add((fired - (broke + ForDuration)).TotalSeconds);
                else report.Missing.Add($"{ErrorRule(i)}: not seen firing within {GiveUp}");
                var crossed = Crossing(traffic.Sent[i], broke, SloCounter.Floor(t0), seededFrom);
                if (crossed is null) report.Missing.Add($"{AvailabilitySlo(i)}: its windows never reached {FastRate}× on what was sent");
                else if (seen.TryGetValue(BurnDedup(i), out var burned)) report.FastBurns.Add((burned - crossed.Value).TotalSeconds);
                else report.Missing.Add($"{AvailabilitySlo(i)}: fast burn not seen within {GiveUp}");
            }
        }

        /// <summary>
        /// The first moment both fast-burn windows (1 h and 5 min) reach the rate on what the service sent (and the seeded minutes before), each with at
        /// least the burn minimum of requests: when the burn became true, before any counting or settling.
        /// </summary>
        internal static DateTimeOffset? Crossing(IReadOnlyList<(DateTimeOffset At, int Good, int Bad)> sent, DateTimeOffset broke, DateTimeOffset seededUntil, DateTimeOffset seededFrom)
        {
            var allowed = (100 - Target) / 100 * FastRate;
            var perMinute = PerTick * 12.0;
            (double Eligible, double Bad) Window(DateTimeOffset at, TimeSpan length)
            {
                var from = at - length;
                var live = sent.Where(s => s.At > from && s.At <= at);
                var eligible = live.Sum(s => (double)(s.Good + s.Bad));
                var bad = live.Sum(s => (double)s.Bad);
                // Seeded minutes in the window: all good.
                var seededStart = from > seededFrom ? from : seededFrom;
                if (seededUntil > seededStart) eligible += (seededUntil - seededStart).TotalMinutes * perMinute;
                return (eligible, bad);
            }
            foreach (var tick in sent.Where(s => s.At >= broke))
            {
                var (e1, b1) = Window(tick.At, TimeSpan.FromHours(1));
                var (e5, b5) = Window(tick.At, TimeSpan.FromMinutes(5));
                if (e1 >= 10 && e5 >= 10 && b1 / e1 >= allowed && b5 / e5 >= allowed) return tick.At;
            }
            return null;
        }

        /// <summary>For each burn: what the alert's evidence says its windows held, beside what was sent in them when it was seen.</summary>
        private static async Task ExplainBurnsAsync(ScratchDatabase database, Traffic traffic, ConcurrentDictionary<string, DateTimeOffset> seen, DateTimeOffset t0)
        {
            await using var db = database.Context();
            for (var i = 0; i < Breaks; i++)
            {
                var alert = await db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.DedupKey == BurnDedup(i), Ct);
                if (alert is null || !traffic.BrokeAt.TryGetValue(i, out var broke) || !seen.TryGetValue(BurnDedup(i), out var at)) continue;
                var sent = traffic.Sent[i].Where(s => s.At <= at).ToList();
                var bad = sent.Where(s => s.At >= broke).Sum(s => s.Bad);
                Log($"{AvailabilitySlo(i)}: broke {broke:HH:mm:ss}, seen {at:HH:mm:ss} ({(at - broke).TotalSeconds:0} s after), {bad} bad sent by then; " +
                    $"alert breach {alert.BreachStartedAt:HH:mm:ss}, evidence {string.Join(", ", alert.Evidence.Select(e => $"{e.Key}={e.Value}"))}");
            }
        }

        private static async Task<Stats> DispatchAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var deliveries = await db.WebhookDeliveries.AsNoTracking().Where(d => d.FirstAttemptAt != null).Select(d => new { d.CreatedAt, d.FirstAttemptAt }).ToListAsync(Ct);
            return Stats.Of(deliveries.Select(d => (d.FirstAttemptAt!.Value - d.CreatedAt).TotalSeconds));
        }

        /// <summary>A token of its own: the shared one is taken once per test run and lives 5 minutes, less than this benchmark.</summary>
        private static async Task<string> FreshTokenAsync(string user)
        {
            using var keycloak = new HttpClient();
            using var response = await keycloak.PostAsync($"{LocalStack.Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "juice-platform-test", ["username"] = user, ["password"] = LocalStack.Secret("KEYCLOAK_DEMO_PASSWORD"),
            }), Ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
        }

        /// <summary><c>GET slos</c> at 20 requests a second for 60 seconds, with a token taken just before.</summary>
        private static async Task<Stats> ReadAsync(HttpClient client, Guid tenant, string what)
        {
            const int Rate = 20, Seconds = 60;
            var viewer = new Caller(client, await FreshTokenAsync("victor"));
            var latencies = new ConcurrentBag<double>();
            var statuses = new ConcurrentDictionary<int, int>();
            var errors = 0;
            var started = Stopwatch.GetTimestamp();
            var pending = new List<Task>();
            for (var i = 0; i < Rate * Seconds; i++)
            {
                var due = TimeSpan.FromSeconds((double)i / Rate) - Stopwatch.GetElapsedTime(started);
                if (due > TimeSpan.Zero) await Task.Delay(due, Ct);
                pending.Add(Task.Run(async () =>
                {
                    var t = Stopwatch.GetTimestamp();
                    using var response = await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos");
                    await response.Content.ReadAsByteArrayAsync(Ct);
                    statuses.AddOrUpdate((int)response.StatusCode, 1, (_, n) => n + 1);
                    if (!response.IsSuccessStatusCode && Interlocked.Increment(ref errors) == 1)
                    {
                        var body = await response.Content.ReadAsStringAsync(Ct);
                        Log($"first error: {(int)response.StatusCode} {body[..Math.Min(400, body.Length)]}");
                    }
                    latencies.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                }, Ct));
            }
            await Task.WhenAll(pending);
            var stats = Stats.Of(latencies) with { Errors = errors };
            Log($"GET slos, {what}: {stats}; status codes {string.Join(", ", statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}×{s.Value}"))}");
            return stats;
        }

        private static MeterListener Metrics(Report report)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Name is "juice.observability.rules.lag" or "juice.observability.slos.burn_lag" or "juice.observability.slos.duration" or "juice.observability.rules.duration")
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => report.Metric(instrument.Name, value));
            listener.Start();
            return listener;
        }

        private static async Task WaitAsync(DateTimeOffset until)
        {
            var wait = until - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, Ct);
        }

        private static void Log(string text)
        {
            var line = $"{DateTimeOffset.UtcNow:HH:mm:ss} {text}";
            Console.WriteLine(line);
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        // ─── The report ─────────────────────────────────────────────────────

        internal sealed record Stats(int Count, double P50, double P95, double Max, int Errors = 0)
        {
            public static Stats Of(IEnumerable<double> values)
            {
                var sorted = values.Order().ToList();
                if (sorted.Count == 0) return new(0, double.NaN, double.NaN, double.NaN);
                double Rank(double p) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * p) - 1)];
                return new(sorted.Count, Rank(0.5), Rank(0.95), sorted[^1]);
            }

            public override string ToString() => $"n {Count}, p50 {P50:0.##}, p95 {P95:0.##}, max {Max:0.##}{(Errors > 0 ? $", {Errors} errors" : "")}";
        }

        private sealed class Report
        {
            public List<double> RuleBreaches { get; } = [];
            public List<double> FastBurns { get; } = [];
            public List<string> Missing { get; } = [];
            public Stats? Dispatch { get; set; }
            public Stats? Reads50 { get; set; }
            public Stats? Reads200 { get; set; }
            public Stats? EvaluationRules { get; set; }
            public Stats? EvaluationSlos { get; set; }
            private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _metrics = new();

            public void Metric(string name, double value) => _metrics.GetOrAdd(name, _ => []).Add(value);
            public Stats TakeRuleLag() => Stats.Of(_metrics.GetValueOrDefault("juice.observability.rules.lag") ?? []);
            public Stats TakeBurnLag() => Stats.Of(_metrics.GetValueOrDefault("juice.observability.slos.burn_lag") ?? []);
            private Stats Of(string name) => Stats.Of(_metrics.GetValueOrDefault(name) ?? []);

            public void Write()
            {
                var rules = Stats.Of(RuleBreaches);
                var burns = Stats.Of(FastBurns);
                string Verdict(bool met) => met ? "**Met**" : "**Missed**";
                var rows = new List<(string Measure, Stats? Stats, string Unit, string Target, bool Met)>
                {
                    ("Rule breach-to-alert after ForDuration", rules, "s", "p95 ≤ 120 s", rules.Count > 0 && rules.P95 <= 120),
                    // DR-3 (approved 11 November): the 2-minute settle kept, the target set to 4 minutes.
                    ("SLO fast-burn detection after the rate is crossed", burns, "s", "p95 ≤ 240 s", burns.Count > 0 && burns.P95 <= 240),
                    ("First webhook dispatch", Dispatch, "s", "p95 ≤ 30 s", Dispatch is { Count: > 0, P95: <= 30 }),
                    ("Rule evaluation lag", EvaluationRules, "s", "max ≤ 60 s", EvaluationRules is { Count: > 0, Max: <= 60 }),
                    ("SLO burn evaluation lag", EvaluationSlos, "s", "max ≤ 60 s", EvaluationSlos is { Count: > 0, Max: <= 60 }),
                    ("GET slos, 50 SLOs, under the reference load", Reads50, "ms", "p95 ≤ 500 ms", Reads50 is { Count: > 0, P95: <= 500, Errors: 0 }),
                    ("GET slos, 200 SLOs", Reads200, "ms", "p95 ≤ 500 ms", Reads200 is { Count: > 0, P95: <= 500, Errors: 0 }),
                };
                var text = new StringBuilder();
                text.AppendLine($"Streaming benchmark, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
                text.AppendLine();
                text.AppendLine("| Measure | n | p50 | p95 | max | errors | Target | Result |");
                text.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var (measure, stats, unit, target, met) in rows)
                {
                    text.AppendLine(stats is null || stats.Count == 0 ? $"| {measure} | 0 | | | | | {target} | **Not measured** |"
                        : $"| {measure} | {stats.Count} | {stats.P50:0.#} {unit} | {stats.P95:0.#} {unit} | {stats.Max:0.#} {unit} | {stats.Errors} | {target} | {Verdict(met)} |");
                }
                text.AppendLine();
                text.AppendLine($"Rule evaluation: {Of("juice.observability.rules.duration")} s. SLO counting pass: {Of("juice.observability.slos.duration")} s.");
                text.AppendLine($"Rule breaches (s): {string.Join(", ", RuleBreaches.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))}");
                text.AppendLine($"Fast burns (s): {string.Join(", ", FastBurns.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))}");
                foreach (var m in Missing) text.AppendLine($"Missing: {m}");
                var output = System.Environment.GetEnvironmentVariable("JUICE_BENCHMARK_OUT") ?? Path.Combine(Path.GetTempPath(), "juice-streaming-benchmark.md");
                File.WriteAllText(output, text.ToString());
                Log($"report written to {output}\n{text}");
            }
        }
    }
}
