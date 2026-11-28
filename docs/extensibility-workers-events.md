# Leased workers and events for modules

Part of the module contract (`Juice.Observability.Extensibility`, additive within 2.x). Security: W-23.

## Leased workers

```csharp
module.AddLeasedWorker<MyWorker>("my-module");        // or "my-module-sweeper"

public sealed class MyWorker : ILeasedWorker
{
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken ct) { ... }
}
```

- **One replica at a time.** The worker runs on the same loop as discovery, health and SLO counting (`LeasedWorkerService`): it takes a PostgreSQL
  advisory lease, runs a pass every `Interval`, and when the replica holding the lease stops, another takes it on its next tick.
- **Names.** The module's name, or `<module>-<x>`. A core worker's name (`discovery`, `health`, `rules`, `slos`, `sampler`, `action-requests`,
  `notifications-holds`, `notifications-release`), a name that is another module's, or a name without the prefix is refused at composition with a
  `ModuleCompositionException`. The name is the lease, so a module can't take a core lease.
- **Switch.** `Modules:<module>:Workers:<name>:Enabled` (default true), read at composition. Off means not registered.
- **Scope.** The worker is scoped: every pass gets a new instance in a new scope. `Interval` is read once, when the worker starts.
- **Outcome.** `Succeeded` and `Partial` are counted and timed as passes of the worker (`juice.observability.worker.*`); `Idle` is a heartbeat only.
  A pass that throws is logged, counted as a failure and tried again at the next tick.
- **Leases that move.** `LeasedPass.LeaseLost` is cancelled while the pass runs if the lease is lost, or taken again on a new connection (another
  replica may have run in between). It is checked about once a second. The next pass reads `Retaken`: state kept from earlier passes is stale.
  `Retaken` is also true on the first pass.
- **Start.** After the module's migrations (`AddMigrations`): hosted services start in registration order.

## Events

```csharp
module.AddEventHandler<AlertTransitionedEvent, MyHandler>();

public sealed class MyHandler : IObservabilityEventHandler<AlertTransitionedEvent>
{
    public Task HandleAsync(AlertTransitionedEvent e, CancellationToken ct) { ... }   // idempotent on e.EventId
}
```

| Event | Raised when |
|---|---|
| `AlertTransitionedEvent` | an alert fires, or resolves after it fired (a Pending alert that resolves raises nothing) |
| `IncidentChangedEvent` | an incident is created, changes state, gains or loses an alert, or gets a comment (the incident endpoints) |
| `ResourceChangedEvent` | discovery creates a service, instance, resource or dependency; retires or restores a service; deactivates or prunes an instance |
| `ChangeRecordedEvent` | a change is reported through the API, or discovery records a version change |

- **After commit, at least once.** The event is written to the outbox in the transaction of the change, so it exists only if the change committed.
  It is delivered after the commit, from the outbox, with retries (about 85 minutes). In no guaranteed order, and possibly twice: handlers must be
  idempotent on `EventId`.
- **One envelope per module.** A module's handlers run in a scope of their own. When one throws, the delivery is tried again for that module only.
- **Nothing without a handler.** With no module handling an event type, no row is written: a core-only host's outbox is what it was.
- **Platform tenant only.** `TenantId` is the deployment's tenant; an event never carries another.
- **Not covered.** Incidents opened or joined through correlation suggestions, a service forgotten (`DELETE .../resources/{id}`), and alerts opened
  outside the rules' and SLO burn's commands, raise no event yet.
- **Stopping.** Handlers get the host's stopping token.

Only the four events above can be handled. The registration, publisher and host-factory types are internal to the core: a module can't name them,
and `AddLeasedWorker` and `AddEventHandler` refuse a builder that isn't the host's own. A module worker's lease is keyed `module:<name>`, apart
from every core lease and lock whatever its name; the event's `TenantId` is set by the core, not by the code that raised it.
