using Juice.Notifications.Providers;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// A legacy secret reference as a notifications one (phase-5-plan I70). Both name a secret the same way, so the text is kept:
    /// <c>env:JUICE_WEBHOOK_…</c> stays an <c>env:</c> reference and a file in <c>Webhooks:ApiSecretDirectory</c> stays a <c>file:</c> one. What the
    /// mapping checks is reach: the alias writes as <c>webhooks-alias</c>, so notifications must allow its alias to name it (M46-3:
    /// <c>Delivery:AliasSecretEnvPrefix</c> and <c>AliasSecretDirectory</c>, which the host sets to the legacy ones). No value is ever read: only
    /// the reference's text is looked at.
    /// </summary>
    public static class SecretMapping
    {
        public static Mapped<string> Map(string? reference, DeliveryOptions delivery)
        {
            if (reference is not { Length: > 0 } || !(reference.StartsWith("env:", StringComparison.Ordinal) && reference.Length > 4
                || reference.StartsWith("file:", StringComparison.Ordinal) && reference.Length > 5) || !SecretReferences.IsValid(reference))
            {
                return Mapped<string>.Refused(new MappingProblem(ProblemCodes.SecretRef, "the secret reference isn't env:NAME or file:/path."));
            }
            if (!SecretReferences.IsAllowedFromAlias(reference, delivery))
            {
                var rule = reference.StartsWith("env:", StringComparison.Ordinal)
                    ? delivery.AliasSecretEnvPrefix is { Length: > 0 } prefix
                        ? $"notifications lets the alias name only env:{prefix}… (Notifications:Delivery:AliasSecretEnvPrefix)"
                        : "notifications lets the alias name no environment variable (Notifications:Delivery:AliasSecretEnvPrefix isn't set)"
                    : delivery.AliasSecretDirectory is { Length: > 0 } directory
                        ? $"notifications lets the alias name only files in {directory} (Notifications:Delivery:AliasSecretDirectory)"
                        : "notifications lets the alias name no file (Notifications:Delivery:AliasSecretDirectory isn't set)";
                return Mapped<string>.Refused(new MappingProblem(ProblemCodes.SecretOutOfReach, $"{reference} is out of reach: {rule}."));
            }
            return Mapped<string>.Ok(reference);
        }
    }
}
