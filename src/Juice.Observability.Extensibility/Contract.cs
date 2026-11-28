using System.Buffers.Text;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Extensibility;

/// <summary>An in-process module: composed in code with <c>AddObservabilityModule&lt;T&gt;()</c>.</summary>
public interface IObservabilityModule
{
    ModuleInfo Info { get; }

    void ConfigureServices(IModuleServicesBuilder module);

    /// <summary>Maps the module's endpoints. Default: none.</summary>
    void MapEndpoints(IModuleEndpointsBuilder endpoints) { }
}

/// <summary>Name <c>^[a-z][a-z0-9-]{0,30}[a-z0-9]$</c>; Version SemVer 2.0; SupportedCore NuGet interval such as <c>[2.0.0,3.0.0)</c>; DisplayName 1..64 plain text.</summary>
public sealed record ModuleInfo(string Name, string DisplayName, string Version, string SupportedCore);

/// <summary>A NuGet interval over the core version: <c>[2.0.0,3.0.0)</c>, <c>(1.9,2.1]</c>, <c>[2.0.0,)</c>, <c>[2.0.0]</c>.</summary>
public readonly record struct CoreVersionRange(Version? Min, bool MinInclusive, Version? Max, bool MaxInclusive)
{
    public const int MaxLength = 64;

    public static bool TryParse(string text, out CoreVersionRange range)
    {
        range = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }
        var minInclusive = text[0] == '[';
        var maxInclusive = text[^1] == ']';
        if ((!minInclusive && text[0] != '(') || (!maxInclusive && text[^1] != ')') || text.Length < 3)
        {
            return false;
        }
        var inner = text[1..^1];
        var comma = inner.IndexOf(',');
        if (comma < 0)
        {
            // "[v]" is the only single-version form
            if (!minInclusive || !maxInclusive || !TryVersion(inner, out var only))
            {
                return false;
            }
            range = new CoreVersionRange(only, true, only, true);
            return true;
        }
        var left = inner[..comma];
        var right = inner[(comma + 1)..];
        Version? min = null, max = null;
        if (left.Length > 0 && !TryVersion(left, out min))
        {
            return false;
        }
        if (right.Length > 0 && !TryVersion(right, out max))
        {
            return false;
        }
        if (min is null && max is null)
        {
            return false;
        }
        // an open end is always exclusive
        if ((min is null && minInclusive) || (max is null && maxInclusive))
        {
            return false;
        }
        if (min is not null && max is not null)
        {
            var order = min.CompareTo(max);
            if (order > 0 || (order == 0 && !(minInclusive && maxInclusive)))
            {
                return false;
            }
        }
        range = new CoreVersionRange(min, minInclusive, max, maxInclusive);
        return true;
    }

    public bool Includes(Version core)
    {
        ArgumentNullException.ThrowIfNull(core);
        var version = CoreVersion.Normalize(core);
        if (Min is not null)
        {
            var order = version.CompareTo(Min);
            if (order < 0 || (order == 0 && !MinInclusive))
            {
                return false;
            }
        }
        if (Max is not null)
        {
            var order = version.CompareTo(Max);
            if (order > 0 || (order == 0 && !MaxInclusive))
            {
                return false;
            }
        }
        return true;
    }

    // 1 to 3 numeric parts, no sign, no whitespace; normalized to Major.Minor.Patch
    private static bool TryVersion(string text, out Version version)
    {
        version = null!;
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 9 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
            numbers[i] = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        }
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}

/// <summary>The core's version: Major.Minor.Patch of this assembly's informational version; prerelease and metadata are ignored.</summary>
public static class CoreVersion
{
    public static Version Current { get; } = Parse(
        typeof(CoreVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CoreVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0");

    /// <summary>Major.Minor.Patch of an informational version; anything after <c>-</c> or <c>+</c> is ignored.</summary>
    internal static Version Parse(string informational)
    {
        var end = informational.IndexOfAny(['-', '+']);
        var core = end < 0 ? informational : informational[..end];
        return Version.TryParse(core, out var version) ? Normalize(version) : new Version(0, 0, 0);
    }

    internal static Version Normalize(Version version)
        => new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
}

public static class CoreRoles
{
    public const string Viewer = "viewer", Operator = "operator", Admin = "admin";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Viewer, Operator, Admin };
}

public static class CorePolicies
{
    public const string Read = "platform.read", Operate = "platform.operate";
}

public interface IModuleServicesBuilder
{
    ModuleInfo Module { get; }

    IServiceCollection Services { get; }

    /// <summary>The host configuration; a module's own keys are <c>Modules:&lt;Name&gt;:*</c>.</summary>
    IConfiguration Configuration { get; }

    /// <summary>Runs on every replica (hosted service).</summary>
    IModuleServicesBuilder AddWorker<TWorker>() where TWorker : class, IHostedService;

    /// <summary>Migrates the context at start, after the core, when <c>Database:Migrate</c> is true.</summary>
    IModuleServicesBuilder AddMigrations<TContext>() where TContext : DbContext;

    /// <summary>Name <c>&lt;Name&gt;.&lt;x&gt;</c>; roles a non-empty subset of <see cref="CoreRoles.All"/>.</summary>
    IModuleServicesBuilder AddPolicy(string name, params string[] roles);

    IModuleServicesBuilder SetConsoleManifest(ConsoleManifest manifest);
}

public interface IModuleEndpointsBuilder
{
    ModuleInfo Module { get; }

    /// <summary><c>/api/v1/tenants/{tenantId:guid}/{Name}</c>: Read policy, the core tenant filter, caller naming and <see cref="ModuleEndpointMetadata"/>.</summary>
    RouteGroupBuilder Tenant { get; }
}

public sealed record ModuleEndpointMetadata(string Module);

/// <summary>Scoped. Throws when the endpoint has no <see cref="ModuleEndpointMetadata"/>, the operation lacks the <c>&lt;module&gt;.</c> prefix, or <c>tenantId</c> differs from the authorized route tenant.</summary>
public interface IModuleMutations
{
    IModuleMutation Begin(HttpContext http, Guid tenantId, string operation, string targetType);
}

public interface IModuleMutation
{
    string Actor { get; }

    /// <summary>Core store: audit and idempotency commit together.</summary>
    Task BeginTransactionAsync(CancellationToken ct);

    /// <summary>Null when allowed; a denial is audited.</summary>
    Task<IResult?> RequireAsync(string? target, CancellationToken ct, params string[] roles);

    void Audit(ModuleAuditOutcome outcome, string? target, IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>Idempotency-Key replay, as the core's mutations.</summary>
    Task<IResult?> ReplayAsync(string route, object request, CancellationToken ct);

    IResult Remember<T>(int status, T response, DateTimeOffset now);

    /// <summary>SaveChanges, and commit when a transaction is open.</summary>
    Task SaveAsync(CancellationToken ct);
}

public enum ModuleAuditOutcome { Succeeded, Denied, Failed }

/// <summary>A page of results. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Opaque cursor; the phase names the table in lists that span two.</summary>
public sealed record PageCursor(string Phase, Guid After)
{
    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Phase}:{After:N}"));

    /// <summary>True for a null or empty value (no cursor) and for a valid one; false for anything else.</summary>
    public static bool TryDecode(string? v, out PageCursor? c)
    {
        c = null;
        if (string.IsNullOrEmpty(v))
        {
            return true;
        }
        if (v.Length > 128)
        {
            return false;
        }
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(v));
            var parts = text.Split(':');
            if (parts.Length == 2 && parts[0].Length is > 0 and <= 8 && Guid.TryParseExact(parts[1], "N", out var after))
            {
                c = new PageCursor(parts[0], after);
                return true;
            }
        }
        catch (FormatException)
        {
        }
        return false;
    }
}

public static class Paging
{
    public const int DefaultLimit = 50, MaxLimit = 200;

    /// <summary>Null when valid; a 400 problem otherwise.</summary>
    public static IResult? Validate(int? limit, string? cursor, out int take, out PageCursor? after)
    {
        take = limit ?? DefaultLimit;
        after = null;
        if (take is < 1 or > MaxLimit)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid limit",
                detail: $"limit must be between 1 and {MaxLimit}.");
        }
        if (!PageCursor.TryDecode(cursor, out after))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid cursor",
                detail: "cursor must be a value returned as nextCursor.");
        }
        return null;
    }
}

/// <summary>Data only: validated at composition and returned by <c>/modules</c>.</summary>
public sealed record ConsoleManifest(string Package, IReadOnlyList<ConsoleRoute> Routes, IReadOnlyList<ConsoleMenuEntry> Menu);

public sealed record ConsoleRoute(string Path, string? Policy);

public sealed record ConsoleMenuEntry(string Key, string Label, string Route, string? Icon, string? Policy, int Order)
{
    /// <summary>Null for the top level of the menu, or a core group (<see cref="ConsoleMenuGroups"/>). Left out of the JSON when null.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }
}

/// <summary>The groups of the Console menu an entry may sit in. A group with no visible entry is hidden.</summary>
public static class ConsoleMenuGroups
{
    public const string Explore = "explore", Automation = "automation", Configure = "configure";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Explore, Automation, Configure };
}

public sealed class ModuleCompositionException(string message) : InvalidOperationException(message);
