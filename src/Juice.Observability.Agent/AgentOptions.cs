namespace Juice.Observability.Agent
{
    /// <summary>Configuration section <c>Agent</c>: where the platform is, what to trust, and where to keep this agent's identity.</summary>
    public sealed class AgentOptions
    {
        public const string Section = "Agent";

        /// <summary>The platform's agent listener, <c>https://platform:5181</c>. Only outbound connections are ever made, to here.</summary>
        public string PlatformUrl { get; set; } = "";

        /// <summary>
        /// The CA that issued the platform's server certificate, PEM. The agent trusts only this for the platform, not the machine's store, so a
        /// certificate any public CA issued for the same name isn't accepted.
        /// </summary>
        public string PlatformCaFile { get; set; } = "";

        /// <summary>Where the agent keeps its key, certificate, the platform's command key and the commands it has seen. Readable only by the agent.</summary>
        public string DataDirectory { get; set; } = "";

        /// <summary>The one-time enrollment token from an admin, used once when there's no identity yet. Prefer <see cref="EnrollmentTokenFile"/>.</summary>
        public string? EnrollmentToken { get; set; }

        public string? EnrollmentTokenFile { get; set; }

        /// <summary>Where the host's <c>/proc</c> is, when the agent runs in a container with the host mounted (<c>/hostfs</c>).</summary>
        public string HostRoot { get; set; } = "/";

        /// <summary>How long a poll waits for a command when none is waiting, 1 to 55 seconds.</summary>
        public int PollWaitSeconds { get; set; } = 25;

        public IEnumerable<string> Validate()
        {
            if (!Uri.TryCreate(PlatformUrl, UriKind.Absolute, out var url) || url.Scheme != "https") yield return "Agent:PlatformUrl must be an https URL";
            if (PlatformCaFile.Length == 0) yield return "Agent:PlatformCaFile is required: the agent trusts only this CA for the platform";
            if (DataDirectory.Length == 0) yield return "Agent:DataDirectory is required";
            if (PollWaitSeconds is < 1 or > 55) yield return "Agent:PollWaitSeconds is 1 to 55";
        }
    }
}
