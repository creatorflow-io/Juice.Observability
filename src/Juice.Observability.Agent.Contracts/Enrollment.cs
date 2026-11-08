namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// An agent's first call: a one-time token from an admin, and a certificate signing request for a key the agent generated and keeps. The
    /// private key never leaves the host.
    /// </summary>
    /// <param name="Token">The enrollment token, used once.</param>
    /// <param name="Csr">PKCS#10, PEM.</param>
    /// <param name="Hostname">The host's name, which links the agent to the host discovery found.</param>
    /// <param name="OperatingSystem">The OS description, shown with the agent.</param>
    /// <param name="AgentVersion">The agent's version.</param>
    public sealed record EnrollRequest(string Token, string Csr, string Hostname, string OperatingSystem, string AgentVersion);

    /// <summary>
    /// The agent's identity and what it needs to trust the platform's commands: its client certificate, and the public key that signs commands,
    /// which the agent keeps and checks every command against.
    /// </summary>
    public sealed record EnrollResponse(Guid AgentId, string Tenant, string CertificatePem, string CommandKeyPem, string CommandKeyId);

    /// <summary>A new certificate for a new key, before the current one expires. Sent over mTLS with the current certificate.</summary>
    public sealed record RenewRequest(string Csr);

    public sealed record RenewResponse(string CertificatePem);

    /// <summary>What a poll returns: the commands waiting for this agent, oldest first.</summary>
    public sealed record PollResponse(IReadOnlyList<SignedCommand> Commands);

    /// <summary>Paths of the agent API, on the platform's mTLS listener.</summary>
    public static class AgentRoutes
    {
        public const string Enroll = "/agent/v1/enrollment";
        public const string Certificate = "/agent/v1/certificate";
        public const string Commands = "/agent/v1/commands";
        public static string Result(Guid commandId) => $"/agent/v1/commands/{commandId}/result";
        /// <summary>Headers an agent sends on each poll, so the platform knows its version and host.</summary>
        public const string VersionHeader = "Juice-Agent-Version";
        public const string HostnameHeader = "Juice-Agent-Hostname";
    }
}
