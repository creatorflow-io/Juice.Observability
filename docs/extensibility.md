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

## Limits in 2.0 M2

M2 adds the rest of the seams. Rule templates and threshold providers are in [extensibility-rules.md](extensibility-rules.md); leased workers and
domain events are in [extensibility-workers-events.md](extensibility-workers-events.md). This page covers module data and alert sources.

**Module data** (`AddModuleData<TContext>`, `TContext : ModuleDbContext`; the M1 `AddMigrations` keeps its behaviour):

- The module's tables live in its own schema, the module name with `-` turned into `_`. `ModuleDbContext.Module` must equal the registering module's
  name, and the model must put every table in that schema (call the base `OnModelCreating`) or in `Adopts`. Anything else throws
  `ModuleCompositionException` at composition. Nothing is applied to your model: naming convention and column names are yours, and your migrations
  are generated with the schema, so design-time options must match.
- The history table is `<schema>.__ef_migrations_history`. Migrations run at start, in module order, after the core's, on their own connection and
  under a session advisory lock (`module-migrations:<module>`), so replicas starting together migrate once. A failure stops the host.
  With `Database:Migrate` false, a pending module migration stops the host, naming the module and the migration.
- **Adoption.** A module that takes over tables of a feature that moved out of the core (the SLO, load-run, correlation, escalation and agent tables,
  `CoreMovedTables.All`), or tables outside the core's model, lists them in `Adopts`. Any other table of the core is refused at composition. While
  the module's history is empty:
  - all the tables exist, and every column the first migration's `CreateTable` operations name is there: the first migration is recorded as applied,
    not run, and later migrations run as usual;
  - none exists: migrated as usual, which creates them;
  - otherwise (some exist, or a column is missing): the host stops and nothing is changed.

  The first migration of an adopting module may hold only operations that create the adopted tables (and `EnsureSchema`); put the module's other
  tables in a later migration. A module never drops or alters an adopted table.
- **Atomic writes.** At runtime `TContext` is scoped over the request's connection and joins the core's transaction. `IModuleMutation.SaveAsync`
  saves every module context of the request, then the core's audit and idempotency records, in one transaction (opened if none is), so a failed
  audit or idempotency save leaves no module row. `BeginTransactionAsync` enlists the contexts already created and any created later. A module that
  calls `SaveChangesAsync` on its own context inside the transaction is still covered; outside one, that save commits on its own.

**Alert sources** (`AddAlertSource(template)`; `IAlertSource` is a keyed scoped service, `GetRequiredKeyedService<IAlertSource>(template)`):

- A source acts only on the platform-managed rules of its template, in the platform tenant. A signal naming another rule, a subject or environment of
  another tenant, or a fire without a subject refuses the whole call with `ArgumentException`, and nothing is written.
- `ApplyAsync` joins a transaction already open (a command's handler, which is how the SLO burn evaluation calls it). Outside one it commits the
  alerts, their transitions and the outbox rows together, and takes `pg_advisory_xact_lock('alert-source:<template>')` either way.
- A template is declared once per host and is never a registry template. The core's `SloBurn` is the slos module's. Storing a rule of a module's own
  template (and `IAlertSource.EnsureRules`) comes with the first module that moves a feature, so until then a module's source has no rules to act on.
- The composition calls (`AddModuleData`, `AddAlertSource`) reach the host's implementation by name, so they work only in a Juice Observability host.

## Breaking change for package consumers

`Page<T>` moved from `Juice.Observability.Api.Endpoints` to `Juice.Observability.Extensibility` (one type for the core and its modules, the same
JSON). Code that used `Juice.Observability.Api.Endpoints.Page<T>` adds `using Juice.Observability.Extensibility;`. This is a 2.0 breaking change.

## Compatibility rule for 2.x

Nothing in the contract is removed or changed within 2.x. New capabilities arrive as new interfaces or as default interface members, so a module
built against 2.0 keeps compiling and running on 2.n. The contract never exposes the core's `PlatformDbContext`. `Roles` and `Policies` in the
host keep their names and values; `CoreRoles` and `CorePolicies` carry the same values for modules.
