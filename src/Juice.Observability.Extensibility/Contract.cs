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
    public static bool TryParse(string text, out CoreVersionRange range) => throw new NotImplementedException();

    public bool Includes(Version core) => throw new NotImplementedException();
}

/// <summary>The core's version: Major.Minor.Patch of this assembly's informational version; prerelease and metadata are ignored.</summary>
public static class CoreVersion
{
    public static Version Current => throw new NotImplementedException();
}

public static class CoreRoles
{
    public const string Viewer = "viewer", Operator = "operator", Admin = "admin";

    public static IReadOnlySet<string> All => throw new NotImplementedException();
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
    public string Encode() => throw new NotImplementedException();

    public static bool TryDecode(string? v, out PageCursor? c) => throw new NotImplementedException();
}

public static class Paging
{
    public const int DefaultLimit = 50, MaxLimit = 200;

    /// <summary>Null when valid; a 400 problem otherwise.</summary>
    public static IResult? Validate(int? limit, string? cursor, out int take, out PageCursor? after) => throw new NotImplementedException();
}

/// <summary>Data only: validated at composition and returned by <c>/modules</c>.</summary>
public sealed record ConsoleManifest(string Package, IReadOnlyList<ConsoleRoute> Routes, IReadOnlyList<ConsoleMenuEntry> Menu);

public sealed record ConsoleRoute(string Path, string? Policy);

public sealed record ConsoleMenuEntry(string Key, string Label, string Route, string? Icon, string? Policy, int Order);

public sealed class ModuleCompositionException(string message) : InvalidOperationException(message);
