# Juice OpenTelemetry — 10.8.0 Current State and Gap Analysis

**Baseline:** Juice 10.8.0, published 2026-11-01  
**Signed off:** by the project owner, 2026-11-05, for 10.8.0  
**Status:** implementation inventory and remaining work  
**Later versions:** the diff from 10.8.0 to 10.9.0 (checked 2026-11-05) changes no meter or instrument. It adds spans for `ITimeTracker` scopes (`TimeTrackerActivitySource`), collected by `AddJuiceInstrumentation()`. The sign-off does not cover running against 10.9.0.  
**Evidence note:** meter names, instrument names, and test coverage below were checked against the Juice repository source on 2026-10-30 (`Juice/core/src`, `Juice/core/test`, `.github/workflows`). Behavior not covered by a cited file or test is marked as unverified. Re-check against source before changing code.

## 1. Purpose and boundary

This document records what the Juice instrumentation layer already provides and the three remaining gaps relevant to the Observability Platform. Juice instrumentation is a library capability. Collector deployment, telemetry storage, resource inventory, health, rules, alerts, and Agent are platform responsibilities.

## 2. Current state

| Capability | State in 10.8.0 | Evidence / notes |
|---|---|---|
| Juice `ActivitySource` and `Meter` | Implemented | Both named `Juice` (`JuiceActivitySource.Name`, `JuiceMeter.Name`). `AddJuiceInstrumentation()` adds exactly this source/meter and nothing else. |
| Context attributes | Implemented | `juice.correlation_id`, `juice.causation_id`, `juice.execution_id`, and `juice.source` (`MessagingActivity.Tags`). They are distinct from W3C `TraceId`/`SpanId`. |
| Mediator activities and metrics | Implemented | `juice.mediator.requests`, `juice.mediator.errors`, `juice.mediator.request.duration` (s) on the Juice meter (`MediatorTelemetry`). |
| Messaging metrics | Implemented | `juice.messaging.published`, `juice.messaging.consumed`, `juice.messaging.errors`, `juice.messaging.process.duration` (s) on the Juice meter (`MessagingMetrics`). Message, tenant, user, and entity IDs are not dimensions. |
| Outbox metrics | Implemented | `juice.outbox.published` and `juice.outbox.errors` on the Juice meter. See clarification below. |
| W3C propagation over RabbitMQ | Implemented | Cross-process test: `CrossProcessTraceTests` (commit `7401813`). |
| W3C propagation over NATS | Implemented, unit-tested only | Propagation in `JetStreamConsumerEngine`; covered by `JetStreamConsumerEngineTest`. No cross-process NATS test. |
| End-to-end trace tests | Implemented, runs on CI | `HttpTraceTests`: in-process HTTP → mediator → local channel → consumer, plus metric assertions. `CrossProcessTraceTests` (`[IntegrationTheory]`, RabbitMQ and NATS): HTTP server → mediator → SQL → `create` → outbox `deliver` (+ SQL) → `send` → consumer process `process` → `handle` → mediator → SQL, parent links asserted. Runs in `integration-tests.yml` with SQL Server, RabbitMQ and NATS containers. |
| Audit behavior | Implemented in Juice.EF | Audit annotations/events live in `Juice.EF` and `MediatorExtensions`; there is no separate `Juice.Audit` component. |
| Logs exported to OTLP | Implemented in 10.8.0 | Host-level: `WithLogging(l => l.AddJuiceInstrumentation().AddOtlpExporter())`; Juice log processor adds context and redacts payloads (`LogExportTests`). |

### Outbox measurement clarification

There are **no Outbox duration histograms on the Juice meter**. Outbox delivery latency is exposed as `delivery_latency_ms` (unit `ms`) on the separate `delivery.metrics` meter (`Juice.Messaging.Outbox.Delivery/DeliveryMetrics`). Do not describe `juice.outbox.*` duration histograms as shipped. `juice.outbox.retry` does not exist and must not be claimed. A per-delivery retry count is available only as the span attribute `juice.delivery.retry_count`, not as a metric.

## 3. Gaps

### G1 — Metrics consolidation and registration

Measurements outside the Juice meter, none of which `AddJuiceInstrumentation()` collects:

| Meter | Defined in | Instruments |
|---|---|---|
| `infra.rabbitmq.metrics` | `Juice.EventBus.RabbitMQ/RabbitMQMetrics` | `rabbitmq_channel_error_total`. |
| `infra.nats.metrics` | `Juice.EventBus.NATS/JetStreamMetrics` | `jetstream_publish_error_total`, `jetstream_consume_retry_total`, `jetstream_consume_parked_total`. |
| `delivery.metrics` | `Juice.Messaging.Outbox.Delivery/DeliveryMetrics` | `delivery_attempt_total`, `delivery_success_total`, `delivery_failure_total`, `delivery_latency_ms`. |
| `delivery.metrics` | `Juice.Messaging.Local/Internal/LocalChannelMetrics` | The same four instruments, plus `channel_dropped_total`, `local_delivery_phase1_fallback_total`, and the `channel_queue_depth` gauge. |
| `delivery.metrics` | `Juice.MediatR/MediatorMetrics` | `identified_commands_received`, `_duplicated`, `_processed`, `_failed`, `identified_commands_latency_ms`. |

**`delivery.metrics` defined in three places.** Each class is static and creates its own `Meter("delivery.metrics", "1.0.0")`, so each meter is created once per process (there is no repeated-setup bug). `DeliveryMetrics` and `LocalChannelMetrics` intentionally share four instrument names with currently identical type, unit, and description, so that local and broker delivery appear as one series. **Verified 2026-10-30** with OpenTelemetry .NET 1.19 (`DeliveryMetricsTests`): the SDK merges the identical copies into one stream per instrument, so this was a drift risk, not a live bug. **Fixed in 10.8.0 (unreleased):** the four shared instruments are now defined once in public `Juice.Messaging.Diagnostics.DeliveryMetrics` (`MeterName`, `Meter`); outbox delivery and the local transports both record on it, and the local-only instruments use the same `Meter`. `Juice.MediatR.MediatorMetrics` still creates its own `delivery.metrics` meter instance (Juice.MediatR does not reference Juice.Messaging) and shares no instruments. Instrument naming also does not follow the `juice.*` convention.

**Required outcome:** make all supported Juice and transport measurements collectable through documented host registration, while preserving meter and instrument names as compatibility contracts unless a versioned breaking change is approved. Define the shared `delivery.metrics` instruments once (one internal static definition referenced by outbox and local-channel delivery) so their identity cannot diverge.

**Acceptance criteria:**

- A sample host registers each meter explicitly (or through a documented opt-in helper) and exports at least one measurement from each.
- Registering the Juice meter alone is documented accurately and does not imply coverage of the meters above.
- An exporter test with the pinned OpenTelemetry SDK records outbox and local-channel deliveries in one process and asserts exactly one exported stream per shared `delivery.metrics` instrument, with combined values and no duplicate-instrument warning.
- Shared `delivery.metrics` instruments have a single definition in source.
- Instrument names, units, descriptions, and bounded dimensions are enumerated from source in package docs.
- No unbounded identifiers, payloads, or secrets become metric dimensions.

### G2 — Logs to OTLP

Logs → OTLP was not implemented in the supported Juice setup before 10.8.0. **Closed in 10.8.0, commit `78073fc`:** `AddJuiceInstrumentation()` on `LoggerProviderBuilder` (`Juice.OpenTelemetry`, which now references the OpenTelemetry SDK, still no exporter) adds a processor that exports the `MessageContext` ids as `juice.correlation_id`, `juice.causation_id`, `juice.execution_id`, `juice.source` and removes destructured and `Payload` attributes from `Juice.*` logs unless `IncludePayloads`. Trace and span ids come from the SDK. `LogExportTests` covers each acceptance criterion below, including OTLP/HTTP to a local receiver and a stalled exporter (bounded queue, records dropped). Application logs and exception messages are exported as written. A standalone sample app is part of Phase 1A.

**Required outcome:** document and demonstrate host-level .NET OpenTelemetry logging and OTLP exporter setup. Keep exporter/backend choices outside core Juice packages. Define allowlisted Juice context, trace/span correlation, redaction, and failure behavior.

**Acceptance criteria:**

- A sample host exports a structured log through OTLP.
- A log emitted in an active span carries supported trace and span correlation fields.
- Only approved context fields are exported; payloads, secrets, and unrestricted request/user data are excluded by default.
- Exporter outages do not fail business operations or create unbounded memory growth.

### G3 — End-to-end trace validation

**Closed in 10.8.0: `3df6e51`..`e3b001d`, green on CI (`integration-tests.yml`, run 41023576350).** `CrossProcessTraceTests` is an `[IntegrationTheory]` over RabbitMQ and NATS JetStream and asserts:

```text
HTTP server span                         (asserted)
  → Juice mediator                       (asserted)
  → SQL span (outbox insert)             (asserted; SqlClient instrumentation)
  → create → outbox deliver (+ SQL) → send (asserted)
  → RabbitMQ or NATS → consumer process  (asserted)
  → handle → consumer-side mediator span (asserted)
  → SQL span (EF insert)                 (asserted; SqlClient instrumentation)
```

EF/SQL spans come from standard host instrumentation (EF Core or SqlClient), not from Juice; the test enables that instrumentation and asserts the parent links, it does not add Juice-owned database spans. The NATS path is covered by the same test.

Running in CI is infrastructure work, not an attribute change: the existing workflows (`build-test.yml`, `alpha.yml`, `beta.yml`, `nuget-release.yml`) provide no RabbitMQ, SQL Server, or NATS services, and 94 tests are marked `IgnoreOnCI`. The new `integration-tests.yml` starts SQL Server, RabbitMQ and NATS from `test/infra/docker-compose.yml` and runs the trace tests on net8.0/net9.0/net10.0 in parallel (per-framework names). `[IntegrationTheory]` (Juice.XUnit) skips on CI unless `JUICE_INTEGRATION_TESTS=true`; the other `IgnoreOnCI` tests are unchanged. The test also asserts that HTTP and SQL spans come only from the standard instrumentations (no Juice HTTP/database spans, no SQL command recorded twice).

**Acceptance criteria:**

- Assertions verify trace continuity and parent/child or link semantics across process boundaries.
- HTTP, EF/SQL, publish, consumer, and consumer-side mediator spans are asserted; the test identifies standard .NET instrumentation versus Juice-owned spans.
- Both RabbitMQ and NATS cross-process paths are covered.
- The CI test workflow provides the broker and database dependencies, runs these tests, and fails on missing propagation or missing expected spans.
- No duplicate HTTP or SQL spans are introduced when standard instrumentation already provides them.

## 4. Explicit exclusions and compatibility

- Do not claim a `juice.outbox.retry` instrument without source evidence and an approved implementation.
- Do not claim Outbox duration histograms on the Juice meter; `delivery_latency_ms` belongs to `delivery.metrics`.
- Do not invent a `Juice.Audit` component. Business audit remains in Juice.EF and mediator extensions, separate from telemetry signals.
- Keep `MessageContext` identifiers distinct from W3C trace identity.
- Keep telemetry optional at the framework boundary; exporters/listeners must not be required for business execution.
- Use W3C propagation; do not replace it with custom trace headers.
- Avoid payloads, secrets, raw request data, or unbounded IDs in default attributes.
- Preserve current package target frameworks; the platform host may choose its runtime independently.

## 5. Work order

1. Enumerate units and dimensions for every instrument on the Juice meter and the three external meters into package docs.
2. Add the `delivery.metrics` exporter test; consolidate the shared instruments into one definition; document opt-in registration for all external meters.
3. Add host-level logs → OTLP sample and redaction/correlation guidance.
4. Add RabbitMQ, SQL Server, and NATS service containers to the CI test workflow; extend the cross-process fixture with EF/SQL instrumentation, consumer-side mediator and database work, and a NATS variant; run it in CI.
5. Update release notes only after behavior is verified.

The platform roadmap treats this inventory as the Juice baseline. It does not schedule already implemented tracing, metrics, or propagation as new platform phases.
