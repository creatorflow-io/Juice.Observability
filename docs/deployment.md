# Deployment guide (single-tenant)

One deployment serves one customer organization (requirements §2.1). Everything runs from `deploy/local`, the pinned
reference configuration. A production install uses the same files with real certificates, credentials and backups.

## Components

| Component | Version (pinned) | Role |
|---|---|---|
| OpenTelemetry Collector contrib | 0.163.1 | the only ingestion point: OTLP over TLS with bearer tokens |
| ClickHouse | 26.8.36.3 | telemetry storage (traces 30 d, logs 30 d, metrics 90 d) |
| PostgreSQL | 17.10 | platform state: inventory, health, rules, alerts, webhooks, audit |
| Platform API and Juice Console | this repository (`Dockerfile`) | `/api/v1`, the discovery, health, rule and webhook workers, and the Console (the product UI) on the same origin |
| Keycloak | 26.7.5 | OIDC for the Console, the API and Grafana (replace with the customer's IdP) |
| Grafana + ClickHouse plugin (optional) | 13.2.5 + 4.22.1 | only for customers who use Grafana: `docker compose --profile grafana` |

## Install

```bash
cd deploy/local
scripts/init.sh                                     # .env secrets, local CA and Collector certificate, OTLP token
docker compose --profile platform --profile test up -d --build --wait
tests/verify.sh                                     # ingestion, tenant stamping, redaction, Grafana, OIDC checks
docker compose --profile test stop otel-collector-test
```

`init.sh` asks for the tenant Id, or takes it from `JUICE_PLATFORM_TENANT_ID`: the tenant's Id from juice-tenants, a GUID (the tenant
pattern, juice-docs decision record). Locally it defaults to the local stacks' tenant, `0193f0aa-0000-7000-8000-0000000000a1`. Set `PUBLIC_HOST` before it runs, or run
`scripts/init.sh --reissue-cert` after changing it, so the Collector certificate names the address hosts send to.
`tests/verify.sh` sends through the test Collector, so its records stay out of the deployment's tenant.

The API applies database migrations on start (`Database:Migrate`). It creates the tenant from
`JUICE_PLATFORM_TENANT_ID`, and refuses to start against a database that belongs to another tenant.

The Juice.Notifications pipeline is embedded in the API (phase 5): its tables are the platform database's `notifications` schema, migrated
right after the platform's when `Database:Migrate` is on (`Notifications:Database:Migrate` overrides it). `Notifications:Pipeline` says
which pipeline sends: `legacy`, the default and for now the only one, keeps the platform's own webhooks and runs none of the pipeline's
stages.

## Configuration

| What | Where | Notes |
|---|---|---|
| Tenant | `.env` `JUICE_PLATFORM_TENANT_ID` | the tenant's Id (a GUID); stamped by the Collector, provisioned by the API; a deployment named before the tenant pattern moves to its Id once (below) |
| OTLP clients | `secrets/otlp-tokens`, one token per line | restart the Collector after a change (runbook) |
| Rules | `rules.json` (`Rules:File`), or the API and Console | validated at start; an invalid rule stops the API; every change audited |
| Channels and allowlist | `webhooks.json` (`Webhooks:File`), or the API and Console | secrets as `env:NAME` or `file:/path`, never in the file |
| SLOs | `slos.json` (`Slos:File`) only; the API and Console show them | validated at start; an invalid SLO stops the API; a changed one is a new version, audited (see below) |
| SLO counting | `Slos:Interval` (30 s), `Slos:Lateness` (5 min), `Slos:MaxMinutesPerPass` (720), `Slos:Retention` (35 days), `Slos:QueryTimeout` (30 s), `Slos:MinimumEvents` (1,000 in 30 days), `Slos:MinimumBurnEvents` (10 per burn window), `Slos:UnknownTolerance` (0.10), `Slos:Enabled` | at most 200 SLOs; `Interval` at most 1 minute; `MaxMinutesPerPass` at least `Lateness` plus 5 minutes; the newest minutes are counted again until they're `Lateness` behind the service's newest telemetry, then sealed; a new SLO counts its 30 days `MaxMinutesPerPass` at a time, half that after a pass that runs out of `QueryTimeout` |
| Secrets for channels set through the API | environment variables named `Webhooks:ApiSecretEnvPrefix` (default `JUICE_WEBHOOK_`) + a name, or files directly in `Webhooks:ApiSecretDirectory` | the API refuses any other reference, so an admin can't name other secrets on the host |
| Email channels | `Webhooks:Smtp`: `Host`, `Port` (587), `From`, `UseTls` (true: STARTTLS), `UsernameRef`, `PasswordRef` | without it, email channels are refused; `UseTls` requires STARTTLS (a server without it is refused); implicit TLS on 465 isn't supported |
| Slack and Teams | The channel's URL behind a secret reference | they post only to `hooks.slack.com`, `*.webhook.office.com`, `*.logic.azure.com` and `*.api.powerplatform.com`, plus any in `Webhooks:AdditionalChatHosts` |
| Links in notifications | `Webhooks:ConsoleUrl` | optional; unset means no links |
| Health policy | `Health` section (environment `Health__…`) | grace, thresholds, critical dependencies, service weights |
| Users and roles | the IdP | Console and API: client `juice-console` (public, PKCE) issuing tokens for `juice-platform` with roles `admin`, `operator`, `viewer` and the `platform_tenant` claim; Grafana (optional): client `grafana` roles `admin`, `editor`, `viewer` |
| Console sign-in | `wwwroot/config.json` in the API image | `{"apiBase": "", "auth": {"issuer": "<IdP>", "clientId": "juice-console", "scope": "openid profile email"}}`; mount your own file over it. `auth.requireHttps` (default `"remoteOnly"`: plain HTTP only for localhost) is `false` only for a local stack |
| Grafana links | `Integrations:Grafana:BaseUrl` | optional; unset means no links |
| The platform's status | `/health/platform` (anonymous, `ok`/`degraded`, 503 when degraded) and `/api/v1/platform/status` (operators); `Telemetry:CollectorHealthUrl` to include the Collector | for alerting probes, not liveness or readiness (every replica answers the same); see [the platform's status](platform-metrics.md#the-platforms-status) |
| The Overview's platform panel and capacity view | `/api/v1/platform/overview` (operators); `Telemetry:CollectorMetricsUrl` (the Collector's own metrics page, `http://otel-collector:8888/metrics`) for the ingestion figures; `Capacity:CalibrationFile` (`capacity/calibration.json`) | the capacity view shows utilisation only without a calibration for the machine ClickHouse runs on; one is written by `scale/steps.sh` on that machine (see [the panel and the capacity view](platform-metrics.md#the-overviews-platform-panel-and-the-capacity-view)) |
| The platform's own metrics | `Telemetry:Otlp:Endpoint` (the Collector's platform receiver, `https://otel-collector:4319`), `Telemetry:Otlp:TokenFile` (`secrets/platform-otlp-token`), `Telemetry:Otlp:CaFile`; `ClickHouse:MonitorConnectionString` (user `platform_monitor`) | on by default (`Telemetry:Enabled`); stored under the reserved tenant `juice-platform`, never the deployment's; see [platform metrics](platform-metrics.md) |
| The API's ClickHouse queries | `ClickHouse:Api`: `MaxConcurrent` (8 per replica), `QueueTimeout` (5 s), `MaxExecutionSeconds` (20), `MaxThreads` (2) | searches and the Console's other telemetry pages; past `MaxConcurrent` a query waits up to `QueueTimeout`, then the API answers 503 "Busy, try again" with `Retry-After`; so does a query ClickHouse cuts or refuses under load. `MaxConcurrent` × `MaxThreads` (16) is the most of ClickHouse's cores searches take from one replica, so ingest and the workers keep the rest; with more replicas, divide. The workers' queries aren't bounded here |
| Discovery of metrics-only resources | `Discovery:MetricsInterval` (5 min; 0 reads the metric tables every pass) | discovery reads spans and logs every `Discovery:Interval` (30 s), and the metric tables, most of the rows, on their own checkpoint every `MetricsInterval`. A resource seen only through metrics appears, and its last-seen time moves, up to this much later; keep it well under `Discovery:InstanceInactiveAfter` (15 min). At 30 s each metric point was read about 20 times, about half a ClickHouse core at 1× ingest |
| Health liveness reads | `Health:LivenessOverlap` (1 min) | the health worker remembers each resource's last-seen time across passes and reads only from the previous pass's end less this, instead of its whole 5-minute window every 30 s. When ingestion runs late, the read reaches back further by as much as the newest row remembered trails the previous end, so a backlog widens the read instead of losing rows; a runner without memory (a restart or a change of leader) reads the whole window, and so does a pass at least every `Health:LivenessFullRead` (2 min), so one service whose rows are all stored late (long spans: a span is written when it ends) is still seen if it is less late than `FreshnessGrace` less this, less `Lag` and a pass (about 2.5 min). The whole reads raise health's liveness reads from about 3 to about 5 a row (10 before Phase 11) |
| Collector export concurrency | `sending_queue.num_consumers` (16 per exporter) and the ClickHouse driver's pool, `connection_params: max_open_conns` and `max_idle_conns` (32), in `collector/config.yaml` | an exporter sends about min(pool, consumers × the tables an export inserts at once) × batch ÷ the insert's wait: the pool caps the inserts in flight. At the driver's default of 10 the metrics export stopped near 110,000 points/s at the ingest ceiling, with 32 consumers as with 16; at 32 connections it stored 99.6 % at 20× (about 200,000/s), at the new cap. Raise the pool with the consumers. Each exporter has its own pool: up to 96 connections, against ClickHouse's `max_concurrent_queries` (1000 in the image's config; the inserts, searches and workers together stay far below it). The insert's wait: each returns only after ClickHouse's async insert flushes it, about 200 ms (its busy timeout). At 4 the metrics exporter stopped near 85,000 points/s at 10× load and refused the rest; at 16 it took all 100,000/s, the queue at a sixth of its bound. Async insert gathers concurrent inserts into one part, so more consumers don't make more parts |
| Platform database connection | `ConnectionStrings:Platform` (Npgsql) | GSS encryption is turned off unless the string sets `GSS Encryption Mode`: Npgsql's default probes Kerberos through the native GSSAPI library on every new connection, and that probe crashed the API in a burst of new connections. Set it only if the database really uses Kerberos encryption |
| Notification planning at once | `Messaging:Local:MaxConcurrency` (8) | how many alert transitions a replica plans notifications for at once, each with a PostgreSQL connection; a burst (many SLOs added together, an incident opening many alerts) waits its turn. Keep this, `Rules:MaxConcurrency`, `Webhooks:MaxParallel` and the API's requests within the connection pool (Npgsql's `Maximum Pool Size`, 100 by default), and the pools of every replica, plus one connection per leased worker, within PostgreSQL's `max_connections` (100 by default) |
| Restarting the API | the container's restart policy (`restart: unless-stopped` in the compose file) or the service manager's | a crashed API is started again; without one every worker stays down until someone notices |
| Grafana's data | ClickHouse user `grafana_reader` with the row policy `grafana_tenant` (`clickhouse/init-users.sh`, applied by the `clickhouse-access` service, which the Collectors, the API and Grafana wait for; needs `JUICE_PLATFORM_TENANT_ID`) | Grafana sees only this deployment's tenant, even when one ClickHouse holds more than one; except on the trace-ID index (`otel_traces_trace_id_ts`), which Grafana's trace view needs and which has no tenant: from it a Grafana user can list every tenant's trace IDs and times (no span content), so share one ClickHouse between deployments only if that is acceptable; `tests/verify.sh` checks every table with the tenant stamp has the policy |

Every one of these files lives in version control (requirements §6.1). Changes take effect on the next API start, with
an audit record for each. What the API creates lives in the platform database instead, also audited: the [runbook](runbook.md#change-rules-webhooks-or-the-allowlist) says which side owns what.

### An SLO

```json
{
  "Slos": {
    "Definitions": [
      { "Key": "checkout-availability", "Name": "Checkout availability", "Sli": "Availability",
        "Service": "shop/checkout", "Environment": "production", "Target": 99.5 },
      { "Key": "checkout-latency", "Name": "Checkout latency", "Sli": "Latency",
        "Service": "shop/checkout", "Environment": "production", "Target": 99, "ThresholdMs": 300,
        "Operations": [ "/checkout*" ], "EffectiveFrom": "2026-12-01T00:00:00Z" }
    ]
  }
}
```

- **What it measures:** `Sli` is `Availability` (requests that didn't fail) or `Latency` (successful requests at or under `ThresholdMs`,
  one of 1, 2, 5, 10, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 3000, 5000, 7500, 10000, 15000, 30000 or
  60000). Both are technical SLOs. `BusinessSuccess`, `MessageProcessing`, `QueueLatency` and `DataFreshness` are reserved for business
  and other measures and refused for now. The budget window is 30 days, rolling.
- **What counts as a request:** the service's server and consumer spans in that environment (`Operations` narrows them to routes or span
  names, `*` as a wildcard). Each SLO can change these defaults:

  | Request | Default | Setting |
  |---|---|---|
  | Health checks: `/health`, `/healthz`, `/ready`, `/live`, `/metrics` and the paths under them (not `/livestream`) | not counted | `ExcludeHealthChecks`, `ExcludeOperations` |
  | 5xx, 429, a failed span without an HTTP code, a message Juice failed to dispatch | bad | none |
  | Other 4xx (the client's mistake) | good | `ClientErrors`: `Good`, `Bad` or `Exclude` |
  | 408 and 499 (the client gave up) | not counted | `Cancellations`: `Exclude` or `Bad` |
  | A retry | each attempt the service received counts on its own | none |

  `ExcludeOperations` adds patterns to the health checks. To count the health checks as requests, set `ExcludeHealthChecks` to `false`.

- **Versions:** a changed definition becomes the next version from `EffectiveFrom` (when the API starts, by default; never in the past).
  Each minute is counted under the version in effect then, so a new target doesn't rewrite the past. A version still waiting for its date
  is withdrawn by any newer one, such as the file put back or only its date moved (earlier or later): it never applies, and the SLO's
  page lists it as `Withdrawn`. `EffectiveFrom` may carry any offset; it's stored as UTC. A version moving the SLO to another service
  changes nothing until its date. Changing `Sli` needs a new `Key`. A
  key removed from the file is retired, and comes back if it's listed again. Each step is audited (`slo.create`, `slo.version`,
  `slo.retire`, `slo.restore`) and shown on the timeline.
- **Missing telemetry is never success.** Each SLO looks at its own service: minutes after the service's newest telemetry (spans or
  metrics or logs) aren't counted, and neither are minutes it sent nothing in at all, so a service that stops shows even while the rest of the
  tenant carries on. Those minutes stay unknown. A running service exports metrics every minute, so a quiet one isn't taken for a
  stopped one; a service that sends only spans reads as unknown while it has no traffic. An SLO with more
  than `UnknownTolerance` of its minutes unknown reads `Unknown`, and one with fewer than `MinimumEvents` requests `InsufficientData`.
  Neither has a budget or a burn rate.
- **Burn alerts.** Every SLO gets three, judged after each counting pass. They belong to three rules the platform creates with the first
  SLO and keeps itself (`managedBy: platform`): the rules file and API can't change them or use their keys.

  | Rule | Fires when both windows reach | Long | Short | Severity |
  |---|---|---|---|---|
  | `slo-fast-burn` | 14.4× | 1 h | 5 min | Critical |
  | `slo-slow-burn` | 6× | 6 h | 30 min | Warning |
  | `slo-slow-burn-3d` | 1× | 3 days | 6 h | Warning |

  An alert resolves when its short window is under the rate: the burn has stopped, though the long window remembers it. A window
  without a rate (`Unknown` or `InsufficientData`) holds it as it is, neither firing nor resolving. After a move to another service, only
  the new service's minutes count. Its subject is the SLO's service in its environment (`service:shop/orders@production`), so
  endpoints, silences and escalation policies match it as any alert: by rule key (`slo-fast-burn` pages on every SLO's fast burn),
  service, environment or severity. Its evidence names the SLO, version, category and both windows' numbers. An SLO that's retired, or
  moved to another service, resolves its open alerts with that reason. After resolving, a burn alert waits out a cooldown (10 minutes,
  or its short window if longer) before firing again. With `Slos:Enabled` false, the API resolves any open burn alert at startup, since
  nothing would judge it again, unless another replica is counting (holds the `slos` worker lease). The cooldown follows only a burn
  that cooled, not an alert closed for another reason.
- **Rolling back to a build without burn alerts** (before Phase 9 milestone 2): that build can't read the `SloBurn` template, and its
  rule scheduler would fail on every tick; nothing in it would resolve an open burn alert either. Before rolling back:
  1. Set `Slos:Enabled` to `false` on every replica and restart them. With no replica counting, the API resolves the open burn alerts
     at startup (`slo_counting_disabled`), and says so in its log; resolves are notified as usual. While any replica still counts, it
     leaves them, since they're still being judged.
  2. Set the three rules aside:
     `UPDATE rules SET template = 'ErrorRate', enabled = false, definition = jsonb_set(definition, '{Template}', '"ErrorRate"') WHERE managed_by = 'platform';`
  3. Roll back. Upgrading again puts the rules back as they were; their alerts keep their history either way.
- **Upgrading to the resource columns** (1.3.0, Phase 11 R12): the platform reads twelve resource attributes (tenant, service namespace, environment, instance and version, host name and id, process id, container id, pod uid, namespace and name) from columns of their own, `JuiceTenant` and the like, instead of looking each up in `ResourceAttributes` row by row. That cut the workers' ClickHouse CPU by about 72 %, for about 0.07 cores at insert and in merges at 1× ([scale tests](scale-tests.md), Phase 11 fixes §8). The `clickhouse-columns` service adds them to the Collector's seven tables (`clickhouse/resource-columns.sh`) on every start, after the Collector has created its tables; the API and Grafana wait for it. Grafana's row policy `grafana_tenant` reads `JuiceTenant` too. Nothing is rewritten: each column is `MATERIALIZED` from the map, so ClickHouse fills it on insert, and a part written before the upgrade computes it from the map when read, with the same answers. Adding them is a metadata change, quick on any amount of data. On a running stack after an update of the scripts, `scripts/apply-clickhouse-access.sh` applies both. `tests/verify.sh` checks them. **Rolling back:** an older build reads the map and ignores the columns, which can stay; its `init-users.sh` sets Grafana's policy back to the map.

### A webhook endpoint

```json
{
  "Webhooks": {
    "Endpoints": [
      {
        "Key": "ops-pager",
        "Url": "https://hooks.example.com/juice",
        "SecretRef": "env:WEBHOOK_OPS_SECRET",
        "Events": ["alert.firing", "alert.resolved"],
        "RuleKeys": [],
        "MinimumSeverity": "Warning"
      }
    ],
    "Allowlist": [ { "Destination": "10.20.0.0/16", "Ports": [8443] } ]
  }
}
```

- **Allowlist.** Private receivers need an allowlist entry. Loopback, link-local and cloud-metadata addresses are
  never reachable.
- **Signing.** Requests are signed with HMAC-SHA256 in the Standard Webhooks form:
  - headers `webhook-id`, `webhook-timestamp` and `webhook-signature: v1,<base64>`;
  - the signed content is `{id}.{timestamp}.{body}`.
- **Receiving.** Receivers should:
  - verify the signature with a constant-time comparison (reference: `WebhookSigner.Verify`);
  - reject timestamps more than 5 minutes off;
  - deduplicate on `webhook-id`, since delivery is at least once.
- **Action requests.** An endpoint listed in `Actions:NotifyEndpoints` also gets `action.request.created` (waiting for approval),
  `.approved`, `.rejected`, `.cancelled` and `.expired`, signed the same way. They are never silenced or grouped. The body has `type`,
  `occurredAt`, `tenant`, and `request`: `id`, `agent` (`id`, `hostname`), `action`, `parameters`, `risk`, `policy`, `reason`,
  `requestedBy`, `requestedAt`, `expiresAt`, `state`, `decidedBy`, `decidedAt` and `comment`. Slack, Teams and email get it written for
  people, with a link to the Console's Approvals page when `Webhooks:ConsoleUrl` is set.

## Moving to a tenant Id

A deployment installed before the tenant pattern names its tenant (`local-dev`, say). The pattern makes the tenant its Id from juice-tenants,
a GUID that doesn't depend on the name (owner, 24 November 2026). The API refuses a name as `Platform:TenantKey`. To move, once per
deployment:

1. **Back up:** `scripts/backup.sh` and `scripts/backup-telemetry.sh`.
2. **In `.env`:** set `JUICE_PLATFORM_PREVIOUS_TENANT_ID` to the old name, and `JUICE_PLATFORM_TENANT_ID` to the tenant's Id.
3. **Keycloak:** the platform's clients issue `platform_tenant` from `JUICE_PLATFORM_TENANT_ID`, so update the shared Keycloak's realm the
   same way. A token still naming the old tenant gets 403 until then.
4. **Restart:** `docker compose up -d`, every API replica together. A replica still running with the old name finds no tenant once the
   rename lands.
   - The Collector stamps the Id from now on, and Grafana's row policy follows it.
   - The API renames the tenant at start. It is the same row, so every rule, SLO, alert and audit record stays, and the rename is recorded
     as `tenant.rekey`.
5. **Rewrite the stamped telemetry, right after the restart:** `scripts/rekey-tenant.sh <old name> <Id>` rewrites the telemetry stamped
   with the old name, on every table that carries the stamp.
   - Until it has run, the workers (rules, health, discovery) see no history under the Id: rules may fire on missing data, and services
     may look silent. On a large deployment, turn the workers off until it's done.
   - A second run finds nothing to do.
6. **Agents:**
   - An agent that says it holds the old tenant is told the new one on its next poll. The message is signed for that agent by a command key
     it trusts, and the agent checks that signature. An agent adopts a new tenant only from a name, so an Id never moves again.
   - Until an agent has adopted the new tenant, its commands are signed with the old name, and so are the commands of agents too old to
     say which tenant they hold. A command signed before the change still runs after it.
   - Adopting the Id is one-way: an agent older than this version can't read its identity afterwards.
   - **Before removing `JUICE_PLATFORM_PREVIOUS_TENANT_ID`**, every active agent must report the Id. Upgrade the ones that report nothing,
     and wait for any that are offline:
     `SELECT hostname, reported_tenant FROM agents WHERE status = 'Active' AND reported_tenant IS DISTINCT FROM '<Id>';` lists the ones left.
     Then remove it and restart. The API warns at start while any are left; set the old name again (the `tenant.rekey` audit record has
     it) until they catch up.
7. **Backups taken before the move:** these still carry the old name. Restore one, then run `rekey-tenant.sh` again.

## Connecting a Juice service

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://<collector>:4317
OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <token>"
OTEL_EXPORTER_OTLP_CERTIFICATE=<ca.crt>
OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=production,service.namespace=<application>
```

The service registers `AddOtlpExporter()` on tracing, metrics and logging. On logging, `AddJuiceInstrumentation()`
comes first. `deploy/local/scripts/run-sample.sh` shows a complete host. Services, instances, hosts and dependencies
appear without registration within about a minute.

## Production differences

The local profile cuts these corners on purpose. Each one changes for a production install:

| Local | Production |
|---|---|
| self-signed CA, certificates from `init.sh` | the customer's PKI for the Collector, the API and Console, and Grafana if used |
| Keycloak, the API and Console (and Grafana) over HTTP | TLS in front of each |
| Keycloak dev mode, demo users, password grant client | the customer's IdP; no `juice-platform-cli` client |
| secrets in `.env` | the platform's secret store |
| backups on the host disk | scheduled `scripts/backup.sh` and `scripts/backup-telemetry.sh` copied off the host |
| the agent CA and command key as files from `init.sh` | kept in the secret store, the CA key offline where the deployment allows it |
| the platform receiver (4319) published on loopback for `tests/verify.sh` | not published: the API reaches it on the internal network |

The pinned versions, the rules and the webhook configuration stay the same.

## Juice Agents

Agents are off unless `Agents:Enabled` is `true`, with:

| Setting | What |
|---|---|
| `Agents:CaCertificateFile`, `Agents:CaKeyFile` | the agent CA, PEM. It issues agent client certificates and nothing else: don't reuse another CA |
| `Agents:CommandKeyFile` | an ECDSA P-256 key, PEM, that signs commands. Separate from the CA |
| `Agents:ServerCertificateFile`, `Agents:ServerKeyFile` | the agent listener's server certificate, for the name agents use. When an intermediate issued it, put the intermediates after it in the same file: they're sent with it. Agents trust only the CA they're configured with |
| `Agents:Port` | the agent listener, 8443 by default |
| `Agents:CertificateLifetime` | agent certificates, 7 days by default. An agent renews when a third is left |
| `Agents:PackageDirectory`, `Agents:ReleaseKeyFiles` | for `agent.update`: where signed agent packages are, and the release public keys, PEM, that must have signed them. Without a directory there are no updates |
| `Agents:PreviousCaCertificateFiles`, `Agents:PreviousCommandKeyFiles` | for rotation: CAs and command keys the current ones replaced (see below) |
| `Actions:Approval:{Low,Medium,High}` | what each risk needs before a command is signed: `None`, `Admin` or `SecondPerson`. Defaults: `None`, `Admin`, `SecondPerson` |
| `Actions:Overrides:{action}` | the same, for one action over its risk's policy, e.g. `Actions__Overrides__agent.update=SecondPerson` |
| `Actions:ApprovalLifetime` | how long a request waits for a decision, 30 minutes by default |
| `Actions:NotifyEndpoints:{n}` | webhook endpoint keys (from `Webhooks:Endpoints` or the API) told when a request waits for approval and how it ends, whatever alert events they take. None by default: then only the Console's Approvals count tells anyone |
| `Actions:SweepInterval` | how often requests past their deadline are closed as expired, and issued remediations checked for recovery, 30 seconds by default |
| `Actions:StaleEvidence` | a request's alert or target health older than this is stale, and it's neither created nor approved on it (`stale_evidence`), 10 minutes by default |
| `Actions:BundleRetention` | how long diagnostic bundles are kept in PostgreSQL before they're deleted, 14 days by default (each is at most 5 MB) |
| `Actions:VerificationWindow` | how long after a remediation succeeds its target may take to be seen healthy, from telemetry newer than the action, before it's `NotVerified`, 10 minutes by default |

- **The listener** serves only the agent API, and the agent API is on no other port. With agents on, Kestrel listens only on the
  endpoints code names, so the host's own are named too, as Kestrel would have chosen them: `URLS` (or `ASPNETCORE_URLS`), otherwise
  `HTTP_PORTS` and `HTTPS_PORTS`, otherwise 8080. Endpoints in `Kestrel:Endpoints` are left to Kestrel. A customer's own host calls
  `ListenForAgents` and `UseAgentListener` as `Juice.Observability.Host` does.
- **On the local stack** the agent CA and command keys are readable by their owner only, and the platform container runs as that
  user (`JUICE_UID` in `.env`, written by `init.sh`).
- **Behind a proxy or load balancer,** pass this port through as TCP: the client certificate is the agent's identity, and a proxy that
  ends TLS takes it away. Every replica can serve agents.
- **Run an agent as a service** under its launcher:
  - systemd: `deploy/agent/juice-agent.service`, which has the install steps at its top.
  - Windows: `deploy/agent/install-windows-service.ps1` (Administrator). It creates the `JuiceAgent` service with its own virtual account and restart on failure.

  Either way, an agent that can't start exits with a failure code, 78 for invalid configuration, so the service manager sees it.
- **Run an agent** with `Dockerfile.agent` or `dotnet juice-agent.dll`. It reads `Agent:PlatformUrl`, `Agent:PlatformCaFile`,
  `Agent:DataDirectory` (its key and state: keep it private and persistent), `Agent:EnrollmentTokenFile`, `Agent:HostRoot` (`/hostfs`
  in a container with the host mounted) and its allowlist, `Allowlist:Actions` (see `deploy/local/agent.json`). An empty allowlist
  permits nothing.
- **Remediation on a host.** Two built-in actions change the host, each only within what its allowlist lists:

  ```json
  "Allowlist": { "Actions": {
    "service.restart": { "Services": [ "orders.service" ], "RequireApproval": true },
    "files.cleanup": { "Folders": [ { "Path": "/var/log/legacy-app", "Pattern": "*.log", "KeepHours": 72, "Recursive": false } ] }
  } }
  ```

  - `service.restart` restarts one service the host lists, by exact name. On Linux the agent runs `/usr/bin/systemctl` (or `/bin/systemctl`)
    with the unit as its own argument after `--`, never through a shell or `PATH`. The agent's account needs the right to restart that
    unit, and only that: a polkit rule allowing `org.freedesktop.systemd1.manage-units` for that unit and that account. On Windows it uses the Service Control Manager, so the
    agent's account needs start and stop rights on that service. A host without systemd (such as a container) fails the command with
    `systemctl not found`.
  - `files.cleanup` deletes regular files matching `Pattern` and last written more than `KeepHours` ago, in `Path` (and below it with
    `Recursive`). It never follows or deletes a link, never leaves the folder, and stops at 10,000 files per run. A command may ask to
    keep files longer, never shorter. `dryRun: true` only reports what would go.
  - An invalid entry (a root folder, a pattern with a folder in it, no keep time, a service name with anything but letters, digits and
    `@ . _ : -`) stops the agent with exit code 78.
- **Diagnostic bundles.** `diagnostics.bundle` (read-only, Low risk) zips the host summary and disk usage (each only if the host allows
  `host.summary`, and `disk.usage` within its `PathPrefixes`), the agent's version and allowlist, and the last lines (200 by default, at
  most 5,000) of each log the host lists:
  `"diagnostics.bundle": { "LogFiles": [ "/var/log/orders/app.log" ] }`, up to 20 files, each by exact path. A log is read from at most its
  last 512 KB, never through a link. Values that look like credentials (`password=`, `token:`, `api_key=`, `AccountKey=`,
  `SharedAccessKey=`, a `Bearer` token, the password in `scheme://user:password@host`) are replaced with `[redacted]`. That's a safety net, not a guarantee: list only logs you'd hand to an operator. The zip is at most 5 MB; a
  log that doesn't fit is left out and named in the answer. The agent uploads it to the platform, which keeps it for
  `Actions:BundleRetention`. Anyone who sees the agent sees it exists; only operators and admins download it, audited as
  `agent.bundle.download`.
- **Updating agents.** An agent updates itself only when it runs under its launcher, `juice-agent-launcher --home DIR --seed DIR`,
  started by a service manager that restarts it (systemd, a container restart policy). `--seed` installs the published agent it
  names when that version isn't installed yet. The host must also list `agent.update` in its allowlist and trust a release key in
  `Agent:ReleaseKeyFiles`. That key is set on the host, never by the platform. A container started from `Dockerfile.agent` runs the
  agent without the launcher and is updated by its image; `deploy/local` runs the launcher, to try updates.
- **Releasing a package.** Make a release key pair once and keep the private key offline:
  `dotnet run deploy/agent/pack.cs -- --new-key release.key`. For each release, publish the agent and sign it with
  `dotnet run deploy/agent/pack.cs -- --publish DIR --key release.key --out PACKAGES`. Copy the zip and its `.sig` into
  `Agents:PackageDirectory`. The signature is the one `openssl dgst -sha256 -sign` makes, so either tool can sign or check it. The
  platform offers only packages that `Agents:ReleaseKeyFiles` verifies, and whose manifest names the version in the file name.
- **Custom actions.** A customer can add actions of their own to the agent, as code in their own agent build. There are no scripts or commands:
  requirements §8 rules them out.
  - Reference the `Juice.Observability.Agent` package from a console program and derive from `AgentAction<TParameters>`:
    - a name with your own prefix (`acme.queue_depth`);
    - a description;
    - `Changing` if it alters the host (then admins only);
    - a `Timeout`, 10 seconds by default and at most 5 minutes;
    - a parameter record of `string`, `int`, `long` and `bool`, bounded with `[MaxLength]` (required on strings), `[Range]`,
      `[RegularExpression]` and `[AllowedValues]`.

    Start it with `return await JuiceAgent.RunAsync(args, a => a.Add<QueueDepth>());`. The package writes the `manifest.json` the
    launcher needs. `samples/Juice.Observability.Agent.Sample` is a complete example.
  - Ship it like any agent release: publish it, sign it with `pack.cs`, and install it with `agent.update`.
  - List the action in the host's `Allowlist:Actions`. `"Values": { "param": ["a", "b"] }` limits a parameter to those values.
  - An action with an invalid name or schema, or a name used twice or taken from a built-in, stops the agent with exit code 78.
  - Each time it starts, the agent reports its actions and what the host permits (`POST /agent/v1/actions`). The platform:
    - signs a custom action only as the agent described it, and checks its parameters against that description narrowed to the host's
      bounds;
    - issues it under the approval policy for its risk: a changing one is Medium unless it declares more, so it needs an admin.

    The agent checks everything again before running.
- **Rotating the command key.** Generate a new key and set it as `Agents:CommandKeyFile`. Put the old one in
  `Agents:PreviousCommandKeyFiles`. The old key signs an announcement of the new one, and each agent takes it at its next poll; it then
  trusts the new key and the one that announced it, and no older ones. Remove the old key once every agent has polled. An agent offline
  through two rotations has to enroll again.
- **Rotating the agent CA.** Set the new CA as `Agents:CaCertificateFile` and `Agents:CaKeyFile`. Put the old certificate (not its key)
  in `Agents:PreviousCaCertificateFiles`. Certificates the old CA issued still get in, and each agent gets one from the new CA at its
  next renewal. Remove the old certificate after one `Agents:CertificateLifetime`. The listener's server certificate is separate:
  agents trust it through their own `Agent:PlatformCaFile`.

## Sizing and scaling

What to give each part of the platform, and what to do when the Overview's platform panel turns amber or red (Phase 11 R18).
The figures are measured on the reference host (24 cores, a Docker VM with 24 vCPU and 31 GiB, everything on one machine) with
the reference mix: 10 services of 10 instances, 50 rules, 50 SLOs and 2 searches a second. 1× is §9's reference load: 1,000
spans, 10,000 metric points and 1,000 logs a second. Raw data and method: [scale tests](scale-tests.md), the rerun of 17 November
with the Collector's ClickHouse pool at 32 connections, on a ClickHouse already holding 36 GB (2 billion rows) when it began.

### What one machine carries

| Load | ClickHouse CPU, mean (max) | ClickHouse memory, max | Collector CPU, mean | API CPU, mean | PostgreSQL CPU, mean | Disk at full retention |
|---|---|---|---|---|---|---|
| 1× | 1.6 cores (11.2) | 5.3 GiB | 0.17 | 0.14 | 0.07 | about 1 TB |
| 3× | 1.6 (7.2) | 5.7 GiB | 0.41 | 0.13 | 0.06 | about 3 TB |
| 5× | 3.8 (18.4) | 6.7 GiB | 0.77 | 0.16 | 0.07 | about 5 TB |
| 6× | 4.4 (20.4) | 8.9 GiB | 0.98 | 0.17 | 0.08 | about 6 TB |
| 7× | 6.0 (23.6) | 7.8 GiB | 1.56 | 0.24 | 0.14 | about 7 TB |
| 10× | 7.9 (24, all) | 10.6 GiB | 2.55 | 0.27 | 0.15 | about 10 TB |

- **Every §9 target holds through 6×** (17 November, with the pool at 32). From 7× ClickHouse's bursts cross "nothing lost": for 20
  seconds to a minute it takes all of this host's 24 cores, inserts wait, and the Collector's queue fills past its bound (7×: one
  burst, 0.7 % of points; 8×: 1.1 %; 10×: 2.6 %, in six minutes of bursts). Spans, logs, the workers, freshness and the API stay in line.
  Before the pool was raised, 6× crossed with one burst too (16 November, 0.012 % of points) and 10× lost 5.8 % steadily.
- **Plan at no more than 5× the reference on this host** (owner, 17 November): one step under the last that held, because the limit
  is the bursts' headroom, and a 30-minute step meets only a few of them. Measure your own (below). The Overview's capacity view shows
  what held (6× here) and today's load as a share of it: a measurement, not this planning figure. Its lines are the steps'
  means, set against one 15-second sample, so an ordinary burst can turn ClickHouse's CPU or inserts amber or red for a sample at any
  load (at 1× samples reached 5.1 cores, past 6×'s mean of 4.1): read a colour that stays, not one sample.
- **ClickHouse's CPU comes in bursts:** its mean grows by about 0.7 cores per 1×, but merges and searches take it to 7–24 cores for
  seconds at a time, all 24 from 7× (`docker stats` read up to 28.2, more than the host has: sampling noise). Size the node for the bursts, not the mean: more cores is what moves the knee.
- **Disk:** about 1 TB per 1× at the reference retention (traces and logs 30 days, metrics 90): 17.5 GB a day, measured after
  ClickHouse's merges; metric sums at 90 days are 71 % of it ([drills](drills.md#the-disk-the-real-retention-needs)). A day's
  partition goes within about 4 hours of passing its TTL, so the disk holds the retention plus about a day. The panel's disk figure
  measures your own a day.

### What scales out, and what doesn't

- **More API replicas** carry more of the Console and the API: searches (each replica runs at most `ClickHouse:Api:MaxConcurrent`
  at once), reads and the webhook and outbox delivery, which every replica runs, claiming each delivery so none is sent twice.
- **Six workers run in one replica at a time:** discovery, health, rules, SLOs, notification planning and agent action requests.
  The replica that holds a worker's PostgreSQL advisory lock runs it; the others skip their passes. When it stops or loses its
  database connection, another takes over within a pass (30 s for discovery, health, SLOs and action requests; seconds for rules and
  notifications); the log says `This replica now runs {worker}`. A worker doesn't get faster with more replicas: its levers are its
  own settings.
- **Workers on their own replicas:** to keep the workers off the API replicas, turn them off there, and on in one or two worker
  replicas (a second is a standby):

  | Worker | Switch | Also turns off |
  |---|---|---|
  | Discovery | `Discovery:Enabled` | – |
  | Health | `Health:Enabled` | – |
  | Rules | `Rules:Enabled` | – |
  | SLOs | `Slos:Enabled` | – |
  | Notification planning | `Webhooks:Enabled` | webhook delivery in that replica |
  | Agent action requests | `Agents:Enabled` | the agent listener in that replica |

- **ClickHouse is one node, and scales up:** more cores for the bursts, more memory for the merges. A second node (a replica for
  searches, or shards) is new infrastructure: the roadmap's trigger table applies, with an ADR first.
- **The Collector is one instance.** Its metrics export was the platform's limit on this host in 1.3.0: about 95,000 points a
  second (9.5× the reference) with the full mix, where the inserts in flight saturated near 10. The cap was the ClickHouse driver's connection pool
  (10 connections by default), not the consumers: with `connection_params: max_open_conns: "32"` (shipped after 1.3.0) the ingest
  ceiling at 20× stored 99.6 % of its points (about 200,000 a second, against 55 % before). Its queue holds 200,000 items per signal;
  beyond, data is dropped and counted.
- **PostgreSQL** is far from any line: about 0.1 core and 130 commands a second at every step to 10×.

### Each worker's levers

| Worker | Levers | What they trade |
|---|---|---|
| Rules | `Rules:MaxConcurrency`, `Rules:EvaluationTimeout`; the number of rules | more at once, more ClickHouse at once |
| SLOs | `Slos:Interval`, `Slos:MaxMinutesPerPass`; the number of SLOs (at most 200) | a longer interval, a later burn alert |
| Discovery | `Discovery:Interval`, `Discovery:MetricsInterval` (5 min) | a resource seen only in metrics found later |
| Health | `Health:Interval`, `Health:LivenessOverlap`, `Health:LivenessFullRead` | fresher health against more reads |
| Searches | `ClickHouse:Api:MaxConcurrent`, `MaxExecutionSeconds`, `MaxThreads` (per replica) | more searches at once, less ClickHouse for the workers and inserts |

### When the panel turns amber or red

| Figure | What it means | What to do |
|---|---|---|
| A worker's "busy" | its passes take most of its interval | its levers above; a replica of its own; fewer rules or SLOs |
| A worker stale, stuck, unheld or gone | its replica stopped, or its passes do | the runbook's worker entries; `/api/v1/platform/status` names the replica |
| A backlog behind | delivery or discovery is late | webhooks: the receiving end, or more replicas; discovery: its levers, ClickHouse's CPU |
| Refused or dropped | the Collector's limit is reached: refused is what the receivers turned back (the memory limiter, or an export queue full), dropped is what an exporter's sends lost for good | less load per Collector; the ClickHouse driver's pool (`max_open_conns`, which caps the inserts in flight) with the export's consumers, and the batch size (measure first); ClickHouse's insert p95 |
| An export queue filling | inserts are slower than the load | ClickHouse's CPU and insert p95; the queue absorbs bursts, and past its bound the receivers refuse what it can't take (the panel's refused; the platform's own telemetry, its dropped) |
| ClickHouse CPU | the node is busy | a larger node; fewer searches at once (`ClickHouse:Api:MaxConcurrent`); the reads by caller say which worker or page costs most |
| Insert p95 | inserts wait on a busy ClickHouse | as ClickHouse CPU; amber at 200 ms, where the rerun's 6× burst was |
| A search's read p95 | searches are slow | narrower time ranges; `ClickHouse:Api` bounds; ClickHouse's CPU |
| Disk: days until full | it fills before retention lets old data go | a larger disk, or shorter retention: `ALTER TABLE otel.<table> MODIFY TTL …` on each table (`JUICE_*_TTL` in `.env` applies only when the tables are created; see `deploy/local/README.md`, Retention) |

### Measuring your own

The capacity view is calibrated on the reference host. On another machine it shows utilisation only, and says so. To calibrate
it on yours, run the scale steps there (`deploy/local/scale/steps.sh`, on a stack that isn't serving, with `HELD_OUT` steps to check
it), and copy the calibration they write into `capacity/calibration.json`: the view then sets each figure beside what that machine
measured where every line held, and where one crossed.
