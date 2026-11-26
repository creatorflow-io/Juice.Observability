using System.Text.Json;
using Juice.Notifications;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health.DeadMan;
using Juice.Observability.Health.Delivery;
using Juice.Observability.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The dead-man alarm (phase 5, M49): each cause, the rate limit, the resolve, the signature, the SSRF refusal and the options check. The
    /// pipeline's health is a fake here; the alarm posts to a real receiver on this machine's private address, through the SSRF-checked client.
    /// </summary>
    public class DeadManSenderTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string SecretVariable = "JUICE_DEADMAN_TEST";

        public DeadManSenderTests() => Environment.SetEnvironmentVariable(SecretVariable, "dead-man-secret");

        private sealed class Clock(DateTimeOffset start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = start;
            public override DateTimeOffset GetUtcNow() => Now;
            public void Advance(TimeSpan by) => Now += by;
        }

        /// <summary>The pipeline's health as the test sets it: a reading, an error, or a read that never answers.</summary>
        private sealed class FakeHealth : INotificationsPipelineHealth
        {
            public PipelineHealth Health { get; set; } = Reading();
            public Exception? Error { get; set; }
            public bool Hangs { get; set; }

            public async ValueTask<PipelineHealth> ReadAsync(CancellationToken cancellationToken = default)
            {
                if (Hangs) await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
                return Error is { } error ? throw error : Health;
            }
        }

        private sealed class FakeShadow : IShadowReport
        {
            public string? Failing { get; set; }
            public ValueTask<string?> FailingAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Failing);
        }

        private static PipelineHealth Reading(TimeSpan? dispatcherDue = null, int dispatcherFailing = 0, SendHealth? sends = null) => new()
        {
            Running = true,
            Sends = sends ?? new SendHealth { Succeeded = 3 },
            OldestDueAny = dispatcherDue,
            Stages = new Dictionary<string, StageHealth>
            {
                ["planner"] = new() { LastPassEndedAt = DateTimeOffset.UtcNow, LastProgressAt = DateTimeOffset.UtcNow },
                ["dispatcher"] = new() { OldestDue = dispatcherDue, Failing = dispatcherFailing, LastPassEndedAt = DateTimeOffset.UtcNow },
            },
        };

        private sealed record Setup(DeadManSender Sender, FakeHealth Health, FakeShadow Shadow, Clock Clock, WebhookTests.Receiver Receiver) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                Sender.Dispose();
                await Receiver.DisposeAsync();
            }

            public List<JsonElement> Posted() => [.. Receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.Clone())];
        }

        private static async Task<Setup> SetupAsync(Action<DeadManOptions>? configure = null, bool allowlisted = true)
        {
            var receiver = await WebhookTests.Receiver.StartAsync();
            var options = new DeadManOptions
            {
                Url = receiver.Url, SecretRef = $"env:{SecretVariable}", AllowHttpToAllowlisted = true, ReadTimeout = TimeSpan.FromMilliseconds(300),
                Allowlist = allowlisted ? [new DeadManDestination { Destination = $"{receiver.Address}/32", Ports = [receiver.Port] }] : [],
            };
            configure?.Invoke(options);
            var health = new FakeHealth();
            var shadow = new FakeShadow();
            var clock = new Clock(DateTimeOffset.UtcNow);
            var services = new ServiceCollection().AddSingleton<INotificationsPipelineHealth>(health).BuildServiceProvider();
            var sender = new DeadManSender(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
                Options.Create(new PlatformOptions { TenantKey = LocalStack.TenantKey }), clock, NullLogger<DeadManSender>.Instance, shadow);
            return new Setup(sender, health, shadow, clock, receiver);
        }

        [Fact]
        public async Task A_quiet_pipeline_raises_nothing_Async()
        {
            await using var setup = await SetupAsync();
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
            Assert.Empty(setup.Sender.Status.Causes);
        }

        [Fact]
        public async Task A_stalled_stage_is_posted_signed_at_most_once_per_resend_time_and_resolved_when_it_clears_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(dispatcherDue: TimeSpan.FromMinutes(6));

            await setup.Sender.RunOnceAsync(Ct);
            var firing = Assert.Single(setup.Posted());
            Assert.Equal(("observability.deadman.firing", DeadManCauses.Stalled), (firing.GetProperty("type").GetString(), firing.GetProperty("cause").GetString()));
            Assert.Contains("dispatcher", firing.GetProperty("detail").GetString());
            Assert.Equal(LocalStack.TenantKey, firing.GetProperty("tenant").GetString());
            // Pass and progress times ride along for diagnosis.
            Assert.True(firing.GetProperty("pipeline").GetProperty("stages").GetProperty("planner").TryGetProperty("lastProgressAt", out _));
            var received = setup.Receiver.Requests.Single();
            Assert.True(WebhookSigner.Verify("dead-man-secret", received.Id, received.Timestamp, received.Signature, received.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
            Assert.Single(setup.Sender.Status.Causes);

            // Still stalled a minute on: not again.
            setup.Clock.Advance(TimeSpan.FromMinutes(1));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Single(setup.Posted());
            // Still stalled after the resend time: once more.
            setup.Clock.Advance(TimeSpan.FromMinutes(15));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(2, setup.Posted().Count);

            setup.Health.Health = Reading(dispatcherDue: TimeSpan.FromSeconds(10));
            await setup.Sender.RunOnceAsync(Ct);
            var resolved = setup.Posted()[^1];
            Assert.Equal(("observability.deadman.resolved", DeadManCauses.Stalled), (resolved.GetProperty("type").GetString(), resolved.GetProperty("cause").GetString()));
            Assert.Empty(setup.Sender.Status.Causes);
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(3, setup.Posted().Count);
        }

        [Fact]
        public async Task Work_due_under_the_threshold_is_a_slow_pipeline_not_a_stalled_one_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(dispatcherDue: TimeSpan.FromMinutes(4));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
        }

        [Fact]
        public async Task Failing_rows_raise_it_only_on_two_reads_far_enough_apart_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(dispatcherFailing: 12);

            await setup.Sender.RunOnceAsync(Ct);
            setup.Clock.Advance(TimeSpan.FromMinutes(5));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);

            setup.Clock.Advance(TimeSpan.FromMinutes(5));
            await setup.Sender.RunOnceAsync(Ct);
            var firing = Assert.Single(setup.Posted());
            Assert.Equal(DeadManCauses.Failing, firing.GetProperty("cause").GetString());

            setup.Health.Health = Reading(dispatcherFailing: 2);
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal("observability.deadman.resolved", setup.Posted()[^1].GetProperty("type").GetString());
        }

        [Fact]
        public async Task A_burst_of_failures_that_clears_between_reads_starts_over_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(dispatcherFailing: 12);
            await setup.Sender.RunOnceAsync(Ct);
            setup.Clock.Advance(TimeSpan.FromMinutes(6));
            setup.Health.Health = Reading(dispatcherFailing: 0);
            await setup.Sender.RunOnceAsync(Ct);
            setup.Clock.Advance(TimeSpan.FromMinutes(6));
            setup.Health.Health = Reading(dispatcherFailing: 12);
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
        }

        [Fact]
        public async Task A_read_that_throws_or_takes_too_long_is_a_cause_and_leaves_the_others_as_they_were_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(dispatcherDue: TimeSpan.FromMinutes(6));
            await setup.Sender.RunOnceAsync(Ct);

            setup.Health.Error = new InvalidOperationException("relation \"notifications.deliveries\" does not exist");
            await setup.Sender.RunOnceAsync(Ct);
            var unreadable = setup.Posted()[^1];
            Assert.Equal(DeadManCauses.Unreadable, unreadable.GetProperty("cause").GetString());
            Assert.Contains("does not exist", unreadable.GetProperty("detail").GetString());
            // Unread, the stall is unknown: not resolved.
            Assert.DoesNotContain(setup.Posted(), p => p.GetProperty("type").GetString() == "observability.deadman.resolved");
            Assert.Equal(2, setup.Sender.Status.Causes.Count);

            setup.Health.Error = null;
            setup.Health.Hangs = true;
            setup.Clock.Advance(TimeSpan.FromMinutes(20));
            var started = DateTimeOffset.UtcNow;
            await setup.Sender.RunOnceAsync(Ct);
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5), "a read that never answers is cut at the read timeout");
            Assert.Contains("no answer", setup.Posted().Last(p => p.GetProperty("cause").GetString() == DeadManCauses.Unreadable).GetProperty("detail").GetString());
        }

        [Fact]
        public async Task Nothing_getting_out_is_raised_on_two_reads_apart_rate_limited_and_resolved_by_one_success_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(sends: new SendHealth { Window = TimeSpan.FromMinutes(15), Failed = 6, Succeeded = 0 });

            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
            setup.Clock.Advance(TimeSpan.FromMinutes(10));
            await setup.Sender.RunOnceAsync(Ct);
            var firing = Assert.Single(setup.Posted());
            Assert.Equal(DeadManCauses.NoSuccess, firing.GetProperty("cause").GetString());
            Assert.Equal(6, firing.GetProperty("pipeline").GetProperty("sends").GetProperty("failed").GetInt32());
            setup.Clock.Advance(TimeSpan.FromMinutes(1));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Single(setup.Posted());

            // One send got out: something is.
            setup.Health.Health = Reading(sends: new SendHealth { Window = TimeSpan.FromMinutes(15), Failed = 6, Succeeded = 1 });
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(("observability.deadman.resolved", DeadManCauses.NoSuccess),
                (setup.Posted()[^1].GetProperty("type").GetString(), setup.Posted()[^1].GetProperty("cause").GetString()));
        }

        [Fact]
        public async Task Too_few_failures_or_a_quiet_window_is_not_an_outage_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(sends: new SendHealth { Failed = 4, Succeeded = 0 });
            await setup.Sender.RunOnceAsync(Ct);
            setup.Clock.Advance(TimeSpan.FromMinutes(11));
            await setup.Sender.RunOnceAsync(Ct);
            setup.Health.Health = Reading(sends: new SendHealth());
            setup.Clock.Advance(TimeSpan.FromMinutes(11));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
        }

        [Fact]
        public async Task A_delivery_stuck_in_retry_is_raised_rate_limited_and_resolved_Async()
        {
            await using var setup = await SetupAsync();
            setup.Health.Health = Reading(sends: new SendHealth { Succeeded = 1, OldestUndelivered = TimeSpan.FromMinutes(29) });
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);

            setup.Health.Health = Reading(sends: new SendHealth { Succeeded = 1, OldestUndelivered = TimeSpan.FromMinutes(31) });
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(DeadManCauses.StuckInRetry, Assert.Single(setup.Posted()).GetProperty("cause").GetString());
            setup.Clock.Advance(TimeSpan.FromMinutes(5));
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Single(setup.Posted());

            setup.Health.Health = Reading(sends: new SendHealth { Succeeded = 1 });
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(("observability.deadman.resolved", DeadManCauses.StuckInRetry),
                (setup.Posted()[^1].GetProperty("type").GetString(), setup.Posted()[^1].GetProperty("cause").GetString()));
        }

        [Fact]
        public async Task A_failing_shadow_comparison_is_a_cause_Async()
        {
            await using var setup = await SetupAsync();
            setup.Shadow.Failing = "12 differences in the last hour";
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Equal(DeadManCauses.Shadow, Assert.Single(setup.Posted()).GetProperty("cause").GetString());
        }

        [Fact]
        public async Task A_private_endpoint_that_isnt_allowlisted_is_refused_and_tried_again_Async()
        {
            await using var setup = await SetupAsync(allowlisted: false);
            setup.Health.Health = Reading(dispatcherDue: TimeSpan.FromMinutes(6));

            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
            // Not sent, so not rate-limited: the next pass tries again (and is refused again).
            Assert.Single(setup.Sender.Status.Causes);
            await setup.Sender.RunOnceAsync(Ct);
            Assert.Empty(setup.Receiver.Requests);
        }

        [Fact]
        public async Task A_pipeline_that_didnt_start_raises_the_alarm_in_the_real_host_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var connection = new Npgsql.NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var block = new Npgsql.NpgsqlCommand("CREATE SCHEMA notifications; CREATE TABLE notifications.applications (unrelated int);", connection);
                await block.ExecuteNonQueryAsync(Ct);
            }
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = "new", ["Notifications:DeadMan:Url"] = receiver.Url, ["Notifications:DeadMan:SecretRef"] = $"env:{SecretVariable}",
                ["Notifications:DeadMan:AllowHttpToAllowlisted"] = "true", ["Notifications:DeadMan:Interval"] = "00:00:01",
                ["Notifications:DeadMan:Allowlist:0:Destination"] = $"{receiver.Address}/32", ["Notifications:DeadMan:Allowlist:0:Ports:0"] = receiver.Port.ToString(),
            });
            _ = factory.Server;

            for (var i = 0; i < 100 && receiver.Requests.IsEmpty; i++) await Task.Delay(100, Ct);

            // The pipeline's own services were never started; the alarm, which doesn't depend on them, was.
            var alarm = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
            Assert.Equal((DeadManCauses.Unreadable, "observability.deadman.firing"), (alarm.GetProperty("cause").GetString(), alarm.GetProperty("type").GetString()));
        }

        [Theory]
        [InlineData(1, 1, "at least 00:02:00")]
        [InlineData(3, 120, "at least 00:04:00")]
        public void The_options_refuse_a_threshold_below_its_minimums(int dueMinutes, int pollSeconds, string expected)
        {
            var options = new DeadManOptions { Url = "https://alarm.example/hook", SecretRef = "env:X", DueThreshold = TimeSpan.FromMinutes(dueMinutes) };
            Assert.Contains(options.Problems(TimeSpan.FromSeconds(pollSeconds)), p => p.Contains(expected));
            Assert.Empty(new DeadManOptions { Url = "https://alarm.example/hook", SecretRef = "env:X" }.Problems(TimeSpan.FromSeconds(1)));
        }

        private static IServiceCollection Register(string pipeline, Dictionary<string, string?>? extra = null)
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=x", ["Platform:TenantKey"] = LocalStack.TenantKey, ["Notifications:Pipeline"] = pipeline,
                ["Notifications:DeadMan:Url"] = "https://alarm.example/hook", ["Notifications:DeadMan:SecretRef"] = "env:X",
            };
            foreach (var (key, value) in extra ?? []) values[key] = value;
            var services = new ServiceCollection();
            services.AddLogging();
            Api.PlatformExtensions.AddPlatformMessaging(services);
            return services.AddPlatformNotifications(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }

        [Fact]
        public void It_runs_beside_the_pipeline_whatever_the_pipeline_does_and_never_in_legacy()
        {
            var services = Register("new");
            Assert.Contains(services, s => s.ServiceType == typeof(DeadManSender));
            // Registered after the gating of the pipeline's services: a failed pipeline start doesn't stop it.
            var hosted = services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
            Assert.Contains(hosted, h => h.ImplementationFactory?.Method.DeclaringType?.DeclaringType == typeof(NotificationsRegistration)
                || h.ImplementationFactory?.Method.DeclaringType == typeof(NotificationsRegistration));
            Assert.DoesNotContain(Register("legacy"), s => s.ServiceType == typeof(DeadManSender));
            // Below the minimum given the stages' poll: refused at start.
            Assert.Throws<InvalidOperationException>(() => Register("new", new() { ["Notifications:DeadMan:DueThreshold"] = "00:01:00" }));
        }
    }
}
