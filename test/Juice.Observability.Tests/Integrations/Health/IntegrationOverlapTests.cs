using Juice.Observability.Api;
using Juice.Observability.Api.Integrations;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Integrations.Sample;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>What a plugin that ignores its cancellation token does to the worker: nothing beyond its own instance (decision 7, review #4). Local stack.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class IntegrationOverlapTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>What <c>hang.jobs</c> does: it counts its calls and answers only when the test lets it, whatever token it is given.</summary>
        public sealed class HangControl
        {
            public int Starts;

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class HangPlugin(HangControl control) : IIntegrationPlugin, IIntegrationCollector
        {
            public const string Key = "hang.jobs";

            public static readonly IntegrationDescriptor Descriptor = new(Key, "Hang", "1.0.0", new HashSet<IntegrationCapability> { IntegrationCapability.Discovery },
                new IntegrationConfigSchema(1, []), new IntegrationConfigSchema(1, []), [], [], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

            public Task<ValidationReport> ValidateAsync(IntegrationContext context, CancellationToken ct) => Task.FromResult(new ValidationReport(true, []));

            public async Task<CollectionOutcome> CollectAsync(IntegrationContext context, ICollectionSinks sinks, CancellationToken ct)
            {
                Interlocked.Increment(ref control.Starts);
                await control.Release.Task;
                return new CollectionOutcome(CollectionResult.Succeeded);
            }
        }

        public sealed class HangModule : IObservabilityModule
        {
            public ModuleInfo Info { get; } = new("hang", "Hang", "1.0.0", "[2.0.0,3.0.0)");

            public void ConfigureServices(IModuleServicesBuilder module)
            {
                module.Services.AddSingleton<HangControl>();
                module.AddIntegrationPlugin<HangPlugin>(HangPlugin.Descriptor);
            }
        }

        [Fact]
        public async Task Plugin_ignoring_cancellation_never_runs_twice_at_once_and_other_instances_still_run()
        {
            await using var host = await IntegrationHealthHost.StartAsync(new Dictionary<string, string?> { ["Modules:integrations:RunTimeout"] = "00:00:00.300" },
                compose: b => b.AddObservabilityModule<HangModule>());
            var control = host.Services.GetRequiredService<HangControl>();
            var hanging = await host.AddAsync(plugin: HangPlugin.Key, name: "hanging", config: "{}");
            var other = await host.AddAsync(name: "other");

            await host.PassAsync();

            // The run timed out and was recorded, though the plugin's call is still going.
            Assert.Equal(1, control.Starts);
            var timedOut = await host.GetAsync(hanging);
            Assert.Equal("Timeout", timedOut.LastFailure);
            Assert.Equal(1, host.Control.Collections);

            // Due again (as its backoff passing would make it): not called again while the first call hasn't returned. The other instance still runs.
            await host.MakeDueAsync(hanging);
            await host.MakeDueAsync(other);
            await host.PassAsync();

            Assert.Equal(1, control.Starts);
            Assert.Equal(2, host.Control.Collections);

            // The plugin answers at last: the instance is free, and runs again.
            control.Release.SetResult();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (control.Starts < 2 && DateTime.UtcNow < until)
            {
                await host.MakeDueAsync(hanging);
                await host.OnePassAsync();
                await host.State.WhenRunsDoneAsync();
                await Task.Delay(50, Ct);
            }
            Assert.Equal(2, control.Starts);
        }

        [Fact]
        public async Task A_slow_run_does_not_hold_back_the_alert_reconcile_or_the_pass()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var slow = await host.AddAsync(name: "slow");
            var failing = await host.AddAsync(plugin: "gone.plugin", name: "gone");
            host.Control.CollectDelay = TimeSpan.FromSeconds(20);

            // The slow run is going, and the pass (which stays open for it) has already raised the alert of the instance whose plugin is gone.
            using var lost = new CancellationTokenSource();
            var pass = host.OnePassAsync(lost.Token);
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!(await host.AlertsAsync()).Any(a => a.DedupKey.EndsWith(failing.ToString("N"), StringComparison.Ordinal)))
            {
                Assert.True(DateTime.UtcNow < until, "The alert wasn't raised while the slow run was going");
                await Task.Delay(50, Ct);
            }

            Assert.NotNull((await host.GetAsync(slow)).RunToken);
            Assert.False(pass.IsCompleted);
            await lost.CancelAsync();
            Assert.Equal(LeasedPassOutcome.Succeeded, await pass);
        }
    }
}
