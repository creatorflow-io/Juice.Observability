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
using Microsoft.Extensions.DependencyInjection;
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

            // An update is Medium risk: an admin's runs at once; an operator's isn't signed, it has to be requested and approved (ActionRequestTests).
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync(agentPath, new { action = AgentActions.AgentUpdate, parameters = new { version = "1.1.0" } })).StatusCode);
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

        private static async Task<JsonElement> RequestAsync(Caller caller, Guid tenant, Guid agent, string action, object parameters, HttpStatusCode expected = HttpStatusCode.Created)
        {
            var response = await caller.PostAsync($"/api/v1/tenants/{tenant}/agents/{agent}/action-requests", new { action, parameters, reason = "e2e test" });
            Assert.True(response.StatusCode == expected, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return expected == HttpStatusCode.Created ? await Caller.JsonAsync(response) : default;
        }

        private static async Task<HttpStatusCode> DecideAsync(Caller caller, Guid tenant, Guid request, string decision, string? comment = "ok")
            => (await caller.PostAsync($"/api/v1/tenants/{tenant}/action-requests/{request}/{decision}", new { comment })).StatusCode;

        /// <summary>
        /// Phase 8's approval gate end to end, with a real agent: what each risk's policy needs, the second-person rule, duplicates, rejection and
        /// cancellation, every step audited; and the host's own <c>RequireApproval</c>, met by the approval reference the signed command carries.
        /// </summary>
        [Fact]
        public async Task Actions_run_as_their_risks_approval_policy_says_and_a_second_person_approves_their_own_kind_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string> { ["Actions:Overrides:test.restart_worker"] = "SecondPerson" });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var adam = new Caller(users, await LocalStack.TokenAsync("adam"));
            var allowlist = Allowlist();
            allowlist.Actions["test.queue_depth"] = new ActionPolicy();
            allowlist.Actions["test.restart_worker"] = new ActionPolicy { RequireApproval = true };
            var data = TempDirectory();
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, data, await TokenAsync(admin, tenant))),
                Microsoft.Extensions.Options.Options.Create(allowlist), TimeProvider.System, new TestLog<AgentService>(), actions: new AgentActionCatalog([new QueueDepth(), new Restart()]));
            await agent.StartAsync(Ct);
            try
            {
                Guid agentId = default;
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(100, Ct);
                    var list = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents"))).GetProperty("items");
                    if (list.GetArrayLength() == 0) continue;
                    agentId = list[0].GetProperty("id").GetGuid();
                    var actions = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/actions"));
                    if (actions.GetProperty("reportedAt").ValueKind != JsonValueKind.Null) break;
                }
                var reported = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/actions"))).GetProperty("items").EnumerateArray()
                    .ToDictionary(a => a.GetProperty("name").GetString()!, a => (a.GetProperty("risk").GetString(), a.GetProperty("approval").GetString()));
                Assert.Equal(("Low", "None"), reported["test.queue_depth"]);
                Assert.Equal(("Medium", "SecondPerson"), reported["test.restart_worker"]);
                Assert.Equal(("Medium", "Admin"), reported[AgentActions.AgentUpdate]);

                // Low: runs on request, the request issued at once.
                var low = await RequestAsync(@operator, tenant, agentId, "test.queue_depth", new { queue = "orders", top = 1 });
                Assert.Equal("Issued", low.GetProperty("state").GetString());
                var lowRun = await WaitForCommandAsync(viewer, tenant, agentId, low.GetProperty("command").GetProperty("id").GetGuid(), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", lowRun.GetProperty("state").GetString());

                // Second person: not straight to a command, not approved by the one who asked, nor by an operator; a duplicate waits its turn.
                Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/commands", new { action = "test.restart_worker" })).StatusCode);
                var asked = await RequestAsync(admin, tenant, agentId, "test.restart_worker", new { });
                var id = asked.GetProperty("id").GetGuid();
                Assert.Equal(("Pending", "SecondPerson"), (asked.GetProperty("state").GetString(), asked.GetProperty("policy").GetString()));
                await RequestAsync(@operator, tenant, agentId, "test.restart_worker", new { }, HttpStatusCode.Conflict);
                Assert.Equal(HttpStatusCode.Forbidden, await DecideAsync(admin, tenant, id, "approval"));
                Assert.Equal(HttpStatusCode.Forbidden, await DecideAsync(@operator, tenant, id, "approval"));
                Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, id, "approval", "seen the alert"));
                Assert.Equal(HttpStatusCode.Conflict, await DecideAsync(adam, tenant, id, "approval"));
                var approved = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests/{id}"));
                // The host requires an approval reference for it: that it ran shows the signed command carried one.
                var ran = await WaitForCommandAsync(viewer, tenant, agentId, approved.GetProperty("command").GetProperty("id").GetGuid(), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", ran.GetProperty("state").GetString());

                // Rejected: final, with the reason; cancelled by the one who asked.
                var rejected = (await RequestAsync(@operator, tenant, agentId, "test.restart_worker", new { })).GetProperty("id").GetGuid();
                Assert.Equal(HttpStatusCode.BadRequest, await DecideAsync(adam, tenant, rejected, "rejection", null));
                Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, rejected, "rejection", "not during business hours"));
                Assert.Equal(HttpStatusCode.Conflict, await DecideAsync(admin, tenant, rejected, "approval"));
                var cancelled = (await RequestAsync(@operator, tenant, agentId, "test.restart_worker", new { })).GetProperty("id").GetGuid();
                Assert.Equal(HttpStatusCode.OK, await DecideAsync(@operator, tenant, cancelled, "cancellation", null));

                var pending = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests?state=Pending"));
                Assert.Equal(0, pending.GetProperty("items").GetArrayLength());
                var all = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests"))).GetProperty("items").EnumerateArray()
                    .Select(r => r.GetProperty("state").GetString()).ToList();
                Assert.Equal(["Cancelled", "Rejected", "Issued", "Issued"], all);

                await using var db = database.Context();
                var audit = await db.AuditRecords.AsNoTracking().Where(a => a.Operation.StartsWith("action.request.")).Select(a => new { a.Operation, a.Outcome }).ToListAsync(Ct);
                Assert.Contains(audit, a => a.Operation == "action.request.approve" && a.Outcome == AuditOutcome.Denied);
                Assert.Contains(audit, a => a.Operation == "action.request.approve" && a.Outcome == AuditOutcome.Succeeded);
                Assert.Contains(audit, a => a.Operation == "action.request.create" && a.Outcome == AuditOutcome.Failed);   // the duplicate
                Assert.Contains(audit, a => a.Operation == "action.request.reject" && a.Outcome == AuditOutcome.Succeeded);
                Assert.Contains(audit, a => a.Operation == "action.request.cancel" && a.Outcome == AuditOutcome.Succeeded);
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
                Directory.Delete(data, recursive: true);
            }
        }

        /// <summary>A request nobody decides expires: it can't be approved after, it no longer counts as pending, and the same action can be asked again.</summary>
        [Fact]
        public async Task An_undecided_request_expires_and_cant_be_approved_after_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string> { ["Actions:ApprovalLifetime"] = "00:00:01" });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var data = TempDirectory();
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), data);
            using (var client = new PlatformClient(Options(host, pki, data), store.LoadCertificate()))
            {
                await client.ReportActionsAsync(Allowlist().Report(new AgentActionCatalog([new Restart()])), Ct);
            }
            var id = (await RequestAsync(@operator, tenant, identity.AgentId, "test.restart_worker", new { })).GetProperty("id").GetGuid();
            await Task.Delay(1500, Ct);
            Assert.Equal("Expired", (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests/{id}"))).GetProperty("state").GetString());
            Assert.Equal(0, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests?state=Pending"))).GetProperty("items").GetArrayLength());
            Assert.Equal(HttpStatusCode.Conflict, await DecideAsync(admin, tenant, id, "approval"));
            Assert.Equal(1, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests?state=Expired"))).GetProperty("items").GetArrayLength());
            await RequestAsync(@operator, tenant, identity.AgentId, "test.restart_worker", new { });
            await using var db = database.Context();
            Assert.Empty(await db.AgentCommands.AsNoTracking().ToListAsync(Ct));
        }

        /// <summary>
        /// Approvers hear of requests: the endpoints in <c>Actions:NotifyEndpoints</c> get a signed notice when one waits and when it's rejected,
        /// cancelled, expired (closed by the sweeper, audited as the platform) or approved, and nothing for one issued at once. The menu's count
        /// is what the caller can decide: an operator none, and an admin not their own second-person request.
        /// </summary>
        [Fact]
        public async Task Approvers_are_told_when_a_request_waits_and_how_it_ends_Async()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            const string secretVariable = "JUICE_TEST_APPROVALS_SECRET";
            Environment.SetEnvironmentVariable(secretVariable, "approvals-test-secret");
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string>
            {
                ["Actions:Overrides:test.restart_worker"] = "SecondPerson", ["Actions:Overrides:test.queue_depth"] = "None",
                ["Actions:NotifyEndpoints:0"] = "approvers", ["Actions:SweepInterval"] = "00:00:00.200",
                ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Endpoints:0:Key"] = "approvers", ["Webhooks:Endpoints:0:Url"] = receiver.Url,
                ["Webhooks:Endpoints:0:SecretRef"] = $"env:{secretVariable}", ["Webhooks:Allowlist:0:Destination"] = $"{receiver.Address}/32",
                ["Webhooks:Allowlist:0:Ports:0"] = receiver.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var adam = new Caller(users, await LocalStack.TokenAsync("adam"));
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), TempDirectory());
            using (var client = new PlatformClient(Options(host, pki, TempDirectory()), store.LoadCertificate()))
            {
                var allowlist = Allowlist();
                allowlist.Actions["test.queue_depth"] = new ActionPolicy();
                allowlist.Actions["test.restart_worker"] = new ActionPolicy();
                await client.ReportActionsAsync(allowlist.Report(new AgentActionCatalog([new QueueDepth(), new Restart()])), Ct);
            }
            async Task<(int Total, int ForYou)> WaitingAsync(Caller caller)
            {
                var waiting = await Caller.JsonAsync(await caller.GetAsync($"/api/v1/tenants/{tenant}/action-requests/waiting"));
                return (waiting.GetProperty("total").GetInt32(), waiting.GetProperty("forYou").GetInt32());
            }
            const string restart = "test.restart_worker";
            var agentId = identity.AgentId;

            // Issued at once: nobody to tell.
            await RequestAsync(@operator, tenant, agentId, "test.queue_depth", new { queue = "orders", top = 1 });
            Assert.Equal((0, 0), await WaitingAsync(admin));

            var rejected = (await RequestAsync(@operator, tenant, agentId, restart, new { })).GetProperty("id").GetGuid();
            Assert.Equal((1, 1), await WaitingAsync(admin));
            Assert.Equal((1, 0), await WaitingAsync(@operator));
            Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, rejected, "rejection", "not now"));

            var cancelled = (await RequestAsync(@operator, tenant, agentId, restart, new { })).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, await DecideAsync(@operator, tenant, cancelled, "cancellation", null));

            var lapsed = (await RequestAsync(@operator, tenant, agentId, restart, new { })).GetProperty("id").GetGuid();
            await using (var db = database.Context())
            {
                await db.ActionRequests.Where(r => r.Id == lapsed).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
                for (var i = 0; i < 50 && await db.ActionRequests.AsNoTracking().Where(r => r.Id == lapsed).Select(r => r.State).SingleAsync(Ct) != ActionRequestState.Expired; i++)
                {
                    await Task.Delay(100, Ct);
                }
                Assert.Equal(ActionRequestState.Expired, await db.ActionRequests.AsNoTracking().Where(r => r.Id == lapsed).Select(r => r.State).SingleAsync(Ct));
                Assert.Single(await db.AuditRecords.AsNoTracking().Where(a => a.Operation == "action.request.expire" && a.TargetId == lapsed.ToString() && a.Actor == "platform").ToListAsync(Ct));
            }

            var own = (await RequestAsync(admin, tenant, agentId, restart, new { })).GetProperty("id").GetGuid();
            Assert.Equal((1, 0), await WaitingAsync(admin));
            Assert.Equal((1, 1), await WaitingAsync(adam));
            Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, own, "approval", "go"));

            string[] expected =
            [
                ActionRequestEvents.Created, ActionRequestEvents.Rejected, ActionRequestEvents.Created, ActionRequestEvents.Cancelled,
                ActionRequestEvents.Created, ActionRequestEvents.Expired, ActionRequestEvents.Created, ActionRequestEvents.Approved,
            ];
            await using (var db = database.Context())
            {
                var events = await db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).Select(d => d.Event).ToListAsync(Ct);
                Assert.Equal(expected, events);
            }

            // Sent like any notification: signed, to the allowlisted receiver.
            var dispatcher = new Juice.Observability.Webhooks.WebhookDispatcher(
                new ServiceCollection().AddDbContext<PlatformDbContext>(o => PlatformDbContext.Configure(o, database.ConnectionString))
                    .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(new Juice.Observability.Webhooks.WebhookOptions { AllowHttpToAllowlisted = true }), TimeProvider.System,
                NullLogger<Juice.Observability.Webhooks.WebhookDispatcher>.Instance);
            for (var i = 0; i < 10 && receiver.Requests.Count < expected.Length; i++) await dispatcher.DispatchOnceAsync(Ct);
            var received = receiver.Requests.ToList();
            Assert.Equal(expected.Length, received.Count);
            Assert.All(received, r => Assert.True(Juice.Observability.Webhooks.WebhookSigner.Verify("approvals-test-secret", r.Id, r.Timestamp, r.Signature, r.Body,
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)), "unsigned notice"));
            var bodies = received.Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
            Assert.Equal(expected.Order(), bodies.Select(b => b.GetProperty("type").GetString()!).Order());
            var waited = bodies.First(b => b.GetProperty("type").GetString() == ActionRequestEvents.Created && b.GetProperty("request").GetProperty("id").GetGuid() == own);
            Assert.Equal((restart, "SecondPerson", "e2e test"), (waited.GetProperty("request").GetProperty("action").GetString(),
                waited.GetProperty("request").GetProperty("policy").GetString(), waited.GetProperty("request").GetProperty("reason").GetString()));
            var turnedDown = bodies.Single(b => b.GetProperty("type").GetString() == ActionRequestEvents.Rejected).GetProperty("request");
            Assert.Equal(("not now", "Rejected"), (turnedDown.GetProperty("comment").GetString(), turnedDown.GetProperty("state").GetString()));
        }

        /// <summary>Stands in for systemd or the Windows service manager: restarts nothing, says the service runs.</summary>
        private sealed class FakeServices : IServiceControl
        {
            public List<string> Restarted { get; } = [];

            public Task<ServiceRestartResult> RestartAsync(string service, CancellationToken cancellationToken)
            {
                Restarted.Add(service);
                return Task.FromResult(new ServiceRestartResult(service, "active", true));
            }
        }

        /// <summary>
        /// Phase 8's remediation gate, with a real agent: a restart is refused on stale evidence and on a target already healthy, closed if its target
        /// recovers before it's approved, and once it ran, counts as recovered only when health from telemetry newer than the action says so;
        /// otherwise it ends not verified when the window passes. A cleanup's dry run runs on request and deletes nothing; the real one deletes.
        /// </summary>
        [Fact]
        public async Task A_remediation_needs_fresh_evidence_and_counts_as_recovered_only_by_newer_health_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string>
            {
                ["Actions:VerificationWindow"] = "00:00:03", ["Actions:SweepInterval"] = "00:00:00.200",
            });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var adam = new Caller(users, await LocalStack.TokenAsync("adam"));
            var folder = TempDirectory();
            Directory.CreateDirectory(folder);
            var allowlist = Allowlist();
            allowlist.Actions[AgentActions.ServiceRestart] = new ActionPolicy { Services = ["orders.service"], RequireApproval = true };
            allowlist.Actions[AgentActions.FilesCleanup] = new ActionPolicy { Folders = [new CleanupFolder { Path = folder, Pattern = "*.log", KeepHours = 24 }] };
            var services = new FakeServices();
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, TempDirectory(), await TokenAsync(admin, tenant))),
                Microsoft.Extensions.Options.Options.Create(allowlist), TimeProvider.System, new TestLog<AgentService>())
            { Remedies = new Remediation(allowlist, TimeProvider.System, services) };
            await agent.StartAsync(Ct);
            try
            {
                Guid agentId = default;
                for (var i = 0; i < 100 && agentId == default; i++)
                {
                    await Task.Delay(100, Ct);
                    var list = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents"))).GetProperty("items");
                    if (list.GetArrayLength() > 0 && (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{list[0].GetProperty("id").GetGuid()}/actions")))
                        .GetProperty("reportedAt").ValueKind != JsonValueKind.Null) agentId = list[0].GetProperty("id").GetGuid();
                }
                var target = Guid.NewGuid();
                Guid platformTenant;
                await using (var db = database.Context()) platformTenant = (await db.Tenants.SingleAsync(Ct)).Id;
                async Task HealthAsync(HealthState state, TimeSpan evaluatedAgo)
                {
                    await using var db = database.Context();
                    var at = DateTimeOffset.UtcNow - evaluatedAgo;
                    var snapshot = await db.HealthSnapshots.SingleOrDefaultAsync(s => s.SubjectId == target, Ct);
                    if (snapshot is null)
                    {
                        db.HealthSnapshots.Add(snapshot = new HealthSnapshot { TenantId = platformTenant, SubjectType = HealthSubject.Host, SubjectId = target, PolicyVersion = "test" });
                    }
                    (snapshot.State, snapshot.EvaluatedAt, snapshot.LastEvidenceAt, snapshot.EvidenceFrom, snapshot.EvidenceTo, snapshot.Since) = (state, at, at, at.AddMinutes(-5), at, at);
                    await db.SaveChangesAsync(Ct);
                }
                async Task<HttpResponseMessage> AskAsync(Caller caller, string action, object parameters, bool aimed = true)
                    => await caller.PostAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/action-requests",
                        new { action, parameters, reason = "orders is down", target = aimed ? new { type = "Host", id = target } : null });
                async Task<JsonElement> RequestOf(Guid id) => await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/action-requests/{id}"));
                async Task<JsonElement> VerifiedAsync(Guid id)
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var r = await RequestOf(id);
                        if (r.GetProperty("verification").GetString() is "Verified" or "NotVerified") return r;
                        await Task.Delay(100, Ct);
                    }
                    throw new TimeoutException($"request {id} never finished verifying");
                }
                var restart = new { service = "orders.service" };

                // Stale: health evaluated 20 minutes ago says nothing about now.
                await HealthAsync(HealthState.Unhealthy, TimeSpan.FromMinutes(20));
                var stale = await AskAsync(@operator, AgentActions.ServiceRestart, restart);
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.Contains("stale_evidence", await stale.Content.ReadAsStringAsync(Ct));
                // Healthy: nothing to fix.
                await HealthAsync(HealthState.Healthy, TimeSpan.Zero);
                var healthy = await AskAsync(@operator, AgentActions.ServiceRestart, restart);
                Assert.Equal(HttpStatusCode.Conflict, healthy.StatusCode);
                Assert.Contains("precondition_failed", await healthy.Content.ReadAsStringAsync(Ct));

                // Asked while unhealthy, recovered before anyone approved: closed by the platform, never run.
                await HealthAsync(HealthState.Unhealthy, TimeSpan.Zero);
                var recovered = (await Caller.JsonAsync(await AskAsync(@operator, AgentActions.ServiceRestart, restart))).GetProperty("id").GetGuid();
                Assert.Equal(("High", "SecondPerson"), ((await RequestOf(recovered)).GetProperty("risk").GetString(), (await RequestOf(recovered)).GetProperty("policy").GetString()));
                await HealthAsync(HealthState.Healthy, TimeSpan.Zero);
                Assert.Equal(HttpStatusCode.Conflict, await DecideAsync(adam, tenant, recovered, "approval"));
                var closed = await RequestOf(recovered);
                Assert.Equal(("Cancelled", "platform"), (closed.GetProperty("state").GetString(), closed.GetProperty("decidedBy").GetString()));

                // Approved, run, and recovered by newer health.
                await HealthAsync(HealthState.Unhealthy, TimeSpan.Zero);
                var fixedIt = (await Caller.JsonAsync(await AskAsync(@operator, AgentActions.ServiceRestart, restart))).GetProperty("id").GetGuid();
                Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, fixedIt, "approval", "orders is down for everyone"));
                var ran = await WaitForCommandAsync(viewer, tenant, agentId, (await RequestOf(fixedIt)).GetProperty("command").GetProperty("id").GetGuid(), "Succeeded", "Failed", "Rejected");
                Assert.Equal("Succeeded", ran.GetProperty("state").GetString());
                Assert.True(ran.GetProperty("result").GetProperty("running").GetBoolean());
                Assert.Equal(["orders.service"], services.Restarted);
                // The agent's "done" isn't recovery: still verifying while health hasn't seen newer telemetry.
                await Task.Delay(600, Ct);
                Assert.Equal("Verifying", (await RequestOf(fixedIt)).GetProperty("verification").GetString());
                await HealthAsync(HealthState.Healthy, TimeSpan.Zero);
                var verified = await VerifiedAsync(fixedIt);
                Assert.Equal("Verified", verified.GetProperty("verification").GetString());

                // Run, but never seen healthy within the window: not verified.
                await HealthAsync(HealthState.Unhealthy, TimeSpan.Zero);
                var notFixed = (await Caller.JsonAsync(await AskAsync(@operator, AgentActions.ServiceRestart, restart))).GetProperty("id").GetGuid();
                Assert.Equal(HttpStatusCode.OK, await DecideAsync(adam, tenant, notFixed, "approval"));
                var unverified = await VerifiedAsync(notFixed);
                Assert.Equal("NotVerified", unverified.GetProperty("verification").GetString());
                Assert.Contains("unhealthy", unverified.GetProperty("verificationNote").GetString());

                // A cleanup's dry run is Low: it runs on request and deletes nothing. The real one, Medium, runs when an admin asks.
                var old = Path.Combine(folder, "app-1.log");
                await File.WriteAllTextAsync(old, "old", Ct);
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
                var dry = await Caller.JsonAsync(await AskAsync(@operator, AgentActions.FilesCleanup, new { folder, dryRun = true }, aimed: false));
                Assert.Equal(("Low", "Issued"), (dry.GetProperty("risk").GetString(), dry.GetProperty("state").GetString()));
                var dryRun = await WaitForCommandAsync(viewer, tenant, agentId, dry.GetProperty("command").GetProperty("id").GetGuid(), "Succeeded", "Failed", "Rejected");
                Assert.Equal(1, dryRun.GetProperty("result").GetProperty("filesRemoved").GetInt32());
                Assert.True(File.Exists(old));
                var real = await Caller.JsonAsync(await AskAsync(admin, AgentActions.FilesCleanup, new { folder }, aimed: false));
                Assert.Equal(("Medium", "Issued", "None"), (real.GetProperty("risk").GetString(), real.GetProperty("state").GetString(), real.GetProperty("verification").GetString()));
                await WaitForCommandAsync(viewer, tenant, agentId, real.GetProperty("command").GetProperty("id").GetGuid(), "Succeeded");
                Assert.False(File.Exists(old));

                await using var audit = database.Context();
                var records = await audit.AuditRecords.AsNoTracking().Where(a => a.Operation.StartsWith("action.request.")).Select(a => new { a.Operation, a.Outcome, a.Metadata }).ToListAsync(Ct);
                Assert.Contains(records, r => r.Operation == "action.request.create" && r.Outcome == AuditOutcome.Failed && r.Metadata["reason"] == "stale_evidence");
                Assert.Contains(records, r => r.Operation == "action.request.approve" && r.Outcome == AuditOutcome.Failed && r.Metadata["reason"] == "precondition_failed");
                Assert.Equal(2, records.Count(r => r.Operation == "action.request.verify"));
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
            }
        }

        /// <summary>A body that doesn't say how long it is, so it goes chunked.</summary>
        private sealed class UnknownLength(byte[] bytes) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        private static byte[] Zip(string name, string text)
        {
            using var buffer = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Phase 8's diagnostic bundle end to end: a real agent makes it from the log its host lists, redacted, and uploads it; anyone sees it's
        /// there, only an operator or admin downloads it (audited), and it's deleted once its retention has passed.
        /// </summary>
        [Fact]
        public async Task A_bundle_is_made_on_the_host_downloaded_by_operators_only_and_dropped_after_its_retention_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki, extra: new Dictionary<string, string> { ["Actions:SweepInterval"] = "00:00:00.200" });
            using var users = host.Users();
            var (viewer, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var logs = TempDirectory();
            Directory.CreateDirectory(logs);
            var log = Path.Combine(logs, "orders.log");
            await File.WriteAllLinesAsync(log, ["boot", "Server=pg;Password=hunter2;Database=orders", "listening on 8080"], Ct);
            var allowlist = Allowlist();
            allowlist.Actions[AgentActions.DiagnosticsBundle] = new ActionPolicy { LogFiles = [log] };
            allowlist.Actions[AgentActions.DiskUsage] = new ActionPolicy();
            var agent = new AgentService(Microsoft.Extensions.Options.Options.Create(Options(host, pki, TempDirectory(), await TokenAsync(admin, tenant))),
                Microsoft.Extensions.Options.Options.Create(allowlist), TimeProvider.System, new TestLog<AgentService>());
            await agent.StartAsync(Ct);
            try
            {
                Guid agentId = default;
                for (var i = 0; i < 100 && agentId == default; i++)
                {
                    await Task.Delay(100, Ct);
                    var list = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents"))).GetProperty("items");
                    if (list.GetArrayLength() > 0) agentId = list[0].GetProperty("id").GetGuid();
                }
                // Low risk: an operator runs it straight away.
                var command = await IssueAsync(@operator, tenant, agentId, AgentActions.DiagnosticsBundle, new { logLines = 50 });
                var done = await WaitForCommandAsync(viewer, tenant, agentId, command, "Succeeded", "Failed", "Rejected");
                Assert.True(done.GetProperty("state").GetString() == "Succeeded", done.GetRawText());
                var summary = done.GetProperty("result");
                var bundleId = summary.GetProperty("bundleId").GetGuid();
                Assert.Equal(1, summary.GetProperty("redacted").GetInt32());

                var listed = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/agents/{agentId}/bundles"))).GetProperty("items");
                Assert.Equal(bundleId, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
                var url = $"/api/v1/tenants/{tenant}/agents/{agentId}/bundles/{bundleId}";
                Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);
                var download = await @operator.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
                var bytes = await download.Content.ReadAsByteArrayAsync(Ct);
                Assert.Equal(summary.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
                {
                    using var reader = new StreamReader(archive.GetEntry("logs/01-orders.log")!.Open());
                    var text = await reader.ReadToEndAsync(Ct);
                    Assert.Contains("Password=[redacted]", text);
                    Assert.DoesNotContain("hunter2", text);
                }

                await using (var db = database.Context())
                {
                    var audit = await db.AuditRecords.AsNoTracking().Where(a => a.Operation.StartsWith("agent.bundle.")).Select(a => new { a.Operation, a.Outcome }).ToListAsync(Ct);
                    Assert.Contains(audit, a => a.Operation == "agent.bundle.upload" && a.Outcome == AuditOutcome.Succeeded);
                    Assert.Contains(audit, a => a.Operation == "agent.bundle.download" && a.Outcome == AuditOutcome.Succeeded);
                    Assert.Contains(audit, a => a.Operation == "agent.bundle.download" && a.Outcome == AuditOutcome.Denied);
                    // Past its retention: gone, not just hidden.
                    await db.AgentBundles.Where(b => b.Id == bundleId).ExecuteUpdateAsync(s => s.SetProperty(b => b.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
                    for (var i = 0; i < 50 && await db.AgentBundles.AnyAsync(b => b.Id == bundleId, Ct); i++) await Task.Delay(100, Ct);
                    Assert.False(await db.AgentBundles.AnyAsync(b => b.Id == bundleId, Ct));
                }
                Assert.Equal(HttpStatusCode.NotFound, (await @operator.GetAsync(url)).StatusCode);
            }
            finally
            {
                await agent.StopAsync(CancellationToken.None);
            }
        }

        /// <summary>
        /// The upload takes a bundle only for a running <c>diagnostics.bundle</c> command of the agent's own, as a zip, within its limit, and once:
        /// the same bytes again are the same bundle, different ones are refused, and so is anything after the command finished.
        /// </summary>
        [Fact]
        public async Task A_bundle_upload_is_bounded_and_belongs_to_one_running_command_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            using var pki = new AgentPki();
            await using var host = new AgentHost(database, pki);
            using var users = host.Users();
            var (_, @operator, admin, tenant) = await Caller.AllAsync(users, database);
            var data = TempDirectory();
            var (store, identity) = await EnrollAsync(host, pki, await TokenAsync(admin, tenant), data);
            using var client = new PlatformClient(Options(host, pki, data), store.LoadCertificate());
            var summaryCommand = await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.HostSummary, new { });
            var bundleCommand = await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.DiagnosticsBundle, new { });
            var failedCommand = await IssueAsync(@operator, tenant, identity.AgentId, AgentActions.DiagnosticsBundle, new { logLines = 20 });
            Assert.Equal(3, (await client.PollAsync(1, "test", "test-host", Ct, identity.CommandKeys.Keys)).Commands.Count);
            // Finished without a bundle: too late to send one.
            await client.ReportAsync(new CommandResult(failedCommand, CommandOutcome.Failed, "timeout", null, 1), Ct);
            Assert.StartsWith("409", (await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(failedCommand, Zip("x", "y"), Ct))).Message);

            var zip = Zip("host.json", "{}");
            await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(summaryCommand, zip, Ct));
            await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(bundleCommand, "not a zip"u8.ToArray(), Ct));
            var oversized = new byte[BundleLimits.MaxBytes + 1];
            (oversized[0], oversized[1]) = ((byte)'P', (byte)'K');
            var tooBig = await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(bundleCommand, oversized, Ct));
            Assert.StartsWith("413", tooBig.Message);
            // No length declared (chunked): the platform still stops reading at the limit.
            using (var chunked = new UnknownLength(oversized))
            {
                Assert.StartsWith("413", (await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(bundleCommand, chunked, Ct))).Message);
            }

            // The same upload several times at once (retries racing the first): one bundle, the same answer to all. The connections are opened first,
            // so the uploads arrive together rather than one TLS handshake apart.
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(summaryCommand, zip, Ct))));
            var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.UploadBundleAsync(bundleCommand, zip, Ct)));
            var id = all[0];
            Assert.All(all, each => Assert.Equal(id, each));
            Assert.Equal(id, await client.UploadBundleAsync(bundleCommand, zip, Ct));
            await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(bundleCommand, Zip("host.json", "{\"other\":1}"), Ct));
            await client.ReportAsync(new CommandResult(bundleCommand, CommandOutcome.Succeeded, null, JsonSerializer.SerializeToElement(new { bundleId = id }), 1), Ct);
            var finished = await Assert.ThrowsAsync<ResultRefusedException>(() => client.UploadBundleAsync(bundleCommand, Zip("x", "y"), Ct));
            Assert.StartsWith("409", finished.Message);
            await using var db = database.Context();
            Assert.Equal(1, await db.AgentBundles.CountAsync(Ct));
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

                // A custom action that changes the host is Medium risk unless it says more: an operator's needs an admin's approval, an admin's runs.
                Assert.Equal(HttpStatusCode.Conflict, await Issue(@operator, "test.restart_worker", new { }));
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
