using System.Text.RegularExpressions;

namespace Juice.Observability.Extensibility;

/// <summary>Composition-time checks. Each returns null when valid, otherwise the reason (the composer wraps it in a <see cref="ModuleCompositionException"/>).</summary>
internal static partial class ModuleValidation
{
    public const int MaxEntries = 50, MaxLabel = 64;

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,30}[a-z0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();

    // SemVer 2.0.0, the regular expression the specification gives
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRule();

    /// <summary>Names an external module can't take: the core's own, the internal modules' and the core's tenant-root segments (a floor; the host pins the full list to the real routes).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "core", "platform", "slos", "agents", "integrations", "modules", "tenants", "notifications", "inbox", "capacity", "correlation", "escalation",
        "action-requests", "alerts", "applications", "changes", "environments", "escalation-policies", "health", "incidents",
        "load-runs", "resources", "rules", "search", "silences", "telemetry", "timeline", "topology",
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9/_:-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathRule();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRule();

    // Material icon names use underscores (show_chart)
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IconRule();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PolicySuffixRule();

    /// <param name="name">The module name.</param>
    /// <param name="allowReserved">True only on the internal path (core, slos, agents).</param>
    public static string? Name(string? name, bool allowReserved = false)
        => name is null || name.Length > 32 || !NameRule().IsMatch(name)
            ? "the name must match ^[a-z][a-z0-9-]{0,30}[a-z0-9]$ (2 to 32 characters, lower case ASCII)"
            : !allowReserved && Reserved.Contains(name)
                ? $"the name '{name}' is reserved by the core"
                : null;

    public static string? DisplayName(string? displayName)
        => PlainText(displayName, 64) ? null : "the display name must be 1 to 64 characters of plain text";

    public static string? Version(string? version)
        => version is not null && version.Length <= 128 && SemVerRule().IsMatch(version)
            ? null
            : "the version must be SemVer 2.0 (for example 1.2.3 or 1.2.3-alpha.1)";

    public static string? SupportedCore(string? range)
        => range is not null && CoreVersionRange.TryParse(range, out _)
            ? null
            : "the supported core range must be a NuGet interval such as [2.0.0,3.0.0)";

    /// <summary>Name, display name, version and range together.</summary>
    public static string? Info(ModuleInfo info, bool allowReserved = false)
        => info is null ? "the module has no info" : Name(info.Name, allowReserved) ?? DisplayName(info.DisplayName) ?? Version(info.Version) ?? SupportedCore(info.SupportedCore);

    /// <summary>A policy name carries the module prefix; roles are a non-empty subset of the core roles.</summary>
    public static string? Policy(ModuleInfo module, string? name, string[]? roles)
    {
        var prefix = module.Name + ".";
        if (name is not null && (name == CorePolicies.Read || name == CorePolicies.Operate || name.StartsWith("platform.", StringComparison.Ordinal)))
        {
            return $"the policy name '{name}' belongs to the core";
        }
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal) || !PolicySuffixRule().IsMatch(name[prefix.Length..]))
        {
            return $"the policy name must be '{prefix}<x>'";
        }
        if (roles is null or { Length: 0 })
        {
            return $"the policy '{name}' needs at least one role";
        }
        var unknown = roles.FirstOrDefault(role => role is null || !CoreRoles.All.Contains(role));
        return unknown is null ? null : $"the policy '{name}' names '{unknown}', which is not a core role";
    }

    /// <summary>Validates the manifest; a policy must be a core policy or one of <paramref name="modulePolicies"/>.</summary>
    public static string? Manifest(ConsoleManifest? manifest, ModuleInfo module, IReadOnlySet<string> modulePolicies)
        => Manifest(manifest, module, modulePolicies, isInternal: false);

    /// <summary>The library that holds the pages of the core, slos and agents modules.</summary>
    public const string CorePackage = "@juice-js/observability";

    /// <summary>
    /// As the three-argument form. <paramref name="isInternal"/> (core, slos, agents; only the composer's own path sets it) lets the manifest declare
    /// <see cref="CorePackage"/> and the empty route (the Overview). An external module keeps <c>@juice-js/observability-&lt;name&gt;</c> and relative, non-empty routes.
    /// </summary>
    public static string? Manifest(ConsoleManifest? manifest, ModuleInfo module, IReadOnlySet<string> modulePolicies, bool isInternal)
    {
        if (manifest is null)
        {
            return "the Console manifest is null";
        }
        if (isInternal ? manifest.Package != CorePackage : manifest.Package != $"@juice-js/observability-{module.Name}")
        {
            return isInternal ? $"the Console package of an internal module must be {CorePackage}" : $"the Console package must be @juice-js/observability-{module.Name}";
        }
        if (manifest.Routes is null || manifest.Menu is null)
        {
            return "the Console manifest needs routes and menu lists";
        }
        if (manifest.Routes.Count > MaxEntries || manifest.Menu.Count > MaxEntries)
        {
            return $"the Console manifest allows at most {MaxEntries} routes and {MaxEntries} menu entries";
        }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in manifest.Routes)
        {
            var problem = route is null ? "a Console route is null" : RelativePath(route.Path, "route path", isInternal) ?? PolicyRef(route.Policy, modulePolicies);
            if (problem is not null)
            {
                return problem;
            }
            if (!paths.Add(route!.Path))
            {
                return $"the Console route '{route.Path}' is declared twice";
            }
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Menu)
        {
            if (entry is null)
            {
                return "a Console menu entry is null";
            }
            var problem = entry.Key is null || !KeyRule().IsMatch(entry.Key) ? "a menu key must match ^[a-z0-9][a-z0-9._-]{0,63}$"
                : !PlainText(entry.Label, MaxLabel) ? $"the menu label for '{entry.Key}' must be 1 to {MaxLabel} characters of plain text"
                : RelativePath(entry.Route, "menu route", isInternal)
                ?? (entry.Group is not null && !ConsoleMenuGroups.All.Contains(entry.Group) ? $"the group for '{entry.Key}' must be one of: {string.Join(", ", ConsoleMenuGroups.All.Order(StringComparer.Ordinal))}" : null)
                ?? (entry.Icon is not null && !IconRule().IsMatch(entry.Icon) ? $"the icon for '{entry.Key}' must match ^[a-z0-9][a-z0-9_-]{{0,63}}$" : null)
                ?? PolicyRef(entry.Policy, modulePolicies);
            if (problem is not null)
            {
                return problem;
            }
            if (!keys.Add(entry.Key!))
            {
                return $"the menu key '{entry.Key}' is declared twice";
            }
        }
        return null;
    }

    // The empty path is the Console's Overview: only an internal module declares it
    private static string? RelativePath(string? path, string what, bool allowEmpty = false)
        => (allowEmpty && path == "") || (path is not null && PathRule().IsMatch(path) && !path.Contains("//", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
                // ':' only starts a parameter segment (":id"); anywhere else it would read as a scheme ("javascript:x")
                && path.Split('/').All(segment => segment.Length > 0 && segment.LastIndexOf(':') <= 0 && segment != ":"))
            ? null
            : $"the {what} must be relative (^[a-z0-9][a-z0-9/_:-]{{0,127}}$, no '//', no '..', no scheme)";

    private static string? PolicyRef(string? policy, IReadOnlySet<string> modulePolicies)
        => policy is null || policy == CorePolicies.Read || policy == CorePolicies.Operate || modulePolicies.Contains(policy)
            ? null
            : $"the Console policy '{policy}' is neither a core policy nor one of the module's own";

    // 1..max characters, no control characters, no markup characters
    private static bool PlainText(string? text, int max)
        => !string.IsNullOrWhiteSpace(text) && text.Length <= max
            && !text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || c is '<' or '>' or '&');
}
