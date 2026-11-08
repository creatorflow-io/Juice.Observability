namespace Juice.Observability.Agent
{
    /// <summary>
    /// Juice Agent. Configuration: appsettings.json, then the file in <c>JUICE_AGENT_CONFIG</c> (this host's own settings and local allowlist), then
    /// environment variables (<c>Agent__PlatformUrl</c> and so on). The allowlist is read once, at start.
    /// </summary>
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            if (Environment.GetEnvironmentVariable("JUICE_AGENT_CONFIG") is { Length: > 0 } file)
            {
                builder.Configuration.AddJsonFile(Path.GetFullPath(file), optional: false, reloadOnChange: false);
                builder.Configuration.AddEnvironmentVariables();
            }
            builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
            builder.Services.Configure<LocalAllowlist>(builder.Configuration.GetSection(LocalAllowlist.Section));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddHostedService<AgentService>();
            await builder.Build().RunAsync();
        }
    }
}
