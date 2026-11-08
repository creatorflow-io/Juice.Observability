using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// Who this agent is, from its enrollment, and the keys it trusts commands from by id: the one from enrollment, or after a rotation the newest
    /// (<see cref="CommandKeyId"/>) and the one that announced it.
    /// </summary>
    public sealed record AgentIdentity(Guid AgentId, string Tenant, IReadOnlyDictionary<string, ECDsa> CommandKeys, string CommandKeyId)
    {
        public AgentIdentity(Guid agentId, string tenant, ECDsa commandKey, string commandKeyId)
            : this(agentId, tenant, new Dictionary<string, ECDsa> { [commandKeyId] = commandKey }, commandKeyId)
        {
        }
    }

    /// <summary>A command that passed every check, with its parameters typed.</summary>
    public sealed record VerifiedCommand(CommandEnvelope Envelope, object Parameters);

    /// <summary>The outcome of checking one command: either it may run, or the reason it may not. <see cref="CommandId"/> is known once the payload reads.</summary>
    public sealed record Verification(VerifiedCommand? Command, string? Rejection, Guid? CommandId)
    {
        public static Verification Reject(string reason, Guid? commandId = null) => new(null, reason, commandId);
    }

    /// <summary>
    /// Every check an agent makes before acting, in this order, each refusal with its own reason: the bytes are signed by the key this agent
    /// trusts; the envelope reads and is a version it knows; it names this agent and this tenant; it is inside its time window and not issued for
    /// longer than commands may live; the action is one this agent knows, with valid parameters; and this host's local allowlist permits the
    /// action with those parameters. Replay is checked against the agent's memory of nonces, outside this pure check. Nothing a command contains
    /// can skip a step or choose how it's checked.
    /// </summary>
    public static partial class CommandVerifier
    {
        public static Verification Verify(SignedCommand signed, AgentIdentity identity, LocalAllowlist allowlist, DateTimeOffset now)
        {
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(signed.Payload);
            }
            catch (FormatException)
            {
                return Verification.Reject(RejectionReasons.Malformed);
            }
            if (payload.Length > CommandLimits.MaxPayloadBytes)
            {
                return Verification.Reject(RejectionReasons.Malformed);
            }
            if (!identity.CommandKeys.TryGetValue(signed.KeyId, out var key))
            {
                return Verification.Reject(RejectionReasons.UnknownKey, PeekId(payload));
            }
            if (!CommandSignature.Verify(key, payload, signed.Signature))
            {
                return Verification.Reject(RejectionReasons.InvalidSignature, PeekId(payload));
            }

            CommandEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<CommandEnvelope>(payload, CommandSignature.Json)
                    ?? throw new JsonException("empty");
            }
            catch (JsonException)
            {
                return Verification.Reject(RejectionReasons.Malformed, PeekId(payload));
            }
            var id = envelope.CommandId;
            if (envelope.Version != CommandEnvelope.CurrentVersion) return Verification.Reject(RejectionReasons.UnsupportedVersion, id);
            if (envelope.AgentId != identity.AgentId) return Verification.Reject(RejectionReasons.WrongTarget, id);
            if (!string.Equals(envelope.Tenant, identity.Tenant, StringComparison.Ordinal)) return Verification.Reject(RejectionReasons.WrongTenant, id);
            if (envelope.IssuedAt > now + CommandLimits.ClockSkew) return Verification.Reject(RejectionReasons.NotYetValid, id);
            if (envelope.ExpiresAt + CommandLimits.ClockSkew <= now) return Verification.Reject(RejectionReasons.Expired, id);
            if (envelope.ExpiresAt - envelope.IssuedAt > CommandLimits.MaxLifetime || envelope.ExpiresAt <= envelope.IssuedAt)
            {
                return Verification.Reject(RejectionReasons.LifetimeTooLong, id);
            }
            if (!Token().IsMatch(envelope.Nonce) || !Token().IsMatch(envelope.IdempotencyKey)) return Verification.Reject(RejectionReasons.Malformed, id);

            if (!AgentActions.All.Contains(envelope.Action)) return Verification.Reject(RejectionReasons.UnknownAction, id);
            if (AgentActions.Parse(envelope.Action, envelope.Parameters, out _) is not { } parameters) return Verification.Reject(RejectionReasons.InvalidParameters, id);
            // The platform signed it, and it may still not run here.
            if (allowlist.Check(envelope.Action, parameters) is { } refused) return Verification.Reject(refused, id);

            return new Verification(new VerifiedCommand(envelope, parameters), null, id);
        }

        /// <summary>
        /// A new command key, if a key this agent trusts announced it: the announcement's signature verifies with that key, over the new key's id and
        /// public key, and the id is that key's own. Null when any of it doesn't hold; the platform can't add a key any other way.
        /// </summary>
        public static ECDsa? VerifyAnnouncement(SignedKey announcement, AgentIdentity identity)
        {
            if (!identity.CommandKeys.TryGetValue(announcement.SignedBy, out var announcer)
                || !CommandSignature.Verify(announcer, SignedKey.Content(announcement.KeyId, announcement.PublicKeyPem), announcement.Signature))
            {
                return null;
            }
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(announcement.PublicKeyPem);
                if (key.KeySize == 256 && CommandSignature.KeyId(key) == announcement.KeyId)
                {
                    return key;
                }
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
            }
            key.Dispose();
            return null;
        }

        /// <summary>The command id of a payload that failed its checks, so the refusal can be reported against it; null if even that doesn't read.</summary>
        private static Guid? PeekId(byte[] payload)
        {
            try
            {
                using var json = JsonDocument.Parse(payload);
                return json.RootElement.TryGetProperty("commandId", out var id) && id.TryGetGuid(out var guid) ? guid : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$")]
        private static partial Regex Token();
    }
}
