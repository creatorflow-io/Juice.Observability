# Juice Observability Platform

Turns telemetry from Juice services into a resource model, health, rules, alerts and investigation workflows.

| Path | Contents |
|---|---|
| [`docs/`](docs) | [Architecture diagram](docs/architecture.md), [requirements and architecture](docs/observability-platform-requirements.md), [phased roadmap](docs/observability-platform-roadmap.md), [Phase 0 decisions](docs/phase-0-decisions.md), [deployment guide](docs/deployment.md), [runbook](docs/runbook.md), [MVP acceptance](docs/mvp-acceptance.md), [Juice 10.8 telemetry baseline](docs/juice-opentelemetry-10.8-current-state.md) |
| [`console/`](console) | **Juice Console**, the product UI (Angular 22, `juice-layout`): services, operations, traces, logs, metrics, topology, resources, alerts, rules. The pages are the `@juice-js/observability` library (`console/projects`); served by the API image; reads only the Juice API |
| [`deploy/local/`](deploy/local/README.md) | Local single-tenant stack: OpenTelemetry Collector, ClickHouse, PostgreSQL, Keycloak, optional Grafana, with integration and load tests |
| `src/Juice.Observability.Data` | PostgreSQL domain model (EF Core) and migrations |
| `src/Juice.Observability.Discovery` | Discovery: reads Collector-stamped telemetry from ClickHouse and reconciles services, instances, environments, resources and dependencies |
| `src/Juice.Observability.Health` | Health evaluation: instances, services and applications per environment, and hosts, from ClickHouse evidence |
| `src/Juice.Observability.Rules` | Typed scheduled rules over ClickHouse templates and the alert lifecycle |
| `src/Juice.Observability.Webhooks` | Signed webhook notification of alert transitions: outbox, SSRF-safe delivery, retries, history |
| `src/Juice.Observability.Api` | `/api/v1` (OIDC, tenant-scoped) and the background workers, as a library (`AddJuiceObservability`, `MapJuiceObservability`) |
| `src/Juice.Observability.Host` | The reference host (the image): the API library and the Console on one origin |
| `test/Juice.Observability.Tests` | Unit tests, and integration tests against the running local stack |

## Build and test

Needs the .NET 10 SDK. Integration tests need the local stack running and are skipped otherwise.

```bash
deploy/local/scripts/init.sh && (cd deploy/local && docker compose up -d --wait)
dotnet test
deploy/local/scripts/run-api.sh          # API on http://localhost:5180, migrates and provisions the tenant
(cd console && npm ci && npm start)      # Console on http://localhost:4200, proxying /api to :5180
```

`docker compose --profile platform up -d --build` serves the Console and the API together on http://localhost:5180.
`console/e2e/journey.mjs` signs in through Keycloak and walks the investigation journey (Console requirements §21) in a
headless browser.

Build output goes to `build/`. Add a migration with
`dotnet tool restore && dotnet ef migrations add <Name> --project src/Juice.Observability.Data --output-dir Migrations`;
`MigrationTests` fails when the model changes without one.

## Packages and CI

Besides the image, the platform ships as libraries for a host app of the customer's own:

| Package | Feed | Contents |
|---|---|---|
| `Juice.Observability.Api` (NuGet) | nuget.org; alpha/beta on GitHub Packages | `AddJuiceObservabilityFiles()`, `AddJuiceObservability()`, `MapJuiceObservability()`: the API, the workers, auth and health checks. Depends on `.Data`, `.Discovery`, `.Health`, `.Rules`, `.Webhooks`, also published |
| `@juice-js/observability` (npm) | npmjs | `provideJuiceObservability()`, `OBSERVABILITY_ROUTES`, `observabilityMenus()` and the page styles ([usage](console/projects/juice-js/observability/README.md)) |

`src/Juice.Observability.Host` and `console/src` are the reference host and app: they use the packages the way a
customer would.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJuiceObservabilityFiles();
builder.Services.AddJuiceObservability(builder.Configuration);
var app = builder.Build();
app.UseCorrelationId();
app.UseAuthentication();
app.UseAuthorization();
app.MapJuiceObservability();
app.Run();
```

Publishing (`.github/workflows`): the backend and the console library are versioned and released independently.

| Package | Its next version lives in | Release tag |
|---|---|---|
| `Juice.Observability.*` | `<VersionPrefix>` in `Directory.Build.props` | `api-<version>` |
| `@juice-js/observability` | `"version"` in `console/projects/juice-js/observability/package.json` | `console-<version>` |

| You do | You get |
|---|---|
| push to `main`, `dev/**` | build and test only (`build.yml`) |
| push to `release/**` that changes `src/**` (or the props / `nuget.config`) | NuGet alphas at `<VersionPrefix>-alpha.<run>` on GitHub Packages (`alpha-nuget.yml`) |
| push to `release/**` that changes `console/projects/juice-js/observability/**` | an npm alpha at `<version>-alpha.<run>` under `@alpha` (`alpha-npm.yml`) |
| create a GitHub release on `main` tagged `api-1.2.0` | the backend at 1.2.0 on nuget.org (`release.yml`) |
| create a GitHub release on `main` tagged `console-1.4.0` | the console library at 1.4.0 on npm under `latest` |
| the same, on `release/1.x` | a patch of an older line; npm under `release-1.x`, never replacing `latest` |
| the same, with a pre-release version (`api-1.2.0-beta.1`, `console-1.4.0-rc.1`) | a beta or rc: nuget.org as a pre-release, npm under the label (`beta`, `rc`) |

Each next version is the one to release: raise it after a release so alphas sort above it. Tags have no `v`.

The console library calls the API over HTTP, so record which versions work together:

| `@juice-js/observability` | needs `Juice.Observability.Api` |
|---|---|
| 0.1.x | 0.1.x |

## API

All routes need a bearer token from the deployment's OIDC provider, with audience `juice-platform` and a platform role
(`viewer`, `operator` or `admin`) in the `roles` claim. Errors are RFC 9457 problem details with `correlationId` and
`traceId`; send `X-Correlation-Id` to set your own. Lists take `limit` (1–200, default 50) and `cursor` (the previous
page's `nextCursor`).

| Route | Returns |
|---|---|
| `GET /api/v1/tenants` | the caller's tenant (from its `platform_tenant` claim) |
| `GET /api/v1/tenants/{tenantId}/applications` | applications with their service counts |
| `GET /api/v1/tenants/{tenantId}/resources?type=` | services first, then hosts, containers, pods, messaging destinations and databases |
| `GET /api/v1/tenants/{tenantId}/resources/{id}` | a service with its instances, or a resource (a host lists the instances on it) |
| `GET /api/v1/tenants/{tenantId}/topology?environment=` | services and resources with the dependencies between them, each with its evidence and confidence; with an environment, each node's health |
| `GET /api/v1/tenants/{tenantId}/health?subjectType=&environment=&state=` | current health with reasons, policy version, evidence window and `stale` |
| `GET /api/v1/tenants/{tenantId}/rules` | the loaded rules (read-only) |
| `GET /api/v1/tenants/{tenantId}/alerts?state=&rule=` | alerts, newest first |
| `GET /api/v1/tenants/{tenantId}/alerts/{alertId}` | an alert with its evidence, state history, the subject's health and any integration links |
| `GET /api/v1/tenants/{tenantId}/environments` | the environments seen in telemetry |
| `GET /api/v1/tenants/{tenantId}/telemetry/traces?serviceId=&environment=&operation=&status=&minDurationMs=&maxDurationMs=&instanceId=&resourceId=&dependencyId=&traceId=&spanId=&attribute=key=value&from=&to=&cursor=` | traces with a span matching every filter, by latest matching span (window ≤ 24 h); pass `nextCursor` as `cursor`, with the same window, for the next page |
| `GET /api/v1/tenants/{tenantId}/telemetry/traces/{traceId}` | `traceId` is the 32-hex id or a W3C `traceparent`; every span (≤ 1,000) with service id, instance, host, scope, attributes, events, links, and the service or resource a call reaches |
| `GET /api/v1/tenants/{tenantId}/telemetry/logs?serviceId=&instanceId=&resourceId=&traceId=&spanId=&minSeverity=&q=&from=&to=&cursor=` | log records, newest first; `nextCursor` pages back like traces |
| `GET /api/v1/tenants/{tenantId}/telemetry/operations?serviceId=` | a service's entry operations with throughput, error rate, p50/p95/p99 |
| `GET /api/v1/tenants/{tenantId}/telemetry/dependencies/{dependencyId}` | an edge's evidence: calls, error rate, p95, last seen, representative traces |
| `GET /api/v1/tenants/{tenantId}/telemetry/metrics`, `…/metrics/series?name=&stepSeconds=`, `…/service-stats?serviceId=` | metric names, one metric as a series (counter → rate), a service's requests, errors and p95; empty buckets have no point |
| `GET /api/v1/tenants/{tenantId}/search?q=` | a trace id or W3C `traceparent`, a span id (last 24 h) or services by name |
| `GET /api/v1/tenants/{tenantId}/webhooks` | webhook endpoints (destination host only, never the secret) |
| `GET /api/v1/tenants/{tenantId}/webhooks/{endpointId}/deliveries?status=` | delivery history, each with every attempt |
| `POST /api/v1/tenants/{tenantId}/alerts/{alertId}/acknowledgement` | acknowledge an open alert: `operator` or `admin`; send `Idempotency-Key` to retry safely |

A resource's detail also carries its health per environment and each instance's state. With
`Integrations:Grafana:BaseUrl` set, a service and an alert also carry a link to the optional Grafana overview; without
it, `links` is empty. Telemetry queries are platform-owned templates, tenant-filtered and bounded; services, instances,
resources and dependencies are named by platform id, never by telemetry names from the caller.

`{tenantId}` must be the caller's tenant; any other id, existing or not, gets the same 403. `/health/live` and
`/health/ready` are anonymous.

A local token, for example for `victor` (viewer):

```bash
source deploy/local/.env
curl -s -d grant_type=password -d client_id=juice-platform-cli -d username=victor \
  -d "password=$KEYCLOAK_DEMO_PASSWORD" http://localhost:8080/realms/juice/protocol/openid-connect/token
```

`juice-platform-cli` exists only in the local realm, for development and tests.

## Discovery

Every 30 s the worker reads telemetry from its checkpoint to one minute ago, in windows of at most 15 minutes, as
`platform_reader`. Every query filters on the Collector-stamped tenant. It reconciles each window and moves the
checkpoint in one transaction.

Each window also re-reads the 10 minutes before the checkpoint (`Discovery:Overlap`). Telemetry is stored by its own
time, so a row can land after its window was read: a long span is written when it ends but timed by its start, and the
Collector holds data back while ClickHouse is slow. Upserts make the re-read harmless. A dependency's span count adds
only spans after the checkpoint, unless the dependency is first seen in the overlap. Telemetry later than one minute
plus the overlap is still missed.

- **Services** are keyed by namespace and name, across environments. Each namespace is one application.
- **Instances** are keyed by `service.instance.id`, or by host and process id (marked derived). An instance goes
  inactive after 15 minutes without telemetry and is deleted 30 days later.
- **Resources:**

  | Resource | Key |
  |---|---|
  | Host | `host.id`, else `host.name` |
  | Container | `container.id` |
  | Pod | `k8s.pod.uid` |
  | Messaging destination | system + destination |
  | Database | system + server + namespace |

- **Dependencies:**
  - A client or producer span whose child server or consumer span is in another service gives a dependency between
    the two services, confidence `observed`.
  - A span naming a destination or a database gives a dependency on that resource, confidence `inferred`, since the
    resource itself never reports.
- **Renamed services** map through `Discovery:Aliases`.
- **Limits.** A window that fills `Discovery:RowLimit` rows is split, down to 10 s. Values are cut to 256 characters
  and attributes to 64. Both are counted in `juice.observability.discovery.truncated`.

## Health

Every 30 s the health worker reads a 5-minute evidence window ending 10 s ago. It evaluates:

- each active instance;
- each service in every environment it was seen in;
- each application per environment;
- each host.

It writes one current snapshot per subject, and a transition only when the state changes.

| State | Meaning |
|---|---|
| `Unknown` | Never seen, or not enough evidence. Never shown as healthy. |
| `Offline` | Seen before and silent for more than 5 minutes. |
| `Healthy`, `Degraded`, `Unhealthy`, `Critical` | Judged on fresh evidence. |

- **Instances.** The error rate of server and consumer spans: 1 % is `Degraded` and 5 % is `Unhealthy`, counted only
  once there are at least 3 requests. With less traffic, recent telemetry alone is a heartbeat.
- **Services:**
  - a failed critical dependency (`Health:CriticalDependencies`) makes the service `Critical`;
  - if half or more of its fresh instances are unhealthy, it is `Unhealthy`;
  - any partial failure, or an elevated service error rate, makes it `Degraded`.

  Instances that went silent after a restart don't count.
- **Applications.** Children are weighted, optionally critical (`Health:ServiceWeights`), with a minimum coverage of
  50 %. Missing optional services never make an application unhealthy.
- **Reasons** are stable codes, such as `partial_instance_failure` or `critical_dependency_failed:shop/billing`.
- **Policy version.** Each snapshot and transition carries the policy version, a hash of the rule settings.
- **Replays.** A transition is unique per subject, evidence window and policy version, so re-evaluating the same
  evidence never adds one.
- **ClickHouse unavailable.** The pass fails and the states are kept. The API marks a state `stale` once it is older
  than three intervals.
- **Freshness.** A new failure is visible after about the evidence lag (10 s) plus ingestion; tested at 10–11 s with a
  1 s evaluation loop. With the 30 s interval the bound is about 42 s, against the 60 s target.
  `juice.observability.health.evidence_age` records it in operation.

## Rules and alerts

Rules are deployment configuration. `Rules:File` names a JSON file (`deploy/local/rules.json`) that is validated and
loaded at startup:

- an invalid rule stops the host;
- each create, update or disable writes an audit record (actor `deployment-config`);
- the API only reads rules.

| Template | Value (fires above, except ThroughputAbsent) |
|---|---|
| `ErrorRate` | failed / all server and consumer spans |
| `LatencyP95` | p95 duration of server and consumer spans, ms |
| `ThroughputAbsent` | requests in the window, for services with traffic in the hour before (fires at or below) |
| `DeliveryFailureRate` | increase of `delivery_failure_total` / `delivery_attempt_total` |
| `HostMemory` | used / total memory from host metrics |

Each rule is scoped by `Service` and `Environment` (or `Host`). It also has a schedule (default 1 min), a window,
a threshold and a recovery threshold (hysteresis), minimum samples, `ForDuration`, `RecoveryDuration` and
`Cooldown`.

- **Lifecycle.** A breach opens a **Pending** alert. It becomes **Firing** once the breach has held for
  `ForDuration`, and **Resolved** after `RecoveryDuration` at or below the recovery threshold.
  - A Pending alert that stops breaching resolves without ever firing, so a transient breach never fires.
  - Values between the two thresholds keep a Firing alert firing.
  - After an alert that fired resolves, a new one can't fire until `Cooldown` has passed.
- **Deduplication.** There is one open alert per rule and subject, enforced by a unique index.
- **No data.** Too few samples, or no rows, means *no data*. It is recorded on the alert and holds the alert's
  state; it is never read as zero.
- **Scheduler.** It runs 4 evaluations at once, each cut off after 20 s. A rule still running when due again is
  skipped, never stacked.
  - Metrics: `juice.observability.rules.evaluations`, `.duration`, `.lag`, `.skipped` and
    `juice.observability.alerts.transitions`.
- **Breach-to-alert.** Firing comes at most lag (10 s) plus one schedule after `ForDuration`: 70 s for a 1-minute
  rule, against the 2-minute target. Measured in tests at 22 s with a 5 s schedule and a 10 s `ForDuration`.

## Webhooks

When an alert fires, or resolves after firing, one delivery per matching endpoint is written in the same transaction
as the alert change. A webhook can therefore never roll an alert back, and replaying an evaluation never notifies
twice. The dispatcher sends every 2 s.

- **Signing.** HMAC-SHA256, Standard Webhooks form: `webhook-id`, `webhook-timestamp`,
  `webhook-signature: v1,<base64>` over `{id}.{timestamp}.{body}`.
- **Destinations.** HTTPS only, no redirects. DNS is checked on every connection: one internal answer blocks the
  destination. Private addresses need an allowlist entry. Loopback, link-local and cloud metadata are never
  reachable.
- **Retries.** Transient failures retry with exponential backoff and jitter (10 s to 10 min, 8 attempts). Other 4xx
  fail at once. Every attempt is recorded.

Configuration, secrets and receiver guidance are in [docs/deployment.md](docs/deployment.md).
