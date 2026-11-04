# MVP acceptance record (requirements §11)

**Run:** 2026-11-01, local reference environment (`deploy/local`). Windows 11 workstation, Docker Desktop 29.9.1.
**Rerun:** 2026-11-04 for `dotnet test`, `verify.sh --outage`, the load, API latency, breach-to-alert and the Console
journey. The §9 rows for ingestion freshness, reference load, API reads and breach-to-alert, and the Console section, are
from that rerun. Everything else is from 2026-11-01 unless it gives another date.
**Decisions:** the [Phase 0 recommendations](phase-0-decisions.md), approved 2026-11-01.
**Repeat** (with `docker compose --profile platform --profile test`; run breach-to-alert and API latency during a load run):

```bash
dotnet test
deploy/local/tests/verify.sh --outage
deploy/local/tests/load.sh 30m
deploy/local/tests/breach-to-alert.sh 3
node deploy/local/tests/api-latency.mjs
deploy/local/tests/restore-drill.sh
node console/e2e/demo-traffic.mjs &    # then, about a minute later, in console/:
BASE=http://localhost:5180 node e2e/journey.mjs
```

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | A Juice 10.8 host sends traces, metrics and logs over authenticated OTLP; logs carry trace/span ids and the `juice.*` context, no payloads | **Pass** | Sample host via `scripts/run-sample.sh`. Juice G2 `LogExportTests`. Collector redaction checks in `verify.sh`. |
| 2 | Host metrics: CPU, memory, disk, network, process | **Pass** | `host_metrics` pipeline. The `HostMemory` template reads it (`RuleEvaluatorTests`). |
| 3 | Collector authenticates, strips and overwrites tenant attributes, stamps the platform tenant; forged-tenant tests pass | **Pass** | `verify.sh`: no or invalid token, plain HTTP and untrusted certificate refused; forged tenant at resource, scope and record level on every signal stored as the trusted one. `DiscoveryTests` restamping. |
| 4 | Signals stored with documented retention, queryable in provisioned Grafana (now: through the Juice API and Console; Grafana optional) | **Pass** | Per-signal TTL, 30/30/90 days (`verify.sh` checks it). Provisioned data source and dashboard: every panel's query returns data. |
| 5 | PostgreSQL holds tenant, application, environment, resource, service, instance, dependency, health, rule, alert, webhook delivery and audit records; migrations; stable reconciliation | **Pass** | 4 migrations. `MigrationTests`: clean install, step-by-step upgrade, data backfill, no unmigrated model changes. `DiscoveryTests`: restarts, replays, no cross-tenant merge. |
| 6 | Health states, Unknown/Offline semantics, freshness, policy rollups | **Pass** | `HealthEvaluatorTests` (26 cases). `HealthRunnerTests` end to end, including the outage that keeps states and evidence-to-visible in 10–11 s. |
| 7 | Scheduled typed threshold rule: stable Pending/Firing/Resolved, duration and recovery, stale data | **Pass** | `AlertLifecycleTests`. `RuleEvaluatorTests`: full lifecycle on real telemetry, no-data holding, deduplication, timeout, overload. |
| 8 | Signed webhook: SSRF protection, bounded retries, signature verification guidance, delivery history | **Pass** | `WebhookSecurityTests`. `WebhookTests`: a real signed delivery; retries with the same id and body; permanent failure; blocked destinations never contacted; alert state untouched. Delivery history API. Guidance in `deployment.md`. |
| 9 | Operator goes from alert to evidence and a tenant-safe Grafana view (now: the Console investigation) | **Pass** | Alert detail gives evidence, history, subject health (`AlertApiTests`); the Console's alert page opens the service over the breach window; `runbook.md` "From an alert to its cause". |
| 10 | §9 targets measured and recorded | see below | |

## §9 targets

| Measure | Target | Measured | Result |
|---|---|---|---|
| Ingestion freshness under reference load | p95 ≤ 30 s | p95 1.39 s, p50 0.87 s, max 1.44 s (161 probes over 30 min) | **Pass** |
| Reference load sustained 30 min: 1,000 spans/s, 10,000 points/s, 1,000 logs/s | no loss | 1,800,002 spans, 17,805,113 metric points, 1,799,993 logs stored in 30 min; Collector: 0 refused, 0 enqueue failures, 0 send failures. Points are 1.1 % under the nominal 18 M; the Collector dropped nothing, so the generator (telemetrygen, rates approximate) sent fewer | **Pass** |
| Platform API reads at 20 req/s (under reference load) | p95 ≤ 500 ms | p95 6.8 ms, p50 4.3 ms, 0 errors (1,200 requests) | **Pass** |
| Health freshness | evaluated ≤ 30 s; p95 evidence-to-visible ≤ 60 s | 30 s interval; 10–11 s measured on a 1 s loop; bound about 42 s at the 30 s interval | **Pass** |
| Rule breach-to-alert (1-minute schedule, under reference load) | p95 ≤ 2 min after ForDuration | 23 s, 59 s, 59 s after ForDuration over 3 runs (p95 59 s), polled every 5 s, during the reference load | **Pass** |
| Webhook first dispatch after transition | p95 ≤ 30 s | dispatcher polls every 2 s; delivery written in the transition's transaction; `juice.observability.webhooks.dispatch_delay` | **Pass** (bound 2 s + send) |
| Durability | daily backup; RPO ≤ 24 h, RTO ≤ 4 h | `scripts/backup.sh` and `scripts/backup-telemetry.sh` daily; restore drill 11 s (21 tables), telemetry 7 s (8 tables, 2026-11-04) | **Pass** |
| Retention defaults | 30 d traces/logs, 90 d metrics | TTL on the tables | **Pass** |
| Availability objective | 99.5 % monthly | tracked in operation; readiness `/health/ready`, restart tested | not in this gate (§11) |

## Console acceptance (Console requirements §21)

**Run 2026-11-04**, `console/e2e/journey.mjs` in headless Edge against the API image (`docker compose --profile platform`),
Grafana not used, demo traffic from `console/e2e/demo-traffic.mjs`: `demo-shop/checkout` calling `demo-shop/payments`, whose
instance `payments-2` fails every charge. **All steps pass with no browser error and no failed API call:**

1. sign in through Keycloak as `erin` (operator); 2. the overview shows the firing alerts and unhealthy services;
3. the services list, filtered to `production`; 4. `demo-shop/payments`: health `Unhealthy` with reasons, instances
(`payments-2` unhealthy); 5. Operations: `POST /charge` at 50 % errors with p50/p95/p99; 6. its error traces;
7. a trace's waterfall; 8. the failing `POST /charge` span with its log ("connection refused (pool exhausted)") from
its span id; 9. the `INSERT charges` span to the database `payments-db/payments` and the traces that reach it;
10. metrics around the span (`payments.charges` as a rate); 11. the service's logs around it; 12. the topology;
13. the `checkout → payments` edge's evidence (observed, 50 % errors, representative traces); 14. global search by
service name; 15. alerts; 16. rules. Context (environment, time range, selected operation and span) is kept in each
URL and survives back navigation.

The tenant filter of the telemetry API is mutation-checked (`TelemetryApiTests`); the Console serving rules are tested
(`ConsoleHostingTests`).

## Not covered or open

- **Console gaps:** Kubernetes workloads are not a resource type yet (§11); rules are read-only (D5); custom dashboards
  and advanced analytics are P2 and not built.
- **The production differences** in `deployment.md`: PKI, TLS in front of Grafana, Keycloak and the API, the
  customer's IdP, a secret store.
- **The load run** used one telemetrygen resource per signal. It does not model 10 services and 100 instances. Since
  2026-11-04 it goes through the test Collector, so the load shares ClickHouse with the main Collector's traffic but not
  the main Collector itself. The test Collector has the same configuration.
