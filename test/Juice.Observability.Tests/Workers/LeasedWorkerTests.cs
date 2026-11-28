using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Juice.Observability.Tests.Workers
{
    /// <summary>The leased-worker host: what a module may name, and the lease, retry and ordering a module's worker gets on the core's loop.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class LeasedWorkerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static WebApplicationBuilder Core()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        private static ModuleCompositionException Refused(params string[] names)
        {
            WorkerNamesModule.Names = names;
            return Assert.Throws<ModuleCompositionException>(() => Core().AddObservabilityModule<WorkerNamesModule>());
        }

        private static async Task<T> WaitForAsync<T>(Func<T?> read, int seconds = 30) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (read() is { } value)
                {
                    return value;
                }
                await Task.Delay(50, Ct);
            }
            throw new TimeoutException("Not seen in time");
        }

        [Fact]
        public void Worker_name_rules()
        {
            // a core worker's name, whichever module asks
            Assert.Contains("worker of the core", Refused("rules").Message);
            Assert.Contains("worker of the core", Refused("discovery").Message);
            // no prefix, a prefix without its dash, an empty part, and a name that isn't a name
            Assert.Contains("'wrk' or 'wrk-<x>'", Refused("other").Message);
            Assert.Contains("'wrk' or 'wrk-<x>'", Refused("wrkx").Message);
            Assert.Contains("must match", Refused("wrk-").Message);
            Assert.Contains("must match", Refused("Wrk_One").Message);
            Assert.Contains("must match", Refused(new string('a', 41)).Message);
            // twice in one module, and the same name taken by another module
            Assert.Contains("already a worker", Refused("wrk", "wrk").Message);
            var builder = Core();
            builder.AddObservabilityModule<WorkerModule>();
            Assert.Contains("wrk", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<WorkerModule>()).Message);
            // what is allowed
            WorkerNamesModule.Names = ["wrk", "wrk-one", "wrk-two-three"];
            Core().AddObservabilityModule<WorkerNamesModule>();
        }

        [Fact]
        public void Refused_core_names_are_the_known_workers() => Assert.Equal(WorkerPulse.KnownWorkers.Order(), LeasedWorkerNames.Core.Order());

        [Fact]
        public void Disabled_module_worker_not_registered_and_not_enabled_in_snapshot()
        {
            static int Hosted(IServiceCollection s) => s.Count(d => d.ServiceType == typeof(IHostedService));

            var on = Core();
            on.AddObservabilityModule<WorkerModule>();

            var off = CoreHost.CreateBuilder(new Dictionary<string, string?> { ["Modules:wrk:Workers:wrk:Enabled"] = "false" });
            off.Services.AddJuiceObservability(off.Configuration);
            off.AddObservabilityModule<WorkerModule>();
            // the migration runner comes with any module; the worker's own hosted service only when it is on
            Assert.Equal(Hosted(off.Services) + 1, Hosted(on.Services));
            // still a known name, so a second composition can't take it
            Assert.Contains(off.Services, d => d.ServiceType == typeof(LeasedWorkerRegistration) && d.ImplementationInstance is LeasedWorkerRegistration { Name: "wrk", Enabled: false });
            Assert.DoesNotContain(WorkerPulse.Snapshot(), s => s.Worker == "wrk" && s.Enabled);
        }

        [Fact]
        public async Task One_replica_runs_module_worker_other_takes_over_on_stop()
        {
            LocalStack.RequireAvailable();
            WorkerProbe.Reset();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var a = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<WorkerModule>(), database, hostedServices: true, settings: new Dictionary<string, string?> { ["Test:Marker"] = "A" });
            await using var b = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<WorkerModule>(), database, hostedServices: true, settings: new Dictionary<string, string?> { ["Test:Marker"] = "B" });
            try
            {
                await WaitForAsync(() => WorkerProbe.Passes.Count >= 4 ? "ran" : null);
                var holder = WorkerProbe.Passes.Select(p => p.Marker).Distinct().ToList();
                Assert.Single(holder);

                var stopped = holder[0];
                await (stopped == "A" ? a : b).DisposeAsync();
                WorkerProbe.Reset();
                var other = stopped == "A" ? "B" : "A";
                await WaitForAsync(() => WorkerProbe.Passes.Any(p => p.Marker == other) ? "took over" : null);
                Assert.DoesNotContain(WorkerProbe.Passes, p => p.Marker == stopped);
            }
            finally
            {
                await a.DisposeAsync();
            }
        }

        [Fact]
        public async Task Lease_lost_mid_pass_cancels_LeaseLost_and_next_pass_is_Retaken()
        {
            LocalStack.RequireAvailable();
            WorkerProbe.Reset();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var lostSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = true;
            WorkerProbe.Behavior = async (pass, ct) =>
            {
                if (!first)
                {
                    return LeasedPassOutcome.Succeeded;
                }
                first = false;
                inPass.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), pass.LeaseLost);
                    lostSeen.TrySetResult(false);
                }
                catch (OperationCanceledException)
                {
                    lostSeen.TrySetResult(pass.LeaseLost.IsCancellationRequested);
                }
                return LeasedPassOutcome.Partial;
            };
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<WorkerModule>(), database, hostedServices: true);
            await inPass.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            await using (var admin = new NpgsqlConnection(database.ConnectionString))
            {
                await admin.OpenAsync(Ct);
                await using var kill = new NpgsqlCommand($"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = '{database.Name}' AND application_name LIKE 'juice-worker:wrk:%'", admin);
                Assert.Equal(1L, await kill.ExecuteScalarAsync(Ct));
            }

            Assert.True(await lostSeen.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            var next = await WaitForAsync(() => WorkerProbe.Passes.Skip(1).FirstOrDefault());
            Assert.True(next.Retaken);
            Assert.True(next.Generation > WorkerProbe.Passes.First().Generation);
        }

        [Fact]
        public async Task Failing_pass_logged_failure_outcome_and_retried()
        {
            LocalStack.RequireAvailable();
            WorkerProbe.Reset();
            var capture = new LogCapture();
            var fail = true;
            WorkerProbe.Behavior = (_, _) => fail ? Task.FromException<LeasedPassOutcome>(new InvalidOperationException("pass broke")) : Task.FromResult(LeasedPassOutcome.Succeeded);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => { b.AddObservabilityModule<WorkerModule>(); b.Services.AddSingleton<ILoggerProvider>(capture); }, database, hostedServices: true);

            await WaitForAsync(() => capture.At(LogLevel.Error).FirstOrDefault(e => e.Message.Contains("Worker wrk pass failed")));
            Assert.Equal("failure", WorkerPulse.Snapshot().Single(s => s.Worker == "wrk").LastOutcome);
            var failed = WorkerProbe.Passes.Count;
            fail = false;

            // tried again at the next interval, and now it succeeds
            await WaitForAsync(() => WorkerProbe.Passes.Count > failed && WorkerPulse.Snapshot().Single(s => s.Worker == "wrk").LastOutcome == "success" ? "ok" : null);
        }

        [Fact]
        public async Task Module_worker_starts_after_module_migrations()
        {
            LocalStack.RequireAvailable();
            TableWorker.First = null;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<MigratedWorkerModule>(), database, hostedServices: true);

            var first = await WaitForAsync(() => TableWorker.First);
            Assert.Equal("table-present:0", first);
        }
    }
}
