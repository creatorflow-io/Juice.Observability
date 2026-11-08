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
| Rules | `rules.json` (`Rules:File`), or the API and Console | validated at start; an invalid rule stops the API; every change audited |
| Channels and allowlist | `webhooks.json` (`Webhooks:File`), or the API and Console | secrets as `env:NAME` or `file:/path`, never in the file |
| Secrets for channels set through the API | environment variables named `Webhooks:ApiSecretEnvPrefix` (default `JUICE_WEBHOOK_`) + a name, or files directly in `Webhooks:ApiSecretDirectory` | the API refuses any other reference, so an admin can't name other secrets on the host |
| Email channels | `Webhooks:Smtp`: `Host`, `Port` (587), `From`, `UseTls` (true: STARTTLS), `UsernameRef`, `PasswordRef` | without it, email channels are refused; implicit TLS on 465 isn't supported |
| Slack and Teams | The channel's URL behind a secret reference | they post only to `hooks.slack.com`, `*.webhook.office.com`, `*.logic.azure.com` and `*.api.powerplatform.com`, plus any in `Webhooks:AdditionalChatHosts` |
| Links in notifications | `Webhooks:ConsoleUrl` | optional; unset means no links |
| Health policy | `Health` section (environment `Health__…`) | grace, thresholds, critical dependencies, service weights |
| Users and roles | the IdP | Console and API: client `juice-console` (public, PKCE) issuing tokens for `juice-platform` with roles `admin`, `operator`, `viewer` and the `platform_tenant` claim; Grafana (optional): client `grafana` roles `admin`, `editor`, `viewer` |
| Console sign-in | `wwwroot/config.json` in the API image | `{"apiBase": "", "auth": {"issuer": "<IdP>", "clientId": "juice-console", "scope": "openid profile email"}}`; mount your own file over it. `auth.requireHttps` (default `"remoteOnly"`: plain HTTP only for localhost) is `false` only for a local stack |
| Grafana links | `Integrations:Grafana:BaseUrl` | optional; unset means no links |

Every one of these files lives in version control (requirements §6.1). Changes take effect on the next API start, with
an audit record for each. What the API creates lives in the platform database instead, also audited: the [runbook](runbook.md#change-rules-webhooks-or-the-allowlist) says which side owns what.

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
| the agent CA and command key as files from `init.sh` | kept in the secret store, the CA key offline where the deployment allows it |

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
- **Rotating the command key.** Generate a new key and set it as `Agents:CommandKeyFile`. Put the old one in
  `Agents:PreviousCommandKeyFiles`. The old key signs an announcement of the new one, and each agent takes it at its next poll; it then
  trusts the new key and the one that announced it, and no older ones. Remove the old key once every agent has polled. An agent offline
  through two rotations has to enroll again.
- **Rotating the agent CA.** Set the new CA as `Agents:CaCertificateFile` and `Agents:CaKeyFile`. Put the old certificate (not its key)
  in `Agents:PreviousCaCertificateFiles`. Certificates the old CA issued still get in, and each agent gets one from the new CA at its
  next renewal. Remove the old certificate after one `Agents:CertificateLifetime`. The listener's server certificate is separate:
  agents trust it through their own `Agent:PlatformCaFile`.

## More than one API replica

Replicas can run side by side behind a load balancer. Discovery, health and rules each run in one replica at a time:
the replica that holds a PostgreSQL advisory lock for that worker runs it, and the others skip their passes. When it
stops or loses its database connection, another replica takes over within one pass (30 s for discovery and health,
5 s for rules); the log says `This replica now runs {worker}`. Webhook delivery runs in every replica and
claims each delivery, so none is sent twice. To keep workers off the API replicas, set `Discovery:Enabled`,
`Health:Enabled`, `Rules:Enabled` and `Webhooks:Enabled` to `false` there.
