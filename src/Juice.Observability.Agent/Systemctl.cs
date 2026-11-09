using System.Diagnostics;

namespace Juice.Observability.Agent
{
    // The one place the agent starts a process (roadmap Phase 8, decision P7). AgentVerifierTests holds it to: one start, a program from
    // Systemd.Paths only, no shell, and every argument added on its own.

    /// <summary>
    /// systemd through <c>systemctl</c> at a fixed path, never found on <c>PATH</c> and never through a shell: the verb is fixed, the unit is a
    /// separate argument after <c>--</c>, and the unit is one the host lists, checked against a strict pattern before it gets here.
    /// </summary>
    public sealed class Systemd : IServiceControl
    {
        public static readonly IReadOnlyList<string> Paths = ["/usr/bin/systemctl", "/bin/systemctl"];

        /// <summary>The arguments for a verb: never a string a shell would split.</summary>
        public static IReadOnlyList<string> Arguments(string verb, string unit) => [verb, "--", unit];

        public async Task<ServiceRestartResult> RestartAsync(string service, CancellationToken cancellationToken)
        {
            var systemctl = Paths.FirstOrDefault(File.Exists) ?? throw new ActionFailedException("systemctl not found: this host doesn't run systemd");
            var (restarted, error) = await RunAsync(systemctl, Arguments("restart", service), cancellationToken);
            if (restarted != 0) throw new ActionFailedException($"systemctl restart failed ({restarted}): {error}");
            var (_, state) = await RunAsync(systemctl, Arguments("is-active", service), cancellationToken);
            var active = state.Trim();
            return new ServiceRestartResult(service, active, active == "active");
        }

        private static async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new ActionFailedException("systemctl didn't start");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
            var text = (await output) + (await error);
            return (process.ExitCode, text.Length > 200 ? text[..200] : text);
        }
    }
}
