using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agent.Launcher;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The launcher's trial (roadmap Phase 7, milestone 2): a new version is kept only when it confirms it reached the platform within the trial
    /// period; if it exits or stays silent, it's stopped and the version before runs again, and the outcome is left for the agent to report. A trial
    /// nobody watched to the end, because the launcher itself stopped, counts as failed.
    /// </summary>
    public sealed class AgentLauncherTests : IDisposable
    {
        private const string Old = "1.0.0";
        private const string New = "1.1.0";
        private static readonly TimeSpan Trial = TimeSpan.FromMilliseconds(600);
        private readonly string _home = Path.Combine(Path.GetTempPath(), $"juice-launcher-{Guid.NewGuid():N}");
        private readonly List<string> _log = [];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public AgentLauncherTests()
        {
            Install(Old);
            Install(New);
        }

        private void Install(string version)
        {
            var directory = Path.Combine(_home, AgentHome.VersionsDirectory, version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, AgentPackages.ManifestEntry), $"{{\"version\":\"{version}\",\"entryPoint\":\"juice-agent.dll\"}}");
        }

        private Launcher Launcher(FakeStarter starter) => new(_home, starter, TimeProvider.System, m => { lock (_log) _log.Add(m); }, Trial, TimeSpan.FromMilliseconds(20));

        private void State(LauncherState state) => File.WriteAllText(Path.Combine(_home, Juice.Observability.Agent.Launcher.Launcher.StateFile), JsonSerializer.Serialize(state, CommandSignature.Json));

        private LauncherState ReadState() => JsonSerializer.Deserialize<LauncherState>(File.ReadAllBytes(Path.Combine(_home, Juice.Observability.Agent.Launcher.Launcher.StateFile)), CommandSignature.Json)!;

        private AgentHome.UpdateOutcome? Outcome() => File.Exists(Path.Combine(_home, AgentHome.OutcomeFile))
            ? JsonSerializer.Deserialize<AgentHome.UpdateOutcome>(File.ReadAllBytes(Path.Combine(_home, AgentHome.OutcomeFile)), CommandSignature.Json) : null;

        private void Request(Guid command, string version)
            => File.WriteAllText(Path.Combine(_home, AgentHome.UpdateFile), JsonSerializer.Serialize(new AgentHome.UpdateRequest(command, version), CommandSignature.Json));

        private void Confirm(Guid command) => File.WriteAllText(Path.Combine(_home, AgentHome.ConfirmedFile), command.ToString());

        /// <summary>Each start is scripted: what the "agent" does once it runs.</summary>
        private sealed class FakeStarter(params Func<FakeProcess, Task>[] script) : IAgentStarter
        {
            public List<(string Version, string? Trial)> Starts { get; } = [];
            public List<FakeProcess> Processes { get; } = [];

            public IAgentProcess Start(string versionDirectory, string entryPoint, IReadOnlyDictionary<string, string?> environment)
            {
                Assert.Equal("juice-agent.dll", entryPoint);
                Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(versionDirectory)), environment[AgentHome.HomeVariable]);
                var process = new FakeProcess();
                Starts.Add((Path.GetFileName(versionDirectory), environment[AgentHome.TrialVariable]));
                Processes.Add(process);
                var step = Starts.Count <= script.Length ? script[Starts.Count - 1] : _ => Task.CompletedTask;
                _ = Task.Run(() => step(process));
                return process;
            }
        }

        private sealed class FakeProcess : IAgentProcess
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Stopped { get; private set; }
            public Task<int> Exited => _exit.Task;
            public void Exit(int code) => _exit.TrySetResult(code);

            public void Stop()
            {
                Stopped = true;
                _exit.TrySetResult(-1);
            }

            public void Dispose()
            {
            }
        }

        [Fact(Timeout = 10_000)]
        public async Task A_version_that_confirms_in_time_is_kept_and_the_one_before_is_kept_to_go_back_to_Async()
        {
            State(new LauncherState(Old, null, null));
            var command = Guid.NewGuid();
            var starter = new FakeStarter(
                p => { Request(command, New); p.Exit(AgentHome.UpdateExitCode); return Task.CompletedTask; },
                async p => { await Task.Delay(100); Confirm(command); await Task.Delay(Trial * 2); p.Exit(3); });

            Assert.Equal(3, await Launcher(starter).RunAsync(Ct));

            Assert.Equal([(Old, null), (New, command.ToString())], starter.Starts);
            Assert.False(starter.Processes[1].Stopped, "a confirmed version was stopped");
            Assert.Equal(new LauncherState(New, Old, null), ReadState());
            var outcome = Outcome()!;
            Assert.True(outcome.Succeeded);
            Assert.Equal((command, Old, New), (outcome.CommandId, outcome.From, outcome.To));
            Assert.False(File.Exists(Path.Combine(_home, AgentHome.UpdateFile)));
            Assert.False(File.Exists(Path.Combine(_home, AgentHome.ConfirmedFile)));
        }

        [Fact(Timeout = 10_000)]
        public async Task A_version_that_exits_during_its_trial_is_rolled_back_Async()
        {
            State(new LauncherState(Old, null, null));
            var command = Guid.NewGuid();
            var starter = new FakeStarter(
                p => { Request(command, New); p.Exit(AgentHome.UpdateExitCode); return Task.CompletedTask; },
                p => { p.Exit(134); return Task.CompletedTask; },
                p => { p.Exit(0); return Task.CompletedTask; });

            Assert.Equal(0, await Launcher(starter).RunAsync(Ct));

            Assert.Equal([(Old, null), (New, command.ToString()), (Old, null)], starter.Starts);
            Assert.Equal(Old, ReadState().Current);
            Assert.Null(ReadState().Trial);
            var outcome = Outcome()!;
            Assert.False(outcome.Succeeded);
            Assert.Contains("exited with code 134", outcome.Reason);
            // The failed version is gone; only what can run is kept.
            Assert.False(Directory.Exists(Path.Combine(_home, AgentHome.VersionsDirectory, New)));
        }

        [Fact(Timeout = 10_000)]
        public async Task A_version_that_never_confirms_is_stopped_when_its_trial_ends_and_rolled_back_Async()
        {
            State(new LauncherState(Old, null, null));
            var command = Guid.NewGuid();
            var starter = new FakeStarter(
                p => { Request(command, New); p.Exit(AgentHome.UpdateExitCode); return Task.CompletedTask; },
                _ => Task.CompletedTask,                                  // runs, never reaches the platform
                p => { p.Exit(0); return Task.CompletedTask; });

            await Launcher(starter).RunAsync(Ct);

            Assert.True(starter.Processes[1].Stopped);
            Assert.Equal(Old, starter.Starts[2].Version);
            Assert.Contains("didn't reach the platform", Outcome()!.Reason);
        }

        [Fact(Timeout = 10_000)]
        public async Task A_confirmation_for_another_update_doesnt_count_Async()
        {
            State(new LauncherState(Old, null, null));
            var command = Guid.NewGuid();
            var starter = new FakeStarter(
                p => { Request(command, New); p.Exit(AgentHome.UpdateExitCode); return Task.CompletedTask; },
                _ => { Confirm(Guid.NewGuid()); return Task.CompletedTask; },
                p => { p.Exit(0); return Task.CompletedTask; });

            await Launcher(starter).RunAsync(Ct);

            Assert.False(Outcome()!.Succeeded);
            Assert.Equal(Old, ReadState().Current);
        }

        [Fact(Timeout = 10_000)]
        public async Task A_launcher_that_stopped_during_a_trial_goes_back_when_it_starts_again_Async()
        {
            var command = Guid.NewGuid();
            State(new LauncherState(New, Old, new LauncherTrial(command, New, Old, DateTimeOffset.UtcNow)));
            var starter = new FakeStarter(p => { p.Exit(0); return Task.CompletedTask; });

            await Launcher(starter).RunAsync(Ct);

            Assert.Equal([(Old, null)], starter.Starts);
            Assert.False(Outcome()!.Succeeded);
            Assert.Contains("launcher stopped during the trial", Outcome()!.Reason);
        }

        [Fact(Timeout = 10_000)]
        public async Task A_request_for_a_version_that_isnt_installed_is_answered_and_the_current_one_runs_again_Async()
        {
            State(new LauncherState(Old, null, null));
            var command = Guid.NewGuid();
            var starter = new FakeStarter(
                p => { Request(command, "9.9.9"); p.Exit(AgentHome.UpdateExitCode); return Task.CompletedTask; },
                p => { p.Exit(0); return Task.CompletedTask; });

            await Launcher(starter).RunAsync(Ct);

            Assert.Equal([(Old, null), (Old, null)], starter.Starts);
            Assert.Equal(command, Outcome()!.CommandId);
            Assert.False(Outcome()!.Succeeded);
        }

        [Fact(Timeout = 10_000)]
        public async Task Stopping_the_launcher_stops_the_agent_Async()
        {
            State(new LauncherState(Old, null, null));
            var starter = new FakeStarter(_ => Task.CompletedTask);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = Launcher(starter).RunAsync(stop.Token);
            await Task.Delay(100, Ct);
            stop.Cancel();

            Assert.Equal(0, await run);
            Assert.True(starter.Processes[0].Stopped);
        }

        [Fact]
        public void Seeding_installs_a_version_once_and_a_new_image_takes_over()
        {
            var seed = Path.Combine(_home, "seed");
            Directory.CreateDirectory(seed);
            File.WriteAllText(Path.Combine(seed, AgentPackages.ManifestEntry), "{\"version\":\"2.0.0\",\"entryPoint\":\"juice-agent.dll\"}");
            File.WriteAllText(Path.Combine(seed, "juice-agent.dll"), "x");
            State(new LauncherState(New, Old, null));
            var launcher = Launcher(new FakeStarter());

            launcher.Seed(seed);
            Assert.Equal(new LauncherState("2.0.0", New, null), ReadState());
            Assert.True(File.Exists(Path.Combine(_home, AgentHome.VersionsDirectory, "2.0.0", "juice-agent.dll")));

            // Started again with the same image after updating to another version: what it updated to stays.
            State(new LauncherState(New, "2.0.0", null));
            launcher.Seed(seed);
            Assert.Equal(New, ReadState().Current);
        }

        public void Dispose()
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
    }
}
