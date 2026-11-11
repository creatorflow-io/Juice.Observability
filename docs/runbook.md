# Runbook

Commands run from `deploy/local`. API calls need a bearer token (see the root README).

## From an alert to its cause

1. `GET /api/v1/tenants/{t}/alerts?state=firing` lists what is firing; the newest come first.
2. `GET /alerts/{id}` gives:
   - **`evidence`:** template, window, value, threshold and samples, enough to repeat the query.
   - **`history`:** Pending, Firing and Resolved, with reasons.
   - **`subjectHealth`:** the service's current state and reason codes, such as `partial_instance_failure` or
     `critical_dependency_failed:shop/billing`. `dependency_failed:shop/billing` names a failing service this one was
     seen calling in the last 5 minutes. It points at the likely cause but does not change the state; to make it
     change the state, list the pair under `Health:CriticalDependencies`.
   - **`links`:** optional integration links. With `Integrations:Grafana:BaseUrl` set, one opens the Grafana Juice
     overview for that service over the last hour; without Grafana the list is empty.
3. `GET /resources/{subjectId}` shows the service's instances, each with its own state. One unhealthy instance among
   healthy ones points at that process or host, not the service.
4. `GET /topology?environment=production` shows what the service calls and uses, and each node's health. A failing
   dependency is the usual cause of a `Critical` caller.
5. `POST /alerts/{id}/acknowledgement` with `{"comment": "..."}` needs operator or admin. Send an `Idempotency-Key` so
   a retry does not acknowledge twice.

`noData: true` on an alert means the last evaluation had too few samples: the alert keeps its state. Silence is
never read as recovery. `stale: true` on health means evaluation has stopped (next section).

## From an audit record to its trace

A Juice.Audit access log (`Req_TraceId`) or data change (`TraceId`) holds the trace id of the request that wrote
it; the `X-Trace-Id` response header carries the same id. Open `{console}/traces/{traceId}` to see that request's
spans and logs, or paste the id into the Console's search box. Records written before Juice.Audit 10.4 hold the whole
W3C `traceparent` (`00-<trace id>-<span id>-01`); the Console and the API accept that form too. A record whose id is
neither form was written without an active trace, so the platform has no telemetry for it.

The audit records themselves stay in Juice.Audit's database: before and after values are business data and never
enter telemetry.

## Health is stale or rules stop

- **Symptom:** `stale: true` on health. Check the API log for `Health evaluation failed` or `Rule ... evaluation
  failed`, and the metrics `juice.observability.health.evaluations{outcome=failure}` and
  `juice.observability.rules.evaluations{outcome}`.
- **ClickHouse unreachable.** Every pass fails and states are kept, never turned Offline or resolved. Once
  ClickHouse is back, the next pass (≤ 30 s) catches up.
- **`juice.observability.rules.lag` above a few seconds.** Rules wait for a worker longer than they should. Every rule due on a tick
  starts then and waits for one of `Rules:MaxConcurrency` workers, so `juice.observability.rules.queued` (an evaluation that waited)
  rises steadily in a healthy deployment with more rules than workers: it is the lag that matters. Raise `Rules:MaxConcurrency`, or look
  at `juice.observability.rules.duration` to find the slow template.
- **`juice.observability.rules.skipped` rising.** Due evaluations that didn't run: `overrun`, a rule still running (or waiting) when due
  again, so it is slower than its schedule; `released` and `disabled`, a rule that gave up waiting because another replica took the lease
  or the rule was turned off meanwhile (occasional, around a failover or a change).

## Telemetry missing

1. Start the test Collector (`docker compose --profile test up -d --wait otel-collector-test`) and run `tests/verify.sh`: it
   checks authentication, the tenant stamp and storage end to end through that Collector, which has the main one's
   configuration, and checks that the main one answers with a trusted certificate.
2. Check the Collector's metrics at `http://127.0.0.1:8888/metrics`:
   - `otelcol_receiver_refused_*` means clients are rejected (token or TLS);
   - `otelcol_exporter_enqueue_failed_*` means the queue was full and data was dropped;
   - `otelcol_exporter_queue_size` means data is waiting for ClickHouse.
3. A client getting 401 has the wrong token; a TLS error means it lacks `OTEL_EXPORTER_OTLP_CERTIFICATE` or the CA.

## Rotate an OTLP token

1. Add the new token on a new line of `secrets/otlp-tokens`, then run `docker compose restart otel-collector`.
2. Switch clients to the new token.
3. Remove the old line and restart the Collector again.

Token changes need the restart; this was tested.

## Change rules, webhooks or the allowlist

Each rule, endpoint and allowlist entry belongs to one side:

| It's | Change it | Notes |
|---|---|---|
| Listed in `rules.json` or `webhooks.json` | In the file, then restart the API | The API refuses with `409`. If the API created the same key, the file takes it over |
| Created through the API or the Console | Through the API or the Console, as an admin | A restart leaves it alone |
| Removed from the file | Through the API or the Console | A removed rule or endpoint is disabled and handed over; a removed allowlist entry is deleted |

To change a file:

1. Edit `rules.json` or `webhooks.json` and commit it.
2. Restart the API: `docker compose --profile platform restart platform-api`.
3. Check `audit_records` for `rule.create|update|disable`, `webhook.*` and `webhook.allowlist.*`.

An invalid file keeps the API from starting, and the log names every error.

**Disabling a rule, or changing its template or scope, resolves its open alerts** with the reason `rule_disabled` or `rule_changed`, and sends those notifications. Removing a rule from the file does too, so check a file before deploying it: an emptied one resolves every alert its rules had open.

## Webhook deliveries

- **An alert fired and nothing was planned.** An alert's change reaches notifications through the Juice outbox. Every
  transition that notifies has a row in `outbox_events`, and `alert_transitions.notifications_planned_at` is set once
  its deliveries are planned. Waiting rows in `outbox_deliveries` have `state` 0, and ones that will retry have 3 with
  `next_attempt_on`. Retries go on for about 85 minutes; the last error is in `last_error`. A row still 3 with no
  `next_attempt_on` has given up: fix the cause, then set it back to `state = 0`. Planning twice is harmless, since a
  transition already planned is skipped.
- **History.** `GET /webhooks` lists the endpoints. `GET /webhooks/{id}/deliveries?status=failed` shows each
  delivery with every attempt: status code or error, and duration.
- **Retries.** Network errors, timeouts, 408, 429 and 5xx retry with backoff, from 10 s up to 10 minutes, for 8
  attempts. Any other 4xx fails at once.
- **Blocked destinations** show as `blocked_address:…`, `private_address_not_allowlisted:…` or `https_required`, and
  are never retried.
- **Alert state.** A failed delivery never changes alert state. To resend, fix the receiver, then press Retry beside the
  delivery in the Console's channel history, or `POST /webhooks/{id}/deliveries/{deliveryId}/retry` (admin, audited). It
  gets one more attempt. Receivers deduplicate on `webhook-id`.
- **Check a channel** before relying on it: the Test button, or `POST /webhooks/{id}/test` (admin, audited). It sends a
  `webhook.test` notification now through the same checks and signing as an alert, and answers with the status or the error.
  It works on a disabled channel too.
- **`escalation.step.unreachable`** in the audit: an escalation step reached nobody, because all its channels are disabled or gone.
  The next step went at once; if it was the last, the escalation stopped as `unreachable`. Enable or replace the channels.
- **A host on an older version** logs nothing wrong itself. The newer host logs `took … over from an older version`: upgrade or
  stop the older one.
- **One alert.** `GET /alerts/{id}/notifications` and the alert page show every delivery, escalation, silence and incident for it.
- **Other statuses.** `Suppressed` is held by a silence, `Dropped` was held and no longer mattered, and `Grouped` went out inside the delivery named by `groupedInto`.
- **Other errors.** `chat_url_not_allowed`: the URL behind a Slack or Teams reference isn't that service's, and isn't in `Webhooks:AdditionalChatHosts`. `secret_unavailable`: the reference isn't set on the host; it retries. `smtp_not_configured`: no `Webhooks:Smtp`. `smtp_5xx`: the mail server refused for good. `smtp_4xx` and `smtp_unavailable` retry.

## Silences and maintenance windows

Use the Silences page, or `Silence this` on an alert. Operators and admins can do this.

- **Alerts carry on.** A silence holds notifications and escalation only: alerts still fire, resolve and appear in the Console.
- **When it ends,** what it held goes out if it still matters: a firing alert still firing and not yet acknowledged, an escalation step its policy would still send, or a resolve for a receiver told it was firing. The rest is dropped.
- **Every silence** needs a reason, a scope and an end, at most 7 days away. To hold everything, say so with `allAlerts`. A silence with a maximum severity lets more serious alerts through.
- **A maintenance window** can start up to 90 days ahead and repeat weekly, for at most 72 hours each time, until it's ended.
- **Where did the alerts go?** The Silences page lists what's active and how much each holds. `End now` releases on the next pass, about 2 seconds later.

## Escalation

A policy tells named channels as an alert stays open, by minutes after it fired, and starts when a matching alert fires. It's on the Notifications page, for admins.

- **It stops** when the alert resolves, when it's acknowledged if the policy says so, or when the policy is disabled. The alert page shows why.
- **A silence holds it.** The step stays due, and goes out when the silence ends if the alert is still open.
- **Late steps keep their spacing.** If a step goes out late, after a silence or while no replica was running, the next one waits at least the gap the policy puts between them. With steps at 5 and 15 minutes after an hour's silence, the first goes when it ends and the second 10 minutes later, not both at once.
- **Check the channels.** A step whose channels are all disabled counts as sent, with only a warning in the log.

## Incidents

Use `Open an incident` on an alert, or the Incidents page. Operators and admins can do this.

- **States:** Open, Investigating, Mitigated, Resolved. Resolving and reopening need a comment.
- **They don't touch alerts.** Resolving an incident leaves its alerts as they are, and the page says if some are still open.
- **The record** is on the page, and `audit_records` holds `incident.*`. Notes are masked for common secret shapes, but write a summary rather than paste a terminal.

## SLOs

- **Where they come from:** the file named by `Slos:File`. Edit it, restart the API, and a changed SLO becomes a new version. The SLO's
  page lists its versions, and the audit records them as `slo.*`.
- **States:** `Met`, `AtRisk` (over 75% of the budget used), `Exhausted`, `InsufficientData` (too few requests in 30 days) and `Unknown`
  (more than 10% of the minutes weren't counted). The last two show no budget.
- **An SLO reads Unknown:**
  - **Just added:** its 30 days are being counted, `Slos:MaxMinutesPerPass` minutes a pass; it settles within the hour at the default.
    If the log says `out of time: next pass counts N minutes`, the stretch was too big for `Slos:QueryTimeout`: passes narrow until they
    fit, so it catches up more slowly. Raise the timeout if it never does.
  - **Telemetry stopped:** minutes after the service's newest telemetry (its spans or metrics) aren't counted. See [Telemetry missing](#telemetry-missing). Once it flows
    again, the minutes still in ClickHouse are counted. Minutes the tenant sent nothing in at all stay a gap: they don't read as quiet.
    The SLO reads `Unknown` until the gap is less than 10% of the window, so a long outage shows on the 30 days for a while.
  - **Counting failed:** the API's log says `SLO …: counting failed` with the reason; `juice.observability.slos.counted` counts passes
    by outcome.
- **Counting falls behind:** `juice.observability.slos.duration` is how long a pass over every SLO takes, and
  `juice.observability.slos.burn_lag` how late burn alerts are judged after their windows move on (each minute). Burn lag past a minute
  means passes take longer than the minute they have: fewer SLOs, a shorter `Slos:QueryTimeout`, or a faster ClickHouse.
- **What was left out:** an SLO's page lists, by day, the health checks, given-up requests and client errors it didn't count.
- **A burn alert fires** (`slo-fast-burn`, `slo-slow-burn`, `slo-slow-burn-3d`): both its windows spend the budget at least that many
  times faster than 30 days allow. Its evidence names the SLO and both windows' totals, bad requests and rates; the SLO's page shows
  which days and which requests. A fast burn is an outage now. A slow burn is a leak that will spend the budget early if nothing changes.
- **A burn alert stays firing though things look fine:** it resolves once its short window is under its rate, so up to 5 minutes after
  a fast burn ends, 30 minutes for the 6-hour burn and 6 hours for the 3-day one. If its alert says `held: a window has no rate`, a
  window is `Unknown` or has too few requests, and missing data never resolves an alert: see why the SLO reads `Unknown` above.
- **A burn alert fires again soon after resolving:** the long window still remembers the burn, so a new spike in the short window is
  enough to fire it again. That's a burn that hasn't really stopped.
- **After an SLO moves to another service** (a new version naming another `Service`), its windows count only the new service's
  minutes, so they read `Unknown` and raise nothing until the new service has filled them: an hour for the fast burn, three days for the
  slowest.
- **Silencing them:** by rule key for one kind across every SLO, or by service for every alert on that service, rules' and SLOs'
  alike. The burn rules themselves can't be changed or disabled; change the SLO, or retire it, which resolves its alerts.

## Juice Agents

Everything below can be done on the Console's **Agents** page, with the same roles. The list shows each agent's state, version and
certificate expiry. An agent's page runs diagnostics, custom actions and updates, shows each command's answer, and revokes. The API calls
are given for scripts.

**What an agent can be asked** comes from the agent itself. Each time it starts, it reports its actions, custom ones included, and what
its host's allowlist permits (`GET …/agents/{id}/actions`). The page:
- greys out what the host doesn't permit;
- fills lists with the allowed values;
- shows the host's other limits under the form.

An agent older than the reports shows the built-in actions with a note. A custom action is refused with `400` until the agent has reported
it. The agent's log says `The platform refused this agent's action report` when its build's custom actions don't hold: the reason follows.
`agent.actions.report` in `audit_records` records each change in what an agent reports.

- **Approvals.** Every action has a risk, and the platform's `Actions:Approval` says what each risk needs (see the deployment guide).
  - **Needing no one else:** the action runs when asked.
  - **Needing approval:** it becomes a request, with a reason, and waits on the **Approvals** page for 30 minutes. Admins approve or reject
    it there. A rejection needs a reason, and a request needing a second person can't be approved by whoever asked.
  - **Once approved,** the command is signed and the agent runs it at its next poll.
  - **The same request again** while one is open is refused as a duplicate. Wait for it, or cancel it from the agent's page.
  - **A host's allowlist** can add `"RequireApproval": true` to an action. The host then refuses any command for it that didn't come from an
    approved request (`approval_required`).
  - **Finding out:** the **Approvals** menu entry shows how many requests wait for you (an admin's own second-person requests don't count;
    operators see none). The endpoints in `Actions:NotifyEndpoints` get a message when a request waits, and when it's approved, rejected,
    cancelled or expires. A webhook delivery of a request has `actionRequestId` set instead of `alertId`, and shows in that channel's
    deliveries like any other.
  - **Expiry:** a request nobody decided is closed as `Expired` within `Actions:SweepInterval` (30 seconds) of its deadline, recorded as
    `action.request.expire` by `platform`.
  - **Audit:** `action.request.*` records who asked, who decided, and every refusal.
- **Remediation.** `service.restart` (High risk) and `files.cleanup` (Medium; a dry run is Low) act only on what the host's allowlist lists.
  - **Aim it.** A request can name the alert that prompted it (`alertId`) or a health target (`target`; the Console offers the agent's
    own host). It's refused, when asked and again when approved, if the alert isn't firing or the target is healthy
    (`precondition_failed`), or their evidence is older than `Actions:StaleEvidence` (`stale_evidence`). A request whose target recovered
    before approval is closed as `Cancelled` by `platform`.
  - **Recovery.** Once it ran, an aimed request reads `Verifying` until health from telemetry newer than the action says the target is
    healthy (`Verified`). If the action fails, or the target isn't seen healthy within `Actions:VerificationWindow`, it's `NotVerified`
    with the reason. The agent reporting success is never taken as recovery. `action.request.verify` records each outcome, and the
    notify endpoints get `action.request.verified` or `.not_verified`.
  - **A diagnostic bundle** runs on request from the agent's page and answers with its size, what's in it, how many values were redacted,
    and which listed logs were left out and why (missing, a link, unreadable, no room). **Download** saves the zip (operators and admins).
    It's deleted after `Actions:BundleRetention` (14 days); a download after that says it's gone. `agent.bundle.upload` and
    `agent.bundle.download` are audited.
  - **A restart that fails** says why in the command's answer: `systemctl not found` (no systemd here), `systemctl restart failed (…)`
    (the unit, or the agent's rights), or a timeout after 90 seconds.
- **Enroll a host.** An admin creates a token with `POST …/agents/enrollment-tokens`. It is valid for an hour by default, works once,
  and is shown once. Put it in the agent's `Agent:EnrollmentTokenFile` and start the agent. It appears in `GET …/agents`, linked to
  the discovered host of the same name.
- **Run a diagnostic.** `POST …/agents/{id}/commands` with `host.summary`, `disk.usage` (`pathPrefix`) or `tcp.reachability`
  (`host`, `port`, `timeoutMs`), as an operator or admin. The command shows `Pending`, `Delivered`, then `Succeeded` with its data,
  `Failed`, or `Rejected` with the agent's reason. One not answered before it expires, 2 minutes by default, shows `Expired`.
- **`Rejected`** comes from the agent. `action_not_allowed` and `outside_local_bounds` mean this host's allowlist doesn't permit it,
  and only the host's owner can change that. `invalid_signature`, `unknown_key`, `wrong_target`, `expired` and `replayed` mean the
  command was altered, sent to the wrong agent, or the clocks are more than 2 minutes apart.
- **`Failed` with `interrupted`** means the agent stopped while running it: it isn't run again, so issue it again if it's still needed.
- **`Failed` with `timeout`, then `busy`,** on `disk.usage` or `host.summary`: a read on the host hung, often a network mount that stopped
  answering. The agent keeps working; those two fail `busy` until the read returns or the agent restarts.
- **Enrollment answers `429`:** more than 10 enrollments from that address failed in the last minute, usually a wrong or used token. Wait a
  minute, and check the token.
- **An agent offline.** `online` is false when it hasn't polled for 2 minutes. Its log says why: the platform is unreachable, or
  `The platform refused this agent`, which means it was revoked or its certificate expired while it was down. To bring it back, use
  **Bring this host back** on its page (admin): it creates a token for the host. Replace the token in the agent's token file with it.
  - The agent notices within seconds and moves its old identity to `revoked/{agent}-{time}` in its data directory.
  - It enrolls again with a new key, as a new agent. The revoked page then links to it.
  - The token it last used changes nothing, so a token left in the file can't bring a revoked agent back.
  - An agent from before 9 November doesn't do this. For one of those, delete `agent.pem` (or `agent.key` and `agent.crt`),
    `identity.json`, `keys.json` and `commands.json` from its data directory, then restart it with the new token. Keep the rest of the
    directory: the launcher's home may be in it.
- **Update an agent.** `GET …/agents/packages` lists the versions offered. As an admin, `POST …/agents/{id}/commands` with
  `agent.update` and `{ "version": "…" }`; the platform adds the package's hash. The command stays `Delivered` while the new version is
  on trial, up to about 2 minutes. It ends `Succeeded` (`{ from, to }`) once the new version has reached the platform, and the agent's
  `version` changes. It ends `Failed` with `rolled_back: …` when the new version exited or stayed silent, and the version before runs
  again. Other `Failed` reasons are refusals before anything was replaced:
  - `updates_unavailable`: no launcher, as in a plain container.
  - `no_release_key`: the host trusts no release key.
  - `package_mismatch`: the bytes aren't the ones the command names.
  - `invalid_package_signature`: no release key this host trusts signed it.
  - `invalid_package`: the zip, its manifest or its paths don't hold.
  - `package_not_found`.
- **After a rollback,** the launcher's log in the agent's output says why (`rolled back to version …`). The failed version is removed;
  the version before and the one before it are kept.
- **Revoke.** `POST …/agents/{id}/revocation` with a reason (admin). The platform refuses the agent from its next call, and its waiting
  commands are cancelled. It's for good: the same key never gets back in. The host can return only as a new agent, with a new token (see
  above). Enrollment, renewal, revocation and every command and result are in `audit_records` (`agent.*`).

## Backup and restore

| Task | Command |
|---|---|
| Back up (daily, copy off the host) | `scripts/backup.sh` → `backups/juice_observability-<time>.dump` |
| Back up telemetry (daily, copy off the host) | `scripts/backup-telemetry.sh` → `backups/clickhouse/otel-<time>.zip` |
| Drill (restore into a scratch database, compare tables, report time) | `tests/restore-drill.sh` |
| Restore the live database | stop the API, `scripts/restore.sh <file> juice_observability`, start the API |
| Restore live telemetry | stop the Collector and the API, `scripts/restore-telemetry.sh <file> otel`, start them |

- **Measured (2026-11-01):** a drill of 21 tables completed in 11 s, against the 4-hour RTO target. Daily backups
  meet the 24-hour RPO target.
- **Measured (2026-11-04):** telemetry backup and restore of 8 tables, about 29 million rows, 114 MB, in 7 s.
- **Telemetry backups** are full copies, hold every tenant's telemetry, and are never pruned: protect them, and remove
  old ones. Restoring live telemetry loses what arrived since the backup. Without one, losing ClickHouse loses telemetry
  history; the platform's own state survives in PostgreSQL, inventory rebuilds from new telemetry, and health and rules
  resume on the next pass.

## Upgrade

1. Back up first.
2. Build and start the new API: `docker compose --profile platform up -d --build platform-api`. It applies pending
   migrations on start.
3. `MigrationTests` checks every upgrade path from an empty database up, one migration at a time, and the data
   carried across.
