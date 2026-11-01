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
   - **`grafanaUrl`:** the Juice overview dashboard for that service over the last hour.
3. `GET /resources/{subjectId}` shows the service's instances, each with its own state. One unhealthy instance among
   healthy ones points at that process or host, not the service.
4. `GET /topology?environment=production` shows what the service calls and uses, and each node's health. A failing
   dependency is the usual cause of a `Critical` caller.
5. `POST /alerts/{id}/acknowledgement` with `{"comment": "..."}` needs operator or admin. Send an `Idempotency-Key` so
   a retry does not acknowledge twice.

`noData: true` on an alert means the last evaluation had too few samples: the alert keeps its state. Silence is
never read as recovery. `stale: true` on health means evaluation has stopped (next section).

## Health is stale or rules stop

- **Symptom:** `stale: true` on health. Check the API log for `Health evaluation failed` or `Rule ... evaluation
  failed`, and the metrics `juice.observability.health.evaluations{outcome=failure}` and
  `juice.observability.rules.evaluations{outcome}`.
- **ClickHouse unreachable.** Every pass fails and states are kept, never turned Offline or resolved. Once
  ClickHouse is back, the next pass (≤ 30 s) catches up.
- **`juice.observability.rules.skipped{reason=saturated}` keeps growing.** Rules are queued behind slow ones. Raise
  `Rules:MaxConcurrency`, or look at `juice.observability.rules.duration` to find the slow template.

## Telemetry missing

1. Run `tests/verify.sh`: it checks authentication, the tenant stamp and storage end to end.
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

1. Edit `rules.json` or `webhooks.json` and commit it.
2. Restart the API: `docker compose --profile platform restart platform-api`.
3. Check `audit_records` for `rule.create|update|disable`, `webhook.*` and `webhook.allowlist.*`.

An invalid file keeps the API from starting, and the log names every error.

## Webhook deliveries

- **History.** `GET /webhooks` lists the endpoints. `GET /webhooks/{id}/deliveries?status=failed` shows each
  delivery with every attempt: status code or error, and duration.
- **Retries.** Network errors, timeouts, 408, 429 and 5xx retry with backoff, from 10 s up to 10 minutes, for 8
  attempts. Any other 4xx fails at once.
- **Blocked destinations** show as `blocked_address:…`, `private_address_not_allowlisted:…` or `https_required`, and
  are never retried.
- **Alert state.** A failed delivery never changes alert state. To resend, fix the receiver, then set the delivery
  back to `Pending` in `webhook_deliveries`. Receivers deduplicate on `webhook-id`.

## Backup and restore

| Task | Command |
|---|---|
| Back up (daily, copy off the host) | `scripts/backup.sh` → `backups/juice_observability-<time>.dump` |
| Drill (restore into a scratch database, compare tables, report time) | `tests/restore-drill.sh` |
| Restore the live database | stop the API, `scripts/restore.sh <file> juice_observability`, start the API |

- **Measured (2026-11-01):** a drill of 21 tables completed in 11 s, against the 4-hour RTO target. Daily backups
  meet the 24-hour RPO target.
- **ClickHouse telemetry is not in these backups.** Losing ClickHouse loses telemetry history; the platform's own
  state survives in PostgreSQL. Inventory rebuilds from new telemetry. Health and rules resume on the next pass.

## Upgrade

1. Back up first.
2. Build and start the new API: `docker compose --profile platform up -d --build platform-api`. It applies pending
   migrations on start.
3. `MigrationTests` checks every upgrade path from an empty database up, one migration at a time, and the data
   carried across.
