# Local single-tenant stack (roadmap Phase 1, Track A)

OpenTelemetry Collector, ClickHouse, Grafana and PostgreSQL for local development, configured as requirements §4–§5
describe: authenticated TLS ingestion, tenant strip then stamp, redaction, bounded queue and retry, per-signal retention,
read-only Grafana with a provisioned data source and dashboard.

| Component | Image (pinned) | Role |
|---|---|---|
| Collector | `otel/opentelemetry-collector-contrib:0.163.1` | OTLP receiver (TLS + bearer token), host metrics, export to ClickHouse |
| ClickHouse | `clickhouse/clickhouse-server:26.8.36.3-alpine` | traces, metrics, logs (`otel` database) |
| Grafana | `grafana/grafana:13.2.5` + `grafana-clickhouse-datasource` 4.22.1 | exploration, starter dashboard |
| PostgreSQL | `postgres:17.10-alpine` | platform database (the API migrates it) |
| Platform API | built from `Dockerfile` (profile `platform`) | `/api/v1` and the platform workers; `docker compose --profile platform up -d --build --wait` |
| Keycloak | `quay.io/keycloak/keycloak:26.7.5` | OIDC identity provider for Grafana sign-in (realm `juice`) |

## Start

Needs Docker with Compose, bash (Git Bash on Windows) and openssl.

```bash
scripts/init.sh                            # once: .env with random secrets, local CA + Collector certificate, OTLP token
docker compose --profile test up -d --wait # --profile test adds the Collector that the tests and tests/verify.sh send to
tests/verify.sh                            # ingestion security, tenant stamping, redaction, storage and Grafana checks
```

`init.sh` asks for the tenant id of this deployment when run in a terminal; `JUICE_PLATFORM_TENANT_ID=<id> scripts/init.sh`
sets it without asking, and otherwise it is `local-dev`. It is set once, when `.env` is created.

| Endpoint | Address |
|---|---|
| OTLP gRPC / HTTP | `https://localhost:4317` / `https://localhost:4318` (CA `secrets/tls/ca.crt`, token in `secrets/otlp-tokens`) |
| Juice Console | http://localhost:5180 with `--profile platform` (or `npm start` in `console/` on :4200); sign in with a demo user below |
| Grafana (optional, `--profile grafana`) | http://localhost:3000, **Sign in with Keycloak** (demo users below); dashboard **Juice › Juice overview** |
| Keycloak | http://localhost:8080, admin console user `admin`, `KEYCLOAK_ADMIN_PASSWORD` |
| ClickHouse HTTP | http://127.0.0.1:8123 (admin user `admin`, `CLICKHOUSE_ADMIN_PASSWORD`) |
| PostgreSQL | `127.0.0.1:5433`, database `juice_observability`, user `juice`, `POSTGRES_PASSWORD` |
| Collector self-metrics / health | http://127.0.0.1:8888/metrics, http://127.0.0.1:13133 |

`.env` and `secrets/` are generated, git-ignored and local only. `docker compose down -v` deletes all data.

### Another machine

To open the Console, Keycloak and Grafana by the machine's address instead of `localhost`, set `PUBLIC_HOST` in
`.env` (for example `PUBLIC_HOST=10.10.10.147`) and recreate them:

```bash
docker compose --profile platform --profile grafana up -d --wait keycloak grafana platform-api
```

- Keycloak then issues every token as `http://<PUBLIC_HOST>:8080/realms/juice`; the Console's `config.json`, Grafana's
  sign-in URLs and the Console's Grafana links follow `PUBLIC_HOST`. `localhost:5180` and `localhost:3000` keep working.
- Recreating Keycloak re-imports the realm: users and changes made in its admin console are reset.
- The Console's `config.json` sets `"requireHttps": false`, because Keycloak is plain HTTP here. Production uses HTTPS
  (`docs/deployment.md`).
- `npm start` in `console/` reads `console/public/config.json` (issuer `localhost`); with another `PUBLIC_HOST`, set its
  issuer to `http://<PUBLIC_HOST>:8080/realms/juice` too.
- The Collector certificate from `init.sh` names `PUBLIC_HOST` too, so other machines can send OTLP to it. After changing
  `PUBLIC_HOST`, run `scripts/init.sh --reissue-cert` and `docker compose restart otel-collector`. The CA stays the same, so
  hosts that already trust `ca.crt` keep working.

## Send telemetry from a Juice host

`scripts/run-sample.sh` runs the Juice sample (`samples/Juice.Samples.Observability` in the Juice repository) against
the stack. Any Juice 10.8 host does the same with the standard OpenTelemetry variables:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:4317            # gRPC; or :4318 with OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <token>"
OTEL_EXPORTER_OTLP_CERTIFICATE=<path>/secrets/tls/ca.crt     # trusts the local CA (OpenTelemetry .NET 1.19)
```

The host registers `AddOtlpExporter()` on tracing, metrics and logging, with `AddJuiceInstrumentation()` before the
exporter on logging (G2). The .NET metric exporter sends every 60 s by default; `OTEL_METRIC_EXPORT_INTERVAL`
(milliseconds) shortens it.

## What the Collector does

The pipeline order is a security property (requirements §4 step 5); `collector/config.yaml` documents it inline.

1. **Authentication.** Both OTLP protocols require TLS 1.2+ and `bearertokenauth` against the token file
   (`secrets/otlp-tokens`, one token per line, comments after the token). Token changes take effect after
   `docker compose restart otel-collector`. Plain HTTP, a missing or unknown token, and
   an untrusted certificate are refused. The authenticator provides no `auth.*` claim used here: in single-tenant
   mode the tenant is static (§4 step 4), so no request value reaches the tenant.
2. **Strip** (`transform/strip_untrusted_tenant`): deletes `juice.platform_tenant_id`, `platform.tenant.id` and
   `tenant.id` at resource, scope and record level (span, span event, data point, log) on every signal.
3. **Stamp** (`resource/stamp_tenant`): upserts `juice.platform_tenant_id` = `JUICE_PLATFORM_TENANT_ID` from `.env`.
4. **Redact** (`transform/redact`): deletes credential-like attributes (password, secret, API key, authorization,
   cookie, bearer, access/refresh token, connection string) and, on `Juice.*` log categories, the `Payload` attribute,
   a second line behind the Juice log processor.
5. **Export** to ClickHouse with batching inside the exporter's `sending_queue` (1 s flush, 5,000–20,000 items), so it
   runs after strip and stamp; no `batch` processor. The queue is bounded at 200,000 items per signal and drops beyond
   that (`otelcol_exporter_enqueue_failed_*`); retries back off from 2 s to 30 s for up to 5 minutes.
   The queue is in memory: a Collector restart loses queued data.

Host metrics (`host_metrics`: CPU, memory, load, disk, filesystem, network, processes, process) use their own pipeline
with `resource_detection` so application telemetry never gets the Collector's host name. With Docker Desktop the
"host" is the Docker Linux VM; `JUICE_HOST_NAME` sets `host.name` (default `local-docker-host`).

## Storage

- **Tables.** The exporter creates its default tables in the `otel` database (`create_schema: true`, exporter
  v0.163.0): `otel_traces`, `otel_logs`, `otel_metrics_{sum,gauge,histogram,exponential_histogram,summary}`.
- **Tenant.** Recorded approach (§5.1): default tables, tenant read from
  `ResourceAttributes['juice.platform_tenant_id']`. Platform queries (Track B) must filter on it. The traces table
  does not store scope attributes.
- **Retention.** One exporter per signal, so each has its own TTL: traces and logs 30 days, metrics 90 days
  (`JUICE_*_TTL` in `.env`, the §9 reference profile). The TTL is applied when the tables are created. To change it
  later, run `ALTER TABLE otel.<table> MODIFY TTL toDateTime(<time column>) + toIntervalHour(<hours>)` on each table of
  the signal (time column: `Timestamp` for traces/logs, `TimeUnix` for metrics, `Start` for
  `otel_traces_trace_id_ts`), and update `.env`.
- **Users** (`clickhouse/init-users.sh`, first start only):

  | User | Can do |
  |---|---|
  | `otel_writer` | Collector: create tables, insert |
  | `grafana_reader` | Grafana: SELECT on `otel` only |
  | `platform_reader` | Track B workers: SELECT on `otel` only |

  The two readers share the `bounded_reader` profile: `readonly = 2` (no writes or DDL), with execution time 60 s,
  result rows 1,000,000 and memory 4 GB, each capped with `MAX` so a client cannot raise it. The Grafana data source
  sets `queryTimeout` 50 s to stay under the cap.
- **Backups.** `scripts/backup.sh` (PostgreSQL) and `scripts/backup-telemetry.sh` (ClickHouse `otel`) write to
  `backups/`, which git ignores. Each run is a full backup and nothing prunes old ones. See the runbook.
- **Integration test tenant.** `--profile test` adds `otel-collector-test` on `127.0.0.1:14318`, the same configuration
  stamping the tenant `local-test`, and the tests get their tokens from the Keycloak client `juice-platform-test`. What
  they write stays out of the tenant the running API works on. `tests/verify.sh` and `tests/load.sh` send to it too. Only
  `tests/breach-to-alert.sh` sends to the main Collector, because it waits for the running API's alert, so each run leaves
  a `breach-<id>` service in the Console.

## Grafana

The data source (`grafana/provisioning/datasources`) and the **Juice overview** dashboard are provisioned from files.
They are read-only in the UI. Anonymous access and sign-up are off.

### Sign-in (OIDC)

People sign in through Keycloak (realm `juice`, confidential client `grafana`, authorization code with PKCE). The
username/password form is hidden. Grafana roles come from the `grafana` client roles, which Keycloak puts in the
`roles` claim. Grafana re-reads the role at every sign-in.

| Demo user | Client role | Grafana |
|---|---|---|
| `alice` | `admin` | Admin of the organization (not Grafana server admin) |
| `erin` | `editor` | Editor |
| `victor` | `viewer` | Viewer |
| `nora` | none | refused (`role_attribute_strict`) |
| `adam` | none (platform `admin` only) | refused; a second platform admin, for approvals that need a second person |

All demo users share the password `KEYCLOAK_DEMO_PASSWORD` from `.env`. To add a person, create the user in the
Keycloak admin console and assign a `grafana` client role.

- **Dev mode.** Keycloak runs `start-dev` with its built-in database, and `keycloak/juice-realm.json` is imported when
  the container is created. Users added in the console are lost when the container is recreated, so put lasting
  changes in the realm file. Placeholders like `${GRAFANA_OIDC_CLIENT_SECRET}` in that file are filled from `.env`.
  Users in the file have fixed ids. Grafana links each account to the token's `sub`, so a new id on re-import would
  lock the user out with `user.sync ... user not found`.
- **Separate clients, separate tokens.** The same realm serves the platform API: client `juice-platform` (the audience,
  with roles `viewer`, `operator`, `admin`) and `juice-platform-cli` (password grant, local only). Every client
  carries only its own roles (`fullScopeAllowed: false`). Without that, Keycloak adds the audience of every client
  whose roles a user holds, and a Grafana token would carry `aud: juice-platform`. `tests/verify.sh` checks this.
- **Two URLs for one issuer.** The browser reaches Keycloak at `localhost:8080`, and Grafana calls the token and
  userinfo endpoints at `keycloak:8080` on the compose network. `KC_HOSTNAME` keeps the issuer as
  `http://localhost:8080/realms/juice` for both.
- **The local `admin` account remains.** Its password is `GRAFANA_ADMIN_PASSWORD`. Use it for the API (basic auth),
  the test scripts, and break-glass access. Password sign-in through `/login` is refused (`auth.client.notConfigured`); only
  HTTP basic auth on the API accepts it.

The dashboard has these panels:

- **Traffic, from traces:** server and consumer spans, failures, and a p50/p95 table per operation.
- **Juice metrics:** `juice.mediator.*` requests and errors, `juice.messaging.consumed` by result, and outbox delivery outcomes.
- **Logs:** warnings and errors, with the correlation id and a link to the trace.
- **Host:** memory used and load average.

The service variable is presentation only, not a security control (§5.2). To change the dashboard, edit
`grafana/build-dashboard.mjs`, run `node grafana/build-dashboard.mjs`, then `docker compose restart grafana`.

**Explore access (§5.2).** Explore and ad-hoc queries are open to Editor and Admin. Viewers see dashboards only,
Grafana's default (`viewers_can_edit` off). Every query, from a dashboard, Explore or the API, runs as
`grafana_reader`:

- it can only read the `otel` database;
- time, rows and memory are capped by a profile the client cannot raise;
- it can't write, and it can't read `system.users`.

This instance and its ClickHouse credentials serve one deployment, so the boundary is the deployment itself, not a
dashboard filter.

## Tests

| Script | Checks |
|---|---|
| `tests/verify.sh` | Plain HTTP refused; missing and unknown token give 401; untrusted certificate refused. Forged tenant at resource, scope and record level, on traces, metrics and logs, is stored as only the trusted tenant. Redaction. TTL present. `grafana_reader` cannot write or read `system.users`. Grafana data source healthy; anonymous access refused. OIDC sign-in, run headless with curl through Keycloak: alice → Admin, erin → Editor, victor → Viewer, nora and a wrong password refused, no OIDC user is server admin; a Grafana access token carries no platform audience or roles. Prints this run's ingestion freshness. |
| `tests/verify.sh --outage` | Also: ClickHouse stopped, a record accepted and held in the exporter queue, then stored after ClickHouse returns. |
| `tests/breach-to-alert.sh [runs]` | Through the running platform: sends a failing service and times rule `service-error-rate` firing after its ForDuration (§9: ≤ 2 min). |
| `node tests/api-latency.mjs [seconds]` | Platform API reads at 20 requests/s, p50/p95 (§9: p95 ≤ 500 ms). |
| `tests/restore-drill.sh` | Backs up the platform database and the telemetry database, restores each into a scratch database, compares tables, reports the time. |
| `tests/load.sh [duration]` | telemetrygen v0.163.0 at the §9 reference rates (1,000 spans/s, 10,000 metric points/s, 1,000 logs/s) over authenticated TLS, with a freshness probe every 10 s. Reports p50/p95 freshness, stored counts, and Collector refused/dropped counts. Fails if p95 > 30 s. |

### Measured (2026-10-30)

Hardware: Windows 11 developer workstation, Docker Desktop 29.9.1.

- **Idle:** `tests/verify.sh` freshness 1.5 s (three OTLP posts, then poll until queryable).
- **60 s load at reference rates:** freshness p50 0.86 s, p95 1.36 s (target p95 ≤ 30 s). Stored 60,010 spans, 593,263
  metric points and 60,007 logs. 0 refused, 0 enqueue failures, 0 send failures. This is a short run on one
  workstation, not the 30-minute release acceptance run.

## Not covered here

- **Forged-attempt counting.** A `count` connector before the strip step (§4 step 3) is optional and not configured.
  Forged values are deleted silently.
- **Collector self-metrics** are exposed only on the Prometheus endpoint (`:8888`), not stored in ClickHouse.
- **Persistent queue.** The queue is not on `file_storage`, and the Collector is not highly available.
- **Production deployment.** HTTPS for Grafana and Keycloak, Keycloak on a real database, OIDC for OTLP clients, credential rotation, real certificates, backups and an SBOM belong to the
  deployable profile.
