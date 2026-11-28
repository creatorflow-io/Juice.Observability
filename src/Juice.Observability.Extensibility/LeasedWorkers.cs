using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juice.Observability.Extensibility;

/// <summary>
/// A pass of work that one replica at a time runs (a lease in PostgreSQL), every <see cref="Interval"/>. Registered with
/// <see cref="LeasedWorkerModuleExtensions.AddLeasedWorker{T}"/>; scoped: each pass gets a new instance. Passes that fail are logged and tried again.
/// </summary>
public interface ILeasedWorker
{
    TimeSpan Interval { get; }

    Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct);
}

/// <summary>Idle is a heartbeat only.</summary>
public enum LeasedPassOutcome { Succeeded, Partial, Idle }

/// <summary>
/// <paramref name="Generation"/> goes up each time this replica takes the lease on a new connection. <paramref name="Retaken"/> is true when it is not
/// the generation of the pass before (and on the first pass): another replica may have run in between, so state kept from earlier passes is stale.
/// <paramref name="LeaseLost"/> is cancelled while the pass runs if the lease is lost or taken again: stop work that another replica may now be doing.
/// </summary>
public sealed record LeasedPass(string Worker, int Generation, bool Retaken, CancellationToken LeaseLost);

/// <summary>A module's leased worker as composed. Core side: modules don't create these.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record LeasedWorkerRegistration(string Module, string Name, Type WorkerType, bool Enabled);

/// <summary>Core side: starts the loop of a registered worker (implemented by the host; modules don't use it).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ILeasedWorkerHostFactory
{
    IHostedService Create(LeasedWorkerRegistration registration);
}

public static class LeasedWorkerModuleExtensions
{
    /// <summary>
    /// The name is the module name or <c>&lt;module&gt;-&lt;x&gt;</c>, never a core worker name, and unique in the host: it names the lease, so a module
    /// can't take a core lease (W-23). It runs unless <c>Modules:&lt;module&gt;:Workers:&lt;name&gt;:Enabled</c> is false, read now: off means not registered.
    /// Starts after the module migrations.
    /// </summary>
    public static IModuleServicesBuilder AddLeasedWorker<T>(this IModuleServicesBuilder b, string name) where T : class, ILeasedWorker
    {
        ArgumentNullException.ThrowIfNull(b);
        var module = b.Module.Name;
        if (LeasedWorkerNames.Validate(module, name) is { } invalid)
        {
            throw new ModuleCompositionException($"Module '{module}' leased worker '{name}' is refused: {invalid}");
        }
        // Read from what is registered, not kept apart: a refused module's registrations are put back, and so are these.
        var taken = b.Services.Where(d => d.ServiceType == typeof(LeasedWorkerRegistration)).Select(d => d.ImplementationInstance).OfType<LeasedWorkerRegistration>()
            .FirstOrDefault(r => r.Name == name);
        if (taken is not null)
        {
            throw new ModuleCompositionException($"Module '{module}' leased worker '{name}' is refused: the name is already a worker of module '{taken.Module}'.");
        }
        var enabled = b.Configuration.GetSection($"Modules:{module}:Workers:{name}").GetValue("Enabled", true);
        var registration = new LeasedWorkerRegistration(module, name, typeof(T), enabled);
        b.Services.AddSingleton(registration);
        if (enabled)
        {
            b.Services.AddScoped<T>();
            b.Services.AddSingleton<IHostedService>(sp => new LeasedWorkerStarter(registration, sp));
        }
        return b;
    }
}

internal static partial class LeasedWorkerNames
{
    /// <summary>The workers of the core, which is what <c>WorkerPulse.KnownWorkers</c> lists (a test keeps the two equal).</summary>
    internal static readonly IReadOnlyList<string> Core =
        ["action-requests", "discovery", "health", "notifications-holds", "notifications-release", "rules", "sampler", "slos"];

    // A lease is held on a connection named "juice-worker:<name>:<version>", which PostgreSQL keeps 63 bytes of.
    internal const int MaxLength = 40;

    [GeneratedRegex(@"^[a-z][a-z0-9-]*[a-z0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();

    internal static string? Validate(string module, string? name)
    {
        if (name is null || name.Length > MaxLength || !NameRule().IsMatch(name))
        {
            return $"the name must match ^[a-z][a-z0-9-]*[a-z0-9]$ and be at most {MaxLength} characters";
        }
        if (Core.Contains(name))
        {
            return $"'{name}' is a worker of the core";
        }
        if (name != module && !(name.StartsWith(module + "-", StringComparison.Ordinal) && name.Length > module.Length + 1))
        {
            return $"the name must be '{module}' or '{module}-<x>'";
        }
        return null;
    }
}

internal sealed class LeasedWorkerStarter(LeasedWorkerRegistration registration, IServiceProvider services) : IHostedService
{
    private IHostedService? _host;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _host = ((ILeasedWorkerHostFactory)(services.GetService(typeof(ILeasedWorkerHostFactory))
            ?? throw new InvalidOperationException("The host has no leased worker support: AddJuiceObservability registers it."))).Create(registration);
        return _host.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _host?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
