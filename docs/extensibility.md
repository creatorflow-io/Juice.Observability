# Extending Juice Observability with modules

A module is in-process compiled code that adds services, workers, migrations, tenant endpoints and (as data) Console routes to the host. It
implements `IObservabilityModule` from the `Juice.Observability.Extensibility` package and the host composes it in code:

```csharp
builder.AddJuiceObservability(...);
builder.AddObservabilityModule<SampleModule>();   // after AddJuiceObservability, before Build()
```

## The contract

- `ModuleInfo(Name, DisplayName, Version, SupportedCore)`
  - `Name`: `^[a-z][a-z0-9-]{0,30}[a-z0-9]$`, lower case ASCII. It is the route segment, the policy and audit prefix and the configuration key
    (`Modules:<Name>:*`). It may not equal a core tenant-root segment (`alerts`, `telemetry`, ...) or an internal module's name (`core`, `slos`, `agents`).
  - `Version`: SemVer 2.0.
  - `SupportedCore`: a NuGet interval over the core version, for example `[2.0.0,3.0.0)`, `(1.9,2.1]`, `[2.0.0,)` or `[2.0.0]`.
    The core version is Major.Minor.Patch of the Extensibility assembly's informational version: prerelease and build metadata are ignored, so
    `2.0.0-alpha.N` satisfies `[2.0.0,3.0.0)`. A module outside the range makes `AddObservabilityModule` throw `ModuleCompositionException`
    before the host is built.
- `ConfigureServices(IModuleServicesBuilder)`: `Services`, `Configuration`, `AddWorker<T>()`, `AddMigrations<TContext>()`,
  `AddPolicy(name, roles)` (name `<Name>.<x>`, roles from `CoreRoles` only) and `SetConsoleManifest(...)`.
- `MapEndpoints(IModuleEndpointsBuilder)`: `Tenant` is the group `/api/v1/tenants/{tenantId:guid}/{Name}`. It carries the `platform.read` floor,
  the core tenant filter, caller naming and `ModuleEndpointMetadata`. A module can't map anywhere else.
- `IModuleMutations.Begin(http, tenantId, operation, targetType)`: audited, idempotent mutations through the core store. The operation must start
  with `<Name>.`, and `tenantId` must be the route tenant that the filter authorized; otherwise `Begin` throws (a 500, nothing audited).
- `Paging`, `PageCursor` and `Page<T>`: the same cursor encoding and the same 400 problems as the core lists.
- `ConsoleManifest`: data only (package, routes, menu), validated at composition and returned by `GET /api/v1/modules`.

`GET /api/v1/modules` lists what a site runs (core, slos, agents, then external modules in registration order). Any platform role may read it; it
makes no database access.

## Limits in 2.0 M1

- **Workers** (`AddWorker<T>`) are plain hosted services and run on every replica. Leased, single-owner workers arrive later (E3).
- **Migrations** (`AddMigrations<TContext>`) run `MigrateAsync` per declared context, in module order, after the core and Notifications migrations and
  only when `Database:Migrate` is true. A failure stops the host. A module-owned history table, schema rule and adoption of existing tables arrive
  later (E4).
- **Audit and idempotency** are written to the core store. A module's own state and its audit record are therefore **not** atomic in M1; a mutation
  must tolerate a retry (use the idempotency key) until E4 enlists the module context in the core transaction.
- Modules are trusted compiled code. The contract bounds the honest path; code review bounds the rest.
- **The core's Console manifest is null in M1.** The internal modules (`core`, `slos`, `agents`) declare none, because a manifest needs a
  `@juice-js/observability-<name>` package and the core's own package has no such name. How the core and the internal modules appear in the
  Console menu is a question for M3.

## Breaking change for package consumers

`Page<T>` moved from `Juice.Observability.Api.Endpoints` to `Juice.Observability.Extensibility` (one type for the core and its modules, the same
JSON). Code that used `Juice.Observability.Api.Endpoints.Page<T>` adds `using Juice.Observability.Extensibility;`. This is a 2.0 breaking change.

## Compatibility rule for 2.x

Nothing in the contract is removed or changed within 2.x. New capabilities arrive as new interfaces or as default interface members, so a module
built against 2.0 keeps compiling and running on 2.n. The contract never exposes the core's `PlatformDbContext`. `Roles` and `Policies` in the
host keep their names and values; `CoreRoles` and `CorePolicies` carry the same values for modules.
