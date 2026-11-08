using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Juice.Observability.Agent.Contracts
{
    /// <summary>
    /// One command for one agent, as the platform signed it. The agent checks every field itself before acting, whatever the platform says:
    /// the target, the tenant, the time window, the nonce, and the action and its parameters against its own allowlist.
    /// </summary>
    public sealed record CommandEnvelope(
        int Version,
        Guid CommandId,
        string Tenant,
        Guid AgentId,
        string Action,
        JsonElement Parameters,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string Nonce,
        string IdempotencyKey,
        string? ApprovalReference)
    {
        public const int CurrentVersion = 1;
    }

    /// <summary>
    /// A command on the wire: the envelope's exact bytes and the platform's signature over them. The agent verifies the bytes it received and only
    /// then reads them, so no re-serialization can change what was signed.
    /// </summary>
    /// <param name="Payload">The envelope as UTF-8 JSON, base64.</param>
    /// <param name="Signature">ECDSA P-256 with SHA-256 over the payload bytes, IEEE P1363 format, base64.</param>
    /// <param name="KeyId">Which platform key signed it, so the key can be rotated.</param>
    public sealed record SignedCommand(string Payload, string Signature, string KeyId);

    /// <summary>The limits every command is held to, by the platform when it issues one and by the agent when it receives one.</summary>
    public static class CommandLimits
    {
        /// <summary>A command is valid for at most this long after it was issued.</summary>
        public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
        /// <summary>How far apart the platform's and the agent's clocks may be.</summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
        public const int MaxPayloadBytes = 16 * 1024;
        public const int MaxResultBytes = 64 * 1024;
    }

    /// <summary>Why an agent refused a command. Stable codes: the platform records them and the gate's tests check them.</summary>
    public static class RejectionReasons
    {
        public const string Malformed = "malformed";
        public const string InvalidSignature = "invalid_signature";
        public const string UnknownKey = "unknown_key";
        public const string UnsupportedVersion = "unsupported_version";
        public const string WrongTarget = "wrong_target";
        public const string WrongTenant = "wrong_tenant";
        public const string NotYetValid = "not_yet_valid";
        public const string Expired = "expired";
        public const string LifetimeTooLong = "lifetime_too_long";
        public const string Replayed = "replayed";
        public const string UnknownAction = "unknown_action";
        /// <summary>The action is valid and signed, but this host's local allowlist doesn't permit it.</summary>
        public const string ActionNotAllowed = "action_not_allowed";
        public const string InvalidParameters = "invalid_parameters";
        /// <summary>The parameters are valid for the action, but outside this host's local bounds.</summary>
        public const string OutsideLocalBounds = "outside_local_bounds";
    }

    /// <summary>Signing and verifying command bytes. The algorithm is fixed: no field of a command can choose it.</summary>
    public static class CommandSignature
    {
        public static string Sign(ECDsa key, byte[] payload)
            => Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public static bool Verify(ECDsa publicKey, byte[] payload, string signature)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(signature);
            }
            catch (FormatException)
            {
                return false;
            }
            return publicKey.VerifyData(payload, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        /// <summary>A short, stable name for a public key: the start of the SHA-256 of its SubjectPublicKeyInfo.</summary>
        public static string KeyId(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

        /// <summary>The JSON every command and result uses: camelCase, strict about unknown members.</summary>
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        };

        public static byte[] Serialize(CommandEnvelope envelope) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, Json));
    }

    /// <summary>Written as its name wherever it's serialized, whatever the serializer's own settings.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CommandOutcome>))]
    public enum CommandOutcome { Succeeded, Failed, Rejected }

    /// <summary>What an agent reports for a command: the action's data when it succeeded, the reason when it failed or was refused.</summary>
    public sealed record CommandResult(Guid CommandId, CommandOutcome Outcome, string? Reason, JsonElement? Data, int DurationMs);
}
