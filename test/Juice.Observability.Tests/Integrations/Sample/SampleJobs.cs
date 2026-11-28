using Juice.Observability.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Tests.Integrations.Sample
{
    /// <summary>What <c>sample.jobs</c> does next: set by a test on the host's singleton, so tests running side by side never share it.</summary>
    public sealed class SampleJobsControl
    {
        /// <summary>The outcome a collection returns (default: succeeded, with a detail that echoes the secret).</summary>
        public CollectionResult Result { get; set; } = CollectionResult.Succeeded;

        public IntegrationFailure Failure { get; set; } = IntegrationFailure.None;

        public TimeSpan? RetryAfter { get; set; }

        public bool ThrowOnCollect { get; set; }

        public TimeSpan CollectDelay { get; set; }

        public bool ValidatePasses { get; set; } = true;

        public bool ThrowOnValidate { get; set; }

        public TimeSpan ValidateDelay { get; set; }

        /// <summary>The delay ignores the cancellation token: a plugin that does not stop when asked.</summary>
        public bool IgnoreCancellation { get; set; }

        /// <summary>Reads a checkpoint every few milliseconds for three seconds, ignoring cancellation: a plugin still running after the request moved on.</summary>
        public bool LateReads { get; set; }

        public System.Collections.Concurrent.ConcurrentQueue<Exception> LateErrors { get; } = new();

        public TaskCompletionSource LateReadsDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Called inside the plugin before it answers: a hook to change the instance while a validation runs.</summary>
        public Func<Task>? OnValidate { get; set; }

        public int Validations { get; set; }

        public int Collections { get; set; }
    }

    /// <summary>
    /// The test-only plugin <c>sample.jobs</c> (test module <c>sample</c>): a configurable outcome, one secret (<c>token</c>) that tests use as a canary, and a
    /// detail that echoes the secret, so a test can see the core scrub it.
    /// </summary>
    public sealed class SampleJobsPlugin(SampleJobsControl control) : IIntegrationPlugin, IIntegrationCollector
    {
        public const string Key = "sample.jobs";

        public static IntegrationDescriptor Descriptor(int configVersion = 1) => new(
            Key, "Sample jobs", "1.0.0", new HashSet<IntegrationCapability> { IntegrationCapability.Discovery, IntegrationCapability.Health },
            new IntegrationConfigSchema(configVersion,
            [
                new("baseUrl", IntegrationFieldKind.String, "Base URL", Required: true, MaxLength: 200, Pattern: "^https?://"),
                new("batchSize", IntegrationFieldKind.Integer, "Batch size", Min: 1, Max: 1000),
                new("mode", IntegrationFieldKind.Enum, "Mode", Allowed: ["fast", "slow"]),
                new("outcome", IntegrationFieldKind.Enum, "Outcome the next collection reports", Allowed: ["Succeeded", "Failed"]),
                new("ratio", IntegrationFieldKind.Number, "Ratio", Min: 0, Max: 1),
                new("dryRun", IntegrationFieldKind.Boolean, "Dry run"),
                new("timeout", IntegrationFieldKind.Duration, "Timeout", Min: 1, Max: 600),
            ]),
            new IntegrationConfigSchema(1, [new("projects", IntegrationFieldKind.StringList, "Projects", MaxItems: 10, MaxLength: 40)]),
            [new IntegrationSecretSpec("token", "API token")],
            ["jobs.read"], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        public async Task<ValidationReport> ValidateAsync(IntegrationContext context, CancellationToken ct)
        {
            control.Validations++;
            if (control.LateReads)
            {
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        await context.Checkpoints.GetAsync("jobs", CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        control.LateErrors.Enqueue(e);
                    }
                    await Task.Delay(3, CancellationToken.None);
                }
                control.LateReadsDone.TrySetResult();
                return new ValidationReport(true, [new("connectivity", true)]);
            }
            if (control.OnValidate is { } hook)
            {
                await hook();
            }
            if (control.ValidateDelay > TimeSpan.Zero)
            {
                await Task.Delay(control.ValidateDelay, control.IgnoreCancellation ? CancellationToken.None : ct);
            }
            var token = await context.Secrets.GetAsync("token", ct);
            context.Logger.LogInformation("Validating with {Token}", token);
            if (control.ThrowOnValidate)
            {
                throw new InvalidOperationException($"the jobs service refused {token}");
            }
            return new ValidationReport(control.ValidatePasses,
            [
                new("connectivity", true),
                new("authentication", control.ValidatePasses, $"signed in with {token}"),
            ]);
        }

        public async Task<CollectionOutcome> CollectAsync(IntegrationContext context, ICollectionSinks sinks, CancellationToken ct)
        {
            control.Collections++;
            if (control.CollectDelay > TimeSpan.Zero)
            {
                await Task.Delay(control.CollectDelay, ct);
            }
            var token = await context.Secrets.GetAsync("token", ct);
            if (control.ThrowOnCollect)
            {
                throw new InvalidOperationException($"the jobs service refused {token}");
            }
            context.Checkpoints.Set("jobs", context.Clock.GetUtcNow(), "cursor-1");
            return new CollectionOutcome(control.Result, control.Failure, $"jobs answered for {token}", 3, control.RetryAfter);
        }
    }

    /// <summary>The test module <c>sample</c>: registers <c>sample.jobs</c> and its control. A test composes it with <c>AddObservabilityModule&lt;SampleModule&gt;</c>.</summary>
    public sealed class SampleModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("sample", "Sample", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
        {
            module.Services.AddSingleton<SampleJobsControl>();
            module.AddIntegrationPlugin<SampleJobsPlugin>(SampleJobsPlugin.Descriptor());
        }
    }
}
