namespace Juice.Observability.Api
{
    /// <summary>
    /// Host commands that 2.0 removed. <c>notifications migrate</c> moved the legacy webhooks onto the notification pipeline; there is nothing left
    /// to move, since the legacy webhooks are gone. Asked for, it says so and exits 2, rather than starting the host as a server.
    /// </summary>
    public static class RemovedCommands
    {
        /// <summary>The exit code of a removed command.</summary>
        public const int ExitCode = 2;

        /// <summary>Whether the arguments name a removed command.</summary>
        public static bool IsRemoved(string[] args) => args is ["notifications", "migrate", ..];

        /// <summary>Tells why on <paramref name="output"/> and returns <see cref="ExitCode"/>.</summary>
        public static async Task<int> RunAsync(string[] args, TextWriter output)
        {
            await output.WriteLineAsync($"'{string.Join(' ', args.Take(2))}' was removed in 2.0: the legacy webhooks it migrated are gone, and the notification pipeline is the only one. "
                + "Declare endpoints, routes and the allowlist in the file named by Notifications:Provisioning:Path (docs/deployment.md).");
            return ExitCode;
        }
    }
}
