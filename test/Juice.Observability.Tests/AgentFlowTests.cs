using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Phase 7 end to end, over real TLS: the reference host with its agent listener on Kestrel, and the real agent enrolling, polling, checking and
    /// answering. The platform's half of the gate: a token works once, only the agent CA's current certificate for an enrolled, unrevoked agent gets
    /// in, revocation is immediate, the agent API and the rest never share a listener, and no agent can answer for another.
    /// </summary>
    public sealed class AgentFlowTests : IDisposable
    {
        /// <summary>Where the agents of these tests keep their keys. Tests of one class run one at a time, so each clears it after.</summary>
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "juice-agent-flow-tests");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (DirectoryNotFoundException) { }
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The reference host with agents on, on two real ports.</summary>
        private sealed class AgentHost : WebApplicationFactory<Program>
        {
            private readonly ScratchDatabase _database;
            private readonly AgentPki _pki;
            private readonly bool _viaUrls;

            /// <param name="database">The platform database for this host.</param>
            /// <param name="pki">The agent CA, command key and listener certificate.</param>
            /// <param name="viaUrls">The host's address as <c>URLS</c> (as ASPNETCORE_URLS or launchSettings set it) instead of <c>HTTP_PORTS</c>.</param>
            public AgentHost(ScratchDatabase database, AgentPki pki, bool viaUrls = false)
            {
                (_database, _pki, _viaUrls) = (database, pki, viaUrls);
                (AgentPort, HttpPort) = (AgentPki.FreePort(), AgentPki.FreePort());
                UseKestrel();
                StartServer();
            }

            public int AgentPort { get; }
            public int HttpPort { get; }
            public Uri AgentUrl => new($"https://localhost:{AgentPort}");

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(new TestLogs()));
                foreach (var (key, value) in new Dictionary<string, string>
                {
                    ["ConnectionStrings:Platform"] = _database.ConnectionString, ["Platform:TenantKey"] = LocalStack.TenantKey,
                    ["ClickHouse:ConnectionString"] = LocalStack.ClickHouseReader, ["Auth:Authority"] = LocalStack.Authority, ["Auth:RequireHttpsMetadata"] = "false",
                    ["Auth:Audience"] = "juice-platform", ["Discovery:Enabled"] = "false", ["Health:Enabled"] = "false", ["Rules:Enabled"] = "false",
                    ["Webhooks:Enabled"] = "false", ["Database:Migrate"] = "true",
                }.Concat(_pki.Settings(AgentPort, HttpPort).Where(s => !_viaUrls || s.Key != "HTTP_PORTS"))
                 .Concat(_viaUrls ? [new KeyValuePair<string, string>("URLS", $"http://localhost:{HttpPort}")] : []))
                {
                    builder.UseSetting(key, value);
                }
            }

            public HttpClient Users() => new() { BaseAddress = new Uri($"http://localhost:{HttpPort}") };
        }

        private static AgentOptions Options(AgentHost host, AgentPki pki, string data, string? token = null) => new()
        {
            PlatformUrl = host.AgentUrl.ToString(), PlatformCaFile = pki.ServerCa, DataDirectory = data, EnrollmentToken = token, PollWaitSeconds = 2,
        };

        private static LocalAllowlist Allowlist() => new()
        {
            Actions =
            {
                [AgentActions.HostSummary] = new ActionPolicy(),
                [AgentActions.TcpReachability] = new ActionPolicy { Targets = [$"localhost:*"], MaxTimeoutMs = 2000 },
            },
        };

        private static string TempDirectory() => Path.Combine(Root, Guid.NewGuid().ToString("N"));

        /// <summary>The host's warnings and errors in the test's output.</summary>
        private sealed class TestLogs : Microsoft.Extensions.Logging.ILoggerProvider
        {
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Filtered(new TestLog<object>());

            public void Dispose()
            {
            }

            private sealed class Filtered(Microsoft.Extensions.Logging.ILogger inner) : Microsoft.Extensions.Logging.ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (IsEnabled(logLevel)) inner.Log(logLevel, eventId, state, exception, formatter);
                }
            }
        }

        /// <summary>The agent's log in the test's output, so a failure says what the agent saw.</summary>
        private sealed class TestLog<T> : Microsoft.Extensions.Logging.ILogger<T>
        {
            private readonly ITestOutputHelper? _output = TestContext.Current.TestOutputHelper;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try { _output?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)} {exception}"); } catch (InvalidOperationException) { }
            }
        }

        private static async Task<string> TokenAsync(Caller admin, Guid tenant)
            => (await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/agents/enrollment-tokens", new { purpose = "test" }))).GetProperty("token").GetString()!;

        /// <summary>Enrolls the way the agent does, and returns what it keeps.</summary>
        private static async Task<(AgentStore Store, AgentIdentity Identity)> EnrollAsync(AgentHost host, AgentPki pki, string token, string? data = null)
        {
            var directory = data ?? TempDirectory();
            var (key, csr) = AgentStore.NewKey("test-host");
            using var client = new PlatformClient(Options(host, pki, directory), null);
            var response = await client.EnrollAsync(new EnrollRequest(token, csr, "test-host", "test os", "test"), Ct);
            var store = new AgentStore(directory);
            store.SaveEnrollment(key, response);
            return (store, store.LoadIdentity());
        }

        private static async Task<JsonElement> WaitForCommandAsync(Caller caller, Guid tenant, Guid agent, Guid command, params string[] states)
        {
            for (var i = 0; i < 150; i++)
            {
                var dto = await Caller.JsonAsync(await caller.GetAsync($"/api/v1/tenants/{tenant}/agents/{agent}/commands/{command}"));
                if (states.Contains(dto.GetProperty("state").GetString())) return dto;
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"command {command} never reached {string.Join("/", states)}");
        }

        private static async Task<Guid> IssueAsync(Caller caller, Guid tenant, Guid agent, string action, object parameters)
            => (await Caller.JsonAsync(await caller.PostAsync($"/api/v1/tenants/{tenant}/agents/{agent}/commands", new { action, parameters }))).GetProperty("id").GetGuid();

        [Fact]
        public async Task An_agent_enrolls_runs_what_its_allowlist_permits_refuses_the_rest_and_stops_when_revoked_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var token = await TokenAsync(admin, tenant);
            var data = TempDirectory();
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, data, token)), Microsoft.Extensions.Options.Options.Create(Allowlist()),
                TimeProvider.System, new TestLog<AgentService>());
            await agent.StartAsync(Ct);
            try
            {
                Guid agentId = default;
                for (var i = 0; i < 100 && agentId == default; i++)
                {
                    await Task.Delay(100, Ct);
                    var list = await Caller.JsonAsync(await admin.GetAsync($"/api/v1/tenants/{tenant}/agents"));
                    if (list.GetProperty("items").GetArrayLength() == 1) agentId = list.GetProperty("items")[0].GetProperty("id").GetGuid();
                }
                Assert.NotEqual(Guid.Empty, agentId);

                // Allowed by the platform and by the host: it runs.
                var summary = await WaitForCommandAsync(@operator, tenant, agentId, await IssueAsync(@operator, tenant, agentId, AgentActions.HostSummary, new { }), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", summary.GetProperty("state").GetString());
                Assert.True(summary.GetProperty("result").GetProperty("processorCount").GetInt32() > 0);

                // Valid and signed, but this host doesn't allow disk usage at all, nor that TCP target: refused, with the reason.
                var disk = await WaitForCommandAsync(@operator, tenant, agentId, await IssueAsync(@operator, tenant, agentId, AgentActions.DiskUsage, new { }), "Succeeded", "Rejected");
                Assert.Equal(("Rejected", RejectionReasons.ActionNotAllowed), (disk.GetProperty("state").GetString(), disk.GetProperty("reason").GetString()));
                var tcp = await WaitForCommandAsync(@operator, tenant, agentId,
                    await IssueAsync(@operator, tenant, agentId, AgentActions.TcpReachability, new { host = "example.com", port = 443, timeoutMs = 1000 }), "Succeeded", "Rejected");
                Assert.Equal(("Rejected", RejectionReasons.OutsideLocalBounds), (tcp.GetProperty("state").GetString(), tcp.GetProperty("reason").GetString()));

                // The token was used: it can't enroll a second agent.
                await Assert.ThrowsAsync<AgentRefusedException>(() => EnrollAsync(host, pki, token));

                await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/revocation", new { reason = "host decommissioned" }));
                var store = new AgentStore(data);
                using var client = new PlatformClient(Options(host, pki, data), store.LoadCertificate());
                await Assert.ThrowsAsync<AgentRefusedException>(() => client.PollAsync(1, "test", "test-host", Ct));
                Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/commands", new { action = AgentActions.HostSummary })).StatusCode);

                await using var db = database.Context();
                var audit = await db.AuditRecords.AsNoTracking().Select(a => a.Operation).ToListAsync(Ct);
                foreach (var operation in new[] { "agent.enrollment_token.create", "agent.enroll", "agent.command.issue", "agent.command.result", "agent.revoke" })
                {
                    Assert.Contains(operation, audit);
                }
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
                Directory.Delete(data, recursive: true);
            }
        }

        [Fact]
        public async Task Only_an_agent_CA_certificate_of_an_enrolled_agent_gets_in_and_the_listeners_stay_apart_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, _, admin, tenant) = await Caller.AllAsync(users, database);
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));

            // No client certificate.
            using (var anonymous = new PlatformClient(Options(host, pki, TempDirectory()), null))
            {
                await Assert.ThrowsAsync<AgentRefusedException>(() => anonymous.PollAsync(1, "test", "test-host", Ct));
            }
            // A certificate naming the enrolled agent, with its very serial (a certificate is public), from another CA: only the CA check can stop it.
            using (var forged = new PlatformClient(Options(host, pki, TempDirectory()), Forged(identity.AgentId, Convert.FromHexString(store.LoadCertificate().SerialNumber))))
            {
                await Assert.ThrowsAsync<AgentRefusedException>(() => forged.PollAsync(1, "test", "test-host", Ct));
            }
            // The real one works.
            using (var real = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate()))
            {
                Assert.Empty((await real.PollAsync(1, "test", "test-host", Ct)).Commands);
            }

            // The agent API isn't on the people's port, and nothing else is on the agent's.
            Assert.Equal(HttpStatusCode.NotFound, (await users.GetAsync(AgentRoutes.Commands, Ct)).StatusCode);
            using var tls = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, c, _, e) => PlatformClient.IsPlatform(c, null, e, X509CertificateLoader.LoadCertificateFromFile(pki.ServerCa)) },
            }) { BaseAddress = host.AgentUrl };
            Assert.Equal(HttpStatusCode.NotFound, (await tls.GetAsync("/api/v1/tenants", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await tls.GetAsync("/health/live", Ct)).StatusCode);

            // A CSR that doesn't verify, or with a weak key, gets no certificate.
            var token = await TokenAsync(admin, tenant);
            Assert.Equal(HttpStatusCode.BadRequest, (await tls.PostAsJsonAsync(AgentRoutes.Enroll, new EnrollRequest(token, "-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----", "h", "o", "v"), Ct)).StatusCode);
            using var weak = RSA.Create(1024);
            var weakCsr = new CertificateRequest("CN=weak", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
            Assert.Equal(HttpStatusCode.BadRequest, (await tls.PostAsJsonAsync(AgentRoutes.Enroll, new EnrollRequest(token, weakCsr, "h", "o", "v"), Ct)).StatusCode);
        }

        /// <summary>A client certificate shaped like the agent CA's, naming the agent, from a CA the platform doesn't trust.</summary>
        internal static X509Certificate2 Forged(Guid agentId, byte[] serial)
        {
            using var ca = AgentPki.SelfSignedCa("Forger");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={agentId}", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddUri(new Uri($"urn:juice:agent:{agentId}"));
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
            using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serial);
            using var withKey = certificate.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }

        [Fact]
        public async Task A_renewed_agent_keeps_working_and_its_old_certificate_stops_once_the_new_one_is_used_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, _, admin, tenant) = await Caller.AllAsync(users, database);
            var data = TempDirectory();
            var (store, _) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), data);
            var old = store.LoadCertificate();

            var (key, csr) = AgentStore.NewKey("test-host");
            using (var client = new PlatformClient(Options(host, pki, data), old))
            {
                store.SaveRenewal(key, (await client.RenewAsync(new RenewRequest(csr), Ct)).CertificatePem);
            }
            // The answer may not have reached the agent: the old certificate still works until the new one is used.
            using (var stillOld = new PlatformClient(Options(host, pki, data), old))
            {
                await stillOld.PollAsync(1, "test", "test-host", Ct);
            }
            using (var renewed = new PlatformClient(Options(host, pki, data), store.LoadCertificate()))
            {
                await renewed.PollAsync(1, "test", "test-host", Ct);
            }
            using (var stale = new PlatformClient(Options(host, pki, data), old))
            {
                await Assert.ThrowsAsync<AgentRefusedException>(() => stale.PollAsync(1, "test", "test-host", Ct));
            }
        }

        [Fact]
        public async Task An_agent_gets_only_its_own_commands_and_cant_answer_another_agents_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var (a, aIdentity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            var (b, _) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            var command = await IssueAsync(@operator, tenant, aIdentity.AgentId, AgentActions.HostSummary, new { });

            using var bClient = new PlatformClient(Options(host, pki, TempDirectory()), b.LoadCertificate());
            Assert.Empty((await bClient.PollAsync(1, "test", "test-host", Ct)).Commands);
            await Assert.ThrowsAsync<ResultRefusedException>(() => bClient.ReportAsync(new CommandResult(command, CommandOutcome.Succeeded, null, JsonSerializer.SerializeToElement(new { forged = true }), 1), Ct));

            using var aClient = new PlatformClient(Options(host, pki, TempDirectory()), a.LoadCertificate());
            var polled = Assert.Single((await aClient.PollAsync(1, "test", "test-host", Ct)).Commands);
            var verified = CommandVerifier.Verify(polled, aIdentity, Allowlist(), DateTimeOffset.UtcNow);
            Assert.Equal((command, (string?)null), (verified.CommandId!.Value, verified.Rejection));
            Assert.Equal("Delivered", (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{aIdentity.AgentId}/commands/{command}"))).GetProperty("state").GetString());

            // Only operators and admins issue, only valid commands, and viewers can't.
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/tenants/{tenant}/agents/{aIdentity.AgentId}/commands", new { action = AgentActions.HostSummary })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/agents/{aIdentity.AgentId}/commands", new { action = "shell.exec", parameters = new { command = "id" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/agents/{aIdentity.AgentId}/commands",
                new { action = AgentActions.TcpReachability, parameters = new { host = "db", port = 99999, timeoutMs = 1000 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/agents/enrollment-tokens", new { })).StatusCode);
        }

        [Fact]
        public async Task A_result_the_platform_wont_take_is_dropped_and_doesnt_hold_back_the_rest_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            var command = await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.HostSummary, new { });
            using var client = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate());
            Assert.Single((await client.PollAsync(1, "test", "test-host", Ct)).Commands);

            // First in line: a refusal for a command that isn't this agent's (as a forged envelope's id would give). The platform answers 404.
            var unsent = new Queue<CommandResult>([
                new CommandResult(Guid.CreateVersion7(), CommandOutcome.Rejected, RejectionReasons.WrongTarget, null, 0),
                new CommandResult(command, CommandOutcome.Succeeded, null, JsonSerializer.SerializeToElement(new { ok = true }), 3),
            ]);
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, TempDirectory())),
                Microsoft.Extensions.Options.Options.Create(Allowlist()), TimeProvider.System, new TestLog<AgentService>());
            await agent.SendAsync(client, unsent, Ct);

            Assert.Empty(unsent);
            Assert.Equal("Succeeded", (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{identity.AgentId}/commands/{command}"))).GetProperty("state").GetString());
            // And the queue can't grow without end while the platform is away.
            var many = new Queue<CommandResult>();
            for (var i = 0; i < AgentService.MaxUnsent + 5; i++) agent.Keep(many, new CommandResult(Guid.CreateVersion7(), CommandOutcome.Failed, "x", null, 0));
            Assert.Equal(AgentService.MaxUnsent, many.Count);
        }

        [Fact]
        public async Task Failed_enrollments_from_one_address_are_held_back_and_unknown_tokens_leave_no_audit_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var tls = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, c, _, e) => PlatformClient.IsPlatform(c, null, e, X509CertificateLoader.LoadCertificateFromFile(pki.ServerCa)) },
            }) { BaseAddress = host.AgentUrl };
            var (_, csr) = AgentStore.NewKey("h");
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i <= Agents.EnrollmentGuard.MaxFailures; i++)
            {
                statuses.Add((await tls.PostAsJsonAsync(AgentRoutes.Enroll, new EnrollRequest($"guess-{i}", csr, "h", "o", "v"), Ct)).StatusCode);
            }
            Assert.All(statuses.Take(Agents.EnrollmentGuard.MaxFailures), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
            await using var db = database.Context();
            Assert.Empty(await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "agent.enroll").ToListAsync(Ct));
        }

        [Fact]
        public async Task A_waiting_poll_gets_a_new_command_at_once_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            using var client = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate());

            var poll = client.PollAsync(20, "test", "test-host", Ct);
            await Task.Delay(500, Ct);   // the poll is waiting
            var issued = System.Diagnostics.Stopwatch.StartNew();
            await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.HostSummary, new { });
            var received = await poll;
            Assert.Single(received.Commands);
            // Woken by the issue, not found at the next look at the database (every 5 s).
            Assert.True(issued.Elapsed < TimeSpan.FromSeconds(2), $"took {issued.Elapsed}");
        }

        [Fact]
        public async Task An_agent_trusts_a_listener_certificate_from_an_intermediate_under_its_CA_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki(intermediate: true);
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, _, admin, tenant) = await Caller.AllAsync(users, database);
            // The agent trusts only the root; the listener sends the intermediate with its certificate.
            var (store, _) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            using var client = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate());
            Assert.Empty((await client.PollAsync(1, "test", "test-host", Ct)).Commands);
        }

        [Fact]
        public async Task An_agent_on_a_host_discovery_doesnt_know_isnt_looked_up_at_every_poll_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, _, admin, tenant) = await Caller.AllAsync(users, database);
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant));
            using var client = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate());
            await client.PollAsync(0, "test", "test-host", Ct);   // looks, finds nothing

            await using (var db = database.Context())
            {
                db.Resources.Add(new Resource { TenantId = tenant, Type = ResourceType.Host, Key = "test-host", DisplayName = "test-host", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(Ct);
            }
            await client.PollAsync(0, "test", "test-host", Ct);

            // Not looked for again yet: the next look is ten minutes after the last.
            await using var check = database.Context();
            Assert.Null((await check.Agents.AsNoTracking().SingleAsync(a => a.Id == identity.AgentId, Ct)).ResourceId);
        }

        [Fact]
        public async Task A_host_set_by_URLS_keeps_its_address_with_agents_on_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, viaUrls: true);
            using var users = host.Users();
            Assert.Equal(HttpStatusCode.OK, (await users.GetAsync("/health/live", Ct)).StatusCode);
        }

        [Fact]
        public async Task Without_agents_enabled_nothing_can_enroll_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/tenants/{tenant}/agents/enrollment-tokens", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(AgentRoutes.Enroll, new { token = "x", csr = "y" }, Ct)).StatusCode);
        }
    }
}
