using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Juice.Observability.Agent;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    public sealed record QueueParameters(
        [property: MaxLength(32), AllowedValues("orders", "billing", "audit"), Description("Which queue")] string Queue,
        [property: Range(1, 100), DefaultValue(10)] int Top,
        bool? Verbose = null);

    /// <summary>A customer's read-only action, as a test stands it in.</summary>
    public sealed class QueueDepth : AgentAction<QueueParameters>
    {
        public override string Name => "test.queue_depth";
        public override string Description => "Messages waiting in one of the shop's queues.";

        protected override Task<object?> RunAsync(QueueParameters parameters, CancellationToken cancellationToken)
            => Task.FromResult<object?>(new { queue = parameters.Queue, depth = 42, top = parameters.Top });
    }

    /// <summary>One that changes the host: only admins may issue it.</summary>
    public sealed class Restart : AgentAction<NoParameters>
    {
        public override string Name => "test.restart_worker";
        public override string Description => "Restarts the shop's worker.";
        public override bool Changing => true;

        protected override Task<object?> RunAsync(NoParameters parameters, CancellationToken cancellationToken) => Task.FromResult<object?>(new { restarted = true });
    }

    /// <summary>
    /// Custom actions (Phase 8): a customer's build adds typed actions; the agent checks their parameters as strictly as a built-in's, its allowlist
    /// still decides, and it reports what it can run with its host's bounds. Nothing about them reaches a shell: they're compiled code.
    /// </summary>
    public class AgentCustomActionTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
        private static readonly ECDsa PlatformKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private static readonly Guid Me = Guid.CreateVersion7();
        private static readonly AgentIdentity Identity = new(Me, "acme", PlatformKey, CommandSignature.KeyId(PlatformKey));
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class Failing : AgentAction<NoParameters>
        {
            public override string Name => "test.failing";
            public override string Description => "Fails with its own reason.";
            protected override Task<object?> RunAsync(NoParameters parameters, CancellationToken cancellationToken) => throw new ActionFailedException("queue_unreachable");
        }

        /// <summary>Ignores its cancellation: the agent stops waiting at the timeout anyway.</summary>
        private sealed class Hanging : AgentAction<NoParameters>
        {
            public override string Name => "test.hanging";
            public override string Description => "Never returns.";
            public override TimeSpan Timeout => TimeSpan.FromMilliseconds(200);
            protected override async Task<object?> RunAsync(NoParameters parameters, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout * 50, CancellationToken.None);
                return null;
            }
        }

        private sealed class Named(string name, Type? parameters = null) : IAgentAction
        {
            public ActionDescriptor Descriptor => new(name, "x", false, parameters is null ? [] : ParameterSchema.Describe(parameters), true);
            public TimeSpan Timeout => TimeSpan.FromSeconds(1);
            public object Bind(JsonElement parameters) => new NoParameters();
            public Task<object?> RunAsync(object parameters, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
        }

        private sealed record NoLength(string Text);

        private sealed record Unsupported(double Ratio);

        private static readonly AgentActionCatalog Catalog = new([new QueueDepth(), new Restart(), new Failing(), new Hanging()]);

        private static LocalAllowlist Allowlist(params string[] queues) => new()
        {
            Actions =
            {
                [AgentActions.HostSummary] = new ActionPolicy(),
                [AgentActions.TcpReachability] = new ActionPolicy { Targets = ["db:5432"], MaxTimeoutMs = 3000 },
                ["test.queue_depth"] = queues.Length == 0 ? new ActionPolicy() : new ActionPolicy { Values = { ["queue"] = [.. queues] } },
                ["test.failing"] = new ActionPolicy(),
                ["test.hanging"] = new ActionPolicy(),
            },
        };

        private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

        private static SignedCommand Sign(string action, object parameters)
        {
            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", Me, action, JsonSerializer.SerializeToElement(parameters, CommandSignature.Json),
                Now, Now + TimeSpan.FromMinutes(2), Token(), Token(), null);
            var bytes = CommandSignature.Serialize(envelope);
            return new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(PlatformKey, bytes), CommandSignature.KeyId(PlatformKey));
        }

        private static Verification Verify(string action, object parameters, LocalAllowlist? allowlist = null, AgentActionCatalog? catalog = null)
            => CommandVerifier.Verify(Sign(action, parameters), Identity, allowlist ?? Allowlist(), Now, catalog ?? Catalog);

        [Fact]
        public void A_parameter_record_becomes_a_bounded_schema()
        {
            var schema = ParameterSchema.Describe(typeof(QueueParameters));
            Assert.Equal(new ActionParameter("queue", ParameterTypes.String, true, "Which queue", MaxLength: 32), schema[0] with { Allowed = null });
            Assert.Equal(["orders", "billing", "audit"], schema[0].Allowed!);
            Assert.Equal(new ActionParameter("top", ParameterTypes.Integer, true, null, 1, 100, Default: "10"), schema[1]);
            Assert.Equal(new ActionParameter("verbose", ParameterTypes.Boolean, false), schema[2]);
            Assert.Empty(ParameterSchema.Describe(typeof(NoParameters)));
        }

        [Fact]
        public void Custom_actions_that_dont_hold_stop_the_agent_at_start()
        {
            static string Problem(params IAgentAction[] actions) => Assert.Throws<InvalidOperationException>(() => new AgentActionCatalog(actions)).Message;

            Assert.Contains("built-in action's name", Problem(new Named(AgentActions.DiskUsage)));
            Assert.Contains("added twice", Problem(new QueueDepth(), new QueueDepth()));
            Assert.Contains("lowercase words joined by dots", Problem(new Named("NoDots")));
            Assert.Contains("needs [MaxLength]", Problem(new Named("test.text", typeof(NoLength))));
            Assert.Contains("not Double", Problem(new Named("test.ratio", typeof(Unsupported))));
            Assert.Equal([.. AgentActions.All, "test.queue_depth", "test.restart_worker", "test.failing", "test.hanging"], Catalog.Descriptors.Select(d => d.Name));
        }

        [Fact]
        public void Parameters_are_checked_against_the_schema_strictly()
        {
            var schema = ParameterSchema.Describe(typeof(QueueParameters));
            string? Check(object values) => ActionSchema.Validate(schema, JsonSerializer.SerializeToElement(values));

            Assert.Null(Check(new { queue = "orders", top = 5 }));
            Assert.Null(Check(new { queue = "orders", top = 5, verbose = true }));
            Assert.Equal("unknown parameter 'command'", Check(new { queue = "orders", top = 5, command = "rm -rf /" }));
            Assert.Equal("queue is required", Check(new { top = 5 }));
            Assert.Equal("top is a whole number", Check(new { queue = "orders", top = "5" }));
            Assert.Equal("top is 1 to 100", Check(new { queue = "orders", top = 500 }));
            Assert.Equal("queue is one of: orders, billing, audit", Check(new { queue = "payroll", top = 5 }));
            Assert.Equal("verbose is true or false", Check(new { queue = "orders", top = 5, verbose = "yes" }));
            Assert.Equal("parameters must be an object", ActionSchema.Validate(schema, JsonSerializer.SerializeToElement(new[] { 1 })));
            var pattern = new[] { new ActionParameter("name", ParameterTypes.String, true, MaxLength: 10, Pattern: "^[a-z]+$") };
            Assert.Equal("name doesn't have the expected form", ActionSchema.Validate(pattern, JsonSerializer.SerializeToElement(new { name = "Abc" })));
            Assert.Equal("name is at most 10 characters", ActionSchema.Validate(pattern, JsonSerializer.SerializeToElement(new { name = new string('a', 11) })));
        }

        [Fact]
        public void A_custom_command_is_verified_typed_and_still_bound_by_the_local_allowlist()
        {
            var ok = Verify("test.queue_depth", new { queue = "orders", top = 5 });
            Assert.Null(ok.Rejection);
            var invocation = Assert.IsType<CustomInvocation>(ok.Command!.Parameters);
            Assert.Equal(new QueueParameters("orders", 5), invocation.Parameters);

            Assert.Equal(RejectionReasons.UnknownAction, Verify("test.queue_depth", new { queue = "orders", top = 5 }, catalog: AgentActionCatalog.BuiltIn).Rejection);
            Assert.Equal(RejectionReasons.InvalidParameters, Verify("test.queue_depth", new { queue = "orders", top = 5, extra = 1 }).Rejection);
            Assert.Equal(RejectionReasons.ActionNotAllowed, Verify("test.restart_worker", new { }).Rejection);
            // The host narrows it to one queue: another valid one is outside its bounds, and so is leaving it out.
            Assert.Null(Verify("test.queue_depth", new { queue = "orders", top = 5 }, Allowlist("orders")).Rejection);
            Assert.Equal(RejectionReasons.OutsideLocalBounds, Verify("test.queue_depth", new { queue = "billing", top = 5 }, Allowlist("orders")).Rejection);
        }

        [Fact]
        public void A_host_that_requires_approval_refuses_a_command_without_an_approval_reference()
        {
            var allowlist = Allowlist();
            allowlist.Actions["test.restart_worker"] = new ActionPolicy { RequireApproval = true };
            Assert.Equal(RejectionReasons.ApprovalRequired, Verify("test.restart_worker", new { }, allowlist).Rejection);

            var envelope = new CommandEnvelope(CommandEnvelope.CurrentVersion, Guid.CreateVersion7(), "acme", Me, "test.restart_worker", JsonSerializer.SerializeToElement(new { }),
                Now, Now + TimeSpan.FromMinutes(2), Token(), Token(), "action-request:01;approved-by:adam");
            var bytes = CommandSignature.Serialize(envelope);
            var signed = new SignedCommand(Convert.ToBase64String(bytes), CommandSignature.Sign(PlatformKey, bytes), CommandSignature.KeyId(PlatformKey));
            Assert.Null(CommandVerifier.Verify(signed, Identity, allowlist, Now, Catalog).Rejection);
            Assert.Contains("runs only from an approved request", allowlist.Report(Catalog).Actions.Single(a => a.Action.Name == "test.restart_worker").Limits);
            Assert.Contains(new ActionsReport([allowlist.Report(Catalog).Actions.Single(a => a.Action.Name == "test.restart_worker") is var r
                ? r with { Action = r.Action with { Risk = ActionRisks.Low } } : null!]).Validate(), p => p.Contains("Medium or High"));
        }

        private static AgentService Service() => new(Options.Create(new AgentOptions()), Options.Create(Allowlist()), TimeProvider.System, NullLogger<AgentService>.Instance,
            actions: Catalog);

        private static Task<CommandResult?> HandleAsync(string action, object parameters)
            => Service().HandleAsync(Sign(action, parameters), Identity, new CommandMemory(null), new Diagnostics("/", "test"), Ct);

        [Fact]
        public async Task A_custom_action_runs_and_reports_its_data_its_own_failure_or_its_timeout_Async()
        {
            var done = (await HandleAsync("test.queue_depth", new { queue = "audit", top = 3 }))!;
            Assert.Equal(CommandOutcome.Succeeded, done.Outcome);
            Assert.Equal("""{"queue":"audit","depth":42,"top":3}""", done.Data!.Value.GetRawText());

            var failed = (await HandleAsync("test.failing", new { }))!;
            Assert.Equal((CommandOutcome.Failed, "queue_unreachable"), (failed.Outcome, failed.Reason));

            var started = DateTimeOffset.UtcNow;
            var hung = (await HandleAsync("test.hanging", new { }))!;
            Assert.Equal((CommandOutcome.Failed, "timeout"), (hung.Outcome, hung.Reason));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5), "the agent waited for an action that ignored its timeout");
        }

        [Fact]
        public void The_report_says_what_this_host_permits_with_its_bounds()
        {
            var report = Allowlist("orders", "billing").Report(Catalog);
            Assert.Empty(report.Validate());
            var byName = report.Actions.ToDictionary(a => a.Action.Name);
            Assert.Equal([.. AgentActions.All, "test.queue_depth", "test.restart_worker", "test.failing", "test.hanging"], report.Actions.Select(a => a.Action.Name));

            Assert.False(byName[AgentActions.DiskUsage].Permitted);
            Assert.Empty(byName[AgentActions.DiskUsage].Limits);
            Assert.False(byName["test.restart_worker"].Permitted);
            Assert.True(byName["test.restart_worker"].Action is { Custom: true, Changing: true });

            var tcp = byName[AgentActions.TcpReachability];
            Assert.True(tcp.Permitted);
            Assert.Equal(["targets: db:5432", "timeoutMs at most 3000"], tcp.Limits);
            Assert.Equal((3000L, "2000"), (tcp.Action.Parameters.Single(p => p.Name == "timeoutMs").Maximum!.Value, tcp.Action.Parameters.Single(p => p.Name == "timeoutMs").Default!));

            // The host's values, within the action's own.
            var queue = byName["test.queue_depth"];
            Assert.Equal(["orders", "billing"], queue.Action.Parameters.Single(p => p.Name == "queue").Allowed!);
            Assert.Equal(["queue: one of orders, billing"], queue.Limits);
            Assert.Equal(["orders"], Allowlist("orders", "payroll").Report(Catalog).Actions.Single(a => a.Action.Name == "test.queue_depth").Action.Parameters[0].Allowed!);
        }

        [Fact]
        public void A_report_that_doesnt_hold_is_refused_whole()
        {
            var valid = Allowlist().Report(Catalog);
            Assert.Empty(valid.Validate());
            var custom = valid.Actions.Single(a => a.Action.Name == "test.queue_depth");
            Assert.Contains(new ActionsReport([custom with { Action = custom.Action with { Name = AgentActions.HostSummary } }]).Validate(), p => p.Contains("built-in action's name"));
            Assert.Contains(new ActionsReport([custom, custom]).Validate(), p => p.Contains("reported twice"));
            Assert.Contains(new ActionsReport([.. Enumerable.Range(0, ActionsReport.MaxActions + 1).Select(i => custom with { Action = custom.Action with { Name = $"test.a{i}" } })]).Validate(),
                p => p.Contains("at most"));
            var badPattern = custom with { Action = custom.Action with { Parameters = [new ActionParameter("x", ParameterTypes.String, true, MaxLength: 5, Pattern: "([")] } };
            Assert.Contains(new ActionsReport([badPattern]).Validate(), p => p.Contains("valid regular expression"));
            var unbounded = custom with { Action = custom.Action with { Parameters = [new ActionParameter("x", ParameterTypes.Integer, true)] } };
            Assert.Contains(new ActionsReport([unbounded]).Validate(), p => p.Contains("minimum and a maximum"));
        }
    }
}
