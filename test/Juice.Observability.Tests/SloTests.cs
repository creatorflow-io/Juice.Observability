using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// SLOs (Phase 9, milestone 1): definitions and their versions, which requests count and how, minute buckets counted from real telemetry, and
    /// budgets and burn rates that never take missing data for success.
    /// </summary>
    public class SloTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];

        private static SloDefinition Availability(string key = "orders-availability", string service = "shop/orders") => new()
        {
            Key = key, Name = "Orders availability", Sli = SliKinds.Availability, Service = service, Environment = "production", Target = 99.5,
        };

        // ─── definitions ─────────────────────────────────────────────────────

        [Fact]
        public void A_definition_is_checked_before_anything_counts()
        {
            Assert.Empty(Availability().Validate());
            static IEnumerable<string> Errors(Action<SloDefinition> change)
            {
                var d = Availability();
                change(d);
                return d.Validate();
            }
            Assert.Contains(Errors(d => d.Sli = "BusinessSuccess"), e => e.Contains("not supported yet", StringComparison.Ordinal));
            Assert.Contains(Errors(d => d.Sli = "Throughput"), e => e.Contains("Sli is one of Availability, Latency", StringComparison.Ordinal));
            Assert.Contains(Errors(d => d.Target = 100), e => e.Contains("Target", StringComparison.Ordinal));
            Assert.Contains(Errors(d => d.Target = 0), e => e.Contains("Target", StringComparison.Ordinal));
            Assert.Contains(Errors(d => d.ThresholdMs = 300), e => e.Contains("ThresholdMs is for Latency", StringComparison.Ordinal));
            Assert.Contains(Errors(d => { d.Sli = SliKinds.Latency; d.ThresholdMs = 333; }), e => e.Contains("one of 1, 2, 5", StringComparison.Ordinal));
            Assert.Empty(Errors(d => { d.Sli = SliKinds.Latency; d.ThresholdMs = 300; }));
            Assert.Contains(Errors(d => d.Operations = ["/orders%' OR 1=1"]), e => e.Contains("isn't an operation pattern", StringComparison.Ordinal));
            Assert.Contains(Errors(d => d.Environment = "Production"), e => e.Contains("lowercase", StringComparison.Ordinal));
            Assert.Equal("/health%", SloDefinition.ToLike("/health*"));
            Assert.Equal("/a\\_b%", SloDefinition.ToLike("/a_b*"));
            // The limit counts the patterns written, not the health checks that come with them.
            Assert.Empty(Errors(d => d.ExcludeOperations = [.. Enumerable.Range(0, 47).Select(i => $"/x{i}")]));
            Assert.NotEmpty(Errors(d => d.ExcludeOperations = [.. Enumerable.Range(0, 51).Select(i => $"/x{i}")]));
            // Options: a pass at least every minute, and at most 200 SLOs.
            Assert.Contains(new SloOptions { Interval = TimeSpan.FromMinutes(2) }.Validate(), e => e.Contains("Slos:Interval", StringComparison.Ordinal));
            Assert.Empty(new SloOptions { Interval = TimeSpan.FromMinutes(1) }.Validate());
            var many = new SloOptions { Definitions = [.. Enumerable.Range(0, 201).Select(i => Availability($"slo-{i}"))] };
            Assert.Contains(many.Validate(), e => e.Contains("at most 200 SLOs", StringComparison.Ordinal));
        }

        [Fact]
        public void Technical_and_business_kinds_are_apart_and_only_the_technical_ones_exist_yet()
        {
            Assert.All(SliKinds.Supported, k => Assert.Equal(SloCategory.Technical, k.Category));
            Assert.Equal(["BusinessSuccess", "MessageProcessing", "QueueLatency", "DataFreshness"], SliKinds.Reserved);
        }

        [Fact]
        public void A_latency_threshold_reads_straight_from_the_histogram()
        {
            var histogram = new long[SloHistogram.Slots];
            histogram[SloHistogram.Boundaries.ToList().IndexOf(50)] = 10;     // ≤ 50 ms
            histogram[SloHistogram.Boundaries.ToList().IndexOf(300)] = 3;     // ≤ 300 ms
            histogram[SloHistogram.Boundaries.ToList().IndexOf(400)] = 4;     // ≤ 400 ms
            histogram[^1] = 1;                                                  // slower than a minute
            Assert.Equal(13, SloHistogram.AtOrUnder(histogram, 300));
            Assert.Equal(10, SloHistogram.AtOrUnder(histogram, 250));
            var latency = new LatencySli();
            Assert.Equal((18L, 13L), latency.Score(new MinuteCounts(20, 2, 0, 0, 0, histogram), new SloDefinition { ThresholdMs = 300 }));
            Assert.Equal((20L, 18L), new AvailabilitySli().Score(new MinuteCounts(20, 2, 0, 0, 0, histogram), Availability()));
        }

        // ─── budgets ─────────────────────────────────────────────────────────

        [Fact]
        public void Missing_data_is_never_success()
        {
            var day = TimeSpan.FromDays(1);
            // 20% of the minutes unread: unknown, whatever the counted ones say.
            var unknown = SloBudget.Window("1d", day, [new VersionSums(1, 99, 100_000, 100_000, 1152)], 10, 0.10);
            Assert.Equal((SloStates.Unknown, (double?)null, (double?)null), (unknown.State, unknown.Sli, unknown.BurnRate));
            // Every minute read, but too few requests to say anything.
            var thin = SloBudget.Window("1d", day, [new VersionSums(1, 99, 5, 5, 1440)], 10, 0.10);
            Assert.Equal((SloStates.InsufficientData, (double?)null), (thin.State, thin.BurnRate));
            Assert.Equal(SloStates.InsufficientData, SloBudget.Status(thin, []).State);
            Assert.Null(SloBudget.Status(thin, []).BudgetUsed);
            // No minutes at all: unknown, not a perfect score.
            Assert.Equal(SloStates.Unknown, SloBudget.Window("1h", TimeSpan.FromHours(1), [], 10, 0.10).State);
            // Telemetry started two days ago: the 30 days expect those two days, and all of them counted is known.
            var twoDays = 2 * 1440;
            Assert.Equal(SloStates.Known, SloBudget.Window("30d", SloOptions.Window, [new VersionSums(1, 99, 100_000, 99_900, twoDays)], 1000, 0.10, twoDays).State);
            // A gap after that: half a day not counted is unknown.
            Assert.Equal(SloStates.Unknown,
                SloBudget.Window("30d", SloOptions.Window, [new VersionSums(1, 99, 100_000, 99_900, twoDays - 720)], 1000, 0.10, twoDays).State);
            // Nothing expected yet (the first minute is still settling): unknown.
            Assert.Equal(SloStates.Unknown, SloBudget.Window("1h", TimeSpan.FromHours(1), [], 10, 0.10, 0).State);
        }

        [Fact]
        public void Exclusions_from_configuration_add_to_the_health_checks_which_can_be_switched_off()
        {
            var json = """
                { "Slos": { "Definitions": [
                    { "Key": "custom", "ExcludeOperations": [ "/ping" ] },
                    { "Key": "default" },
                    { "Key": "everything", "ExcludeHealthChecks": false, "ExcludeOperations": [] }
                ] } }
                """;
            var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
            var definitions = config.GetSection(SloOptions.Section).Get<SloOptions>()!.Definitions;
            Assert.Equal([.. SloDefinition.DefaultExclusions, "/ping"], definitions[0].Exclusions);
            Assert.Equal(SloDefinition.DefaultExclusions, definitions[1].Exclusions);
            Assert.Empty(definitions[2].Exclusions);
            // However they're written, the same exclusions are the same version, and a stored version reads back as written.
            var written = Availability();
            written.ExcludeHealthChecks = false;
            written.ExcludeOperations = [.. SloDefinition.DefaultExclusions];
            Assert.Equal(Availability().Hash(), written.Hash());
            Assert.Equal(SloDefinition.DefaultExclusions, SloDefinition.FromJson(Availability().ToJson()).Exclusions);
            var none = Availability();
            none.ExcludeHealthChecks = false;
            Assert.Empty(SloDefinition.FromJson(none.ToJson()).Exclusions);
            Assert.NotEqual(Availability().Hash(), none.Hash());
        }

        [Fact]
        public void A_burn_rate_of_one_spends_the_budget_exactly_and_each_version_keeps_its_own_target()
        {
            var hour = TimeSpan.FromHours(1);
            // 99% target: 1% may fail. 1% failed is a rate of 1; 14.4% is a fast burn.
            Assert.Equal(1.0, SloBudget.Window("1h", hour, [new VersionSums(1, 99, 1000, 990, 60)], 10, 0.1).BurnRate!.Value, 6);
            Assert.Equal(14.4, SloBudget.Window("1h", hour, [new VersionSums(1, 99, 1000, 856, 60)], 10, 0.1).BurnRate!.Value, 6);
            // Version 1 at 99% (10 allowed of 1000), version 2 at 99.9% (1 allowed of 1000): 11 allowed, 11 bad is a rate of 1.
            var mixed = SloBudget.Window("1h", hour, [new VersionSums(1, 99, 1000, 995, 30), new VersionSums(2, 99.9, 1000, 994, 30)], 10, 0.1);
            Assert.Equal(1.0, mixed.BurnRate!.Value, 6);
            Assert.Equal(1989.0 / 2000, mixed.Sli!.Value, 6);
        }

        [Theory]
        [InlineData(995_000, SloStates.Met)]        // 5,000 bad of 5,000 allowed... at 99%: 10,000 allowed, half used
        [InlineData(992_000, SloStates.AtRisk)]     // 80% used
        [InlineData(989_000, SloStates.Exhausted)]  // 110% used
        public void The_budget_is_met_at_risk_or_exhausted(long good, string state)
        {
            var budget = SloBudget.Window("30d", SloOptions.Window, [new VersionSums(1, 99, 1_000_000, good, 43_200)], 1000, 0.1);
            Assert.Equal(state, SloBudget.Status(budget, []).State);
        }

        // ─── counting ────────────────────────────────────────────────────────

        private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 30, TimeSpan.Zero);

        [Fact]
        public void A_new_slo_counts_its_window_from_the_start_a_pass_at_a_time()
        {
            var options = new SloOptions();
            var (from, to) = SloCounter.Range(null, Now, Now, options);
            Assert.Equal(SloCounter.Floor(Now - SloOptions.Window), from);
            Assert.Equal(from + TimeSpan.FromMinutes(options.MaxMinutesPerPass), to);
        }

        [Fact]
        public void The_newest_minutes_are_counted_again_until_sealed()
        {
            var (from, to) = SloCounter.Range(Now - TimeSpan.FromMinutes(1), Now, Now, new SloOptions());
            Assert.Equal(SloCounter.Floor(Now - TimeSpan.FromMinutes(5)), from);
            Assert.Equal(SloCounter.Floor(Now - TimeSpan.FromSeconds(10)), to);
        }

        [Fact]
        public void Minutes_after_the_newest_telemetry_stay_unknown()
        {
            // The pipeline stopped 20 minutes ago: nothing after the minute it stopped in is counted, so those minutes can't look quiet.
            var stopped = Now - TimeSpan.FromMinutes(20);
            var (from, to) = SloCounter.Range(Now - TimeSpan.FromMinutes(30), stopped, Now, new SloOptions());
            Assert.Equal(SloCounter.Floor(stopped + TimeSpan.FromMinutes(1)), to);
            Assert.True(to < Now - TimeSpan.FromMinutes(18));
            Assert.Equal(Now - TimeSpan.FromMinutes(30), from);
        }

        [Fact]
        public void Counting_never_reaches_back_past_the_window_and_recounts_behind_lagging_telemetry()
        {
            var options = new SloOptions();
            // Retired in the spring and listed again: counting picks up at the 30-day window, not where it stopped.
            var (from, _) = SloCounter.Range(Now - TimeSpan.FromDays(180), Now, Now, options);
            Assert.Equal(SloCounter.Floor(Now - SloOptions.Window), from);
            // A pass that ran out of time narrows the next one, down to a floor.
            (from, var narrowed) = SloCounter.Range(null, Now, Now, options, SloCounter.NextChunk(options.MaxMinutesPerPass, options));
            Assert.Equal(TimeSpan.FromMinutes(options.MaxMinutesPerPass / 2), narrowed - from);
            Assert.Equal(SloCounter.MinimumChunk(options), SloCounter.NextChunk(SloCounter.MinimumChunk(options) + 1, options));
            // However far it narrows, a caught-up pass still reaches the newest minutes past the ones it counts again, even at the longest Lateness.
            foreach (var lateness in new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1) })
            {
                var slow = new SloOptions { Lateness = lateness, MaxMinutesPerPass = (int)lateness.TotalMinutes + 5 };
                Assert.Empty(slow.Validate());
                var smallest = SloCounter.NextChunk(SloCounter.MinimumChunk(slow), slow);
                var (f, t) = SloCounter.Range(Now - TimeSpan.FromMinutes(1), Now, Now, slow, smallest);
                Assert.Equal(SloCounter.Floor(Now - TimeSpan.FromSeconds(10)), t);
                Assert.True(f <= Now - lateness, $"Lateness {lateness}: counts from {f}");
            }
            // A pass shorter than Lateness plus a margin is refused.
            Assert.Contains(new SloOptions { Lateness = TimeSpan.FromMinutes(30), MaxMinutesPerPass = 20 }.Validate(), e => e.Contains("MaxMinutesPerPass", StringComparison.Ordinal));
            // Telemetry 7 minutes behind: the 5 minutes before it are counted again, though they're older than 5 minutes by the clock.
            var lagging = Now - TimeSpan.FromMinutes(7);
            (from, var to) = SloCounter.Range(Now - TimeSpan.FromMinutes(6), lagging, Now, options);
            Assert.Equal(SloCounter.Floor(lagging - options.Lateness), from);
            Assert.Equal(SloCounter.Floor(lagging + TimeSpan.FromMinutes(1)), to);
        }

        [Fact]
        public void A_minute_is_counted_under_the_version_in_effect_then()
        {
            var v1 = new SloVersion { Version = 1, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now };
            var v2 = new SloVersion { Version = 2, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now + TimeSpan.FromHours(1) };
            Assert.Equal(1, SloCounter.VersionAt([v1, v2], Now - TimeSpan.FromDays(3)).Version);   // history before version 1: version 1
            Assert.Equal(1, SloCounter.VersionAt([v1, v2], Now + TimeSpan.FromMinutes(59)).Version);
            Assert.Equal(2, SloCounter.VersionAt([v1, v2], Now + TimeSpan.FromHours(1)).Version);
        }

        [Fact]
        public void A_version_replaced_before_its_date_never_applies()
        {
            var v1 = new SloVersion { Version = 1, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now };
            var v2 = new SloVersion { Version = 2, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now + TimeSpan.FromDays(20), WithdrawnAt = Now };
            var v3 = new SloVersion { Version = 3, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now + TimeSpan.FromDays(5) };
            SloVersion[] versions = [v1, v2, v3];
            Assert.Equal(1, SloCounter.VersionAt(versions, Now + TimeSpan.FromDays(1)).Version);
            Assert.Equal(3, SloCounter.VersionAt(versions, Now + TimeSpan.FromDays(6)).Version);
            Assert.Equal(3, SloCounter.VersionAt(versions, Now + TimeSpan.FromDays(25)).Version);   // not version 2: it was withdrawn
            // Its date moved later instead: version 2 is still withdrawn, so version 1 holds until version 3's later date.
            var later = new SloVersion { Version = 3, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now + TimeSpan.FromDays(30) };
            Assert.Equal(1, SloCounter.VersionAt([v1, v2, later], Now + TimeSpan.FromDays(25)).Version);
            Assert.Equal(3, SloCounter.VersionAt([v1, v2, later], Now + TimeSpan.FromDays(31)).Version);
            // A new SLO's only version dated later: history before it is counted under it.
            var only = new SloVersion { Version = 1, Definition = "", Hash = "", CreatedBy = "", EffectiveFrom = Now + TimeSpan.FromDays(1) };
            Assert.Equal(1, SloCounter.VersionAt([only], Now).Version);
        }

        [Fact]
        public void A_day_expects_only_its_minutes_inside_the_window_and_since_counting_started()
        {
            var now = new DateTimeOffset(2026, 11, 9, 14, 37, 20, TimeSpan.Zero);
            var windowStart = SloCounter.Floor(now) - SloOptions.Window;                                   // Oct 10, 14:37
            var day = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(1440 - (14 * 60 + 37), SloReader.ExpectedMinutes(day, windowStart, now));       // the window's first day: from 14:37
            Assert.Equal(1440, SloReader.ExpectedMinutes(day + TimeSpan.FromDays(5), windowStart, now));
            Assert.Equal(14 * 60 + 37, SloReader.ExpectedMinutes(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), windowStart, now)); // today, so far
            // Counting started Oct 20 at 06:00: that day expects 18 hours, the days before it none.
            var since = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
            Assert.Equal(18 * 60, SloReader.ExpectedMinutes(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), since, now));
            Assert.Equal(0, SloReader.ExpectedMinutes(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), since, now));
        }

        [Fact]
        public void Query_rows_fold_into_each_minutes_counts()
        {
            var minute = SloCounter.Floor(Now);
            var counts = SloCounter.Fold([
                (minute, SloCounter.GoodRequest, 4, 10L),      // slot 4: ≤ 10 ms
                (minute, SloCounter.GoodRequest, 0, 1L),       // slower than every boundary
                (minute, SloCounter.BadRequest, 0, 3L),
                (minute, SloCounter.ExcludedHealth, 0, 2L),
                (minute, SloCounter.ExcludedCancelled, 0, 1L),
                (minute, SloCounter.ExcludedClient, 0, 4L),
            ])[minute];
            Assert.Equal((14L, 3L, 2L, 1L, 4L), (counts.Eligible, counts.Bad, counts.ExcludedHealth, counts.ExcludedCancelled, counts.ExcludedClientErrors));
            Assert.Equal(10, counts.Histogram[3]);
            Assert.Equal(1, counts.Histogram[^1]);
        }

        // ─── end to end on the local stack ───────────────────────────────────

        private static JsonObject Request(string name, string route, string? status, DateTimeOffset at, TimeSpan duration, int kind = Otlp.Server, bool failed = false,
            params (string, string)[] extra)
        {
            var attributes = new List<(string, string)> { ("http.route", route), ("url.path", route) };
            if (status is not null) attributes.Add(("http.response.status_code", status));
            attributes.AddRange(extra);
            var span = Otlp.Lasting(Otlp.Span(Otlp.TraceId(), Otlp.SpanId(), null, name, kind, at, [.. attributes]), duration);
            return failed ? Otlp.Failed(span) : span;
        }

        private ServiceProvider Services(ScratchDatabase database) => new ServiceCollection()
            .AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString)).BuildServiceProvider();

        private SloCounter Counter(ScratchDatabase database, SloOptions? options = null) => new(Services(database).GetRequiredService<IServiceScopeFactory>(),
            new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })), Options.Create(options ?? new SloOptions()),
            Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), TimeProvider.System, NullLogger<SloCounter>.Instance);

        /// <summary>
        /// Every eligibility case from the Phase 9 plan, as real spans through the Collector, counted under four SLOs: availability, latency at
        /// 300 ms, availability that counts client errors and cancellations as bad, and availability of one operation only.
        /// </summary>
        [Fact]
        public async Task Requests_are_counted_as_the_eligibility_rules_say_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"slo-{_run}";
            var at = SloCounter.Floor(DateTimeOffset.UtcNow) - TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(10);
            var fast = TimeSpan.FromMilliseconds(50);
            JsonObject[] spans =
            [
                .. Enumerable.Range(0, 10).Select(_ => Request("GET /orders", "/orders", "200", at, fast)),
                .. Enumerable.Range(0, 2).Select(_ => Request("GET /orders", "/orders", "200", at, TimeSpan.FromSeconds(2))),
                .. Enumerable.Range(0, 3).Select(_ => Request("GET /orders", "/orders", "500", at, fast, failed: true)),
                Request("GET /orders", "/orders", "429", at, fast),
                .. Enumerable.Range(0, 2).Select(_ => Request("GET /orders/{id}", "/orders/{id}", "404", at, fast)),
                Request("GET /orders", "/orders", "499", at, fast),
                .. Enumerable.Range(0, 2).Select(_ => Request("GET /health", "/health", "200", at, fast)),
                Request("orders.created process", "", null, at, fast, Otlp.Consumer, extra: ("juice.dispatch.result", "failure")),
                Request("orders.created process", "", null, at, TimeSpan.FromMilliseconds(20), Otlp.Consumer, extra: ("juice.dispatch.result", "success")),
                .. Enumerable.Range(0, 3).Select(_ => Request("POST /checkout", "/checkout", "200", at, fast)),
                Request("POST /checkout", "/checkout", "503", at, fast, failed: true),
                // Older instrumentation's status attribute: still a 429.
                Request("GET /orders", "/orders", null, at, fast, extra: ("http.status_code", "429")),
                // Starts like a health check, but isn't one.
                Request("GET /healthcare/claims", "/healthcare/claims", "200", at, fast),
            ];
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.namespace", "slo-tests"), ("deployment.environment.name", "production")], spans));
            await LocalStack.WaitForSpansAsync(service, spans.Length);

            SloDefinition Define(string key, Action<SloDefinition>? change = null)
            {
                var d = Availability(key, $"slo-tests/{service}");
                change?.Invoke(d);
                return d;
            }
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [
                    Define("availability"),
                    Define("latency", d => { d.Sli = SliKinds.Latency; d.ThresholdMs = 300; }),
                    Define("strict", d => { d.ClientErrors = ClientErrorPolicy.Bad; d.Cancellations = CancellationPolicy.Bad; }),
                    Define("checkout", d => d.Operations = ["/checkout"]),
                ], LocalStack.TenantKey, DateTimeOffset.UtcNow, Ct);
                await db.SaveChangesAsync(Ct);
                // Start just before the spans, so one pass reaches them.
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, SloCounter.Floor(at) - TimeSpan.FromMinutes(1)), Ct);
            }
            await Counter(database).PassAsync(Ct);

            await using var check = database.Context();
            var buckets = await check.SloBuckets.AsNoTracking().Join(check.Slos, b => b.SloId, s => s.Id, (b, s) => new { s.Key, b }).Where(x => x.b.Minute == SloCounter.Floor(at))
                .ToDictionaryAsync(x => x.Key, x => x.b, Ct);
            Assert.Equal(4, buckets.Count);
            var a = buckets["availability"];
            // Eligible: 10 + 2 + 3 + 2 (429, new and old attribute) + 2 (404, the client's) + 2 consumers + 4 checkout + 1 healthcare = 26.
            // Bad: 3 + 2 (429) + 1 consumer + 1 checkout = 7.
            Assert.Equal((26L, 7L, 26L, 19L), (a.Eligible, a.Bad, a.Total, a.Good));
            // Health checks and the 499 are left out, and counted as such.
            Assert.Equal((2L, 1L, 0L), (a.ExcludedHealth, a.ExcludedCancelled, a.ExcludedClientErrors));
            // Latency: the 19 that succeeded, good at 300 ms or less: all but the two that took 2 s.
            Assert.Equal((19L, 17L), (buckets["latency"].Total, buckets["latency"].Good));
            Assert.Equal(19L, buckets["latency"].Histogram.Sum());
            // Client errors and cancellations counted as bad: the 499 becomes eligible, and it and both 404s are bad.
            Assert.Equal((27L, 10L), (buckets["strict"].Eligible, buckets["strict"].Bad));
            // One operation only.
            Assert.Equal((4L, 3L), (buckets["checkout"].Total, buckets["checkout"].Good));
            // The test tenant only has what tests send, so the range's other minutes may be gaps (not stored): see
            // Minutes_without_any_telemetry_are_a_gap_not_quiet_minutes_Async.
        }

        private sealed class Ahead(TimeSpan by) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + by;
        }

        /// <summary>
        /// When the tenant's telemetry stops, the minutes after it aren't counted at all, not even as empty: they stay unknown. Here the counter's
        /// clock runs 30 minutes ahead, so the newest telemetry it can find is half an hour old.
        /// </summary>
        [Fact]
        public async Task Without_recent_telemetry_nothing_is_counted_and_the_minutes_stay_unknown_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var later = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30);
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Availability()], LocalStack.TenantKey, later, Ct);
                await db.SaveChangesAsync(Ct);
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, SloCounter.Floor(later) - TimeSpan.FromMinutes(20)), Ct);
            }
            var counter = new SloCounter(Services(database).GetRequiredService<IServiceScopeFactory>(),
                new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader })), Options.Create(new SloOptions()),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), new Ahead(TimeSpan.FromMinutes(30)), NullLogger<SloCounter>.Instance);
            await counter.PassAsync(Ct);
            await using var check = database.Context();
            Assert.Equal(0, await check.SloBuckets.CountAsync(Ct));
            Assert.Equal(SloCounter.Floor(later) - TimeSpan.FromMinutes(20), (await check.Slos.SingleAsync(Ct)).CountedThrough);
        }

        /// <summary>
        /// A minute the SLO's service sent nothing in is a gap: it isn't stored, so it reads as unknown, not as a quiet minute, even while the rest
        /// of the tenant sends. The service here is new: it sends now, and sent nothing 29 days ago, so none of that half day is stored.
        /// </summary>
        [Fact]
        public async Task Minutes_without_any_telemetry_are_a_gap_not_quiet_minutes_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"slo-gap-{_run}";
            JsonObject[] spans = [Request("GET /orders", "/orders", "200", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5))];
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.namespace", "slo-tests"), ("deployment.environment.name", "production")], spans));
            await LocalStack.WaitForSpansAsync(service, 1);
            var start = SloCounter.Floor(DateTimeOffset.UtcNow) - TimeSpan.FromDays(29);
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Availability(service: $"slo-tests/{service}")], LocalStack.TenantKey, DateTimeOffset.UtcNow, Ct);
                await db.SaveChangesAsync(Ct);
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, start), Ct);
            }
            var options = new SloOptions();
            await Counter(database, options).PassAsync(Ct);
            await using var check = database.Context();
            Assert.Equal(0, await check.SloBuckets.CountAsync(Ct));
            // The stretch was read: the counter moves on, and the gaps stay gaps.
            Assert.Equal(start + TimeSpan.FromMinutes(options.MaxMinutesPerPass), (await check.Slos.SingleAsync(Ct)).CountedThrough);
        }

        /// <summary>
        /// Versions: a changed definition is the next version from its effective date, never one in the past; measuring something else needs a new key;
        /// a key the file drops is retired and comes back when listed again. Each step is audited and on the timeline.
        /// </summary>
        [Fact]
        public async Task A_changed_definition_is_a_new_version_from_its_effective_date_and_audited_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var now = DateTimeOffset.UtcNow;
            async Task<int> LoadAsync(DateTimeOffset at, params SloDefinition[] definitions)
            {
                await using var db = database.Context();
                var changes = await SloProvisioner.ProvisionAsync(db, definitions, LocalStack.TenantKey, at, Ct);
                await db.SaveChangesAsync(Ct);
                return changes;
            }
            Assert.Equal(1, await LoadAsync(now, Availability()));
            Assert.Equal(0, await LoadAsync(now + TimeSpan.FromMinutes(1), Availability()));   // the same file again: nothing new

            var stricter = Availability();
            stricter.Target = 99.9;
            stricter.EffectiveFrom = now + TimeSpan.FromDays(1);
            Assert.Equal(1, await LoadAsync(now + TimeSpan.FromMinutes(2), stricter));

            var backdated = Availability();
            backdated.Target = 99.0;
            backdated.EffectiveFrom = now - TimeSpan.FromDays(1);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(now + TimeSpan.FromMinutes(3), backdated));
            Assert.Contains("can't take effect in the past", refused.Message);

            var other = Availability();
            other.Sli = SliKinds.Latency;
            other.ThresholdMs = 300;
            Assert.Contains("new Key", (await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(now + TimeSpan.FromMinutes(4), other))).Message);

            Assert.Equal(1, await LoadAsync(now + TimeSpan.FromMinutes(5)));             // dropped from the file: retired
            Assert.Equal(1, await LoadAsync(now + TimeSpan.FromMinutes(6), stricter));   // listed again: restored, no new version

            await using var check = database.Context();
            var slo = await check.Slos.SingleAsync(Ct);
            Assert.Equal((2, false), (slo.CurrentVersion, slo.Retired));
            var versions = await check.SloVersions.Where(v => v.SloId == slo.Id).OrderBy(v => v.Version).ToListAsync(Ct);
            Assert.Equal([99.5, 99.9], versions.Select(v => v.Target));
            Assert.Equal(now + TimeSpan.FromDays(1), versions[1].EffectiveFrom, TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, SloCounter.VersionAt(versions, now + TimeSpan.FromHours(23)).Version);
            Assert.Equal(2, SloCounter.VersionAt(versions, now + TimeSpan.FromDays(2)).Version);
            var audit = await check.AuditRecords.Where(r => r.TargetType == "slo").OrderBy(r => r.OccurredAt).Select(r => r.Operation).ToListAsync(Ct);
            Assert.Equal(["slo.create", "slo.version", "slo.retire", "slo.restore"], audit);
            Assert.Equal(4, await check.ChangeEvents.CountAsync(e => e.Summary.StartsWith("SLO "), Ct));
        }

        /// <summary>
        /// A version that moves the SLO to another service next week changes nothing until then: the SLO is watched and counted through the
        /// service in effect now, though the new one sends nothing yet.
        /// </summary>
        [Fact]
        public async Task A_pending_move_to_another_service_doesnt_stop_counting_the_current_one_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"slo-move-{_run}";
            var at = SloCounter.Floor(DateTimeOffset.UtcNow) - TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5);
            JsonObject[] spans = [.. Enumerable.Range(0, 3).Select(_ => Request("GET /orders", "/orders", "200", at, TimeSpan.FromMilliseconds(5)))];
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.namespace", "slo-tests"), ("deployment.environment.name", "production")], spans));
            await LocalStack.WaitForSpansAsync(service, spans.Length);
            var now = DateTimeOffset.UtcNow;
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Availability(service: $"slo-tests/{service}")], LocalStack.TenantKey, now, Ct);
                await db.SaveChangesAsync(Ct);
            }
            await using (var db = database.Context())
            {
                var moved = Availability(service: $"slo-tests/{service}-v2");
                moved.EffectiveFrom = now + TimeSpan.FromDays(7);
                await SloProvisioner.ProvisionAsync(db, [moved], LocalStack.TenantKey, now + TimeSpan.FromSeconds(1), Ct);
                await db.SaveChangesAsync(Ct);
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, SloCounter.Floor(at) - TimeSpan.FromMinutes(1)), Ct);
            }
            await Counter(database).PassAsync(Ct);
            await using var check = database.Context();
            var bucket = await check.SloBuckets.SingleAsync(b => b.Minute == SloCounter.Floor(at), Ct);
            Assert.Equal((3L, 3L, 1), (bucket.Total, bucket.Good, bucket.Version));
        }

        /// <summary>
        /// Just after an SLO moves to a service that hasn't sent anything yet, the minutes before the move are still counted, through the service
        /// it moved from: they're in the recount window, and their late telemetry would otherwise be lost.
        /// </summary>
        [Fact]
        public async Task Minutes_before_a_move_are_still_counted_while_the_new_service_is_silent_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"slo-moved-{_run}";
            var at = SloCounter.Floor(DateTimeOffset.UtcNow) - TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(5);
            JsonObject[] spans = [.. Enumerable.Range(0, 2).Select(_ => Request("GET /orders", "/orders", "200", at, TimeSpan.FromMilliseconds(5)))];
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.namespace", "slo-tests"), ("deployment.environment.name", "production")], spans));
            await LocalStack.WaitForSpansAsync(service, spans.Length);
            var now = DateTimeOffset.UtcNow;
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Availability(service: $"slo-tests/{service}")], LocalStack.TenantKey, now - TimeSpan.FromHours(1), Ct);
                await db.SaveChangesAsync(Ct);
            }
            await using (var db = database.Context())
            {
                // Moved two minutes ago, after the spans, to a service that sends nothing.
                await SloProvisioner.ProvisionAsync(db, [Availability(service: $"slo-tests/{service}-new")], LocalStack.TenantKey, at + TimeSpan.FromMinutes(1), Ct);
                await db.SaveChangesAsync(Ct);
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, SloCounter.Floor(at) - TimeSpan.FromMinutes(1)), Ct);
            }
            await Counter(database).PassAsync(Ct);
            await using var check = database.Context();
            var bucket = await check.SloBuckets.SingleAsync(b => b.Minute == SloCounter.Floor(at), Ct);
            Assert.Equal((2L, 1), (bucket.Total, bucket.Version));
        }

        /// <summary>One SLO whose stored definition can't be read doesn't stop the others from being counted.</summary>
        [Fact]
        public async Task An_unreadable_slo_doesnt_stop_the_others_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var service = $"slo-broken-{_run}";
            var at = SloCounter.Floor(DateTimeOffset.UtcNow) - TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5);
            JsonObject[] spans = [Request("GET /orders", "/orders", "200", at, TimeSpan.FromMilliseconds(5))];
            await LocalStack.SendAsync("traces", Otlp.Traces([("service.name", service), ("service.namespace", "slo-tests"), ("deployment.environment.name", "production")], spans));
            await LocalStack.WaitForSpansAsync(service, spans.Length);
            await using (var db = database.Context())
            {
                await SloProvisioner.ProvisionAsync(db, [Availability("a-broken", $"slo-tests/{service}"), Availability("b-healthy", $"slo-tests/{service}")],
                    LocalStack.TenantKey, DateTimeOffset.UtcNow, Ct);
                await db.SaveChangesAsync(Ct);
                await db.Slos.ExecuteUpdateAsync(s => s.SetProperty(x => x.CountedThrough, SloCounter.Floor(at) - TimeSpan.FromMinutes(1)), Ct);
                var broken = await db.Slos.SingleAsync(s => s.Key == "a-broken", Ct);
                await db.SloVersions.Where(v => v.SloId == broken.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Definition, """{"Sli":7}"""), Ct);
            }
            await Counter(database).PassAsync(Ct);
            await using var check = database.Context();
            var healthy = await check.Slos.SingleAsync(s => s.Key == "b-healthy", Ct);
            Assert.True(await check.SloBuckets.AnyAsync(b => b.SloId == healthy.Id && b.Minute == SloCounter.Floor(at), Ct));
        }

        /// <summary>
        /// A version waiting for its date can be withdrawn: putting the file back before then is a new version from now, and the pending one never
        /// applies. The API doesn't start refusing, and the audit says what was withdrawn.
        /// </summary>
        [Fact]
        public async Task A_pending_version_is_withdrawn_when_the_file_is_put_back_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var now = DateTimeOffset.UtcNow;
            async Task LoadAsync(DateTimeOffset at, SloDefinition definition)
            {
                await using var db = database.Context();
                await SloProvisioner.ProvisionAsync(db, [definition], LocalStack.TenantKey, at, Ct);
                await db.SaveChangesAsync(Ct);
            }
            await LoadAsync(now, Availability());
            var stricter = Availability();
            stricter.Target = 99.9;
            stricter.EffectiveFrom = now + TimeSpan.FromDays(20);
            await LoadAsync(now + TimeSpan.FromMinutes(1), stricter);
            await LoadAsync(now + TimeSpan.FromMinutes(2), Availability());   // put back while version 2 waits

            // Stricter again, its date written in UTC+7: stored as the same moment in UTC, and loading the same file again changes nothing.
            SloDefinition Stricter(TimeSpan inDays)
            {
                var d = Availability();
                d.Target = 99.9;
                d.EffectiveFrom = (now + inDays).ToOffset(TimeSpan.FromHours(7));
                return d;
            }
            await LoadAsync(now + TimeSpan.FromMinutes(3), Stricter(TimeSpan.FromDays(20)));                 // version 4
            await LoadAsync(now + TimeSpan.FromMinutes(4), Stricter(TimeSpan.FromDays(20)));                 // nothing
            // Only the date moves, later then earlier: each is a new version, withdrawing the one waiting.
            await LoadAsync(now + TimeSpan.FromMinutes(5), Stricter(TimeSpan.FromDays(25)));                 // version 5
            await LoadAsync(now + TimeSpan.FromMinutes(6), Stricter(TimeSpan.FromDays(10)));                 // version 6

            await using var check = database.Context();
            var versions = await check.SloVersions.OrderBy(v => v.Version).ToListAsync(Ct);
            Assert.Equal([99.5, 99.9, 99.5, 99.9, 99.9, 99.9], versions.Select(v => v.Target));
            Assert.Equal([false, true, false, true, true, false], versions.Select(SloCounter.Withdrawn));
            Assert.Equal(TimeSpan.Zero, versions[3].EffectiveFrom.Offset);
            Assert.Equal(now + TimeSpan.FromDays(20), versions[3].EffectiveFrom, TimeSpan.FromMilliseconds(1));
            Assert.Equal(3, SloCounter.VersionAt(versions, now + TimeSpan.FromDays(9)).Version);
            Assert.Equal(6, SloCounter.VersionAt(versions, now + TimeSpan.FromDays(11)).Version);
            Assert.Equal(6, SloCounter.VersionAt(versions, now + TimeSpan.FromDays(30)).Version);
            var audit = await check.AuditRecords.Where(r => r.Operation == "slo.version").OrderBy(r => r.OccurredAt).ToListAsync(Ct);
            Assert.Equal(["2", "4", "5"], audit.Where(r => r.Metadata.ContainsKey("withdraws")).Select(r => r.Metadata["withdraws"]));
        }

        /// <summary>
        /// The API, read-only: SLOs by category, a detail with versions and days, and an SLO with a gap in its 30 days reads Unknown, with no
        /// budget figures: the minutes that weren't read aren't taken as good.
        /// </summary>
        [Fact]
        public async Task The_api_shows_slos_by_category_and_an_uncounted_window_as_unknown_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = new Dictionary<string, string>
            {
                ["Slos:Enabled"] = "false",
                ["Slos:Definitions:0:Key"] = "orders-availability", ["Slos:Definitions:0:Name"] = "Orders availability", ["Slos:Definitions:0:Sli"] = "Availability",
                ["Slos:Definitions:0:Service"] = "shop/orders", ["Slos:Definitions:0:Environment"] = "production", ["Slos:Definitions:0:Target"] = "99.5",
                ["Slos:Definitions:1:Key"] = "orders-latency", ["Slos:Definitions:1:Name"] = "Orders latency", ["Slos:Definitions:1:Sli"] = "Latency",
                ["Slos:Definitions:1:Service"] = "shop/orders", ["Slos:Definitions:1:Environment"] = "production", ["Slos:Definitions:1:Target"] = "99",
                ["Slos:Definitions:1:ThresholdMs"] = "300",
            };
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);

            // Telemetry since two days ago, but only its first hour and the last hour counted, all good: the gap between makes the budget unknown,
            // not perfect.
            await using (var db = database.Context())
            {
                var slo = await db.Slos.SingleAsync(s => s.Key == "orders-availability", Ct);
                var end = SloCounter.Floor(DateTimeOffset.UtcNow) - SloReader.Settle;
                foreach (var from in new[] { end - TimeSpan.FromDays(2), end - TimeSpan.FromHours(1) })
                {
                    for (var minute = from; minute < from + TimeSpan.FromHours(1); minute += TimeSpan.FromMinutes(1))
                    {
                        db.SloBuckets.Add(new SloBucket { TenantId = slo.TenantId, SloId = slo.Id, Minute = minute, Version = 1, Total = 100, Good = 100, Eligible = 100, CountedAt = minute });
                    }
                }
                await db.SaveChangesAsync(Ct);
            }

            var list = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos?category=Technical"))).GetProperty("items");
            Assert.Equal(["orders-availability", "orders-latency"], list.EnumerateArray().Select(s => s.GetProperty("key").GetString()));
            Assert.Equal(0, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos?category=Business"))).GetProperty("items").GetArrayLength());
            Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos?category=Other")).StatusCode);

            var detail = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos/orders-availability"));
            var summary = detail.GetProperty("slo");
            Assert.Equal(("Unknown", JsonValueKind.Null, JsonValueKind.Null), (summary.GetProperty("state").GetString(), summary.GetProperty("budgetUsed").ValueKind,
                summary.GetProperty("attainment").ValueKind));
            Assert.Equal(12000, summary.GetProperty("total").GetInt64());
            // The last hour is fully counted: its burn rate is known, and zero.
            var hour = summary.GetProperty("burn").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "1h");
            Assert.Equal(("Known", 0.0), (hour.GetProperty("state").GetString(), hour.GetProperty("burnRate").GetDouble()));
            Assert.Equal("Unknown", summary.GetProperty("burn").EnumerateArray().Single(w => w.GetProperty("name").GetString() == "3d").GetProperty("state").GetString());
            Assert.Equal("Effective", Assert.Single(detail.GetProperty("versions").EnumerateArray()).GetProperty("status").GetString());
            Assert.Equal(SloDefinition.DefaultExclusions, detail.GetProperty("definition").GetProperty("excludeOperations").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(12000, detail.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("total").GetInt64()));
            // Days are UTC days and say so, or a browser ahead of UTC shows each a day early.
            Assert.All(detail.GetProperty("days").EnumerateArray(), d => Assert.EndsWith("T00:00:00Z", d.GetProperty("day").GetString()));
            // Each day says how many minutes it should have, and none expects more than a day's. The day between the two counted hours, with
            // nothing counted, is listed as 0 of 1440, not left out.
            var days = detail.GetProperty("days").EnumerateArray().ToList();
            Assert.All(days, d => Assert.InRange(d.GetProperty("expectedMinutes").GetInt64(), 0, 1440));
            Assert.Contains(days, d => d.GetProperty("minutes").GetInt64() == 0 && d.GetProperty("expectedMinutes").GetInt64() == 1440);
            Assert.Equal(days.Count, days.Select(d => d.GetProperty("day").GetString()).Distinct().Count());
            // The allowance is the API's: 0.5% of the 12,000 requests.
            Assert.Equal(60, summary.GetProperty("budgetAllowed").GetDouble(), 6);
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/tenants/{tenant}/slos/nope")).StatusCode);
        }
    }
}
