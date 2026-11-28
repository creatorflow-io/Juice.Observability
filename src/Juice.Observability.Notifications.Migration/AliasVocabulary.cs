using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// The names the migration and the webhooks alias share with the producer adapter (M45): the types an alert transition becomes, the labels
    /// it carries, and the owner of the rows the alias keeps in the notifications schema.
    /// </summary>
    public static class AliasVocabulary
    {
        /// <summary>
        /// The owner of every row the migration and the alias write (phase-5-plan I69): notifications'
        /// <c>ManagedBy.WebhooksAlias</c> (M44-0).
        /// </summary>
        public const string WebhooksAlias = Juice.Notifications.ManagedBy.WebhooksAlias;

        /// <summary>
        /// The owner of the rows the alias writes from the deployment's own <c>Webhooks:Endpoints</c> and <c>Webhooks:Allowlist</c> (M46c; waves
        /// §6.4 B): notifications' <c>ManagedBy.AliasConfig</c>. Only the alias's provisioner writes it; the alias's API and the migration tool
        /// leave it alone, as legacy's API left a configuration-declared endpoint alone.
        /// </summary>
        public const string AliasConfig = Juice.Notifications.ManagedBy.AliasConfig;

        /// <summary>Whether a notifications row is the alias's, from its API or its configuration.</summary>
        public static bool IsAlias(string? owner) => owner is WebhooksAlias or AliasConfig;

        /// <summary>
        /// Whether a writer as <paramref name="owner"/> may write over a row <paramref name="existing"/> keeps: its own, or the alias's
        /// configuration taking over the alias's (an endpoint made through the API, then declared in the file).
        /// </summary>
        public static bool Writes(string owner, string existing) => existing == owner || existing == WebhooksAlias && owner == AliasConfig;

        /// <summary>The type an <c>alert.firing</c> transition is sent as.</summary>
        public const string FiringType = "observability.alert.firing";

        /// <summary>The type an <c>alert.resolved</c> transition is sent as.</summary>
        public const string ResolvedType = "observability.alert.resolved";

        /// <summary>The labels the producer adapter sets on an alert's request, which routes match on.</summary>
        public static class Labels
        {
            public const string Rule = "rule";
            public const string Severity = "severity";
            public const string Environment = "environment";
        }

        /// <summary>The legacy event an endpoint takes, as the notification type it becomes; null for one that isn't an alert event.</summary>
        public static string? TypeOf(string legacyEvent) => legacyEvent switch
        {
            NotificationPlan.Firing => FiringType,
            NotificationPlan.Resolved => ResolvedType,
            _ => null,
        };

        /// <summary>The inverse of <see cref="TypeOf"/>.</summary>
        public static string? EventOf(string type) => type switch
        {
            FiringType => NotificationPlan.Firing,
            ResolvedType => NotificationPlan.Resolved,
            _ => null,
        };

        /// <summary>The severity levels, lowest first.</summary>
        public static readonly IReadOnlyList<AlertSeverity> Severities = [AlertSeverity.Info, AlertSeverity.Warning, AlertSeverity.Critical];

        /// <summary>A severity as the producer adapter (M45) writes it in the <c>severity</c> label: lowercase (<c>info</c>, <c>warning</c>, <c>critical</c>).</summary>
        public static string Label(AlertSeverity severity) => severity.ToString().ToLowerInvariant();
    }
}
