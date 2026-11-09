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
            private readonly IReadOnlyDictionary<string, string> _extra;

            /// <param name="database">The platform database for this host.</param>
            /// <param name="pki">The agent CA, command key and listener certificate.</param>
            /// <param name="viaUrls">The host's address as <c>URLS</c> (as ASPNETCORE_URLS or launchSettings set it) instead of <c>HTTP_PORTS</c>.</param>
            /// <param name="extra">Further settings: packages, rotation.</param>
            public AgentHost(ScratchDatabase database, AgentPki pki, bool viaUrls = false, IReadOnlyDictionary<string, string>? extra = null)
            {
                (_database, _pki, _viaUrls, _extra) = (database, pki, viaUrls, extra ?? new Dictionary<string, string>());
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
                 .Concat(_viaUrls ? [new KeyValuePair<string, string>("URLS", $"http://localhost:{HttpPort}")] : [])
                 .Concat(_extra))
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

        /// <summary>
        /// Milestone 2's platform half: only packages a release key signed are offered; only an admin issues an update, and the platform puts in the hash
        /// of the package it checked; the package is served to enrolled agents only, and the real agent downloads, checks and unpacks it.
        /// </summary>
        [Fact]
        public async Task Only_admins_update_agents_and_only_to_signed_packages_the_platform_offers_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var packages = Path.Combine(pki.Directory, "packages");
            var good = AgentPackageFiles.Zip("1.1.0");
            AgentPackageFiles.Publish(packages, "1.1.0", good, release);
            AgentPackageFiles.Publish(packages, "1.2.0", AgentPackageFiles.Zip("1.2.0"), stranger);        // signed, by no release key
            AgentPackageFiles.Publish(packages, "1.3.0", AgentPackageFiles.Zip("1.3.0", "9.9.9"), release); // signed, but says it's another version
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string>
            {
                ["Agents:PackageDirectory"] = packages, ["Agents:ReleaseKeyFiles:0"] = AgentPackageFiles.WritePublicKey(pki.Directory, release),
            });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var data = TempDirectory();
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), data);
            var agentPath = $"/api/v1/tenants/{tenant}/agents/{identity.AgentId}/commands";

            var offered = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/packages"))).GetProperty("items");
            var only = Assert.Single(offered.EnumerateArray());
            Assert.Equal(("1.1.0", AgentPackages.Sha256(good)), (only.GetProperty("version").GetString(), only.GetProperty("sha256").GetString()));

            Assert.Equal(HttpStatusCode.Forbidden, (await @operator.PostAsync(agentPath, new { action = AgentActions.AgentUpdate, parameters = new { version = "1.1.0" } })).StatusCode);
            foreach (var parameters in new object[] { new { version = "1.2.0" }, new { version = "1.3.0" }, new { version = "1.1.0", sha256 = new string('a', 64) }, new { } })
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(agentPath, new { action = AgentActions.AgentUpdate, parameters })).StatusCode);
            }
            var issued = await Caller.JsonAsync(await admin.PostAsync(agentPath, new { action = AgentActions.AgentUpdate, parameters = new { version = "1.1.0" } }));
            Assert.Equal(AgentPackages.Sha256(good), issued.GetProperty("parameters").GetProperty("sha256").GetString());
            Assert.Equal(CommandLimits.MaxLifetime, issued.GetProperty("expiresAt").GetDateTimeOffset() - issued.GetProperty("issuedAt").GetDateTimeOffset());

            using var client = new PlatformClient(Options(host, pki, data), store.LoadCertificate());
            var (bytes, signature) = await client.DownloadAsync("1.1.0", Ct);
            Assert.Equal(good, bytes);
            Assert.True(AgentPackages.Verify(release, bytes, signature));
            Assert.Equal("package_not_found", (await Assert.ThrowsAsync<UpdateFailedException>(() => client.DownloadAsync("1.2.0", Ct))).Message);
            using (var anonymous = new PlatformClient(Options(host, pki, TempDirectory()), null))
            {
                await Assert.ThrowsAsync<AgentRefusedException>(() => anonymous.DownloadAsync("1.1.0", Ct));
            }

            // The real agent with that command: downloaded, checked against its own release key, unpacked, and handed to the launcher.
            var home = Path.Combine(data, "home");
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, data)),
                Microsoft.Extensions.Options.Options.Create(new LocalAllowlist { Actions = { [AgentActions.AgentUpdate] = new ActionPolicy() } }), TimeProvider.System, new TestLog<AgentService>())
            { Updater = new AgentUpdater(home, [release], null) };
            var command = Assert.Single((await client.PollAsync(1, "1.0.0", "test-host", Ct)).Commands);
            Assert.Null(await agent.HandleAsync(command, identity, new CommandMemory(null), new Diagnostics("/", "1.0.0"), Ct, client.DownloadAsync));
            Assert.True(agent.RestartRequested);
            Assert.True(File.Exists(Path.Combine(home, AgentHome.VersionsDirectory, "1.1.0", "juice-agent.dll")));
        }

        /// <summary>
        /// Rotation against a running platform: the platform moves to a new CA and command key; an agent enrolled before keeps its certificate, learns the
        /// new key at its next poll, runs commands signed with it, and gets a certificate from the new CA when it renews.
        /// </summary>
        [Fact]
        public async Task An_agent_enrolled_before_a_rotation_follows_it_without_enrolling_again_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var before = new AgentPki();
            using var after = new AgentPki();
            var data = TempDirectory();
            AgentStore store;
            AgentIdentity identity;
            Guid tenant;
            await using (var old = new AgentHost(database, before))
            {
                using var users = old.Users();
                var (_, _, admin, t) = await Caller.AllAsync(users, database);
                tenant = t;
                (store, identity) = await EnrollAsync(old, before, await TokenAsync(admin, tenant), data);
            }

            await using var host = new AgentHost(database, after, extra: new Dictionary<string, string>
            {
                ["Agents:PreviousCaCertificateFiles:0"] = before.AgentCaCertificate, ["Agents:PreviousCommandKeyFiles:0"] = before.CommandKey,
            });
            using var platformUsers = host.Users();
            var (_, @operator, _, _) = await Caller.AllAsync(platformUsers, database);
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, after, data)),
                Microsoft.Extensions.Options.Options.Create(Allowlist()), TimeProvider.System, new TestLog<AgentService>());

            using (var client = new PlatformClient(Options(host, after, data), store.LoadCertificate()))
            {
                // Its certificate from the old CA still gets in; the poll hands over the new key, announced by the old one.
                var poll = await client.PollAsync(1, "test", "test-host", Ct, identity.CommandKeys.Keys);
                Assert.NotNull(poll.Keys);
                identity = agent.TrustAnnounced(poll.Keys, identity);
                Assert.Equal(2, identity.CommandKeys.Count);
                // Once it has it, nothing more is announced.
                var command = await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.HostSummary, new { });
                poll = await client.PollAsync(1, "test", "test-host", Ct, identity.CommandKeys.Keys);
                Assert.Null(poll.Keys);
                var signed = Assert.Single(poll.Commands);
                Assert.Null(CommandVerifier.Verify(signed, identity, Allowlist(), DateTimeOffset.UtcNow).Rejection);

                var (key, csr) = AgentStore.NewKey("test-host");
                store.SaveRenewal(key, (await client.RenewAsync(new RenewRequest(csr), Ct)).CertificatePem);
            }
            using var renewed = store.LoadCertificate();
            using var newCa = X509CertificateLoader.LoadCertificateFromFile(after.AgentCaCertificate);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(newCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(chain.Build(renewed), "the renewed certificate isn't from the new CA");
            using var again = new PlatformClient(Options(host, after, data), renewed);
            Assert.Null((await again.PollAsync(1, "test", "test-host", Ct, identity.CommandKeys.Keys)).Keys);
        }

        /// <summary>
        /// A revoked agent comes back only as a new one, with a new key, and only with an admin's new token: the old token in its file changes
        /// nothing, and a new one there makes it set its old identity aside and enroll again, without anyone clearing its data directory.
        /// </summary>
        [Fact]
        public async Task A_revoked_agent_enrolls_again_as_a_new_agent_once_a_new_token_is_in_its_file_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (viewer, _, admin, tenant) = await Caller.AllAsync(users, database);
            var data = TempDirectory();
            Directory.CreateDirectory(data);
            var tokenFile = Path.Combine(data, "..", $"{Guid.NewGuid():N}.token");
            await File.WriteAllTextAsync(tokenFile, await TokenAsync(admin, tenant), Ct);
            var options = Options(host, pki, data);
            options.EnrollmentTokenFile = tokenFile;
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(Allowlist()),
                TimeProvider.System, new TestLog<AgentService>()) { TokenCheck = TimeSpan.FromMilliseconds(100) };
            async Task<List<JsonElement>> AgentsAsync()
                => [.. (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents?all=true"))).GetProperty("items").EnumerateArray()];
            await agent.StartAsync(Ct);
            try
            {
                List<JsonElement> agents = [];
                for (var i = 0; i < 100 && agents.Count == 0; i++)
                {
                    await Task.Delay(100, Ct);
                    agents = await AgentsAsync();
                }
                var first = Assert.Single(agents).GetProperty("id").GetGuid();
                await Caller.JsonAsync(await admin.PostAsync($"/api/v1/tenants/{tenant}/agents/{first}/revocation", new { reason = "key may have leaked" }));

                // Refused, with only the used token in its file: it keeps its identity and waits.
                await Task.Delay(TimeSpan.FromSeconds(4), Ct);
                Assert.True(new AgentStore(data).IsEnrolled, "the agent set its identity aside without a new token");
                Assert.Single(await AgentsAsync());

                await File.WriteAllTextAsync(tokenFile, await TokenAsync(admin, tenant), Ct);
                for (var i = 0; i < 100 && agents.Count < 2; i++)
                {
                    await Task.Delay(100, Ct);
                    agents = await AgentsAsync();
                }
                Assert.Equal(2, agents.Count);
                var second = agents.Single(a => a.GetProperty("id").GetGuid() != first);
                Assert.Equal("Active", second.GetProperty("status").GetString());
                Assert.Equal("Revoked", agents.Single(a => a.GetProperty("id").GetGuid() == first).GetProperty("status").GetString());
                var aside = Assert.Single(Directory.GetDirectories(Path.Combine(data, "revoked")));
                Assert.StartsWith(first.ToString(), Path.GetFileName(aside));
                Assert.True(File.Exists(Path.Combine(aside, "identity.json")) && File.Exists(Path.Combine(aside, "agent.pem")));
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
                File.Delete(tokenFile);
                Directory.Delete(data, recursive: true);
            }
        }

        /// <summary>
        /// Custom actions end to end: the agent reports what it can run and what its host permits; the platform offers exactly that, signs a custom
        /// action only as the agent described it, keeps changing ones for admins, and the agent runs it. An agent that never reported shows the
        /// built-in actions, permitted unknown.
        /// </summary>
        [Fact]
        public async Task An_agent_reports_its_custom_actions_and_the_platform_offers_and_checks_them_as_reported_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);

            // An agent from before reports: the built-ins, permitted unknown.
            var olderData = TempDirectory();
            var (olderStore, older) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), olderData);
            var olderActions = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{older.AgentId}/actions"));
            Assert.Equal(JsonValueKind.Null, olderActions.GetProperty("reportedAt").ValueKind);
            Assert.Equal(AgentActions.All, olderActions.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()!));
            Assert.All(olderActions.GetProperty("items").EnumerateArray(), a => Assert.Equal(JsonValueKind.Null, a.GetProperty("permitted").ValueKind));
            Assert.Equal(AgentActions.All, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/actions"))).GetProperty("items")
                .EnumerateArray().Select(a => a.GetProperty("name").GetString()!));
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"/api/v1/tenants/{tenant}/agents/{older.AgentId}/commands",
                new { action = "test.queue_depth", parameters = new { queue = "orders", top = 1 } })).StatusCode);

            // A report that doesn't hold is refused whole and changes nothing: here a custom action passing itself off as a built-in.
            using (var olderClient = new PlatformClient(Options(host, pki, olderData), olderStore.LoadCertificate()))
            {
                var impostor = new ReportedAction(new ActionDescriptor(AgentActions.HostSummary, "x", true, [], Custom: true), true, []);
                await Assert.ThrowsAsync<ResultRefusedException>(() => olderClient.ReportActionsAsync(new ActionsReport([impostor]), Ct));
                Assert.Equal(JsonValueKind.Null, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{older.AgentId}/actions"))).GetProperty("reportedAt").ValueKind);
            }

            var allowlist = Allowlist();
            allowlist.Actions["test.queue_depth"] = new ActionPolicy { Values = { ["queue"] = ["orders", "billing"] } };
            allowlist.Actions["test.restart_worker"] = new ActionPolicy();
            var data = TempDirectory();
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, data, await TokenAsync(admin, tenant))),
                Microsoft.Extensions.Options.Options.Create(allowlist), TimeProvider.System, new TestLog<AgentService>(), actions: new AgentActionCatalog([new QueueDepth(), new Restart()]));
            await agent.StartAsync(Ct);
            try
            {
                Guid agentId = default;
                JsonElement actions = default;
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(100, Ct);
                    var list = (await Caller.JsonAsync(await admin.GetAsync($"/api/v1/tenants/{tenant}/agents"))).GetProperty("items").EnumerateArray().ToList();
                    if (list.FirstOrDefault(a => a.GetProperty("id").GetGuid() != older.AgentId) is not { ValueKind: JsonValueKind.Object } found) continue;
                    agentId = found.GetProperty("id").GetGuid();
                    actions = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/actions"));
                    if (actions.GetProperty("reportedAt").ValueKind != JsonValueKind.Null) break;
                }
                Assert.NotEqual(JsonValueKind.Null, actions.GetProperty("reportedAt").ValueKind);
                var reported = actions.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
                Assert.Equal([.. AgentActions.All, "test.queue_depth", "test.restart_worker"], reported.Keys);
                Assert.False(reported[AgentActions.DiskUsage].GetProperty("permitted").GetBoolean());
                var queue = reported["test.queue_depth"];
                Assert.True(queue.GetProperty("custom").GetBoolean() && queue.GetProperty("permitted").GetBoolean());
                Assert.Equal(["orders", "billing"], queue.GetProperty("parameters")[0].GetProperty("allowed").EnumerateArray().Select(v => v.GetString()!));
                Assert.Contains("timeoutMs at most 2000", reported[AgentActions.TcpReachability].GetProperty("limits").EnumerateArray().Select(l => l.GetString()));

                // Run as reported.
                var ran = await WaitForCommandAsync(@operator, tenant, agentId,
                    await IssueAsync(@operator, tenant, agentId, "test.queue_depth", new { queue = "billing", top = 7 }), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", ran.GetProperty("state").GetString());
                Assert.Equal(42, ran.GetProperty("result").GetProperty("depth").GetInt32());

                // The platform refuses what the agent would: a value its host doesn't allow, a member the action doesn't take, a name it never reported.
                async Task<HttpStatusCode> Issue(Caller caller, string action, object parameters)
                    => (await caller.PostAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/commands", new { action, parameters })).StatusCode;
                Assert.Equal(HttpStatusCode.BadRequest, await Issue(@operator, "test.queue_depth", new { queue = "audit", top = 7 }));
                Assert.Equal(HttpStatusCode.BadRequest, await Issue(@operator, "test.queue_depth", new { queue = "orders", top = 7, command = "sh" }));
                Assert.Equal(HttpStatusCode.BadRequest, await Issue(@operator, "test.unknown", new { }));

                // A custom action that changes the host is an admin's.
                Assert.Equal(HttpStatusCode.Forbidden, await Issue(@operator, "test.restart_worker", new { }));
                var restarted = await WaitForCommandAsync(admin, tenant, agentId, await IssueAsync(admin, tenant, agentId, "test.restart_worker", new { }), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", restarted.GetProperty("state").GetString());

                await using var db = database.Context();
                var report = await db.AuditRecords.AsNoTracking().SingleAsync(a => a.Operation == "agent.actions.report", Ct);
                Assert.Equal("test.queue_depth,test.restart_worker", report.Metadata["custom"]);
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
