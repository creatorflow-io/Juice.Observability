# Integrations: plugins and instances

Integration core I1. An **integration plugin** is compiled into a module and describes one kind of external system. An admin creates
**instances** of it: a name, a configuration, references to the secrets it needs and an interval. The platform validates an instance, runs it on a
schedule, tracks its health and raises an alert when it fails. The core builds, tests and runs with no plugin: with none installed, or with every
instance disabled or failing, ingest, health, rules and alerts behave exactly as before.

I1 has the contracts, the instances and their lifecycle. What a plugin may send back (entities, mappings, changes) arrives in I2 and I3, and the
outbound HTTP client in I3. Plugins run inside the host process: they are modules, not downloaded assemblies.

## A plugin

```csharp
public void ConfigureServices(IModuleServicesBuilder module)
    => module.AddIntegrationPlugin<JobsPlugin>(new IntegrationDescriptor(
        Key: "sample.jobs", DisplayName: "Sample jobs", Version: "1.0.0",
        Capabilities: new HashSet<IntegrationCapability> { IntegrationCapability.Health },
        Config: new IntegrationConfigSchema(1, [new("endpoint", IntegrationFieldKind.String, "Endpoint", Required: true, MaxLength: 200)]),
        Scope: new IntegrationConfigSchema(1, []),
        Secrets: [new IntegrationSecretSpec("token", "API token")],
        RequiredPermissions: ["jobs.read"],
        DefaultInterval: TimeSpan.FromMinutes(5), MinInterval: TimeSpan.FromMinutes(1)));
```

- The key is `<module>.<name>`: your module's name, a dot and a name matching `^[a-z][a-z0-9-]{0,30}$`. A key held twice, a version that is not
  SemVer, no capability, the `Actions` capability, a collection capability (`Discovery`, `Metadata`, `Relationship`, `Correlation`, `Events`) on a
  plugin that is not an `IIntegrationCollector`, a field or secret name outside `^[a-zA-Z][a-zA-Z0-9_]{0,39}$`, more than 50 fields or 10 secrets,
  a `Pattern` that does not compile within 100 ms, or an interval outside 1 minute <= `MinInterval` <= `DefaultInterval` <= 24 hours throws
  `ModuleCompositionException`, and the host does not start.
- `IIntegrationPlugin.ValidateAsync` checks the instance can work (connectivity, authentication, permissions, capabilities) and returns a
  `ValidationReport`. `IIntegrationCollector.CollectAsync` runs on the schedule and returns a `CollectionOutcome`.
- The plugin is a scoped service. It receives an `IntegrationContext` with the instance's id, **the instance's tenant**, its configuration and
  scope, `Secrets`, `Checkpoints`, a clock and a logger.

## Configuration and secrets

The configuration schema is the core's own typed field list: `String`, `Integer`, `Number`, `Boolean`, `Enum`, `StringList` and `Duration`, each with
optional length, range, allowed values, pattern and item limits. An unknown property, a wrong type, a value out of range or a missing required field
is refused with a 400 that names the field. A configuration is at most 16 KB and a scope 4 KB. A configuration saved under version N is not run by a
plugin that expects version M: the instance goes to Failed ("config version N, plugin expects M") and cannot be enabled until it is saved again.

An instance holds **references to secrets, never secrets**: `env:<NAME>` (the name must start with `Modules:integrations:SecretEnvPrefix`, by default
`JUICE_INTEGRATION_`) or `file:/path` (only inside `Modules:integrations:SecretDirectory`; refused when it is not set). A plugin reads a value with
`context.Secrets.GetAsync("token", ct)`, only for names its descriptor declares, and only when a run or validation needs it. A failure throws
`IntegrationSecretException`, which carries the name and a reason, never a value. Every resolved value is replaced by `***` in anything the platform
stores or logs from a plugin.

Plugin text (a validation check's detail, a failure's detail) is **untrusted**: capped at 500 characters, scrubbed of secret values, shown in the
Console as text and never put into an alert's evidence.

## States

`Configured` -> `Validated` -> `Enabled` -> `Degraded` -> `Disabled` or `Failed`.

| Action | From | Result |
| --- | --- | --- |
| Create | | Configured |
| Change configuration, scope, references or interval | any | Configured, runs stop, the validation is forgotten |
| Validate | Configured, Validated, Disabled, Failed | Validated if it passes, otherwise Configured with the report |
| Enable | Validated; Disabled when its validation is still current and the plugin is installed | Enabled, runs now |
| Disable | any | Disabled (repeating it changes nothing) |
| Delete | Configured, Validated, Disabled | gone, with its checkpoints |

A run that succeeds leaves the instance Enabled; a partial one makes it Degraded. A failed run, a timeout or a plugin that throws counts a
failure: Degraded, then Failed after `Modules:integrations:FailedAfter` (3) in a row. An authentication, permission or configuration failure goes to
Failed at once, as does a plugin that is no longer installed. Retries back off from the interval up to `MaxBackoff` (1 hour), never sooner than a
`RetryAfter` the plugin gave, so a failed instance recovers by itself once the cause is fixed.

An admin's disable or change always wins over a run in progress: the run's result and checkpoints are dropped.

## The API

`/api/v1/tenants/{tenantId}/integrations`: `GET /plugins`, `GET /instances`, `GET /instances/{id}`, `POST /instances`, `PUT` and `DELETE
/instances/{id}`, and `POST /instances/{id}/validate|enable|disable`.

- Reading needs the Read floor. Without the admin role the list and the instance carry state and health only: configuration, scope, references and the
  validation report are null.
- Every change needs `admin` **and** the deployment's own tenant (`PlatformOptions.TenantKey`); otherwise 403, audited as denied. Changes are audited as
  `integrations.instance.{create|update|validate|enable|disable|delete}` with the plugin, state and hash, never the configuration or references.
- `POST /instances` takes an `Idempotency-Key`. `PUT` and `DELETE` need `If-Match` with the instance's ETag: 428 without it, 412 when stale.
- A 409 means the name is taken, the state does not allow it, a validation went stale, or the plugin is not installed.

## Health, metrics and the alert

Each pass of the `integrations` leased worker runs the due instances (at most `MaxConcurrentRuns`, 4, at once, each within `RunTimeout`, 5 minutes).
Metrics on the platform meter: `juice_integration_runs_total{plugin,instance,outcome}`, `juice_integration_failures_total{plugin,instance,reason}`
and `juice_integration_freshness_seconds{plugin,instance}`; the instance label is its id, never its name.

A Failed instance raises one alert from the platform-managed rule `integration-health` (Warning), created the first time one is needed, so an install
without integrations has no such rule. The alert resolves when the instance is Enabled again, disabled or deleted. Its evidence names the plugin, the
instance, the failure kind and the failures in a row, and never the plugin's own text. The rule belongs to the platform: it is not offered when making
a rule, cannot be previewed and cannot be changed through the rules API or file. The service and host health the platform reports never include it.

## The Console

Configure > **Integrations** lists the instances with their state, health, last success and next run. With no plugin installed the page says "No
integration plugins are installed". An admin creates an instance with a form built from the plugin's schema; a secret is asked for as a reference in a
plain text box, never a value. An instance's page shows its health and last validation to everyone, and to an admin its configuration and references,
with Edit, Validate, Enable, Disable and Delete. Each change carries the instance's ETag: when someone else changed it first the page says so and
offers Reload. A viewer or operator gets the read-only view. An instance whose plugin is gone is marked "not installed" and can only be disabled or
deleted.

## Known gap until I3

A plugin runs in the host process and could open its own sockets. Code review bounds that until the outbound client (I3) and its CI check exist.
