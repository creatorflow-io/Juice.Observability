using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Agent.Launcher
{
    /// <summary>
    /// <c>juice-agent-launcher --home DIR [--seed DIR]</c>. The home holds the installed versions and the launcher's state; <c>--seed</c> installs
    /// a published agent there when that version isn't installed yet. The home may also come from <c>JUICE_AGENT_HOME</c>. It runs as a systemd
    /// service (<c>Type=notify</c>), as a Windows service, or as a plain process; under any of them it exits when the agent does, other than to
    /// update, with the agent's exit code, for the service manager to restart it (<c>deploy/agent</c>).
    /// </summary>
    internal static class Program
    {
        public const string ServiceName = "JuiceAgent";

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

            // The arguments are the launcher's own, not configuration.
            var builder = Host.CreateApplicationBuilder([]);
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
            builder.Services.AddSystemd();
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
                options.UseUtcTimestamp = true;
            });
            builder.Services.AddSingleton(new LaunchSettings(home, seed));
            builder.Services.AddHostedService<LauncherService>();
            await builder.Build().RunAsync();
            return Environment.ExitCode;
        }
    }

    internal sealed record LaunchSettings(string Home, string? Seed);

    /// <summary>The launcher inside the host: runs until the agent exits for good or the service is stopped, then stops the host with the agent's code.</summary>
    internal sealed class LauncherService(LaunchSettings settings, IHostApplicationLifetime lifetime, ILogger<LauncherService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var launcher = new Launcher(settings.Home, new AgentStarter(), TimeProvider.System, message => logger.LogInformation("launcher: {Message}", message));
                if (settings.Seed is not null)
                {
                    launcher.Seed(settings.Seed);
                }
                Environment.ExitCode = await launcher.RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogCritical(e, "launcher: can't run the agent");
                Environment.ExitCode = 1;
            }
            lifetime.StopApplication();
        }
    }
}
