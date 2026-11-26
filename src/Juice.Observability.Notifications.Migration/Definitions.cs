using Juice.Observability.Data;

namespace Juice.Observability.Notifications.Migration
{
    // What the mapping reads and writes, as values: a legacy endpoint, policy or allowlist entry on one side, the notifications rows it becomes on
    // the other. Records compare by value (lists through ValueList), so the round trip Unmap(Map(x)) == x is checked by equality (proof point 5).

    /// <summary>A legacy endpoint's definition: every field a <see cref="WebhookEndpoint"/> row holds that the API or the file sets.</summary>
    public sealed record LegacyEndpoint(string Key, ChannelKind Kind, string Url, string SecretRef, ValueList<string> Events, ValueList<string> RuleKeys,
        ValueList<string> Environments, ValueList<string> Recipients, int GroupWindowSeconds, AlertSeverity MinimumSeverity, bool Enabled)
    {
        public static LegacyEndpoint From(WebhookEndpoint e) => new(e.Key, e.Kind, e.Url, e.SecretRef, e.Events, e.RuleKeys, e.Environments, e.Recipients,
            e.GroupWindowSeconds, e.MinimumSeverity, e.Enabled);
    }

    /// <summary>A legacy step: minutes after the alert fired, and the endpoints it notifies.</summary>
    public sealed record LegacyStep(int AfterMinutes, ValueList<string> EndpointKeys);

    /// <summary>
    /// The part of a legacy policy that moves to notifications: its steps and <see cref="StopOnAcknowledge"/>. Its selection (severity, rules,
    /// environments, enabled) and name stay in Observability, where the producer adapter (M45) picks the policy (§5.3).
    /// </summary>
    public sealed record LegacyPolicy(string Key, bool StopOnAcknowledge, ValueList<LegacyStep> Steps)
    {
        public static LegacyPolicy From(EscalationPolicy p) => new(p.Key, p.StopOnAcknowledge, p.Steps.Select(s => new LegacyStep(s.AfterMinutes, s.EndpointKeys)).ToList());
    }

    /// <summary>A legacy allowlist entry. No ports is 443 only, on both sides.</summary>
    public sealed record LegacyAllowlistEntry(string Destination, ValueList<int> Ports)
    {
        public static LegacyAllowlistEntry From(WebhookAllowlistEntry a) => new(a.Destination, a.Ports);
    }

    /// <summary>An integration as <c>PutIntegration</c> states it. <see cref="Settings"/> is canonical JSON.</summary>
    public sealed record IntegrationDefinition(string Key, string Channel, string Kind, string Settings, string? SecretRef, int Priority, bool Enabled);

    /// <summary>A target as <c>PutTarget</c> states it.</summary>
    public sealed record TargetDefinition(string IntegrationKey, string Key, string? Address, string? AddressRef, bool Enabled);

    /// <summary>
    /// A route as <c>PutRoute</c> states it. <see cref="Match"/> is canonical JSON. <see cref="DigestSeconds"/> is the legacy group window, which
    /// <c>PutRoute</c> takes from M46-1 (until then the report says <c>pending_feature</c> for a non-zero one).
    /// </summary>
    public sealed record RouteDefinition(string Key, string Match, ValueList<string> Targets, bool Enabled, int DigestSeconds);

    /// <summary>What one legacy endpoint becomes: an integration, its targets (one per address), and the route that sends to them.</summary>
    public sealed record EndpointSetup(IntegrationDefinition Integration, ValueList<TargetDefinition> Targets, RouteDefinition Route);

    /// <summary>A notifications escalation policy as <c>PutEscalationPolicy</c> states it. <see cref="Steps"/> is canonical JSON.</summary>
    public sealed record PolicyDefinition(string Key, string Steps, bool StopOnAcknowledge);

    /// <summary>A <c>destination_allowlist</c> row as <c>PutAllowlistEntry</c> states it.</summary>
    public sealed record AllowlistDefinition(string Destination, ValueList<int> Ports);

    /// <summary>Why something can't be migrated as it stands; an admin changes the legacy definition (or the notifications row in the way).</summary>
    public sealed record MappingProblem(string Code, string Message);

    /// <summary>A mapping's result: the value, or what stops it.</summary>
    public sealed record Mapped<T>(T? Value, ValueList<MappingProblem> Problems) where T : class
    {
        public bool Succeeded => Value is not null && Problems.Count == 0;

        public static Mapped<T> Ok(T value) => new(value, ValueList<MappingProblem>.Empty);

        public static Mapped<T> Refused(params MappingProblem[] problems) => new(null, problems);

        public static Mapped<T> Refused(IEnumerable<MappingProblem> problems) => new(null, problems.ToList());
    }

    /// <summary>The codes of <see cref="MappingProblem"/>.</summary>
    public static class ProblemCodes
    {
        public const string InvalidKey = "invalid_key";
        public const string KeyTaken = "key_taken";
        public const string GroupWindow = "invalid_group_window";
        /// <summary>Decision G4: a legacy key with a digit first.</summary>
        public const string KeyStartsWithDigit = "key_starts_with_digit";
        /// <summary>Decision G2: what the pinned notifications alpha can't write yet (a route's digestSeconds before M46-1).</summary>
        public const string PendingFeature = "pending_feature";
        /// <summary>The alias's copy of an endpoint nothing in legacy writes any more: disabled (M46c drift).</summary>
        public const string Stale = "stale";
        public const string Events = "unsupported_event";
        public const string Recipients = "invalid_recipients";
        public const string SecretOutOfReach = "secret_out_of_reach";
        public const string SecretRef = "invalid_secret_ref";
        public const string SmtpServer = "smtp_server_unset";
        public const string Steps = "invalid_steps";
        public const string NonIncreasingSteps = "non_increasing_steps";
        public const string UnknownEndpoint = "unknown_endpoint";
        public const string OverlappingPolicies = "overlapping_policies";
        public const string Destination = "invalid_destination";
        public const string NotAliasShaped = "not_alias_shaped";
    }
}
