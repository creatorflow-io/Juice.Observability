using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Extensibility;

// Integration plugins (integration core I1). A plugin is a compiled-in module that registers a descriptor and a class; the core owns instances,
// secrets, scheduling and health. Grows additively: members are never removed or changed.

/// <summary>Checks an instance's configuration against the target system. Scoped per run; resolve nothing else from the host.</summary>
public interface IIntegrationPlugin
{
    Task<ValidationReport> ValidateAsync(IntegrationContext context, CancellationToken ct);
}

/// <summary>A plugin that collects (every capability but <see cref="IntegrationCapability.Context"/> and <see cref="IntegrationCapability.Health"/>).</summary>
public interface IIntegrationCollector
{
    Task<CollectionOutcome> CollectAsync(IntegrationContext context, ICollectionSinks sinks, CancellationToken ct);
}

/// <summary>Where a collector puts what it found. Members arrive in I2 (entities, mappings) and I3 (changes); implemented by the core only.</summary>
public interface ICollectionSinks
{
}

/// <summary>What a plugin is, declared once at registration and validated at composition.</summary>
public sealed record IntegrationDescriptor(string Key, string DisplayName, string Version, IReadOnlySet<IntegrationCapability> Capabilities,
    IntegrationConfigSchema Config, IntegrationConfigSchema Scope, IReadOnlyList<IntegrationSecretSpec> Secrets, IReadOnlyList<string> RequiredPermissions,
    TimeSpan DefaultInterval, TimeSpan MinInterval);

public enum IntegrationCapability { Discovery, Metadata, Context, Relationship, Correlation, Events, Actions, Health }

public sealed record IntegrationConfigSchema(int Version, IReadOnlyList<IntegrationConfigField> Fields);

public sealed record IntegrationConfigField(string Name, IntegrationFieldKind Kind, string Label, bool Required = false, int? MaxLength = null,
    double? Min = null, double? Max = null, IReadOnlyList<string>? Allowed = null, string? Pattern = null, int? MaxItems = null);

public enum IntegrationFieldKind { String, Integer, Number, Boolean, Enum, StringList, Duration }

public sealed record IntegrationSecretSpec(string Name, string Label, bool Required = true);

public sealed record ValidationReport(bool Passed, IReadOnlyList<ValidationCheck> Checks);

/// <summary>A check's name is <c>connectivity</c>, <c>authentication</c>, <c>permissions</c>, <c>capabilities</c> or the plugin's own.</summary>
public sealed record ValidationCheck(string Name, bool Passed, string? Detail = null);

public enum CollectionResult { Succeeded, Partial, Failed }

public enum IntegrationFailure { None, Connectivity, Authentication, Permission, RateLimited, Configuration, Remote, Timeout, Internal }

public sealed record CollectionOutcome(CollectionResult Result, IntegrationFailure Failure = IntegrationFailure.None, string? Detail = null, long Items = 0, TimeSpan? RetryAfter = null);

/// <summary>What one validation or run gets. Only the host builds it; members may be added (the outbound client in I3).</summary>
public sealed class IntegrationContext
{
    internal IntegrationContext(Guid instanceId, Guid tenantId, string pluginKey, JsonElement config, int configVersion, JsonElement scope, IIntegrationSecrets secrets,
        IIntegrationCheckpoints checkpoints, TimeProvider clock, ILogger logger)
    {
        InstanceId = instanceId;
        TenantId = tenantId;
        PluginKey = pluginKey;
        Config = config;
        ConfigVersion = configVersion;
        Scope = scope;
        Secrets = secrets;
        Checkpoints = checkpoints;
        Clock = clock;
        Logger = logger;
    }

    public Guid InstanceId { get; }

    /// <summary>The instance's tenant, never one the plugin names.</summary>
    public Guid TenantId { get; }

    public string PluginKey { get; }

    public JsonElement Config { get; }

    public int ConfigVersion { get; }

    public JsonElement Scope { get; }

    public IIntegrationSecrets Secrets { get; }

    public IIntegrationCheckpoints Checkpoints { get; }

    public TimeProvider Clock { get; }

    /// <summary>Resolved secret values are removed from what is logged through it.</summary>
    public ILogger Logger { get; }
}

public interface IIntegrationSecrets
{
    /// <summary>Resolves a declared secret name from its reference, now. Throws <see cref="IntegrationSecretException"/> (the name, never the value).</summary>
    ValueTask<string> GetAsync(string name, CancellationToken ct);
}

public sealed class IntegrationSecretException(string name, string reason) : Exception($"Secret '{name}': {reason}")
{
    public string SecretName { get; } = name;
}

public interface IIntegrationCheckpoints
{
    ValueTask<IntegrationCheckpoint?> GetAsync(string stream, CancellationToken ct);

    /// <summary>Stream <c>^[a-z0-9._-]{1,64}$</c>, cursor at most 1024 characters, at most 32 streams. Committed with the run's outcome, or not at all.</summary>
    void Set(string stream, DateTimeOffset watermark, string? cursor = null);
}

public sealed record IntegrationCheckpoint(string Stream, DateTimeOffset Watermark, string? Cursor);

public static class IntegrationModuleExtensions
{
    /// <summary>
    /// Registers a plugin of the module: the key is <c>&lt;module&gt;.&lt;name&gt;</c> (name <c>^[a-z][a-z0-9-]{0,30}$</c>) and the descriptor is validated here, so a bad
    /// one refuses the module. <typeparamref name="TPlugin"/> is created per run (scoped); a collection capability needs it to be an <see cref="IIntegrationCollector"/> too.
    /// </summary>
    public static IModuleServicesBuilder AddIntegrationPlugin<TPlugin>(this IModuleServicesBuilder b, IntegrationDescriptor descriptor) where TPlugin : class, IIntegrationPlugin
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(descriptor);
        CoreBuilders.Require(b);
        HostComposers.Find<IIntegrationPluginComposer>(b).AddPlugin<TPlugin>(b, descriptor);
        return b;
    }
}

/// <summary>The host's side of <see cref="IntegrationModuleExtensions.AddIntegrationPlugin{TPlugin}"/>: internal, implemented and registered by the host (see <c>IAlertSourceComposer</c>).</summary>
internal interface IIntegrationPluginComposer
{
    void AddPlugin<TPlugin>(IModuleServicesBuilder b, IntegrationDescriptor descriptor) where TPlugin : class, IIntegrationPlugin;
}
