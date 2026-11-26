using Juice.Observability.Webhooks;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// The legacy delivery rules the alias reads, in one place (M46b, waves §3): M49 moves <see cref="DestinationPolicy"/> and
    /// <see cref="WebhookSigner"/> into Juice.Observability.Health, and only this file follows them.
    /// </summary>
    internal static class LegacyDelivery
    {
        /// <summary>Why legacy refuses an allowlist entry, or null: as <c>PUT /webhooks/allowlist</c> checks it.</summary>
        public static string? RefuseAllowlistEntry(string destination, IReadOnlyList<int> ports)
            => DestinationPolicy.ValidateEntry(new AllowedDestination(destination, [.. ports]));

        /// <summary>Whether the API may name this secret reference, by legacy's rule (<c>Webhooks:ApiSecretEnvPrefix</c>, <c>ApiSecretDirectory</c>).</summary>
        public static bool SecretAllowedFromApi(string reference, WebhookOptions options) => SecretReference.IsAllowedFromApi(reference, options);

        /// <summary>A receiver's check of a signed body, as legacy signs it.</summary>
        public static bool Verify(string secret, string id, string timestamp, string signature, string body, DateTimeOffset now, TimeSpan tolerance)
            => WebhookSigner.Verify(secret, id, timestamp, signature, body, now, tolerance);
    }
}
