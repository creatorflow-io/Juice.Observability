# Juice Observability Platform

Turns telemetry from Juice services into a resource model, health, rules, alerts and investigation workflows.

| Path | Contents |
|---|---|
| [`docs/`](docs) | [Requirements and architecture](docs/observability-platform-requirements.md), [phased roadmap](docs/observability-platform-roadmap.md), [Phase 0 decisions](docs/phase-0-decisions.md), [Juice 10.8 telemetry baseline](docs/juice-opentelemetry-10.8-current-state.md) |
| [`deploy/local/`](deploy/local/README.md) | Local single-tenant stack: OpenTelemetry Collector, ClickHouse, Grafana (OIDC sign-in via Keycloak), PostgreSQL, with integration and load tests |
| `src/Juice.Observability.Data` | PostgreSQL domain model (EF Core) and migrations |
| `src/Juice.Observability.Discovery` | Discovery: reads Collector-stamped telemetry from ClickHouse and reconciles services, instances, environments, resources and dependencies |
| `src/Juice.Observability.Api` | `/api/v1` (OIDC, tenant-scoped) and the background workers, in one host |
| `test/Juice.Observability.Tests` | Unit tests, and integration tests against the running local stack |

## Build and test

Needs the .NET 10 SDK. Integration tests need the local stack running and are skipped otherwise.

```bash
deploy/local/scripts/init.sh && (cd deploy/local && docker compose up -d --wait)
dotnet test
deploy/local/scripts/run-api.sh          # API on http://localhost:5180, migrates and provisions the tenant
```

Build output goes to `build/`. Add a migration with
`dotnet tool restore && dotnet ef migrations add <Name> --project src/Juice.Observability.Data --output-dir Migrations`;
`MigrationTests` fails when the model changes without one.

## API (Phase 1)

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
| `GET /api/v1/tenants/{tenantId}/topology?environment=` | services and resources with the dependencies between them, each with its evidence and confidence |

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
