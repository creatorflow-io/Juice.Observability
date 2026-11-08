namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// How the agent and its launcher hand an update over, through files in the launcher's home directory. The launcher owns which version runs; the
    /// agent asks for a new one and proves it works, and never starts or replaces itself.
    /// <list type="number">
    /// <item>The running agent unpacks a checked package into <c>versions/{version}</c>, writes <see cref="UpdateFile"/>, and exits with
    /// <see cref="UpdateExitCode"/>.</item>
    /// <item>The launcher starts that version on trial, with <see cref="TrialVariable"/> set to the command id.</item>
    /// <item>The new version writes <see cref="ConfirmedFile"/> once it has reached the platform. Without it within the trial period, or if the new
    /// version exits, the launcher stops it and starts the version before.</item>
    /// <item>Either way the launcher writes <see cref="OutcomeFile"/>, and whichever version then runs reports it as the command's result.</item>
    /// </list>
    /// </summary>
    public static class AgentHome
    {
        /// <summary>Set by the launcher for the agent it starts: the home directory. Without it, the agent can't update itself.</summary>
        public const string HomeVariable = "JUICE_AGENT_HOME";
        /// <summary>Set by the launcher while a new version is on trial: the id of the update command.</summary>
        public const string TrialVariable = "JUICE_AGENT_TRIAL";
        /// <summary>The agent's exit code for "start the version in <see cref="UpdateFile"/>" (EX_TEMPFAIL).</summary>
        public const int UpdateExitCode = 75;

        public const string VersionsDirectory = "versions";
        public const string UpdateFile = "update.json";
        public const string ConfirmedFile = "confirmed";
        public const string OutcomeFile = "outcome.json";

        /// <summary>The agent asks for this version, already unpacked under <see cref="VersionsDirectory"/>.</summary>
        public sealed record UpdateRequest(Guid CommandId, string Version);

        /// <summary>How an update ended, for the agent to report. <c>Reason</c> says why it was rolled back; null when it succeeded.</summary>
        public sealed record UpdateOutcome(Guid CommandId, string From, string To, bool Succeeded, string? Reason, DateTimeOffset At);
    }
}
