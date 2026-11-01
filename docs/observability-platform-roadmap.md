# Juice Observability Platform — Phased Roadmap

**Companion specification:** [Requirements and Architecture](observability-platform-requirements.md)  
**Juice baseline:** [10.8.0 Current State and Gap Analysis](juice-opentelemetry-10.8-current-state.md)  
**Initial hosting target:** single-tenant, customer-managed deployment  
**MVP ends:** Phase 4

## Roadmap rules

- Treat Juice tracing, mediator instrumentation, RabbitMQ/NATS propagation, and existing metrics as shipped baseline. Close the three documented gaps as framework work; do not relabel implemented instrumentation as platform milestones.
- Adopt OpenTelemetry Collector, ClickHouse, and Grafana. Build the PostgreSQL domain model, resource discovery/reconciliation, health, rules, alert lifecycle, and Agent later.
- Resource modeling proceeds alongside real telemetry from Phase 1 onward; it must be driven by observed OTLP and hostmetrics, not delayed until after storage is complete or limited to manually declared inventory.
- MVP is the end of Phase 4: telemetry ingestion, resource model, health, rules, alerts, signed webhook notification, and operator investigation.
- Dependencies must follow the phase order. Every phase has an acceptance gate; scaffolding alone does not complete a phase.

## Phase 0 — Baseline and contracts

**Goal:** verify inputs and freeze security/data contracts before implementation.

**Deliverables**

- Verify Juice 10.8.0-unreleased meter names, units, dimensions, the three classes that create the `delivery.metrics` meter (`DeliveryMetrics`, `LocalChannelMetrics`, `MediatorMetrics`; the first two share four instruments, verified by an exporter test), logs-to-OTLP gap, and end-to-end trace coverage against source/tests.
- Record G1/G2/G3 ownership and sequence: three meter registration/consolidation, logs → OTLP, HTTP → mediator → EF/SQL → broker → consumer mediator → EF/SQL CI trace test for RabbitMQ and NATS.
- Approve single-tenant deployment topology and stable identifiers: `PlatformTenantId`, optional untrusted `ApplicationTenantId`, `EnvironmentId`, `ResourceId`, `ServiceId`, `InstanceId`.
- Specify the Collector authenticator and the `auth.*` keys it provides, tenant stripping at resource/scope/record level, static tenant stamping, processor order (strip → stamp → batch), Grafana isolation, and test cases (Requirements §4 and §5.2).
- Approve resource identity/reconciliation, health state semantics, typed rule model, initial quantitative targets (Requirements §9), data classification, retention, and build/adopt decisions.

**Acceptance gate**

- Facts and unknowns are traceable to implementation evidence; telemetry gaps have named acceptance criteria.
- No implementation contract treats a payload tenant as authorization.
- Collector/Grafana/ClickHouse/PostgreSQL versions and supported component choices are recorded for the reference environment.

## Phase 1 — Runnable telemetry and domain foundation (parallel tracks)

**Goal:** create a runnable single-tenant environment while implementing resource identity against real telemetry.

**Track A — Ingestion and exploration**

**Status (2026-10-30):** implemented in [`deploy/local`](../deploy/local/README.md); `tests/verify.sh --outage` and a 60 s `tests/load.sh` run at reference rates pass (freshness p95 1.36 s). Open: the production authenticator choice (local uses `bearertokenauth`), the 30-minute acceptance load run, and a browser check of Grafana Explore.

- Pin and deploy Collector, ClickHouse, Grafana, and PostgreSQL for local development.
- Configure authenticated OTLP receiver over TLS, tenant attribute removal (`transform`) and static stamping (`resource`) before `batch`, memory limiter, bounded retry/queue, redaction, and traces/metrics pipelines. Never derive the tenant from client-supplied request metadata.
- Configure hostmetrics for supported CPU, memory, disk, network, and process measurements.
- Export supported Juice metrics, traces, and logs. G2 is closed in Juice 10.8.0 (unreleased): the sample app adds `WithLogging(l => l.AddJuiceInstrumentation().AddOtlpExporter())` and is the standalone host sample G2 deferred to this phase.
- Provision ClickHouse schema/retention and Grafana data source, starter dashboards, and Explore views.

**Track B — Resource model, API, and security**

- Create PostgreSQL migrations for tenant, application, environment, logical resource, service, ephemeral instance, dependency evidence, audit, and reconciliation checkpoints.
- Implement the discovery worker: query ClickHouse for Collector-stamped OTLP resource attributes and hostmetrics resources since the last checkpoint; normalize, validate, alias, and reconcile to stable platform identity (Requirements §6).
- Implement the cross-cutting API contracts in Requirements §6.1 (versioning, OIDC authentication, tenant-scoped role checks, tenant middleware, cursor pagination, problem details, idempotency, and mutation audit records) and the Phase 1 endpoints listed there (applications, resources, topology).
- Model inferred relationships with evidence source and confidence; never assert causation from temporal association.

**Acceptance gate**

- Reference Juice/host telemetry reaches ClickHouse and appears in Grafana; measured ingestion freshness target is recorded (log load may be synthetic until the pilot host runs a released Juice 10.8).
- Authentication, forged tenant attributes, unknown identity, bounded queues, and backend outage behavior are integration tested.
- Resources appear from real telemetry without manual pre-registration, and restarts/churn reconcile without cross-tenant merges or duplicate logical services.
- API tenant checks and database migrations pass clean-install and upgrade validation.

## Phase 2 — Health, freshness, and policy-driven rollups

**Goal:** explain the current state of discovered resources using explicit evidence and policy.

**Deliverables**

- Health snapshots, transition history, reason codes, evidence window, evaluation timestamp, and policy version in PostgreSQL.
- Policies by resource type for required signals, freshness/grace windows, minimum sample count, instance quorum, critical dependencies, child weighting, and state precedence.
- Implement `Healthy`, `Degraded`, `Unhealthy`, `Critical`, `Offline`, and `Unknown` exactly as requirements define. Never treat missing/stale evidence as zero or healthy.
- Health endpoint, instance and dependency-evidence detail in resource/topology responses (Requirements §6.1), and safe Grafana links.
- Resource discovery/reconciliation continues in parallel using actual stored telemetry and observed semantic attributes.

**Acceptance gate**

- Never-seen or insufficient evidence is `Unknown`; a previously seen resource beyond its freshness grace can become `Offline`.
- Partial child/instance failure and critical dependency examples produce policy-expected results with explanations.
- Identical evidence window and policy version do not create duplicate state transitions.
- Health freshness target is measured and recorded.

## Phase 3 — Typed scheduled rules and alert lifecycle

**Goal:** detect sustained conditions with bounded scheduled evaluation.

**Deliverables**

- Typed rule schemas and platform-owned ClickHouse query templates; no user-supplied SQL. Rules are provisioned from validated deployment configuration (Requirements §6.1); the rules API is read-only in MVP.
- Scheduler and bounded workers with tenant scope, time window, query timeout, concurrency, rate, and resource budgets.
- Rule/alert state machine: Pending, Firing, Resolved, `forDuration`, recovery duration/threshold, hysteresis, deduplication, and cooldown.
- Evidence references, alert APIs, acknowledgement, state history, and operational metrics.

**Acceptance gate**

- Sustained breach fires once after the configured duration; transient breach does not flap; qualifying recovery resolves according to policy.
- Missing/stale data is explicit and cannot be interpreted as zero.
- Tenant scoping, timeout, overload, deduplication, and evaluator lag are tested.
- A 1-minute rule schedule meets the initial breach-to-alert target at reference load.

## Phase 4 — Signed webhook and MVP investigation (MVP complete)

**Goal:** finish an end-to-end operator loop with a notification channel and safe telemetry investigation.

**Deliverables**

- Signed HTTPS webhook notification for alert transitions, with HMAC-SHA256 body signature, timestamp, unique delivery ID, secret references, replay window, SSRF protections with an audited admin allowlist for internal receivers, bounded retries, and delivery history.
- Alert list/detail UI or API views with subject health, reasons, evidence references, and Grafana links.
- Tenant-safe single-deployment Grafana provisioning and documented direct Explore access controls.
- Deployment guide, pinned configuration (including rules, webhook endpoints, and allowlist), sample app, runbook, measured Requirements §9 targets, and backup/restore instructions.

**MVP acceptance gate**

- All ten acceptance criteria in Requirements §11 pass, including ingestion, telemetry-driven resource model, health, scheduled rule, alert lifecycle, signed webhook, Grafana investigation, isolation, load, latency, and restore evidence.
- Webhook failure does not change or roll back alert state; retry behavior is bounded and observable.
- Operators can go from alert to health explanation and relevant Grafana telemetry.

**MVP ends here.** No Agent, automated remediation, advanced correlation, shared SaaS, or extra mandatory infrastructure is required for MVP.

## Phase 5 — Change events and initial correlation

**Goal:** present related changes and signals as evidence while preserving uncertainty.

**Deliverables:** deployment/version/configuration change events, safe summaries, actor/source, resource mapping, timeline joining changes/health/alerts/telemetry, explainable ranking and correlation evidence.

**Acceptance gate:** related timeline is tenant scoped; secrets are excluded; temporal proximity is labeled correlation, not proven root cause.

## Phase 6 — Broader notifications and incident workflow

**Goal:** extend the MVP webhook path and group operational work.

**Deliverables:** create/update APIs for rules, webhook endpoints, and the destination allowlist (replacing MVP configuration provisioning, with audit); prioritized email/Teams/Slack integrations, notification routing/grouping, deduplication, silences, maintenance windows, escalation, delivery history, incident lifecycle and audit trail.

**Acceptance gate:** delivery failure/retry and silence behavior are visible; incident transitions are auditable; escalation stops on resolution or acknowledgement according to policy.

## Phase 7 — Juice Agent enrollment and read-only diagnostics

**Goal:** add a control channel only after the platform can identify and authorize resources.

**Deliverables:** outbound mTLS enrollment/revocation, agent identity and resource association, signed command envelope, expiry/nonce/replay defense/idempotency, local typed action allowlist, selected read-only diagnostics, signed updates and rollback.

**Acceptance gate:** invalid signature, wrong target, expired/replayed command, disallowed action, and out-of-range parameters are rejected. Local allowlist wins over a valid platform signature. No shell execution interface exists.

## Phase 8 — Approved manual actions and verified remediation

**Goal:** support narrowly scoped, human-authorized operational actions.

**Deliverables:** typed action catalog with risk, permissions, preconditions, timeout, approval, execution and postcondition; operator action flow; audit and verification; bounded diagnostic bundle collection.

**Acceptance gate:** failure/timeout/duplicate/stale evidence/approval cases are tested; a resource is not marked recovered until independent health evidence confirms it.

## Phase 9 — Advanced correlation, SLOs, and selective streaming

**Goal:** add advanced detection only when measured requirements exceed scheduled evaluation.

**Deliverables:** SLO/error budgets and burn rates; explainable multi-signal correlation; optional anomaly detection; evaluate streaming only for measured alert latency/fan-out needs.

**Acceptance gate:** benchmark shows scheduled queries cannot meet an explicit requirement before streaming is introduced; new stateful components have an approved ADR, operations plan, recovery design, and cost rationale.

## Phase 10 — Domain console and scale hardening

**Goal:** build proven domain workflows and scale the reference deployment with evidence.

**Deliverables:** focused console for topology, health, alert/incident, enrollment, approvals, action history; retain Grafana for general telemetry exploration; load/retention/capacity/restore/upgrade drills and disaster recovery.

**Acceptance gate:** console does not duplicate general Grafana exploration without a product need; tenant context is preserved; scale tests document throughput, query mix, queue lag, resource use, and cost assumptions.

## Explicit triggers for deferred infrastructure and SaaS

| Addition | Trigger required before adoption |
|---|---|
| Redis | Measured cache/coordination contention or latency that PostgreSQL cannot meet acceptably; ADR defines consistency, failure behavior, capacity, and recovery. |
| NATS JetStream for platform internals | Concrete durable fan-out/replay/decoupling need that PostgreSQL outbox and direct calls cannot meet; benchmark, operational owner, and decision to use only one platform broker. Existing NATS in application telemetry propagation is not a trigger. |
| OpenSearch | Representative ClickHouse log search benchmark proves a material search, ingestion, or retention gap; a second store's cost and operational burden are accepted. |
| Object storage | Quantified need for low-cost long-term archive or diagnostic bundle retention; encryption, access, retention, deletion, restore, and volume are specified. |
| Shared SaaS tenancy | Product/business decision plus complete control-plane identity, credential lifecycle, tenant quotas, query enforcement, Grafana backend isolation, deletion, audit, and cross-tenant isolation evidence. A tenant ID column alone is insufficient. |

## Phase order at a glance

```text
0 Baseline + contracts
  → 1 Telemetry foundation + telemetry-driven resource model (parallel tracks)
  → 2 Health + freshness + policy rollups
  → 3 Scheduled rules + alert lifecycle
  → 4 Signed webhook + operator investigation [MVP]
  → 5 Change events + initial correlation
  → 6 Broader notifications + incident workflow
  → 7 Agent enrollment + read-only diagnostics
  → 8 Approved actions + verified remediation
  → 9 Advanced correlation + SLOs + selective streaming
  → 10 Domain console + scale hardening
```
