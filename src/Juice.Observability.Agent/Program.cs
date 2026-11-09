using System.Reflection;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// Runs a Juice Agent. The agent's own program calls it with no custom actions; a customer's build calls it from its own <c>Main</c> with theirs:
    /// <code>await JuiceAgent.RunAsync(args, actions =&gt; actions.Add&lt;QueueDepth&gt;());</code>
    /// Configuration: appsettings.json, then the file in <c>JUICE_AGENT_CONFIG</c> (this host's own settings and local allowlist), then
    /// environment variables (<c>Agent__PlatformUrl</c> and so on). The allowlist is read once, at start.
    /// </summary>
    public static class JuiceAgent
    {
        /// <returns>The exit code: 0, <see cref="AgentService.ConfigurationExitCode"/> for invalid configuration or custom actions, or what the agent set.</returns>
        public static async Task<int> RunAsync(string[] args, Action<AgentActionsBuilder>? actions = null)
        {
            if (Assembly.GetEntryAssembly() is { } program)
            {
                AgentService.Version = AgentService.VersionOf(program);
            }
            var added = new AgentActionsBuilder();
            actions?.Invoke(added);
            AgentActionCatalog catalog;
            try
            {
                catalog = new AgentActionCatalog(added.Actions);
            }
            catch (InvalidOperationException e)
            {
                // Before the host exists, so before logging: a service manager shows what the process wrote.
                await Console.Error.WriteLineAsync(e.Message);
                Environment.ExitCode = AgentService.ConfigurationExitCode;
                return Environment.ExitCode;
            }

            var builder = Host.CreateApplicationBuilder(args);
            if (Environment.GetEnvironmentVariable("JUICE_AGENT_CONFIG") is { Length: > 0 } file)
            {
                builder.Configuration.AddJsonFile(Path.GetFullPath(file), optional: false, reloadOnChange: false);
                builder.Configuration.AddEnvironmentVariables();
            }
            builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
            builder.Services.Configure<LocalAllowlist>(builder.Configuration.GetSection(LocalAllowlist.Section));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(catalog);
            builder.Services.AddHostedService<AgentService>();
            await builder.Build().RunAsync();
            return Environment.ExitCode;
        }
    }

    internal static class Program
    {
        private static Task<int> Main(string[] args) => JuiceAgent.RunAsync(args);
    }
}
