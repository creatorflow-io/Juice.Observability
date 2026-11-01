namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// A link into an optional external tool for the same subject. Juice Console never needs one: it links to its own
    /// pages. A deployment without integrations returns an empty list.
    /// </summary>
    public sealed record IntegrationLinkDto(string Integration, string Title, string Url);

    /// <summary>Configuration section <c>Integrations</c>: optional external tools (UI architecture §5, §7).</summary>
    public sealed class IntegrationOptions
    {
        public const string Section = "Integrations";

        public GrafanaIntegration Grafana { get; set; } = new();
    }

    public sealed class GrafanaIntegration
    {
        /// <summary>For example <c>http://localhost:3000</c>. Empty: no Grafana links.</summary>
        public string BaseUrl { get; set; } = "";
    }

    public static class IntegrationLinks
    {
        /// <summary>
        /// Links for one service at <paramref name="at"/>. The Grafana link opens the Juice overview dashboard over the hour
        /// before it: the name comes from the platform's own record and is URL-encoded, and the time range is computed
        /// here, never taken from input.
        /// </summary>
        public static IReadOnlyList<IntegrationLinkDto> ForService(IntegrationOptions options, string serviceName, DateTimeOffset at)
        {
            if (!Uri.TryCreate(options.Grafana.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            {
                return [];
            }
            var to = at.ToUnixTimeMilliseconds();
            var from = at.AddHours(-1).ToUnixTimeMilliseconds();
            return [new("grafana", "Grafana: Juice overview",
                $"{baseUri.GetLeftPart(UriPartial.Authority)}/d/juice-overview?var-service={Uri.EscapeDataString(serviceName)}&from={from}&to={to}")];
        }
    }
}
