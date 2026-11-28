using Juice.Observability.Extensibility;

namespace Juice.Observability.Api
{
    /// <summary>What a site runs (<c>GET /api/v1/modules</c>, any platform role): names and versions, read without the database.</summary>
    public sealed record ModulesDto(string CoreVersion, IReadOnlyList<ModuleDto> Items);

    /// <summary><see cref="Kind"/> is <c>core</c> for the platform itself and <c>module</c> for every other.</summary>
    public sealed record ModuleDto(string Name, string DisplayName, string Version, string Kind, string SupportedCore, ConsoleManifest? Console);
}
