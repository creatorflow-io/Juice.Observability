using System.Text.Json;

namespace Juice.Observability.Api.Integrations
{
    // Shapes of the integrations API. Field kinds are camelCase ('stringList'); states and capabilities are the enum names.

    internal sealed record FieldDto(string Name, string Kind, string Label, bool Required, int? MaxLength, double? Min, double? Max, IReadOnlyList<string>? Allowed, string? Pattern, int? MaxItems);

    internal sealed record SchemaDto(int Version, IReadOnlyList<FieldDto> Fields);

    internal sealed record SecretDto(string Name, string Label, bool Required);

    internal sealed record IntegrationPluginDto(string Key, string DisplayName, string Version, IReadOnlyList<string> Capabilities, SchemaDto Config, SchemaDto Scope,
        IReadOnlyList<SecretDto> Secrets, IReadOnlyList<string> RequiredPermissions, int DefaultIntervalSeconds, int MinIntervalSeconds);

    /// <summary>Each of runtime, connectivity, authentication and collection is <c>ok</c>, <c>failing</c> or <c>unknown</c>; freshness is <c>ok</c>, <c>stale</c> or <c>unknown</c>.</summary>
    internal sealed record IntegrationHealthDto(string Runtime, string Connectivity, string Authentication, string Collection, string Freshness, long? FreshnessSeconds,
        string? LastFailure, string? LastFailureDetail, int ConsecutiveFailures);

    internal sealed record ValidationDto(bool Passed, IReadOnlyList<ValidationCheck> Checks, DateTimeOffset At);

    /// <summary>Config, scope, secret references and the last validation are present only for an admin reading one instance, never in a list.</summary>
    internal sealed record IntegrationInstanceDto(Guid Id, string PluginKey, string? PluginName, string Name, string State, string? StateReason, int IntervalSeconds, int ConfigVersion,
        IReadOnlyList<string> Capabilities, IntegrationHealthDto Health, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt,
        JsonElement? Config, JsonElement? Scope, IReadOnlyDictionary<string, string>? SecretRefs, ValidationDto? LastValidation,
        string CreatedBy, DateTimeOffset CreatedAt, string UpdatedBy, DateTimeOffset UpdatedAt, string Etag);

    internal sealed record IntegrationInstanceRequest(string? PluginKey, string? Name, JsonElement? Config, JsonElement? Scope, Dictionary<string, string>? SecretRefs, int? IntervalSeconds);

    internal sealed record IntegrationValidateResponse(IntegrationInstanceDto Instance, ValidationDto Report);
}
