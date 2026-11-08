using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The Phase 7 gate on the agent's side: an invalid signature, a wrong target, an expired or replayed command, a disallowed action and
    /// out-of-range parameters are refused, each with its own reason; the local allowlist wins over a valid signature; and no shell execution
    /// interface exists.
    /// </summary>
    public class AgentVerifierTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        private static readonly ECDsa PlatformKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly Guid Me = Guid.Parse("01a1aaaa-0000-7000-8000-000000000001");
        private static readonly AgentIdentity Identity = new(Me, "acme", PlatformKey, CommandSignature.KeyId(PlatformKey));

        private static LocalAllowlist Allowlist() => new()
        {
            Actions =
            {
                [AgentActions.HostSummary] = new ActionPolicy(),
                [AgentActions.DiskUsage] = new ActionPolicy { PathPrefixes = ["/data"] },
                [AgentActions.TcpReachability] = new ActionPolicy { Targets = ["db.internal:5432", "*.example.com:443"], MaxTimeoutMs = 3000 },
            },
        };

        private static CommandEnvelope Envelope(string action = AgentActions.HostSummary, object? parameters = null, Guid? agent = null, string tenant = "acme",
            DateTimeOffset? issued = null, TimeSpan? lifetime = null, string? nonce = null, string? key = null, int version = CommandEnvelope.CurrentVersion)
        {
            var at = issued ?? Now;
            return new CommandEnvelope(version, Guid.CreateVersion7(), tenant, agent ?? Me, action, JsonSerializer.SerializeToElement(parameters ?? new { }, CommandSignature.Json),
                at, at + (lifetime ?? TimeSpan.FromMinutes(2)), nonce ?? Token(), key ?? Token(), null);
        }

        private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

        private static SignedCommand Sign(CommandEnvelope envelope, ECDsa? key = null)
        {
            var bytes = CommandSignature.Serialize(envelope);
            var signer = key ?? PlatformKey;
            return new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(signer, bytes), CommandSignature.KeyId(PlatformKey));
        }

        private static string? Reason(SignedCommand command, LocalAllowlist? allowlist = null)
            => CommandVerifier.Verify(command, Identity, allowlist ?? Allowlist(), Now).Rejection;

        [Fact]
        public void A_valid_command_passes_with_its_parameters_typed()
        {
            var verification = CommandVerifier.Verify(Sign(Envelope(AgentActions.TcpReachability, new { host = "db.internal", port = 5432, timeoutMs = 1000 })), Identity, Allowlist(), Now);
            Assert.Null(verification.Rejection);
            Assert.Equal(new TcpReachabilityParameters("db.internal", 5432, 1000), verification.Command!.Parameters);
        }

        [Fact]
        public void An_invalid_signature_is_refused()
        {
            var signed = Sign(Envelope());
            var tampered = Convert.FromBase64String(signed.Payload);
            tampered[^3] ^= 0x01;
            Assert.Equal(RejectionReasons.InvalidSignature, Reason(signed with { Payload = Convert.ToBase64String(tampered) }));
            Assert.Equal(RejectionReasons.InvalidSignature, Reason(signed with { Signature = Convert.ToBase64String(new byte[64]) }));
            Assert.Equal(RejectionReasons.InvalidSignature, Reason(signed with { Signature = "not base64!" }));
        }

        [Fact]
        public void A_command_signed_by_another_key_is_refused_even_if_it_claims_the_platforms_key_id()
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Equal(RejectionReasons.InvalidSignature, Reason(Sign(Envelope(), other)));
            Assert.Equal(RejectionReasons.UnknownKey, Reason(Sign(Envelope()) with { KeyId = CommandSignature.KeyId(other) }));
        }

        [Fact]
        public void A_command_for_another_agent_or_tenant_is_refused()
        {
            Assert.Equal(RejectionReasons.WrongTarget, Reason(Sign(Envelope(agent: Guid.CreateVersion7()))));
            Assert.Equal(RejectionReasons.WrongTenant, Reason(Sign(Envelope(tenant: "other"))));
        }

        [Theory]
        [InlineData(-10, 2, RejectionReasons.Expired)]           // expired 8 minutes ago
        [InlineData(-3, 2, null)]                                // expired a minute ago, inside the clock skew allowed
        [InlineData(-4, 2, RejectionReasons.Expired)]            // expired exactly the skew ago: no longer
        [InlineData(5, 2, RejectionReasons.NotYetValid)]          // issued in the future, beyond the skew
        [InlineData(1, 2, null)]                                 // a little in the future: a clock a minute fast
        [InlineData(0, 15, RejectionReasons.LifetimeTooLong)]     // issued for longer than commands may live
        [InlineData(0, 0, RejectionReasons.LifetimeTooLong)]      // expires when issued
        public void A_command_outside_its_time_window_is_refused(int issuedMinutes, int lifetimeMinutes, string? reason)
            => Assert.Equal(reason, Reason(Sign(Envelope(issued: Now.AddMinutes(issuedMinutes), lifetime: TimeSpan.FromMinutes(lifetimeMinutes)))));

        [Fact]
        public void A_disallowed_action_is_refused_though_the_platform_signed_it()
        {
            var allowlist = Allowlist();
            allowlist.Actions.Remove(AgentActions.HostSummary);
            var signed = Sign(Envelope());
            Assert.True(CommandSignature.Verify(PlatformKey, Convert.FromBase64String(signed.Payload), signed.Signature), "a valid platform signature");
            Assert.Equal(RejectionReasons.ActionNotAllowed, Reason(signed, allowlist));
            Assert.Equal(RejectionReasons.ActionNotAllowed, Reason(signed, new LocalAllowlist()));
        }

        [Fact]
        public void An_unknown_action_is_refused() => Assert.Equal(RejectionReasons.UnknownAction, Reason(Sign(Envelope("shell.exec", new { command = "rm -rf /" }))));

        public static TheoryData<string, object> OutOfRange => new()
        {
            { AgentActions.TcpReachability, new { host = "db.internal", port = 70000, timeoutMs = 1000 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5432, timeoutMs = 50_000 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5432, timeoutMs = 10 } },
            { AgentActions.TcpReachability, new { host = "db internal;rm", port = 5432, timeoutMs = 1000 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5432 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5432, timeoutMs = 1000, shell = "sh" } },
            { AgentActions.DiskUsage, new { pathPrefix = "/data/../etc" } },
            { AgentActions.DiskUsage, new { pathPrefix = "relative/path" } },
            { AgentActions.DiskUsage, new { pathPrefix = "/data", extra = 1 } },
            { AgentActions.HostSummary, new { verbose = true } },
        };

        [Theory]
        [MemberData(nameof(OutOfRange))]
        public void Out_of_range_or_unknown_parameters_are_refused(string action, object parameters)
            => Assert.Equal(RejectionReasons.InvalidParameters, Reason(Sign(Envelope(action, parameters))));

        public static TheoryData<string, object> OutsideLocal => new()
        {
            { AgentActions.TcpReachability, new { host = "10.0.0.5", port = 22, timeoutMs = 1000 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5433, timeoutMs = 1000 } },
            { AgentActions.TcpReachability, new { host = "example.com", port = 443, timeoutMs = 1000 } },
            { AgentActions.TcpReachability, new { host = "db.internal", port = 5432, timeoutMs = 5000 } },
            { AgentActions.DiskUsage, new { pathPrefix = "/database" } },
            { AgentActions.DiskUsage, new { } },
        };

        [Theory]
        [MemberData(nameof(OutsideLocal))]
        public void Valid_parameters_outside_this_hosts_bounds_are_refused(string action, object parameters)
            => Assert.Equal(RejectionReasons.OutsideLocalBounds, Reason(Sign(Envelope(action, parameters))));

        [Theory]
        [InlineData("api.example.com", 443, true)]
        [InlineData("a.b.example.com", 443, true)]
        [InlineData("example.com.evil.net", 443, false)]
        [InlineData("DB.INTERNAL", 5432, true)]
        public void Local_targets_match_by_name_and_port(string host, int port, bool allowed)
            => Assert.Equal(allowed ? null : RejectionReasons.OutsideLocalBounds, Reason(Sign(Envelope(AgentActions.TcpReachability, new { host, port, timeoutMs = 1000 }))));

        [Fact]
        public void A_malformed_or_unknown_version_command_is_refused()
        {
            Assert.Equal(RejectionReasons.Malformed, Reason(new SignedCommand("%%%", "x", Identity.CommandKeyId)));
            var junk = Encoding.UTF8.GetBytes("{\"commandId\":\"01a1aaaa-0000-7000-8000-000000000002\",\"what\":1}");
            Assert.Equal(RejectionReasons.Malformed, Reason(new SignedCommand(Convert.ToBase64String(junk), CommandSignature.Sign(PlatformKey, junk), Identity.CommandKeyId)));
            Assert.Equal(RejectionReasons.UnsupportedVersion, Reason(Sign(Envelope(version: 2))));
            Assert.Equal(RejectionReasons.Malformed, Reason(Sign(Envelope(nonce: "short"))));
            var huge = new byte[CommandLimits.MaxPayloadBytes + 1];
            Assert.Equal(RejectionReasons.Malformed, Reason(new SignedCommand(Convert.ToBase64String(huge), CommandSignature.Sign(PlatformKey, huge), Identity.CommandKeyId)));
        }

        // ─── Replay and idempotency ─────────────────────────────────────────────

        private static AgentService Service(LocalAllowlist? allowlist = null, DateTimeOffset? now = null)
            => new(Options.Create(new AgentOptions()), Options.Create(allowlist ?? Allowlist()), new FixedClock(now ?? Now), NullLogger<AgentService>.Instance);

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static readonly Diagnostics Diagnostics = new("/", "test");

        [Fact]
        public async Task A_replayed_command_is_refused_and_a_redelivered_one_is_answered_without_running_again_Async()
        {
            var ct = TestContext.Current.CancellationToken;
            var memory = new CommandMemory(null);
            var agent = Service();
            var envelope = Envelope();
            var first = await agent.HandleAsync(Sign(envelope), Identity, memory, Diagnostics, ct);
            Assert.Equal(CommandOutcome.Succeeded, first!.Outcome);

            // The same signed command again: the platform redelivered it. Same answer, not run twice.
            Assert.Equal(first, await agent.HandleAsync(Sign(envelope), Identity, memory, Diagnostics, ct));

            // A new command reusing that nonce: someone replaying it.
            var replayed = await agent.HandleAsync(Sign(Envelope(nonce: envelope.Nonce)), Identity, memory, Diagnostics, ct);
            Assert.Equal((CommandOutcome.Rejected, RejectionReasons.Replayed), (replayed!.Outcome, replayed.Reason));
        }

        [Fact]
        public async Task A_command_with_an_idempotency_key_already_done_gets_that_result_Async()
        {
            var ct = TestContext.Current.CancellationToken;
            var memory = new CommandMemory(null);
            var agent = Service();
            var first = Envelope(key: "same-key-for-both-commands");
            var done = await agent.HandleAsync(Sign(first), Identity, memory, Diagnostics, ct);
            var second = Envelope(key: "same-key-for-both-commands");
            var again = await agent.HandleAsync(Sign(second), Identity, memory, Diagnostics, ct);
            Assert.Equal((second.CommandId, done!.Outcome, done.Data!.Value.GetRawText()), (again!.CommandId, again.Outcome, again.Data!.Value.GetRawText()));
        }

        [Fact]
        public void The_agent_remembers_nonces_across_a_restart_until_they_could_no_longer_be_valid()
        {
            var file = Path.Combine(Path.GetTempPath(), $"juice-agent-{Guid.NewGuid():N}.json");
            try
            {
                var envelope = Envelope();
                new CommandMemory(file).Record(envelope);
                var restarted = new CommandMemory(file);
                Assert.Equal(Recall.Replayed, restarted.Recall(Envelope(nonce: envelope.Nonce), Now).Recall);
                // Past expiry and skew the nonce is forgotten, and the verifier refuses any command that old anyway.
                Assert.Equal(Recall.New, restarted.Recall(Envelope(nonce: envelope.Nonce), envelope.ExpiresAt + CommandLimits.ClockSkew).Recall);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public async Task A_refused_command_is_reported_with_its_reason_and_never_run_Async()
        {
            var memory = new CommandMemory(null);
            var envelope = Envelope(AgentActions.TcpReachability, new { host = "10.0.0.5", port = 22, timeoutMs = 1000 });
            var result = await Service().HandleAsync(Sign(envelope), Identity, memory, Diagnostics, TestContext.Current.CancellationToken);
            Assert.Equal((envelope.CommandId, CommandOutcome.Rejected, RejectionReasons.OutsideLocalBounds), (result!.CommandId, result.Outcome, result.Reason));
            Assert.Null(result.Data);
            Assert.Equal(Recall.New, memory.Recall(envelope, Now).Recall);
        }

        // ─── No shell ───────────────────────────────────────────────────────────

        /// <summary>
        /// Nothing in the agent or its contracts can start a process, run a shell, or load code a command names. Checked on the source, so a
        /// change that adds one fails here and has to be argued for.
        /// </summary>
        [Fact]
        public void The_agent_has_no_way_to_run_a_command()
        {
            var root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "Juice.Observability.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repository root");
            string[] forbidden = ["Process.Start", "ProcessStartInfo", "new Process(", "/bin/sh", "/bin/bash", "cmd.exe", "powershell", "Assembly.Load",
                "Activator.CreateInstance", "Type.GetType(", "AppDomain", "DllImport", "LibraryImport", "NativeLibrary", "Marshal.GetDelegateForFunctionPointer"];
            var offences = new[] { "Juice.Observability.Agent", "Juice.Observability.Agent.Contracts" }
                .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
                .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, i, line)))
                .Where(x => forbidden.Any(f => x.line.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}: {x.line.Trim()}").ToList();
            Assert.True(offences.Count == 0, string.Join("\n", offences));
        }

        /// <summary>
        /// The launcher has to start a process: the agent version it runs. It may do that in one file only, with a fixed program and arguments, and
        /// nothing in it may run a shell, load code or call native code. Everything else in the launcher is held to the agent's rule.
        /// </summary>
        [Fact]
        public void The_launcher_starts_only_the_agent_and_only_in_one_place()
        {
            var root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "Juice.Observability.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("no repository root");
            const string allowedFile = "AgentProcess.cs";
            string[] starts = ["Process.Start", "ProcessStartInfo", "new Process("];
            string[] never = ["/bin/sh", "/bin/bash", "cmd.exe", "powershell", "Assembly.Load", "Activator.CreateInstance", "Type.GetType(", "AppDomain", "DllImport",
                "LibraryImport", "NativeLibrary", "Marshal.GetDelegateForFunctionPointer", "UseShellExecute = true", "Arguments ="];
            var lines = Directory.EnumerateFiles(Path.Combine(root, "src", "Juice.Observability.Agent.Launcher"), "*.cs", SearchOption.AllDirectories)
                .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, i, line))).ToList();
            var offences = lines
                .Where(x => never.Any(f => x.line.Contains(f, StringComparison.OrdinalIgnoreCase))
                    || (Path.GetFileName(x.file) != allowedFile && starts.Any(f => x.line.Contains(f, StringComparison.OrdinalIgnoreCase))))
                .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}: {x.line.Trim()}").ToList();
            Assert.True(offences.Count == 0, string.Join("\n", offences));
            // The one place: a single start, no shell, the entry point as the only argument.
            var allowed = lines.Where(x => Path.GetFileName(x.file) == allowedFile).Select(x => x.line).ToList();
            Assert.Single(allowed, l => l.Contains("Process.Start(", StringComparison.Ordinal));
            Assert.Contains(allowed, l => l.Contains("UseShellExecute = false", StringComparison.Ordinal));
            Assert.Single(allowed, l => l.Contains("ArgumentList.Add(", StringComparison.Ordinal));
        }
    }
}
