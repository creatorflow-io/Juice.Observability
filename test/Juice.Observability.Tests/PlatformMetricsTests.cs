using System.Diagnostics.Metrics;
using System.Net.Security;
using Juice.Observability.Api;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 11 milestone 1: what the platform measures about itself, and what it lets out (docs/platform-metrics.md).</summary>
    public class PlatformMetricsTests
    {
        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        private static readonly IOptions<PlatformOptions> Tenant = Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey });

        /// <summary>The platform's meters through the exporter's own pipeline (allowlist and buckets included), read back in memory.</summary>
        private static (MeterProvider Provider, List<MetricSnapshot> Exported) Provider()
        {
            var exported = new List<MetricSnapshot>();
            var provider = Sdk.CreateMeterProviderBuilder().AddJuiceObservabilityMeters().AddInMemoryExporter(exported).Build()!;
            return (provider, exported);
        }

        /// <summary>Exports now, and returns what this export holds (copies: the provider exports again when disposed).</summary>
        private static List<MetricSnapshot> Flush(MeterProvider provider, List<MetricSnapshot> exported)
        {
            exported.Clear();
            provider.ForceFlush();
            return [.. exported];
        }

        private static IEnumerable<(MetricSnapshot Metric, MetricPoint Point)> Points(IEnumerable<MetricSnapshot> metrics, string name)
        {
            foreach (var metric in metrics.Where(m => m.Name == name))
            {
                foreach (var point in metric.MetricPoints)
                {
                    yield return (metric, point);
                }
            }
        }

        private static Dictionary<string, object?> Tags(MetricPoint point)
        {
            var tags = new Dictionary<string, object?>();
            foreach (var tag in point.Tags)
            {
                tags[tag.Key] = tag.Value;
            }
            return tags;
        }

        /// <summary>
        /// R3: only allowlisted attribute keys leave the process, on the platform's own instruments and on the standard ones: a webhook receiver's
        /// host (<c>server.address</c>), a full URL, a pool named from its connection string, or any key a framework adds, is dropped.
        /// </summary>
        [Fact]
        public void Only_allowlisted_attribute_keys_are_exported()
        {
            var (provider, exported) = Provider();
            using (provider)
            {
                using var http = new Meter("System.Net.Http");
                var requests = http.CreateHistogram<double>($"test.http.client.request.duration.{_run}", "s");
                requests.Record(0.2, new("http.request.method", "POST"), new("http.response.status_code", 200),
                    new("server.address", "receiver.customer.internal"), new("url.full", "https://receiver.customer.internal/hook?token=x"));
                using var npgsql = new Meter("Npgsql");
                npgsql.CreateCounter<long>($"test.db.client.connection.count.{_run}").Add(1,
                    new("db.client.connection.state", "idle"), new("db.client.connection.pool.name", "Host=postgres;Username=juice"));
                using var own = new Meter(WorkerPulse.MeterName);
                own.CreateCounter<long>($"juice.observability.test.{_run}").Add(1, new("worker", "discovery"), new("juice.platform_tenant_id", "local-dev"));
                exported = Flush(provider, exported);
            }

            var keys = exported.SelectMany(m => Points([m], m.Name)).SelectMany(p => Tags(p.Point).Keys).ToHashSet();
            Assert.Contains("http.request.method", keys);
            Assert.Contains("db.client.connection.state", keys);
            Assert.Contains("worker", keys);
            Assert.All(keys, key => Assert.Contains(key, PlatformTelemetry.AllowedKeys));
            Assert.DoesNotContain("server.address", keys);
            Assert.DoesNotContain("url.full", keys);
            Assert.DoesNotContain("db.client.connection.pool.name", keys);
            Assert.DoesNotContain("juice.platform_tenant_id", keys);
        }

        /// <summary>R4: a worker's passes, by outcome and duration, the pass in progress, the time since the last one, and the lease it holds.</summary>
        [Fact]
        public void A_worker_reports_its_passes_and_its_lease()
        {
            var worker = $"test-{_run}";
            var (provider, exported) = Provider();
            using (provider)
            {
                WorkerPulse.Lease(worker, true);
                using (var pass = WorkerPulse.Begin(worker))
                {
                    pass.Succeeded();
                }
                using (WorkerPulse.Begin(worker))
                {
                    // disposed without Succeeded: a failed pass
                }
                var running = WorkerPulse.Begin(worker);
                Thread.Sleep(20);
                exported = Flush(provider, exported);
                running.Succeeded();
                running.Dispose();
            }

            bool Mine(MetricPoint p) => Equals(Tags(p)["worker"], worker);
            var passes = Points(exported, "juice.observability.worker.passes").Where(p => Mine(p.Point))
                .ToDictionary(p => (string)Tags(p.Point)["outcome"]!, p => p.Point.GetSumLong());
            Assert.Equal(1, passes["success"]);
            Assert.Equal(1, passes["failure"]);
            var duration = Assert.Single(Points(exported, "juice.observability.worker.pass.duration"), p => Mine(p.Point));
            Assert.Equal(2, duration.Point.GetHistogramCount());
            var buckets = 0;
            foreach (var _ in duration.Point.GetHistogramBuckets())
            {
                buckets++;
            }
            Assert.Equal(PlatformTelemetry.SecondsBuckets.Length + 1, buckets);
            Assert.True(Assert.Single(Points(exported, "juice.observability.worker.pass.age"), p => Mine(p.Point)).Point.GetGaugeLastValueDouble() > 0,
                "a pass in progress shows its age");
            Assert.True(Assert.Single(Points(exported, "juice.observability.worker.since_last_pass"), p => Mine(p.Point)).Point.GetGaugeLastValueDouble() >= 0);
            Assert.Equal(1, Assert.Single(Points(exported, "juice.observability.worker.lease"), p => Mine(p.Point)).Point.GetGaugeLastValueDouble());
        }

        /// <summary>A replica that doesn't hold the lease reports 0 for it, and no time since a last pass (a standby has none to report).</summary>
        [Fact]
        public void A_replica_without_the_lease_reports_none()
        {
            var worker = $"standby-{_run}";
            var (provider, exported) = Provider();
            using (provider)
            {
                WorkerPulse.Lease(worker, false);
                exported = Flush(provider, exported);
            }
            bool Mine(MetricPoint p) => Equals(Tags(p)["worker"], worker);
            Assert.Equal(0, Assert.Single(Points(exported, "juice.observability.worker.lease"), p => Mine(p.Point)).Point.GetGaugeLastValueDouble());
            Assert.DoesNotContain(Points(exported, "juice.observability.worker.since_last_pass"), p => Mine(p.Point));
        }

        /// <summary>R4: an API endpoint names itself by its route template below the tenant, never by a request's values.</summary>
        [Theory]
        [InlineData("/api/v1/tenants/{tenantId:guid}/telemetry/traces", "api:/telemetry/traces")]
        [InlineData("/api/v1/tenants/{tenantId:guid}/telemetry/traces/{traceId}", "api:/telemetry/traces/{traceId}")]
        [InlineData("/api/v1/tenants", "api:/tenants")]
        [InlineData(null, "api:?")]
        public void An_endpoint_is_named_by_its_route_template(string? pattern, string caller)
            => Assert.Equal(caller, InventoryEndpoints.CallerOf(pattern));

        [Theory]
        [InlineData("otel-collector:4319")]
        [InlineData("otel-collector/v1/metrics")]
        [InlineData("ftp://collector.example")]
        [InlineData("")]
        [InlineData(null)]
        public void An_endpoint_that_isnt_an_http_url_is_refused(string? endpoint) => Assert.Null(PlatformTelemetry.MetricsEndpoint(endpoint));

        /// <summary>A pass stopped by the host is canceled, not a failure; one with part of its work failed is partial.</summary>
        [Fact]
        public void A_pass_stopped_by_the_host_is_canceled_and_one_partly_failed_is_partial()
        {
            var worker = $"outcomes-{_run}";
            var (provider, exported) = Provider();
            using (provider)
            {
                using var stopping = new CancellationTokenSource();
                using (var pass = WorkerPulse.Begin(worker, stopping.Token))
                {
                    pass.Partial();
                }
                using (WorkerPulse.Begin(worker, stopping.Token))
                {
                    stopping.Cancel();
                }
                exported = Flush(provider, exported);
            }
            var outcomes = Points(exported, "juice.observability.worker.passes").Where(p => Equals(Tags(p.Point)["worker"], worker))
                .Select(p => (string)Tags(p.Point)["outcome"]!).Order().ToList();
            Assert.Equal(["canceled", "partial"], outcomes);
        }

        /// <summary>A lease taken back doesn't report the time since a pass from its earlier hold: that would read as a stopped worker.</summary>
        [Fact]
        public void A_lease_taken_back_reports_no_time_since_an_earlier_holds_pass()
        {
            var worker = $"flap-{_run}";
            WorkerPulse.Lease(worker, true);
            using (var pass = WorkerPulse.Begin(worker))
            {
                pass.Succeeded();
            }
            WorkerPulse.Lease(worker, false);
            WorkerPulse.Lease(worker, true);
            var (provider, exported) = Provider();
            using (provider)
            {
                exported = Flush(provider, exported);
            }
            Assert.DoesNotContain(Points(exported, "juice.observability.worker.since_last_pass"), p => Equals(Tags(p.Point)["worker"], worker));
            Assert.Equal(1, Assert.Single(Points(exported, "juice.observability.worker.lease"), p => Equals(Tags(p.Point)["worker"], worker)).Point.GetGaugeLastValueDouble());
        }

        /// <summary>A Telemetry value that doesn't convert stops the export, not the platform: registering it doesn't throw.</summary>
        [Theory]
        [InlineData("Telemetry:ExportInterval", "15s")]
        [InlineData("Telemetry:Enabled", "yes")]
        [InlineData("Telemetry:ExportInterval", "00:00:00.100")]
        public void A_telemetry_setting_that_doesnt_convert_doesnt_stop_the_platform(string key, string value)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = value,
                ["Telemetry:Otlp:Endpoint"] = "https://otel-collector:4319",
            }).Build();
            var services = new ServiceCollection();
            services.AddPlatformTelemetry(configuration);
            Assert.DoesNotContain(services, d => d.ImplementationFactory?.Method.ReturnType.Name.Contains("PlatformMetricsExport") == true
                || d.ImplementationInstance?.GetType().Name == "PlatformMetricsExport");
        }

        /// <summary>A worker that runs in every replica reports its time since the last pass without a lease, and no lease at all.</summary>
        [Fact]
        public void A_worker_without_a_lease_reports_its_last_pass_and_no_lease()
        {
            var worker = $"unleased-{_run}";
            var (provider, exported) = Provider();
            using var stopping = new CancellationTokenSource();
            using (provider)
            {
                WorkerPulse.Register(worker, TimeSpan.FromSeconds(2), leased: false, stopping: stopping.Token);
                try
                {
                    using (var pass = WorkerPulse.Begin(worker))
                    {
                        pass.Succeeded();
                    }
                    exported = Flush(provider, exported);
                }
                finally
                {
                    stopping.Cancel();
                }
            }
            bool Mine(MetricPoint p) => Equals(Tags(p)["worker"], worker);
            Assert.Single(Points(exported, "juice.observability.worker.since_last_pass"), p => Mine(p.Point));
            Assert.DoesNotContain(Points(exported, "juice.observability.worker.lease"), p => Mine(p.Point));
        }

        /// <summary>
        /// R4 end to end: an API request's ClickHouse query is tagged with the endpoint's route template, in the platform's metric and in ClickHouse's
        /// query log.
        /// </summary>
        [Fact]
        public async Task An_api_request_tags_its_clickhouse_query_with_its_route_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (provider, exported) = Provider();
            var started = DateTimeOffset.UtcNow.AddSeconds(-2);
            using (provider)
            {
                await using var factory = new ApiTests.ApiFactory(database);
                using var client = factory.CreateClient();
                var token = await LocalStack.TokenAsync("victor");
                var tenant = (await ApiTests.JsonAsync(await ApiTests.Get(client, "/api/v1/tenants", token))).GetProperty("items")[0].GetProperty("id").GetGuid();
                await ApiTests.JsonAsync(await ApiTests.Get(client, $"/api/v1/tenants/{tenant}/telemetry/logs?limit=1", token));
                exported = Flush(provider, exported);
            }
            Assert.Contains(Points(exported, "juice.observability.clickhouse.query.duration"), p => Equals(Tags(p.Point)["caller"], "api:/telemetry/logs"));
            await LocalStack.ClickHouseAsync("SYSTEM FLUSH LOGS");
            Assert.NotEqual("0", await LocalStack.ClickHouseAsync(
                $"SELECT count() FROM system.query_log WHERE log_comment = 'juice:api:/telemetry/logs' AND type = 'QueryFinish' AND event_time >= toDateTime({started.ToUnixTimeSeconds()})"));
        }

        [Theory]
        [InlineData("https://otel-collector:4319", "https://otel-collector:4319/v1/metrics")]
        [InlineData("https://otel-collector:4319/", "https://otel-collector:4319/v1/metrics")]
        [InlineData("https://collector.example/v1/metrics", "https://collector.example/v1/metrics")]
        public void The_exporter_posts_to_the_metrics_path(string endpoint, string expected)
            => Assert.Equal(new Uri(expected), PlatformTelemetry.MetricsEndpoint(endpoint));

        [Fact]
        public void A_missing_or_empty_token_file_is_reported_not_thrown()
        {
            Assert.Null(PlatformTelemetry.ReadToken(null, out var none));
            Assert.Null(none);
            Assert.Contains("can't be read", PlatformTelemetry.ReadToken(Path.Combine(Path.GetTempPath(), $"missing-{_run}"), out _));
            var empty = Path.Combine(Path.GetTempPath(), $"empty-{_run}");
            File.WriteAllText(empty, "\n");
            try
            {
                Assert.Contains("is empty", PlatformTelemetry.ReadToken(empty, out _));
                File.WriteAllText(empty, " secret-token \nignored\n");
                Assert.Null(PlatformTelemetry.ReadToken(empty, out var token));
                Assert.Equal("secret-token", token);
            }
            finally
            {
                File.Delete(empty);
            }
        }

        /// <summary>The CA file forgives only a chain the system can't build: a certificate for another name, or none, still fails.</summary>
        [Fact]
        public void The_ca_file_forgives_only_an_untrusted_chain()
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            using var ca = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test ca", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Assert.True(PlatformTelemetry.Trusted(ca, ca, SslPolicyErrors.None));
            Assert.False(PlatformTelemetry.Trusted(ca, null, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.False(PlatformTelemetry.Trusted(ca, ca, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(PlatformTelemetry.Trusted(ca, ca, SslPolicyErrors.RemoteCertificateChainErrors), "a certificate that chains to the CA");
        }

        /// <summary>
        /// R4: a platform query to ClickHouse is timed and sized under its caller, and ClickHouse logs it with <c>log_comment = juice:&lt;caller&gt;</c>,
        /// the cross-check the summary header's statistics are checked against.
        /// </summary>
        [Fact]
        public async Task A_clickhouse_query_is_timed_and_tagged_by_its_caller_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            var caller = $"test-{_run}";
            var source = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));
            var (provider, exported) = Provider();
            using (provider)
            {
                await Task.Run(async () =>
                {
                    PlatformCaller.Set(caller);
                    var now = DateTimeOffset.UtcNow;
                    var rows = await source.QueryAsync("SELECT count() FROM otel.otel_traces WHERE " + ClickHouseTelemetrySource.Tenant +
                        $" AND Timestamp >= {ClickHouseTelemetrySource.From} AND Timestamp < {ClickHouseTelemetrySource.To} LIMIT {ClickHouseTelemetrySource.Limit}",
                        new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 10), r => Convert.ToInt64(r.GetValue(0)), ct);
                    Assert.Single(rows);
                    await Assert.ThrowsAnyAsync<Exception>(() => source.QueryAsync("SELECT no_such_column FROM otel.otel_traces",
                        new TelemetryWindow(LocalStack.TenantKey, now.AddMinutes(-5), now, 10), r => 0, ct));
                }, ct);
                exported = Flush(provider, exported);
            }

            bool Mine(MetricPoint p) => Equals(Tags(p)["caller"], caller);
            var outcomes = Points(exported, "juice.observability.clickhouse.query.duration").Where(p => Mine(p.Point))
                .ToDictionary(p => (string)Tags(p.Point)["outcome"]!, p => p.Point.GetHistogramCount());
            Assert.Equal(1, outcomes["success"]);
            Assert.Equal(1, outcomes["failure"]);
            Assert.Single(Points(exported, "juice.observability.clickhouse.query.read_rows"), p => Mine(p.Point));

            await LocalStack.ClickHouseAsync("SYSTEM FLUSH LOGS");
            Assert.Equal("2", await LocalStack.ClickHouseAsync(
                $"SELECT count() FROM system.query_log WHERE log_comment = '{ClickHouseTelemetrySource.LogComment(caller)}' AND type != 'QueryStart' AND event_date >= today() - 1"));
        }

        /// <summary>
        /// R4, R5: the sampler reads the platform database and, through <c>platform_monitor</c>, ClickHouse; a second sample has ClickHouse's CPU,
        /// which is a rate between two samples.
        /// </summary>
        [Fact]
        public async Task The_sampler_reads_postgres_and_clickhouse_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ClickHouseOptions { MonitorConnectionString = LocalStack.ClickHouseMonitor }), Tenant, TimeProvider.System, NullLogger<PlatformSampler>.Instance);

            var first = await sampler.SampleOnceAsync(ct);
            Assert.True(first.Postgres.Up);
            Assert.True(first.Postgres.Size > 0);
            Assert.Equal(0, first.Postgres.OutboxPending);
            Assert.Null(first.Postgres.OutboxOldest);
            Assert.Equal(0, first.Postgres.WebhookPending);
            var clickHouse = Assert.IsType<PlatformSampler.ClickHouseSample>(first.ClickHouse);
            Assert.True(clickHouse.Up);
            Assert.True(clickHouse.Memory > 0);
            Assert.NotEmpty(clickHouse.Disks);
            Assert.Contains(clickHouse.Tables, t => t.Name == "otel_traces");
            Assert.Contains("platform_monitor", clickHouse.Running.Keys);
            Assert.All(clickHouse.Running.Keys.Concat(clickHouse.Finished.Select(f => f.User)), user => Assert.True(
                PlatformSampler.KnownUsers.Contains(user) || user == "other", user));

            await Task.Delay(1000, ct);
            var second = await sampler.SampleOnceAsync(ct);
            Assert.True(second.ClickHouse!.Cores is >= 0, "CPU is a rate over the time since the previous sample");
        }

        /// <summary>A ClickHouse that doesn't answer is down in the sample, and the platform database is still sampled.</summary>
        [Fact]
        public async Task An_unreachable_clickhouse_is_down_not_an_error_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ClickHouseOptions { MonitorConnectionString = "Host=127.0.0.1;Port=1;Username=nobody;Password=x" }), Tenant, TimeProvider.System,
                NullLogger<PlatformSampler>.Instance);
            var sample = await sampler.SampleOnceAsync(ct);
            Assert.True(sample.Postgres.Up);
            Assert.False(sample.ClickHouse!.Up);
        }

/// <summary>Every instrument the platform declares under its meter, once each type that declares one has been initialised.</summary>
        private static List<Instrument> PlatformInstruments()
        {
            foreach (var assembly in new[] { typeof(WorkerPulse), typeof(ClickHouseTelemetrySource), typeof(PlatformTelemetry), typeof(Health.HealthRunner),
                typeof(Rules.RuleScheduler), typeof(Slos.SloCounter), typeof(Webhooks.WebhookDispatcher) }.Select(t => t.Assembly).Distinct())
            {
                foreach (var type in assembly.GetTypes().Where(t => !t.ContainsGenericParameters && t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Any(field => typeof(Meter).IsAssignableFrom(field.FieldType) || typeof(Instrument).IsAssignableFrom(field.FieldType))))
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                }
            }
            var found = new List<Instrument>();
            using var listener = new MeterListener { InstrumentPublished = (instrument, _) => { if (instrument.Meter.Name == WorkerPulse.MeterName) found.Add(instrument); } };
            listener.Start();
            return [.. found.DistinctBy(i => i.Name)];
        }

        /// <summary>
        /// Gate 1: every instrument the platform declares is listed in docs/platform-metrics.md, and goes through the exporter's pipeline when it
        /// records: each counter and histogram is driven here (the event-driven ones too: transitions, escalations, attempts...), each
        /// observable one is read.
        /// </summary>
        [Fact]
        public void Every_platform_instrument_is_documented_and_exported()
        {
            var instruments = PlatformInstruments();
            Assert.True(instruments.Count >= 40, $"only {instruments.Count} instruments found");
            var doc = File.ReadAllText(Path.Combine(Repository(), "docs", "platform-metrics.md"));
            Assert.All(instruments, i => Assert.True(doc.Contains($"`{i.Name}`"), $"{i.Name} isn't in docs/platform-metrics.md"));

            var (provider, exported) = Provider();
            using (provider)
            {
                var tag = new KeyValuePair<string, object?>("outcome", "success");
                foreach (var instrument in instruments)
                {
                    switch (instrument)
                    {
                        case Counter<long> c: c.Add(1, tag); break;
                        case Histogram<double> h: h.Record(0.5, tag); break;
                        case UpDownCounter<long> u: u.Add(1, tag); break;
                    }
                }
                exported = Flush(provider, exported);
            }
            var names = exported.Select(m => m.Name).ToHashSet();
            // An observable instrument with nothing to report yet (a worker that hasn't run here) exports nothing; every other must be there.
            var missing = instruments.Where(i => !i.GetType().Name.StartsWith("Observable", StringComparison.Ordinal) && !names.Contains(i.Name)).Select(i => i.Name).ToList();
            Assert.True(missing.Count == 0, "not exported: " + string.Join(", ", missing));
        }

        private static string Repository()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docs", "platform-metrics.md")))
                {
                    return dir.FullName;
                }
            }
            throw new InvalidOperationException("repository root not found");
        }

        /// <summary>R2: the allowlist applies to the platform's meters only; a host's own instrument keeps its attributes and its defaults.</summary>
        [Fact]
        public void A_host_instrument_keeps_its_own_attributes()
        {
            using var host = new Meter($"Customer.App.{_run}");
            var exported = new List<MetricSnapshot>();
            using (var provider = Sdk.CreateMeterProviderBuilder().AddJuiceObservabilityMeters().AddMeter(host.Name).AddInMemoryExporter(exported).Build()!)
            {
                host.CreateCounter<long>("orders").Add(1, new("customer.region", "eu"), new("server.address", "shop.internal"));
                exported = Flush(provider, exported);
            }
            var keys = Tags(Assert.Single(Points(exported, "orders")).Point).Keys;
            Assert.Contains("customer.region", keys);
            Assert.Contains("server.address", keys);
            Assert.Null(PlatformTelemetry.View(host.CreateCounter<long>("other")));
        }

        [Fact]
        public void An_unreadable_ca_file_is_reported_not_thrown()
        {
            Assert.Null(PlatformTelemetry.LoadCa(null, out var none));
            Assert.Null(none);
            var bad = Path.Combine(Path.GetTempPath(), $"ca-{_run}.crt");
            File.WriteAllText(bad, "not a certificate");
            try
            {
                Assert.Contains("can't be read", PlatformTelemetry.LoadCa(bad, out _));
            }
            finally
            {
                File.Delete(bad);
            }
            Assert.Contains("can't be read", PlatformTelemetry.LoadCa(Path.Combine(Path.GetTempPath(), $"missing-{_run}.crt"), out _));
        }

        /// <summary>R1: a deployment may not take the reserved key, or its tenant would see the platform's own telemetry as its own.</summary>
        [Fact]
        public async Task The_reserved_tenant_key_is_refused_Async()
        {
            var provisioner = new TenantProvisioner(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new PlatformOptions { TenantKey = ReservedTenant.Key }), new ConfigurationBuilder().Build(), NullLogger<TenantProvisioner>.Instance);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("reserved", refused.Message);
        }

        /// <summary>
        /// A ClickHouse query cut by its caller's own deadline is a timeout (ClickHouse too slow); one stopped because the host is stopping is
        /// canceled.
        /// </summary>
        [Fact]
        public async Task A_query_past_its_deadline_is_a_timeout_and_one_stopped_is_canceled_Async()
        {
            LocalStack.RequireAvailable();
            var caller = $"deadline-{_run}";
            var source = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));
            var window = new TelemetryWindow(LocalStack.TenantKey, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 1);
            var (provider, exported) = Provider();
            using (provider)
            {
                using var stopping = new CancellationTokenSource();
                await Task.Run(async () =>
                {
                    PlatformCaller.Set(caller, stopping.Token);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
                    {
                        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.QueryAsync("SELECT sleep(2)", window, r => 0, deadline.Token));
                    }
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
                    {
                        var query = source.QueryAsync("SELECT sleep(2)", window, r => 0, deadline.Token);
                        await Task.Delay(300);
                        await stopping.CancelAsync();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
                    }
                }, TestContext.Current.CancellationToken);
                exported = Flush(provider, exported);
            }
            var outcomes = Points(exported, "juice.observability.clickhouse.query.duration").Where(p => Equals(Tags(p.Point)["caller"], caller))
                .Select(p => (string)Tags(p.Point)["outcome"]!).Order().ToList();
            Assert.Equal(["canceled", "timeout"], outcomes);
        }

        /// <summary>A store that accepts the connection and never answers is down within the sampler's timeout, and the other store is still sampled.</summary>
        [Fact]
        public async Task A_store_that_hangs_is_down_within_the_timeout_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            using var hang = new TcpListener(IPAddress.Loopback, 0);
            hang.Start();
            var port = ((IPEndPoint)hang.LocalEndpoint).Port;
            var accepted = new List<TcpClient>();
            _ = Task.Run(async () => { while (true) accepted.Add(await hang.AcceptTcpClientAsync(ct)); }, ct);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ClickHouseOptions { MonitorConnectionString = $"Host=127.0.0.1;Port={port};Username=nobody;Password=x" }), Tenant,
                TimeProvider.System, NullLogger<PlatformSampler>.Instance);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var sample = await sampler.SampleOnceAsync(ct);
            Assert.True(started.Elapsed < PlatformSampler.Timeout + TimeSpan.FromSeconds(5), $"took {started.Elapsed}");
            Assert.True(sample.Postgres.Up);
            Assert.False(sample.ClickHouse!.Up);
            accepted.ForEach(c => c.Dispose());
        }

        // (in PlatformSamplerExportTests below: alone, since the gauges read the one sampler running in the process, and a host started by another test has one)
        internal static async Task RunningSamplerExportsItsGaugesAsync()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var services = PlatformServices.Build(database);
            var sampler = new PlatformSampler(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ClickHouseOptions { MonitorConnectionString = LocalStack.ClickHouseMonitor }), Tenant, TimeProvider.System,
                NullLogger<PlatformSampler>.Instance);
            var (provider, exported) = Provider();
            using (provider)
            {
                await sampler.StartAsync(ct);
                try
                {
                    for (var i = 0; i < 100 && sampler.Latest is null; i++) await Task.Delay(100, ct);
                    exported = Flush(provider, exported);
                }
                finally
                {
                    await sampler.StopAsync(CancellationToken.None);
                }
            }
            Assert.Equal(1, Assert.Single(Points(exported, "juice.observability.postgres.up")).Point.GetGaugeLastValueDouble());
            Assert.Equal(1, Assert.Single(Points(exported, "juice.observability.clickhouse.up")).Point.GetGaugeLastValueDouble());
            Assert.Contains(Points(exported, "juice.observability.outbox.backlog"), p => Equals(Tags(p.Point)["state"], "pending"));
            Assert.NotEmpty(Points(exported, "juice.observability.clickhouse.disk.free"));
            Assert.Contains(exported, m => m.Name == "juice.observability.clickhouse.server.queries");
            Assert.Contains(Points(exported, "juice.observability.worker.passes"), p => Equals(Tags(p.Point)["worker"], "sampler"));
        }

        /// <summary>
        /// R1 on the running stack: the platform's and the Collector's own metrics are never part of the deployment's tenant as discovery, health,
        /// rules and SLOs read it (they all read through this source, filtered on the tenant).
        /// </summary>
        [Fact]
        public async Task The_platform_is_not_in_the_deployments_telemetry_Async()
        {
            LocalStack.RequireAvailable();
            var ct = TestContext.Current.CancellationToken;
            Assert.SkipUnless(await LocalStack.ClickHouseAsync(
                $"SELECT count() > 0 FROM otel.otel_metrics_gauge WHERE ResourceAttributes['juice.platform_tenant_id'] = '{ReservedTenant.Key}' AND TimeUnix > now() - INTERVAL 5 MINUTE") == "1",
                "the platform isn't exporting its metrics on this stack");
            var source = new ClickHouseTelemetrySource(Options.Create(new ClickHouseOptions { ConnectionString = LocalStack.ClickHouseReader }));
            var now = DateTimeOffset.UtcNow;
            foreach (var tenant in new[] { LocalStack.Secret("JUICE_PLATFORM_TENANT_ID"), LocalStack.TenantKey })
            {
                var resources = await source.ReadResourcesAsync(new TelemetryWindow(tenant, now.AddMinutes(-5), now, 10_000), ct);
                Assert.DoesNotContain(resources, r => r.ServiceName is PlatformTelemetry.ServiceName or "otelcol-contrib");
            }
            var platform = await source.ReadResourcesAsync(new TelemetryWindow(ReservedTenant.Key, now.AddMinutes(-5), now, 10_000), ct);
            Assert.Contains(platform, r => r.ServiceName == PlatformTelemetry.ServiceName);
        }
    }

    /// <summary>
    /// The sampler running as the host runs it: its gauges and its query counter go through the exporter, and it reports its own passes. Runs on its
    /// own: the gauges read the one sampler running in the process, and a host another test starts has one of its own.
    /// </summary>
    [CollectionDefinition(DisableParallelization = true)]
    public sealed class SamplerAlone;

    [Collection(typeof(SamplerAlone))]
    public class PlatformSamplerExportTests
    {
        [Fact]
        public Task The_running_sampler_exports_its_gauges_Async() => PlatformMetricsTests.RunningSamplerExportsItsGaugesAsync();
    }
}
