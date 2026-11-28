using System.Text.Json;

namespace Juice.Observability.Api.Integrations
{
    internal static class IntegrationMapping
    {
        public static string Etag(IntegrationInstance i) => $"\"{i.Version}\"";

        public static IntegrationPluginDto ToDto(IntegrationDescriptor d) => new(d.Key, d.DisplayName, d.Version, [.. d.Capabilities.Select(c => c.ToString()).Order(StringComparer.Ordinal)],
            ToDto(d.Config), ToDto(d.Scope), [.. d.Secrets.Select(s => new SecretDto(s.Name, s.Label, s.Required))], [.. d.RequiredPermissions],
            (int)d.DefaultInterval.TotalSeconds, (int)d.MinInterval.TotalSeconds);

        private static SchemaDto ToDto(IntegrationConfigSchema s) => new(s.Version, [.. s.Fields.Select(f =>
            new FieldDto(f.Name, Camel(f.Kind.ToString()), f.Label, f.Required, f.MaxLength, f.Min, f.Max, f.Allowed, f.Pattern, f.MaxItems))]);

        private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

        public static IntegrationInstanceDto ToDto(IntegrationInstance i, IIntegrationPluginRegistry plugins, DateTimeOffset now, bool detail)
        {
            var installed = plugins.TryGet(i.PluginKey, out var descriptor);
            return new IntegrationInstanceDto(i.Id, i.PluginKey, installed ? descriptor!.DisplayName : null, i.Name, i.State.ToString(), i.StateReason, i.IntervalSeconds, i.ConfigVersion,
                installed ? [.. descriptor!.Capabilities.Select(c => c.ToString()).Order(StringComparer.Ordinal)] : [],
                Health(i, installed && descriptor!.Config.Version == i.ConfigVersion, now), i.LastSuccessAt, i.LastRunAt, i.NextRunAt,
                detail ? Parse(i.Config) : null, detail ? Parse(i.Scope) : null,
                detail ? JsonSerializer.Deserialize<Dictionary<string, string>>(i.SecretRefs) : null,
                detail && i.LastValidation is not null ? JsonSerializer.Deserialize<ValidationDto>(i.LastValidation, JsonSerializerOptions.Web) : null,
                i.CreatedBy, i.CreatedAt, i.UpdatedBy, i.UpdatedAt, Etag(i));
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        /// <summary>What the instance's own record says about it; a run's detail was cleaned of secrets and capped when it was stored.</summary>
        public static IntegrationHealthDto Health(IntegrationInstance i, bool runtimeOk, DateTimeOffset now)
        {
            var running = i.State is IntegrationState.Enabled or IntegrationState.Degraded or IntegrationState.Failed;
            var failure = i.LastFailure;
            var connectivityFailed = failure is "Connectivity" or "Timeout" or "Remote" or "RateLimited";
            var authFailed = failure is "Authentication" or "Permission";
            var succeeded = i.LastOutcome == "Succeeded";
            string connectivity = connectivityFailed ? "failing" : succeeded ? "ok" : "unknown";
            string authentication = authFailed ? "failing" : succeeded ? "ok" : "unknown";
            string collection = !running ? "unknown" : i.State == IntegrationState.Enabled ? (i.LastOutcome is null ? "unknown" : "ok") : "failing";
            string freshness = "unknown";
            long? seconds = null;
            if (i.LastSuccessAt is { } at)
            {
                seconds = Math.Max(0, (long)(now - at).TotalSeconds);
                freshness = seconds >3L * i.IntervalSeconds ? "stale" : "ok";
            }
            return new IntegrationHealthDto(runtimeOk ? "ok" : "failing", connectivity, authentication, collection, freshness, seconds, failure, i.LastFailureDetail, i.ConsecutiveFailures);
        }
    }
}
