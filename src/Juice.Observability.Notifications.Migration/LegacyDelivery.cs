using Juice.Observability.Health.Delivery;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// The legacy delivery rules the alias reads, in one place (M46b, waves §3): <see cref="DestinationPolicy"/>, <see cref="WebhookSigner"/> and
    /// <see cref="SecretReference"/> are in Juice.Observability.Health.Delivery since I72; only this file follows where they live.
    /// </summary>
    internal static class LegacyDelivery
    {
        /// <summary>Why legacy refuses an allowlist entry, or null: as <c>PUT /webhooks/allowlist</c> checks it.</summary>
        public static string? RefuseAllowlistEntry(string destination, IReadOnlyList<int> ports)
            => DestinationPolicy.ValidateEntry(new AllowedDestination(destination, [.. ports]));

        /// <summary>Whether the API may name this secret reference, by legacy's rule (<c>Webhooks:ApiSecretEnvPrefix</c>, <c>ApiSecretDirectory</c>).</summary>
        public static bool SecretAllowedFromApi(string reference, WebhookOptions options) => SecretReference.IsAllowedFromApi(reference, options.ApiSecretEnvPrefix, options.ApiSecretDirectory);

        /// <summary>Legacy's test notification (<c>webhook.test</c>), shaped like an alert's so every channel can show it: the body a test send carries.</summary>
        public static string TestPayload(Guid id, Guid tenantId, string endpointKey, string requestedBy, DateTimeOffset now)
            => WebhookDispatcher.TestPayload(id, new Juice.Observability.Data.WebhookEndpoint { TenantId = tenantId, Key = endpointKey, Url = "", SecretRef = "", DefinitionHash = "" },
                requestedBy, now);

        /// <summary>A chat message as legacy formats it for Slack or Teams: its title and its plain text (Slack's unescaped: the provider escapes it).</summary>
        public static (string Title, string Text) Chat(Juice.Observability.Data.ChannelKind kind, string payload, string? consoleUrl)
        {
            var view = NotificationView.Parse(payload);
            if (kind == Juice.Observability.Data.ChannelKind.Slack)
            {
                using var slack = System.Text.Json.JsonDocument.Parse(ChannelFormatter.SlackBody(view, consoleUrl));
                var blocks = slack.RootElement.GetProperty("blocks");
                return (blocks[0].GetProperty("text").GetProperty("text").GetString()!,
                    blocks[1].GetProperty("text").GetProperty("text").GetString()!.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&"));
            }
            using var teams = System.Text.Json.JsonDocument.Parse(ChannelFormatter.TeamsBody(view, consoleUrl));
            var body = teams.RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
            return (body[0].GetProperty("text").GetString()!, body[1].GetProperty("text").GetString()!);
        }

        /// <summary>An email as legacy formats it: its subject and text.</summary>
        public static (string Subject, string Text) Email(string payload, string? consoleUrl)
        {
            var (subject, body) = ChannelFormatter.Email(NotificationView.Parse(payload), consoleUrl);
            return (subject, body.TrimEnd().ReplaceLineEndings("\n"));
        }

        /// <summary>A receiver's check of a signed body, as legacy signs it.</summary>
        public static bool Verify(string secret, string id, string timestamp, string signature, string body, DateTimeOffset now, TimeSpan tolerance)
            => WebhookSigner.Verify(secret, id, timestamp, signature, body, now, tolerance);
    }
}
