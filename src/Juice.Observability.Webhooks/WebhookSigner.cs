using System.Security.Cryptography;
using System.Text;

namespace Juice.Observability.Webhooks
{
    /// <summary>
    /// Webhook signatures in the Standard Webhooks form (requirements §8). The sender signs
    /// <c>{webhook-id}.{webhook-timestamp}.{body}</c> with HMAC-SHA256 over the exact body bytes and sends
    /// <c>webhook-signature: v1,{base64}</c>. A receiver recomputes it, compares in constant time, and rejects
    /// timestamps outside its replay window and ids it has already processed.
    /// </summary>
    public static class WebhookSigner
    {
        public const string IdHeader = "webhook-id";
        public const string TimestampHeader = "webhook-timestamp";
        public const string SignatureHeader = "webhook-signature";

        public static string Sign(string secret, string id, long timestamp, string body)
        {
            var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
            return $"v1,{Convert.ToBase64String(mac)}";
        }

        /// <summary>
        /// Receiver-side check, also the reference for receivers in other languages: the timestamp is within
        /// <paramref name="tolerance"/> of <paramref name="now"/>, and one of the space-separated <c>v1</c> signatures
        /// matches.
        /// </summary>
        public static bool Verify(string secret, string id, string timestampHeader, string signatureHeader, string body,
            DateTimeOffset now, TimeSpan tolerance)
        {
            if (!long.TryParse(timestampHeader, out var timestamp)
                || (now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > tolerance)
            {
                return false;
            }
            var expected = Encoding.ASCII.GetBytes(Sign(secret, id, timestamp, body));
            foreach (var candidate in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Resolves a secret reference: <c>env:NAME</c> or <c>file:/path</c>. Secrets are read when sending, never stored or logged.</summary>
    public static class SecretReference
    {
        public static bool IsValid(string reference)
            => reference.StartsWith("env:", StringComparison.Ordinal) && reference.Length > 4
            || reference.StartsWith("file:", StringComparison.Ordinal) && reference.Length > 5;

        public static string Resolve(string reference)
        {
            var value = reference switch
            {
                _ when reference.StartsWith("env:", StringComparison.Ordinal) => Environment.GetEnvironmentVariable(reference[4..]),
                _ when reference.StartsWith("file:", StringComparison.Ordinal) && File.Exists(reference[5..]) => File.ReadAllText(reference[5..]).Trim(),
                _ => null,
            };
            return string.IsNullOrEmpty(value)
                ? throw new InvalidOperationException($"Webhook secret '{reference}' is not set")
                : value;
        }
    }
}
