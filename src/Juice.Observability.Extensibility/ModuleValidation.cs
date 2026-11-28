namespace Juice.Observability.Extensibility;

/// <summary>Composition-time checks. Each returns null when valid, otherwise the reason (the composer wraps it in a <see cref="ModuleCompositionException"/>).</summary>
internal static class ModuleValidation
{
    public static string? Name(string? name) => throw new NotImplementedException();

    public static string? DisplayName(string? displayName) => throw new NotImplementedException();

    public static string? Version(string? version) => throw new NotImplementedException();

    public static string? SupportedCore(string? range) => throw new NotImplementedException();

    /// <summary>Name, display name, version and range together.</summary>
    public static string? Info(ModuleInfo info) => throw new NotImplementedException();

    /// <summary>A policy name carries the module prefix; roles are a non-empty subset of the core roles.</summary>
    public static string? Policy(ModuleInfo module, string? name, string[]? roles) => throw new NotImplementedException();

    /// <summary>Validates the manifest; a policy must be a core policy or one of <paramref name="modulePolicies"/>.</summary>
    public static string? Manifest(ConsoleManifest? manifest, IReadOnlySet<string> modulePolicies) => throw new NotImplementedException();
}
