using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent.Launcher
{
    /// <summary>Which version runs, and the one on trial if any. Kept in <c>state.json</c>, owned by the launcher.</summary>
    public sealed record LauncherState(string Current, string? Previous, LauncherTrial? Trial);

    /// <param name="CommandId">The update command, whose result the outcome becomes.</param>
    /// <param name="Version">The version on trial.</param>
    /// <param name="From">The version to go back to.</param>
    /// <param name="StartedAt">When the trial started.</param>
    public sealed record LauncherTrial(Guid CommandId, string Version, string From, DateTimeOffset StartedAt);

    /// <summary>
    /// Runs the current agent version, and switches versions only when the agent asks (<see cref="AgentHome"/>): the new version runs on trial and
    /// is kept once it confirms it reached the platform; if it exits, or doesn't confirm within <see cref="TrialPeriod"/>, it's stopped and the
    /// version before starts again. A launcher that stops or crashes during a trial goes back to the version before when it starts again: a trial
    /// that wasn't watched to the end didn't pass. Any other exit of the agent ends the launcher with the same code, for the service manager (systemd,
    /// a container's restart policy) to restart both.
    /// </summary>
    public sealed class Launcher
    {
        public static readonly TimeSpan DefaultTrialPeriod = TimeSpan.FromMinutes(2);
        public const string StateFile = "state.json";
        /// <summary>The wait before starting the current version again after it asked for an update it can't have, so a loop of those can't spin.</summary>
        private static readonly TimeSpan BadRequestPause = TimeSpan.FromSeconds(5);

        private readonly string _home;
        private readonly IAgentStarter _starter;
        private readonly TimeProvider _clock;
        private readonly Action<string> _log;
        private readonly TimeSpan _check;

        public Launcher(string home, IAgentStarter starter, TimeProvider clock, Action<string> log, TimeSpan? trialPeriod = null, TimeSpan? check = null)
        {
            _home = Path.GetFullPath(home);
            _starter = starter;
            _clock = clock;
            _log = log;
            TrialPeriod = trialPeriod ?? DefaultTrialPeriod;
            _check = check ?? TimeSpan.FromSeconds(1);
        }

        public TimeSpan TrialPeriod { get; }

        private string PathOf(string name) => Path.Combine(_home, name);

        private string VersionDirectory(string version) => Path.Combine(_home, AgentHome.VersionsDirectory, version);

        /// <summary>
        /// Installs the version in <paramref name="seed"/> (a published agent with its manifest) when it isn't installed yet, and makes it current: how
        /// the first version gets here, and how a new image of a container takes over from what an earlier one installed.
        /// </summary>
        public void Seed(string seed)
        {
            var manifest = ReadManifest(seed) ?? throw new InvalidOperationException($"{seed} has no readable {AgentPackages.ManifestEntry}");
            if (!IsVersion(manifest.Version))
            {
                throw new InvalidOperationException($"{seed}: '{manifest.Version}' isn't a version");
            }
            var target = VersionDirectory(manifest.Version);
            if (Directory.Exists(target))
            {
                if (Load() is null) Save(new LauncherState(manifest.Version, null, null));
                return;
            }
            var staging = Path.Combine(_home, AgentHome.VersionsDirectory, $".{manifest.Version}.{Guid.NewGuid():N}");
            Copy(seed, staging);
            Directory.Move(staging, target);
            var state = Load();
            Save(state is null ? new LauncherState(manifest.Version, null, null) : new LauncherState(manifest.Version, state.Current, null));
            _log($"installed version {manifest.Version} from {seed}");
        }

        public async Task<int> RunAsync(CancellationToken stop)
        {
            var state = Load() ?? throw new InvalidOperationException($"No agent version is installed in {_home}: start with --seed <published agent>.");
            if (state.Trial is { } unfinished)
            {
                state = RollBack(state, unfinished, "the launcher stopped during the trial");
            }
            while (true)
            {
                var trial = state.Trial;
                var version = trial?.Version ?? state.Current;
                using var process = Start(version, trial);
                if (trial is not null)
                {
                    string? failure;
                    try
                    {
                        failure = await TrialAsync(process, trial, stop);
                    }
                    catch (OperationCanceledException)
                    {
                        await StopAsync(process);
                        return 0;
                    }
                    if (failure is not null)
                    {
                        await StopAsync(process);
                        state = RollBack(state, trial, failure);
                        continue;
                    }
                    state = Accept(state, trial);
                }
                int code;
                try
                {
                    code = await process.Exited.WaitAsync(stop);
                }
                catch (OperationCanceledException)
                {
                    await StopAsync(process);
                    return 0;
                }
                if (code != AgentHome.UpdateExitCode)
                {
                    _log($"version {version} exited with code {code}");
                    return code;
                }
                if (TakeRequest(state) is { } next)
                {
                    state = next;
                    continue;
                }
                await Task.Delay(BadRequestPause, _clock, stop).ContinueWith(_ => { }, TaskScheduler.Default);
                if (stop.IsCancellationRequested)
                {
                    return 0;
                }
            }
        }

        private IAgentProcess Start(string version, LauncherTrial? trial)
        {
            var directory = VersionDirectory(version);
            var manifest = ReadManifest(directory) ?? throw new InvalidOperationException($"Version {version} has no readable manifest");
            if (trial is not null)
            {
                File.Delete(PathOf(AgentHome.ConfirmedFile));
            }
            _log(trial is null ? $"starting version {version}" : $"starting version {version} on trial for {TrialPeriod.TotalSeconds:0} s");
            return _starter.Start(directory, manifest.EntryPoint, new Dictionary<string, string?>
            {
                [AgentHome.HomeVariable] = _home,
                [AgentHome.TrialVariable] = trial?.CommandId.ToString(),
                // Its own appsettings, beside it; relative paths in the host's configuration still resolve from where the launcher runs.
                ["DOTNET_CONTENTROOT"] = directory,
            });
        }

        /// <summary>Null when the version confirmed in time; otherwise why it failed. Throws when the launcher is stopped.</summary>
        private async Task<string?> TrialAsync(IAgentProcess process, LauncherTrial trial, CancellationToken stop)
        {
            while (true)
            {
                if (process.Exited.IsCompleted)
                {
                    return $"version {trial.Version} exited with code {await process.Exited} during its trial";
                }
                if (File.Exists(PathOf(AgentHome.ConfirmedFile)) && (await File.ReadAllTextAsync(PathOf(AgentHome.ConfirmedFile), stop)).Trim() == trial.CommandId.ToString())
                {
                    return null;
                }
                if (_clock.GetUtcNow() - trial.StartedAt >= TrialPeriod)
                {
                    return $"version {trial.Version} didn't reach the platform within {TrialPeriod.TotalSeconds:0} s";
                }
                await Task.WhenAny(process.Exited, Task.Delay(_check, _clock, stop));
                stop.ThrowIfCancellationRequested();
            }
        }

        private static async Task StopAsync(IAgentProcess process)
        {
            process.Stop();
            await process.Exited;
        }

        /// <summary>The agent's request, if it names a version that's installed and isn't the current one; it's consumed either way.</summary>
        private LauncherState? TakeRequest(LauncherState state)
        {
            var file = PathOf(AgentHome.UpdateFile);
            AgentHome.UpdateRequest? request = null;
            try
            {
                request = File.Exists(file) ? JsonSerializer.Deserialize<AgentHome.UpdateRequest>(File.ReadAllBytes(file), CommandSignature.Tolerant) : null;
            }
            catch (JsonException)
            {
            }
            File.Delete(file);
            if (request is null)
            {
                _log("the agent asked for an update without a readable request: starting the current version again");
                return null;
            }
            if (!IsVersion(request.Version) || request.Version == state.Current || ReadManifest(VersionDirectory(request.Version)) is not { } manifest || manifest.Version != request.Version)
            {
                Report(new AgentHome.UpdateOutcome(request.CommandId, state.Current, request.Version, false, "the requested version isn't installed", _clock.GetUtcNow()));
                _log($"the agent asked for version {request.Version}, which isn't installed: starting the current version again");
                return null;
            }
            var next = state with { Trial = new LauncherTrial(request.CommandId, request.Version, state.Current, _clock.GetUtcNow()) };
            Save(next);
            return next;
        }

        private LauncherState Accept(LauncherState state, LauncherTrial trial)
        {
            var next = new LauncherState(trial.Version, trial.From, null);
            Save(next);
            Report(new AgentHome.UpdateOutcome(trial.CommandId, trial.From, trial.Version, true, null, _clock.GetUtcNow()));
            File.Delete(PathOf(AgentHome.ConfirmedFile));
            _log($"version {trial.Version} confirmed: it's the current version, {trial.From} is kept to go back to");
            Prune(next);
            return next;
        }

        private LauncherState RollBack(LauncherState state, LauncherTrial trial, string reason)
        {
            var next = new LauncherState(trial.From, state.Previous == trial.From ? null : state.Previous, null);
            Save(next);
            Report(new AgentHome.UpdateOutcome(trial.CommandId, trial.From, trial.Version, false, reason, _clock.GetUtcNow()));
            File.Delete(PathOf(AgentHome.ConfirmedFile));
            _log($"rolled back to version {trial.From}: {reason}");
            Prune(next);
            return next;
        }

        /// <summary>Keeps the current version and the one before; staging directories (named with a dot) are an agent's, unpacking.</summary>
        private void Prune(LauncherState state)
        {
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(_home, AgentHome.VersionsDirectory)))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith('.') && name != state.Current && name != state.Previous)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        private void Report(AgentHome.UpdateOutcome outcome) => Replace(PathOf(AgentHome.OutcomeFile), JsonSerializer.Serialize(outcome, CommandSignature.Json));

        internal LauncherState? Load()
            => File.Exists(PathOf(StateFile)) ? JsonSerializer.Deserialize<LauncherState>(File.ReadAllBytes(PathOf(StateFile)), CommandSignature.Tolerant) : null;

        private void Save(LauncherState state)
        {
            Directory.CreateDirectory(_home);
            Replace(PathOf(StateFile), JsonSerializer.Serialize(state, CommandSignature.Json));
        }

        private static AgentPackages.Manifest? ReadManifest(string directory)
        {
            var file = Path.Combine(directory, AgentPackages.ManifestEntry);
            if (!File.Exists(file))
            {
                return null;
            }
            try
            {
                var manifest = JsonSerializer.Deserialize<AgentPackages.Manifest>(File.ReadAllBytes(file), CommandSignature.Tolerant);
                return manifest is { Version.Length: > 0, EntryPoint.Length: > 0 } ? manifest : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsVersion(string version) => new AgentUpdateParameters(version, new string('0', 64)).Validate() is null;

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
            foreach (var directory in Directory.EnumerateDirectories(from))
            {
                Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
            }
        }

        private static void Replace(string file, string content)
        {
            var temporary = file + ".new";
            File.WriteAllText(temporary, content);
            File.Move(temporary, file, overwrite: true);
        }
    }
}
