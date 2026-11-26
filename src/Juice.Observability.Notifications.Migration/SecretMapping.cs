using Juice.Notifications.Providers;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// A legacy secret reference as a notifications one (phase-5-plan I70). Both name a secret the same way, so the text is kept:
    /// <c>env:JUICE_WEBHOOK_…</c> stays an <c>env:</c> reference and a file in <c>Webhooks:ApiSecretDirectory</c> stays a <c>file:</c> one. What the
    /// mapping checks is reach: the alias writes through the API's rules, so notifications must allow the API to name it
    /// (<c>Notifications:Delivery:ApiSecretEnvPrefix</c> and <c>ApiSecretDirectory</c> set as the legacy ones are). No value is ever read: only
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
            if (!SecretReferences.IsAllowedFromApi(reference, delivery))
            {
                var rule = reference.StartsWith("env:", StringComparison.Ordinal)
                    ? $"notifications lets the API name only env:{delivery.ApiSecretEnvPrefix}… (Notifications:Delivery:ApiSecretEnvPrefix)"
                    : delivery.ApiSecretDirectory is { Length: > 0 } directory
                        ? $"notifications lets the API name only files in {directory} (Notifications:Delivery:ApiSecretDirectory)"
                        : "notifications lets the API name no file (Notifications:Delivery:ApiSecretDirectory isn't set)";
                return Mapped<string>.Refused(new MappingProblem(ProblemCodes.SecretOutOfReach, $"{reference} is out of reach: {rule}."));
            }
            return Mapped<string>.Ok(reference);
        }
    }
}
