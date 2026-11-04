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

`init.sh` asks for the tenant id, or takes it from `JUICE_PLATFORM_TENANT_ID`. Set `PUBLIC_HOST` before it runs, or run
`scripts/init.sh --reissue-cert` after changing it, so the Collector certificate names the address hosts send to.
`tests/verify.sh` sends through the test Collector, so its records stay out of the deployment's tenant.

The API applies database migrations on start (`Database:Migrate`). It creates the tenant from
`JUICE_PLATFORM_TENANT_ID`, and refuses to start against a database that belongs to another tenant.

## Configuration

| What | Where | Notes |
|---|---|---|
| Tenant | `.env` `JUICE_PLATFORM_TENANT_ID` | stamped by the Collector, provisioned by the API; never changes after install |
| OTLP clients | `secrets/otlp-tokens`, one token per line | restart the Collector after a change (runbook) |
| Rules | `rules.json` (`Rules:File`) | validated at start; an invalid rule stops the API; every change audited |
| Webhook endpoints and allowlist | `webhooks.json` (`Webhooks:File`) | secrets as `env:NAME` or `file:/path`, never in the file |
| Health policy | `Health` section (environment `Health__…`) | grace, thresholds, critical dependencies, service weights |
| Users and roles | the IdP | Console and API: client `juice-console` (public, PKCE) issuing tokens for `juice-platform` with roles `admin`, `operator`, `viewer` and the `platform_tenant` claim; Grafana (optional): client `grafana` roles `admin`, `editor`, `viewer` |
| Console sign-in | `wwwroot/config.json` in the API image | `{"apiBase": "", "auth": {"issuer": "<IdP>", "clientId": "juice-console", "scope": "openid profile email"}}`; mount your own file over it. `auth.requireHttps` (default `"remoteOnly"`: plain HTTP only for localhost) is `false` only for a local stack |
| Grafana links | `Integrations:Grafana:BaseUrl` | optional; unset means no links |

Every one of these files lives in version control (requirements §6.1). Changes take effect on the next API start, with
an audit record for each.

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

The pinned versions, the rules and the webhook configuration stay the same.

## More than one API replica

Replicas can run side by side behind a load balancer. Discovery, health and rules each run in one replica at a time:
the replica that holds a PostgreSQL advisory lock for that worker runs it, and the others skip their passes. When it
stops or loses its database connection, another replica takes over within one pass (30 s for discovery and health,
5 s for rules); the log says `This replica now runs {worker}`. Webhook delivery runs in every replica and
claims each delivery, so none is sent twice. To keep workers off the API replicas, set `Discovery:Enabled`,
`Health:Enabled`, `Rules:Enabled` and `Webhooks:Enabled` to `false` there.
