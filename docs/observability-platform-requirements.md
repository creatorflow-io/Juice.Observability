# Juice Observability Platform — Requirements and Architecture

**Status:** implementation specification  
**Initial deployment:** single-tenant, customer-managed deployment  
**MVP boundary:** Phases 0–4 in the companion roadmap  
**Telemetry baseline:** [Juice OpenTelemetry 10.8.0 Current State](juice-opentelemetry-10.8-current-state.md)

## 1. Product scope and decisions

Juice Observability Platform turns telemetry into a resource model, health, actionable rules, alerts, and safe investigation workflows. It adopts commodity telemetry components and builds Juice-specific domain behavior.

| Capability | Decision | Responsibility |
|---|---|---|
| Application instrumentation | Build/maintain in Juice | Juice SDKs and libraries. Existing 10.8.0-unreleased tracing, metrics, and propagation are baseline; close G1–G3 in the companion state document. |
| OTLP ingestion, batching, retry, filtering | Adopt | OpenTelemetry Collector. |
| Metrics, traces, logs storage | Adopt | ClickHouse. |
| General telemetry exploration | Adopt | Grafana with ClickHouse data source. |
| Resource/service/instance domain model | Build | Platform API and PostgreSQL. |
| Health rollups, rules, alert lifecycle | Build | Platform workers and PostgreSQL state. |
| Initial notification | Build small scoped capability | Signed outbound webhook delivery is in MVP. |
| Host-specific control and actions | Build later | Juice Agent, introduced after MVP. |

Do not build a custom OTLP receiver, time-series/trace/log database, general-purpose dashboard engine, or custom collector for commodity host metrics.

## 2. Hosting and tenancy

### 2.1 Initial target: single-tenant deployment

Each deployment serves one customer organization and is operated in that customer's boundary. It contains the Platform API/worker, PostgreSQL, OpenTelemetry Collector, ClickHouse, Grafana, and (later) Agent instances. The data model should still use a `PlatformTenantId` on tenant-owned records so migration to shared SaaS does not require redesign, but the MVP does not need a multi-customer control plane, shared database isolation, or self-service tenant provisioning.

Use distinct identifiers:

- `PlatformTenantId`: authorization and data ownership boundary. In the initial deployment, one configured tenant is provisioned at install time.
- `ApplicationTenantId`: optional business tenant reported by an instrumented application. It is descriptive context, never platform authorization.
- `EnvironmentId`: deployment environment such as production or staging.
- `ResourceId`, `ServiceId`, and `InstanceId`: platform-generated stable identities; never treat telemetry-provided IDs as authority.

### 2.2 Future shared SaaS implications

Shared SaaS is a separate scale/security phase. Before enabling it, add authenticated tenant provisioning, tenant-specific ingestion credentials, tenant-aware quotas, keys/rotation, control-plane authorization, cross-tenant isolation tests at every API/query/export/background-job path, per-tenant retention and deletion, and an operational break-glass audit model. Separate databases or ClickHouse clusters may be selected for high-assurance customers. Do not infer that adding a tenant column alone makes a shared service safe.

## 3. Reference architecture

```text
Juice services / supported host metrics
               │ OTLP over authenticated TLS
               ▼
       OpenTelemetry Collector ─────────────► ClickHouse ──► Grafana Explore/dashboards
                                                  │
                     bounded, tenant-scoped queries│ (discovery, health, rules)
                                                  ▼
       PostgreSQL ◄──────────────────► Platform API + workers
                                          │ health, rules, alerts, webhook delivery
                                          └── Agent control plane (post-MVP)
```

The Collector is the trusted ingestion boundary and exports only to ClickHouse. The platform never receives OTLP directly; resource discovery, health evaluation, and rules read Collector-stamped telemetry from ClickHouse through bounded, tenant-scoped queries. The platform owns business resources, health explanations, policies, alert state, and controlled actions. ClickHouse stores high-volume signals; PostgreSQL stores authoritative domain and workflow state. Grafana remains the general-purpose telemetry investigation tool.

## 4. Collector identity and tenant stamping

Use an explicit, auditable pipeline. In the initial single-tenant mode, authentication gates the receiver and the Collector stamps the one configured `PlatformTenantId` as a static value; no per-request identity-to-tenant mapping is needed. Future shared SaaS replaces the static value with a tenant derived from authenticated identity (step 4).

1. Require TLS and authenticate each OTLP client at the Collector receiver with a server authentication extension selected for the deployment (for example `oidc`, `basicauth`, or `bearertokenauth`; mTLS client certificates in addition where required). Never accept anonymous public ingestion.
2. Trust only identity established by the authenticator. Authenticators populate the `auth.*` request context (for example `auth.subject` or configured claims from `oidc`, the username from `basicauth`); which keys exist depends on the extension, so record them for the pinned version. Request metadata (`metadata.*`, available with `include_metadata: true`) is client-supplied headers and **must never be the source of the tenant**. `include_metadata` is not needed for `auth.*` values; enable it only for a separate, documented purpose. mTLS certificate identity is not exposed as an `auth.*` value by standard extensions.
3. Remove every incoming `juice.platform_tenant_id`, `platform.tenant.id`, or legacy tenant attribute at resource, scope, and record level (span, span event, metric datapoint, log). The `attributes` processor only reaches record-level attributes and the `resource` processor only resource attributes; use the `transform` processor (OTTL) so a single component covers resource, scope, and record contexts. Standard processors delete rather than reject; if forged attempts must be counted, add a `count` connector (or equivalent) that counts records carrying a tenant attribute before the strip step, and alert on it.
4. Stamp the trusted canonical `juice.platform_tenant_id` on the resource after removal, using the `resource` processor:
   - **Single-tenant (initial):** a static value from deployment configuration (`value: ${env:JUICE_PLATFORM_TENANT_ID}`). Authentication still gates the receiver; the tenant itself does not depend on any per-request value.
   - **Shared SaaS (future):** `from_context: auth.<key>`, where the key is an authenticator-populated claim that maps one-to-one to a tenant, or a per-tenant receiver/credential with a static value. Selecting the authenticator is part of the SaaS phase.
5. Place removal and stamping **before** the `batch` processor and any processor that rebuilds request context; `batch` does not carry request context forward unless configured with `metadata_keys`. Route and export only after stamping.
6. Validate the tenant value: in single-tenant mode, deployment tooling checks at install/startup that `JUICE_PLATFORM_TENANT_ID` matches the provisioned `PlatformTenant` record; in future SaaS, only identities in the tenant registry may map to a tenant, and unknown identities are rejected. Do not use application-supplied `ApplicationTenantId` for authorization or storage routing.
7. Test with forged tenant attributes at resource, scope, and record level on every enabled signal and prove the stored record carries only the trusted tenant. Test unauthenticated and invalid-identity rejection.

Illustrative single-tenant configuration (component names, OTTL syntax, and exporter options must be validated against the pinned Collector distribution):

```yaml
extensions:
  oidc:                                   # or basicauth/bearertokenauth, per deployment decision
    issuer_url: https://idp.example.internal/realms/juice
    audience: juice-otlp

receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
        tls:
          cert_file: /etc/otelcol/tls/server.crt
          key_file: /etc/otelcol/tls/server.key
        auth:
          authenticator: oidc

processors:
  memory_limiter:
    check_interval: 1s
    limit_percentage: 80
    spike_limit_percentage: 20
  transform/strip_untrusted_tenant:
    error_mode: ignore
    trace_statements:
      - context: resource
        statements: &strip
          - delete_key(attributes, "juice.platform_tenant_id")
          - delete_key(attributes, "platform.tenant.id")
      - context: scope
        statements: *strip
      - context: span
        statements: *strip
      - context: spanevent
        statements: *strip
    metric_statements:
      - context: resource
        statements: *strip
      - context: scope
        statements: *strip
      - context: datapoint
        statements: *strip
    log_statements:
      - context: resource
        statements: *strip
      - context: scope
        statements: *strip
      - context: log
        statements: *strip
  resource/stamp_tenant:
    attributes:
      - key: juice.platform_tenant_id
        value: ${env:JUICE_PLATFORM_TENANT_ID}
        action: upsert
      # Future shared SaaS: replace `value` with `from_context: auth.<claim>`.
  batch: {}

exporters:
  clickhouse:
    endpoint: tcp://clickhouse:9000
    # credentials, database, TTL, and table/schema settings per pinned exporter version

service:
  extensions: [oidc]
  pipelines:
    traces:
      receivers: [otlp]
      processors: [memory_limiter, transform/strip_untrusted_tenant, resource/stamp_tenant, batch]
      exporters: [clickhouse]
    metrics:
      receivers: [otlp]
      processors: [memory_limiter, transform/strip_untrusted_tenant, resource/stamp_tenant, batch]
      exporters: [clickhouse]
    logs: # Juice gap G2 closed in 10.8.0: hosts add WithLogging(l => l.AddJuiceInstrumentation().AddOtlpExporter())
      receivers: [otlp]
      processors: [memory_limiter, transform/strip_untrusted_tenant, resource/stamp_tenant, batch]
      exporters: [clickhouse]
```

The repository must contain the complete deployable config, including hostmetrics, redaction, bounded queue/retry, and exporter schema settings. Processor order is a security property: removal, then stamping, then batching.

## 5. Telemetry storage and Grafana isolation

### 5.1 ClickHouse

- Store OTLP traces, metrics, and logs in ClickHouse using a supported exporter/schema. The exporter's default tables keep resource attributes in a map column; either filter on `ResourceAttributes['juice.platform_tenant_id']` or provision custom tables with a dedicated tenant column (record the chosen approach and exporter version). Choose partition and ordering keys from measured query patterns, not tenant ID alone.
- Define retention and TTL per signal, schema/version migration, backups, restore procedure, and deletion behavior.
- Enforce tenant filtering in every platform-mediated query, parameterize values, and set time bounds, row/result limits, and query budgets.
- Do not copy raw telemetry into PostgreSQL. Store only normalized resource metadata, bounded summaries, state, and references needed by workflows.

### 5.2 Grafana

Initial single-tenant deployment uses a dedicated Grafana instance and dedicated ClickHouse credentials scoped to that deployment. Provision data sources and dashboards from configuration; do not expose shared administrative credentials to users. Platform-generated links must carry a validated time range and resource/trace filters.

For any future shared Grafana service, use a security boundary enforced by the backend: separate Grafana organizations/instances and ClickHouse credentials or a datasource proxy that enforces tenant predicates and cannot be bypassed. Apply Grafana server-side data-source permissions and query authorization. **Dashboard variables, hidden variables, URL parameters, and dashboard filters are presentation features, not security controls.** Users must not be able to alter a variable or query to read another tenant. Test direct Explore, API, annotations, exports, links, and saved queries.

## 6. Domain model and resource discovery

PostgreSQL is authoritative for platform domain state. Initial entities:

| Entity | Minimum fields/meaning |
|---|---|
| PlatformTenant | ID, display name, status, deployment identity. One provisioned record initially. |
| Application | Tenant, stable logical application identity, display name, metadata. |
| Environment | Tenant/application, stable key, environment type. |
| Resource | Tenant, resource type, stable canonical identity, display name, lifecycle/status, normalized attributes. |
| Service | Logical service identity and ownership mapping. |
| ServiceInstance | Ephemeral process/runtime identity, first/last seen, version, host/resource association. |
| Dependency | Source/target resource, relation type, evidence source, confidence, first/last seen. |
| HealthSnapshot | Subject, policy version, state, reason codes, evaluation time, evidence window. |
| Rule | Typed rule definition, scope, schedule, thresholds, duration/recovery policy, enabled state. |
| Alert | Rule/subject, lifecycle state, deduplication key, evidence references, timestamps, acknowledgement. |
| WebhookEndpoint/Delivery | Tenant-owned URL and secret reference, status, delivery attempt/result, idempotency key. |
| AuditRecord | Actor, operation, target, timestamp, outcome, safe metadata. |

Resource modeling is telemetry-driven. A discovery worker periodically queries ClickHouse for distinct resource attribute sets seen since its last checkpoint (stored in PostgreSQL). Discover candidates from trusted Collector-stamped OTLP resource attributes and standard semantic conventions (`service.name`, `service.namespace`, `service.instance.id`, `deployment.environment.name`, `host.id`, `container.id`, `k8s.*`) plus Collector hostmetrics. Normalize and validate fields; use configured mapping rules and aliases; reconcile observations to stable platform IDs. Do not require manual inventory before telemetry can be accepted. Keep volatile instance IDs separate from logical service identity. Preserve evidence source and confidence for relationships inferred from spans; do not label inferred dependencies as certain.

Use uniqueness constraints scoped by tenant and identity type. Never merge records across tenants. Bound attribute count/length and cardinality; keep high-cardinality values in telemetry storage rather than indexed domain columns. Reconciliation should tolerate restart/churn, late telemetry, renamed services, and temporary absence.

### 6.1 MVP API surface

Versioned HTTPS JSON API under `/api/v1`, authenticated through the deployment's OIDC-compatible identity provider, with role checks scoped to `PlatformTenantId`. OTLP remains the only telemetry ingestion protocol.

```text
GET  /api/v1/tenants/{tenantId}/applications
GET  /api/v1/tenants/{tenantId}/resources
GET  /api/v1/tenants/{tenantId}/resources/{resourceId}
GET  /api/v1/tenants/{tenantId}/topology
GET  /api/v1/tenants/{tenantId}/health
GET  /api/v1/tenants/{tenantId}/rules
GET  /api/v1/tenants/{tenantId}/alerts
GET  /api/v1/tenants/{tenantId}/alerts/{alertId}
POST /api/v1/tenants/{tenantId}/alerts/{alertId}/acknowledgement
GET  /api/v1/tenants/{tenantId}/webhooks/{endpointId}/deliveries
```

`{tenantId}` in the path is checked against the caller's authorized tenant and never grants access by itself. Responses use problem details, a correlation ID, and cursor pagination. Mutations that can be retried accept an idempotency key. Telemetry queries go through Grafana links or bounded platform-owned query templates; never expose ClickHouse credentials or arbitrary SQL.

Every mutation (acknowledgement, and later rule, webhook endpoint, and allowlist changes) writes an `AuditRecord` (§6 entity table) with actor, operation, target, timestamp, outcome, and correlation ID in the same transaction as the state change; audit scope is defined in §10. Denied authorization attempts on mutations are audited too.

Endpoints are delivered with the roadmap phase that owns their data: applications, resources, topology (Phase 1); health, plus instance and dependency-evidence detail in resource and topology responses (Phase 2); rules, alerts, acknowledgement (Phase 3); webhook deliveries (Phase 4). The versioning, authentication, tenant check, problem details, pagination, idempotency, and audit contracts above apply from Phase 1.

In MVP, rules, webhook endpoints, and the webhook private-destination allowlist are provisioned from version-controlled deployment configuration, validated and loaded at startup; each load that changes them writes an `AuditRecord`. The rules endpoint is read-only. Create/update APIs for these are post-MVP (Phase 6).

## 7. Health model and rollup semantics

Supported states: `Healthy`, `Degraded`, `Unhealthy`, `Critical`, `Offline`, `Unknown`.

- `Unknown` means insufficient, invalid, or never-seen evidence to evaluate the subject. It is not healthy and must not be silently converted to zero or `Offline`.
- `Offline` means a previously observed subject has passed its configured freshness/grace window without qualifying signals or heartbeat. A never-seen subject remains `Unknown`.
- A known bad signal can yield `Critical`, `Unhealthy`, or `Degraded` even if other evidence is missing; policy defines precedence.
- Every state includes reason codes, policy version, evaluation time, evidence window, and freshness. UI/API must distinguish stale evidence from a current failure.
- Aggregation is policy-driven by resource type and relationship. Policies define required signals, freshness, minimum sample count, instance quorum, critical dependencies, and state precedence.
- Default service policy: any `Critical` critical-dependency failure makes service `Critical`; insufficient required evidence yields `Unknown`; configured unhealthy instance quorum yields `Unhealthy`; partial failures yield `Degraded`; only adequate fresh healthy evidence yields `Healthy`. A service with zero active instances is `Offline` only after its grace period and only if it was previously observed.
- Parent/application rollups use configured child weights/criticality and minimum coverage; missing optional children do not make a parent unhealthy. No universal “worst child wins” rule.
- Health evaluation is idempotent for the same evidence window and policy version. Persist transitions and explanations; avoid writing a new transition for unchanged state.

## 8. Rules, alerts, and MVP webhook

MVP rules are typed, platform-owned templates evaluated on a schedule against ClickHouse. Do not accept arbitrary SQL from users. Rule execution is tenant scoped, time bounded, parameterized, rate limited, and constrained by concurrency/query budgets. Stale or missing telemetry is not zero. Support `Pending`, `Firing`, and `Resolved`, `forDuration`, recovery threshold/duration, hysteresis, deduplication, and cooldown. Persist transitions and supporting evidence references in PostgreSQL.

MVP includes signed webhook notifications for alert transitions. Endpoint ownership and secret references are tenant scoped. Sign the exact request body with HMAC-SHA256 and include a timestamp and unique delivery ID; receiver verifies signature using constant-time comparison and enforces a replay window. Keep secrets in a secret store or deployment secret provider, never in logs or ordinary database fields. SSRF controls reject loopback, link-local, and private/reserved destinations by default and revalidate DNS/IP on every connection; allow HTTPS only by default. Because the initial deployment runs inside the customer's network, where receivers are often internal, an administrator may configure an explicit allowlist of private hosts/CIDRs and ports. Allowlist changes are audited; loopback, link-local, and cloud metadata addresses stay blocked even when allowlisted ranges overlap them. Bound connect/read timeouts and response size. Retry transient failures with exponential backoff and jitter, cap attempts, use idempotent delivery IDs, and provide delivery history/replay policy. Do not let webhook failure roll back alert state. Alert APIs and operator investigation must work if notification delivery is unavailable.

## 9. Quantitative initial targets

These are MVP engineering targets for a reference deployment, to be revisited using measured workload. They are acceptance thresholds, not a promise for arbitrary scale.

| Measure | Initial target |
|---|---|
| Reference load | 10 instrumented services, 100 active instances, up to 1,000 spans/s, 10,000 metric points/s, and 1,000 log records/s sustained for a 30-minute load run. Juice gap G2 is closed in Juice 10.8.0 (unreleased); until the pilot runs a released 10.8 host, the log share may be synthetic OTLP load (for example `telemetrygen`), which exercises the Collector/ClickHouse path only and is not evidence of Juice log export. |
| Ingestion freshness | p95 ≤ 30 seconds from telemetry emission to queryable in ClickHouse under reference load. |
| Platform API | p95 ≤ 500 ms for inventory/health/alert reads at 20 requests/s, excluding telemetry query endpoints. |
| Health freshness | Health evaluated at least every 30 seconds; p95 evidence-to-visible-state ≤ 60 seconds. |
| Rule/alert latency | For a 1-minute rule schedule, p95 qualifying-breach-to-alert ≤ 2 minutes after `forDuration` is satisfied. |
| Webhook dispatch | p95 first-attempt dispatch ≤ 30 seconds after alert transition; delivery is at-least-once with stable idempotency key. |
| Availability objective | 99.5% monthly availability for Platform API and rule evaluator, excluding planned maintenance and customer infrastructure outages. This is an operating objective measured over a calendar month in a pilot or soak deployment; the release acceptance run verifies only that availability is measured (readiness probes, uptime metric) and that restart/failover behavior is tested. |
| Durability | PostgreSQL daily backups with a documented restore drill; target RPO ≤ 24 hours and RTO ≤ 4 hours for MVP. ClickHouse can be rebuilt from retained telemetry sources where available; configured backup policy must state actual coverage. |
| Retention defaults | 30 days traces/logs and 90 days metrics in the reference profile; configurable and validated against disk budget. |

Expose platform operational metrics for received/exported/dropped telemetry, Collector queue/memory, ClickHouse insert/query latency, PostgreSQL health, evaluation lag, alert transitions, webhook retries, and API latency. Platform-owned metric names use the `juice.observability.*` prefix with bounded, tenant-safe dimensions. Every target except the monthly availability objective is measured in the release acceptance run; document workload and hardware.

## 10. Security and privacy

- Require authenticated OTLP ingestion and TLS; rotate credentials and scope them to deployment/tenant.
- Treat all telemetry, resource attributes, logs, URLs, and webhook responses as untrusted input. Enforce size, type, cardinality, and retention limits.
- Redact secrets and sensitive fields before persistence/export where feasible. Never ingest business payloads by default.
- Authenticate platform users; enforce role and tenant authorization at every API and background-worker boundary. Audit configuration changes, rule changes, endpoint changes, acknowledgements, and actions.
- Parameterize database operations; bound telemetry queries and webhook destinations.
- Encrypt transport and secrets at rest; document key rotation and backup encryption.
- Pin images/packages, generate an SBOM, scan artifacts, and sign deployment artifacts where supported.
- Cross-tenant tests are still required in the single-tenant code path: the provisioned tenant boundary must reject a mismatched tenant scope, and no caller-provided tenant identifier grants access.

### Agent security (post-MVP requirement)

Agent commands use signed, typed envelopes with target identity, issued/expiry timestamps, nonce/replay protection, idempotency key, action name, bounded typed parameters, and approval reference when required. Agent verifies signatures and target locally. A local allowlist and parameter bounds always win, including over a validly signed platform command. No arbitrary shell execution, command string, or user-provided executable path is supported. Start with read-only diagnostics; every action is auditable and has timeout, cancellation, and postcondition verification.

## 11. MVP acceptance gate (end of Phase 4)

1. A sample Juice 10.8.0 host sends traces, metrics, and logs over authenticated OTLP through Collector. Logs use the Juice log processor (G2, closed in 10.8.0): each carries its trace and span ids and the `juice.*` context, and Juice payloads are not exported.
2. Collector hostmetrics provides supported CPU, memory, disk, network, and process signals.
3. Collector authenticates identity, strips/overwrites untrusted tenant attributes, stamps the trusted `PlatformTenantId`, and passes forged-tenant tests.
4. Enabled signals persist in ClickHouse with documented retention and are queryable in provisioned Grafana.
5. PostgreSQL contains tenant/application/environment/resource/service/instance/dependency/health/rule/alert/webhook delivery/audit records with migrations and stable reconciliation.
6. Health states, `Unknown`/`Offline` semantics, freshness, and policy-driven rollups are implemented and tested.
7. A scheduled typed threshold rule produces stable Pending/Firing/Resolved transitions, honors duration/recovery, and handles stale data correctly.
8. A signed webhook sends alert transitions with SSRF protections, bounded retries, signature verification guidance, and delivery history.
9. Operator can inspect alert evidence and open a tenant-safe Grafana investigation view.
10. Isolation, load, latency, backup/restore, and deployment acceptance targets in §9 are measured and recorded (the monthly availability objective is tracked from pilot operation, not this gate).

MVP excludes a Juice Agent, automated remediation, advanced correlation, custom operations console, streaming rule engine, shared SaaS control plane, and mandatory Redis/NATS/OpenSearch/object storage.

## 12. Implementation guidance for Claude Code

1. Read repository instructions and inspect existing code before implementation. `Juice.Observability` contains `docs/` and the local stack in `deploy/local` (Phase 1 Track A); establish the platform solution layout as the first Track B change, and treat the Juice framework repository as read-only from platform work (framework gaps G1–G3 are changed there, separately).
2. Keep Juice framework changes separate from platform code; do not add backend dependencies to Juice instrumentation packages.
3. Implement migrations and tenant/resource identity contracts before API/worker logic that depends on them.
4. Provision pinned Collector, ClickHouse, Grafana, and PostgreSQL for local development with one reproducible configuration. Validate every Collector component against the chosen distribution.
5. Use PostgreSQL for domain/workflow state and ClickHouse for high-volume telemetry. Avoid speculative services and infrastructure.
6. Keep APIs and queries bounded, observable, and tenant scoped. Do not accept arbitrary SQL, payload-based authorization, or shell commands.
7. Deliver acceptance criteria with executable integration evidence; scaffolding alone is not phase completion.

## 13. Upstream references

- [OpenTelemetry Collector components](https://opentelemetry.io/docs/collector/components/)
- [OpenTelemetry Collector architecture](https://opentelemetry.io/docs/collector/architecture/)
- [Collector agent deployment pattern](https://opentelemetry.io/docs/collector/deploy/agent/)
- [Grafana ClickHouse data source](https://grafana.com/docs/plugins/grafana-clickhouse-datasource/latest/)
- [ClickHouse observability overview](https://clickhouse.com/resources/engineering/observability)
