# Signal packs

Most of the platform reads only OpenTelemetry's semantic conventions ([telemetry-requirements.md](telemetry-requirements.md)).
A **signal pack** is the telemetry one framework sends beyond them, which the platform also knows how to read. Each pack
names every signal it reads in one place (`src/Juice.Observability.Signals`), has a contract version, and can be switched
off per deployment. The Juice pack is the only one.

## The Juice pack, version 2

Sent by services built on Juice. A test (`SignalPackTests.Every_Juice_name_the_platform_queries_is_in_the_pack`) fails
if any of these names is queried anywhere outside the pack.

| Signal | Kind | Sent by | Read by |
|---|---|---|---|
| `juice.mediator.requests`, `juice.mediator.errors` | cumulative sum | Juice.MediatR | the MediatorErrors rule template |
| `juice.messaging.consumed` with `juice.dispatch.result` | cumulative sum | Juice.Messaging | the ConsumerFailures rule template |
| `juice.delivery.attempts`, `juice.delivery.failures` | cumulative sum | Juice.Messaging (10.13.1), for outbox delivery and the local routes | the DeliveryFailureRate rule template |
| `delivery_attempt_total`, `delivery_failure_total` | cumulative sum | Juice before 10.13.1, and later with `AddJuiceDeliveryMetrics()` | DeliveryFailureRate, only for a service that sends no `juice.delivery.*` |
| `juice.dispatch.result = failure` on a Consumer span | span attribute | Juice.Messaging | SLOs: the request is bad, whatever its status (`slo.failed-dispatch`) |
| `create {destination}` Producer spans and `deliver {destination}` spans with `juice.delivery.outcome`, scope `Juice` | spans | Juice.Messaging.Outbox | the capacity view's outbox backlog and delivery delay, and load runs' backlog (`capacity.outbox`) |

The version changes when a name above changes, or one is added or removed. A Juice release that renames a signal
needs a platform release that reads the new name.

| Version | Change |
|---|---|
| 1 | the signals above, with the delivery counters under their unprefixed names only |
| 2 | the delivery counters read as `juice.delivery.*`, and as the unprefixed names from a service that sends none of those. A service sending both is counted once |

## Mixed deployments

Keep the pack on when some services use Juice and others don't. Each feature reads Juice signals only from the services
that send them. Every other service is treated as in [telemetry-requirements.md](telemetry-requirements.md):

- A Juice-template rule covering all services evaluates only the Juice services. It raises no alert, and no *no data*, for
  the others.
- SLOs, the capacity view and load runs read the outbox and failed dispatches only where they exist.

**Which services send Juice signals.** Discovery marks each service whose telemetry includes one of Juice's own spans
(scope `Juice`) or a `juice.*` metric. The mark appears as `signalPacks: ["juice"]` on the service in the resources API,
and as a *Juice* badge in the Console's Services list and on the service page. Once seen, it is kept.

`juice.delivery.*` marks a service; the unprefixed `delivery_*_total` counters don't, since another library may export
the same names. A service that delivers an outbox sends Juice's spans anyway.

**Rules that would never observe anything.** Take an enabled MediatorErrors, ConsumerFailures or DeliveryFailureRate
rule scoped to one service that discovery knows and has never seen send Juice signals:

- The API refuses it with 400: "`shop/checkout` has sent no juice signals, so a MediatorErrors rule would never observe
  it".
- From the rules file it is loaded, since the rule may come before the service. The rules list then gives the reason in
  `quiet`, and the Console shows it as *Never observes*.
- A rule for a service discovery hasn't seen yet isn't judged.

## Switching it off

```json
{ "Signals": { "Juice": { "Enabled": false } } }
```

It is on by default. Without Juice services it reads nothing, so leaving it on does no harm. Switching it off is for a
deployment that wants its rule form and its queries to match what it actually monitors. Once it is off:

- **Rules:** an *enabled* rule can't use MediatorErrors, ConsumerFailures or DeliveryFailureRate. In the rules file it
  stops the host, and through the API it is refused with 400.
  - A rule made before the pack was switched off observes nothing, so its open alerts are held as no data. Disable it
    to resolve them; a disabled rule with these templates is still accepted.
  - The Console's rule form leaves these templates out.
- **SLOs:** a failed Juice dispatch is no longer bad by itself; the span's status and HTTP code decide.
- **Capacity view:** `outbox` is `null` and the outbox queries don't run. In load runs, `created`, `delivered` and
  `backlog` are 0.

## Describing the packs

`GET /api/v1/signal-packs` (any platform role) lists each pack with:

- its version and whether it is on;
- the rule templates and features it adds;
- the metrics, attributes and instrumentation scopes it reads.

Use it to check what a service has to send.
