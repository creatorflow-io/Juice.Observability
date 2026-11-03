# Architecture

How telemetry flows from Juice services through the platform to the Console and to webhook receivers. Details are in
the [README](../README.md), the [requirements](observability-platform-requirements.md) and the
[deployment guide](deployment.md); the local stack is [`deploy/local/`](../deploy/local/README.md).

```
                       ┌───────────────────────────────────────────┐
                       │  Juice services (.NET, OpenTelemetry SDK) │
                       │  traces · metrics · logs                  │
                       └─────────────────────┬─────────────────────┘
                                             │ OTLP over TLS, Bearer token
                                             │ gRPC :4317 / HTTP :4318
                                             ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ OpenTelemetry Collector (contrib 0.163)                                      │
│                                                                              │
│  otlp ─► memory_limiter ─► strip_untrusted_tenant ─► stamp_tenant ─► redact  │
│                            (drop client tenant ids)  (trusted       (drop    │
│                                                       tenant from   secrets) │
│  host_metrics (30 s) ─► same pipeline                 config)                │
└─────────────────────────────────────────────────────────────────────┬────────┘
                                                                      │ clickhouse exporter
                                                                      │ (batched in its sending_queue)
                                                                      ▼
                                  ┌──────────────────────────────────────────┐
                                  │ ClickHouse: raw telemetry store          │
                                  │ spans · logs · metrics, tenant-stamped   │
                                  └───┬──────────────────────────────▲───────┘
                     read as          │                              │ read-only, templated,
                     platform_reader  │                              │ tenant-filtered queries
     ┌────────────────────────────────┼──────────────────────────────┼─────────────────────────┐
     │ Juice.Observability.Host       ▼  (one process)               │                         │
     │                                                               │                         │
     │  ┌─────────────────┐   ┌─────────────────┐   ┌─────────────────┐                        │
     │  │ Discovery       │   │ Health          │   │ Rules           │◄── rules.json          │
     │  │ every 30 s      │   │ every 30 s      │   │ scheduler, 4 at │   (validated at        │
     │  │ services, insts,│   │ 5 min window,   │   │ once, 20 s cap  │    startup)            │
     │  │ resources, deps │   │ inst/svc/app/   │   │ Pending→Firing  │                        │
     │  │ checkpointed    │   │ host states     │   │ →Resolved       │                        │
     │  └────────┬────────┘   └────────┬────────┘   └────────┬────────┘                        │
     │           │                     │                     │ alert change + outbox row       │
     │           │                     │                     │ written in one transaction      │
     │           ▼                     ▼                     ▼                                 │
     │  ┌───────────────────────────────────────────────────────────────┐                     │
     │  │ PostgreSQL (EF Core): resource model, health snapshots and    │                     │
     │  │ transitions, alerts, audit, webhook outbox and deliveries     │                     │
     │  └───────────────┬───────────────────────────────────┬───────────┘                     │
     │                  │                                   │                                 │
     │                  ▼                                   ▼                                 │
     │  ┌───────────────────────────────┐   ┌───────────────────────────────┐                 │
     │  │ REST /api/v1 (tenant-scoped)  │   │ Webhook dispatcher (every 2 s)│                 │
     │  │ resources, topology, health,  │   │ HMAC-SHA256, HTTPS only,      │                 │
     │  │ alerts, rules, telemetry      │   │ SSRF guard, retries with      │                 │
     │  │ (traces/logs/metrics/search)  │   │ backoff (8 attempts)          │                 │
     │  └───────────────▲───────────────┘   └───────────────┬───────────────┘                 │
     └──────────────────┼───────────────────────────────────┼─────────────────────────────────┘
                        │ Bearer JWT (aud juice-platform,   │
                        │ role viewer/operator/admin)       ▼
         ┌──────────────┴──────────────┐          ┌─────────────────────┐
         │ Juice Console (Angular 22)  │          │ External receivers  │
         │ served by the API image     │          │ (signed webhooks)   │
         │ :5180, dev :4200            │          └─────────────────────┘
         └──────────────┬──────────────┘
                        │ OIDC sign-in
                        ▼
         ┌─────────────────────────────┐        ┌──────────────────────────────┐
         │ Keycloak :8080              │        │ Grafana :3000 (optional)     │
         │ realm "juice", roles,       │        │ reads ClickHouse; the API    │
         │ platform_tenant claim       │        │ only adds deep links to it   │
         └─────────────────────────────┘        └──────────────────────────────┘
```

## Data flow

1. **Ingest.** Services send OTLP to the Collector. It removes any tenant id the client supplied, stamps the trusted
   tenant from deployment configuration, removes credential-like attributes, and writes to ClickHouse.
2. **Derive.** Three background workers in the API host read ClickHouse and write to PostgreSQL:
   - **Discovery** builds the resource model: services, instances, resources and dependencies.
   - **Health** evaluates instances, services, applications and hosts.
   - **Rules** evaluates the configured rules and runs the alert lifecycle.
3. **Serve.** The API reads derived state from PostgreSQL. It reads raw traces, logs and metrics from ClickHouse through
   platform-owned, tenant-filtered, bounded query templates. The Console reads only the API.
4. **Notify.** An alert change and its webhook outbox rows are written in one transaction. The dispatcher then delivers
   signed webhooks, so a webhook can never roll an alert back.
