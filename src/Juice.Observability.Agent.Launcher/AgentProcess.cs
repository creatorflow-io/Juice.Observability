using System.Diagnostics;

namespace Juice.Observability.Agent.Launcher
{
    /// <summary>A running agent, as the launcher sees it.</summary>
    public interface IAgentProcess : IDisposable
    {
        /// <summary>Completes with the exit code when the agent exits.</summary>
        Task<int> Exited { get; }

        /// <summary>Stops it now, with everything it started.</summary>
        void Stop();
    }

    /// <summary>Starts an installed agent version.</summary>
    public interface IAgentStarter
    {
        /// <param name="versionDirectory">The version's directory under <c>versions/</c>.</param>
        /// <param name="entryPoint">Its entry point from its manifest, relative to the directory and checked to stay inside it.</param>
        /// <param name="environment">Variables to set for it, on top of the launcher's own; a null value removes one.</param>
        IAgentProcess Start(string versionDirectory, string entryPoint, IReadOnlyDictionary<string, string?> environment);
    }

    /// <summary>
    /// The one place anything in the agent's programs starts a process: the launcher starting an installed agent version, the entry point its signed
    /// package names. A <c>.dll</c> runs on the .NET host the launcher itself runs on; nothing goes through a shell, and the arguments are fixed.
    /// The no-shell test allows process starts in this file only.
    /// </summary>
    public sealed class AgentStarter : IAgentStarter
    {
        public IAgentProcess Start(string versionDirectory, string entryPoint, IReadOnlyDictionary<string, string?> environment)
        {
            var path = Path.GetFullPath(Path.Combine(versionDirectory, entryPoint));
            if (!path.StartsWith(Path.GetFullPath(versionDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The entry point {entryPoint} is outside {versionDirectory}");
            }
            var dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(dll ? DotnetHost() : path) { UseShellExecute = false };
            if (dll)
            {
                start.ArgumentList.Add(path);
            }
            foreach (var (name, value) in environment)
            {
                if (value is null) start.Environment.Remove(name);
                else start.Environment[name] = value;
            }
            var process = Process.Start(start) ?? throw new InvalidOperationException($"{path} didn't start");
            return new Running(process);
        }

        /// <summary>The <c>dotnet</c> this launcher runs on, when it runs on one; otherwise the one on the path.</summary>
        private static string DotnetHost()
            => Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";

        private sealed class Running : IAgentProcess
        {
            private readonly Process _process;

            public Running(Process process)
            {
                _process = process;
                Exited = WaitAsync();
            }

            public Task<int> Exited { get; }

            private async Task<int> WaitAsync()
            {
                await _process.WaitForExitAsync();
                return _process.ExitCode;
            }

            public void Stop()
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            public void Dispose() => _process.Dispose();
        }
    }
}
