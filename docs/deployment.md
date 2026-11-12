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

## More than one API replica

Replicas can run side by side behind a load balancer. Discovery, health and rules each run in one replica at a time:
the replica that holds a PostgreSQL advisory lock for that worker runs it, and the others skip their passes. When it
stops or loses its database connection, another replica takes over within one pass (30 s for discovery and health,
5 s for rules); the log says `This replica now runs {worker}`. Webhook delivery runs in every replica and
claims each delivery, so none is sent twice. To keep workers off the API replicas, set `Discovery:Enabled`,
`Health:Enabled`, `Rules:Enabled` and `Webhooks:Enabled` to `false` there.
