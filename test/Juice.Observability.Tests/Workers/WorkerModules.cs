using System.Collections.Concurrent;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Workers
{
    /// <summary>What the test workers saw: one record per pass, and what the next pass should do.</summary>
    internal static class WorkerProbe
    {
        public sealed record PassRecord(string Marker, int Generation, bool Retaken);

        public static ConcurrentQueue<PassRecord> Passes { get; private set; } = new();

        /// <summary>Runs inside each pass; null returns <see cref="LeasedPassOutcome.Succeeded"/>.</summary>
        public static volatile Func<LeasedPass, CancellationToken, Task<LeasedPassOutcome>>? Behavior;

        public static void Reset()
        {
            Passes = new ConcurrentQueue<PassRecord>();
            Behavior = null;
        }
    }

    public sealed class ProbeWorker(IConfiguration configuration) : ILeasedWorker
    {
        public TimeSpan Interval => TimeSpan.FromMilliseconds(200);

        public async Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct)
        {
            WorkerProbe.Passes.Enqueue(new WorkerProbe.PassRecord(configuration["Test:Marker"] ?? "", pass.Generation, pass.Retaken));
            return WorkerProbe.Behavior is { } behavior ? await behavior(pass, ct) : LeasedPassOutcome.Succeeded;
        }
    }

    /// <summary>A module of one worker named for the module.</summary>
    public class WorkerModule : IObservabilityModule
    {
        public virtual ModuleInfo Info { get; } = new("wrk", "Wrk", "1.0.0", "[2.0.0,3.0.0)");

        public virtual void ConfigureServices(IModuleServicesBuilder module) => module.AddLeasedWorker<ProbeWorker>("wrk");
    }

    /// <summary>The worker takes the name of another module's worker as well as its own.</summary>
    public sealed class WorkerNamesModule : WorkerModule
    {
        public static string[] Names = [];

        public override ModuleInfo Info { get; } = new("wrk", "Wrk", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            foreach (var name in Names)
            {
                module.AddLeasedWorker<ProbeWorker>(name);
            }
        }
    }

    public sealed class OtherWorkerModule : WorkerModule
    {
        public override ModuleInfo Info { get; } = new("other", "Other", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module) => module.AddLeasedWorker<ProbeWorker>("other");
    }

    /// <summary>Notes whether the module's table is there when the worker is first made: that is after the module's migrations.</summary>
    public sealed class TableWorker(SampleDbContext db) : ILeasedWorker
    {
        public static volatile string? First;

        private static readonly object Gate = new();

        public TimeSpan Interval
        {
            get
            {
                lock (Gate)
                {
                    if (First is null)
                    {
                        try
                        {
                            First = "table-present:" + db.Items.Count();
                        }
                        catch (Exception e)
                        {
                            First = "table-missing: " + e.Message;
                        }
                    }
                }
                return TimeSpan.FromMilliseconds(200);
            }
        }

        public Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct) => Task.FromResult(LeasedPassOutcome.Idle);
    }

    public sealed class MigratedWorkerModule : TestModule
    {
        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            base.ConfigureServices(module);
            module.AddLeasedWorker<TableWorker>("sample");
        }
    }

    /// <summary>A module named <c>rule</c> with a worker named like the core's rule provisioning lock.</summary>
    public sealed class RuleNamedWorkerModule : WorkerModule
    {
        public override ModuleInfo Info { get; } = new("rule", "Rule", "1.0.0", "[2.0.0,3.0.0)");

        public override void ConfigureServices(IModuleServicesBuilder module) => module.AddLeasedWorker<ProbeWorker>("rule-provisioning");
    }

    public abstract class AbstractWorker : ILeasedWorker
    {
        public abstract TimeSpan Interval { get; }

        public abstract Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct);
    }

    public sealed class AbstractWorkerModule : WorkerModule
    {
        public override void ConfigureServices(IModuleServicesBuilder module) => module.AddLeasedWorker<AbstractWorker>("wrk");
    }
}
