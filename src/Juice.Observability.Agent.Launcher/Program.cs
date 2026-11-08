using System.Runtime.InteropServices;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent.Launcher
{
    /// <summary>
    /// <c>juice-agent-launcher --home DIR [--seed DIR]</c>. The home holds the installed versions and the launcher's state; <c>--seed</c> installs
    /// a published agent there when that version isn't installed yet. The home may also come from <c>JUICE_AGENT_HOME</c>. Run it under a service
    /// manager that restarts it: it exits when the agent does, other than to update.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            string? home = Environment.GetEnvironmentVariable(AgentHome.HomeVariable), seed = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--home" when i + 1 < args.Length: home = args[++i]; break;
                    case "--seed" when i + 1 < args.Length: seed = args[++i]; break;
                    default:
                        Console.Error.WriteLine("usage: juice-agent-launcher --home DIR [--seed DIR]");
                        return 64;
                }
            }
            if (string.IsNullOrEmpty(home))
            {
                Console.Error.WriteLine("usage: juice-agent-launcher --home DIR [--seed DIR]");
                return 64;
            }
            using var stop = new CancellationTokenSource();
            void OnSignal(PosixSignalContext context)
            {
                context.Cancel = true;
                stop.Cancel();
            }
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
            var launcher = new Launcher(home, new AgentStarter(), TimeProvider.System, message => Console.WriteLine($"{DateTimeOffset.UtcNow:O} launcher: {message}"));
            if (seed is not null)
            {
                launcher.Seed(seed);
            }
            return await launcher.RunAsync(stop.Token);
        }
    }
}
