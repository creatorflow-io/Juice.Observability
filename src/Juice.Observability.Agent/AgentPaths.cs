namespace Juice.Observability.Agent
{
    /// <summary>
    /// How the agent compares paths, in one place: the local allowlist decides with it what a command may ask about, and <c>disk.usage</c> chooses
    /// with it what to report, so the two can't disagree.
    /// </summary>
    internal static class AgentPaths
    {
        public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Forward slashes, no trailing slash, <c>/</c> for the root.</summary>
        public static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";

        /// <summary>The path itself or something below it: <c>/data</c> covers <c>/data/logs</c> but not <c>/database</c>. Both normalized.</summary>
        public static bool IsUnder(string path, string prefix, StringComparison comparison)
            => prefix == "/" ? path.StartsWith('/') : path.Equals(prefix, comparison) || path.StartsWith(prefix + "/", comparison);
    }
}
