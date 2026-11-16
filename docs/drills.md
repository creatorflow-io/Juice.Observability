# Drills

The operations rehearsed on a running stack ([Phase 10 plan](phase-10-plan.md) milestone 4, D2), each with its script and its
latest result. Raw logs stay under `build/`.

## Upgrade: 1.2.0 → 1.3.0 (16 November)

`deploy/local/tests/upgrade-drill.sh` (Phase 11 R17, which moved this drill after Phase 11 so it covers 1.3.0).
- **What it does:** checks out the released `api-1.2.0` into a worktree and installs it as its own compose project, `juice-upgrade`, with
  a fresh `.env` and secrets. It sends 10 minutes of the reference load (1×: 1,000 spans, 10,000 points and 1,000 logs a second, from
  `scale/generate.mjs`), and lets discovery, health and rules catch up. Then it upgrades in place, as the [runbook](runbook.md#upgrade)
  says: the new commit checked out over the old (`.env`, secrets and volumes kept), `scripts/init.sh`, `docker compose --profile
  platform up -d --build --wait --remove-orphans`, `scripts/apply-clickhouse-access.sh`. It sends 2 minutes more, and checks.
- **Ran:** three times from `api-1.2.0` (6b0d343), the working stack stopped for the ports. The first run's migration check read the
  wrong table name (the database's history table is snake case), so it failed though the migrations had run; the second passed. The
  milestone 7 review then hardened the drill (every platform table counted, a table missing after counted as lost, the migrations
  against the release's files, the load and the access script failing the drill, the old data read through the platform's API), and
  the third run, to 83e55d5, is the record: `build/upgrade-drill-20261116T1218/result.md`. Its second review round made two checks
  stricter (every enabled worker passing, not only its heartbeat row; the platform's own services by the names they report) and the
  ports checked whoever holds them; the fourth run passed those as well: `build/upgrade-drill-20261116T1246/result.md`.

| Count | Before (1.2.0) | After (1.3.0, 2 min more) |
|---|--:|--:|
| Spans; logs | 599,970; 599,970 | 720,011; 720,011 |
| Metric points (sum, histogram, gauge) | 5,879,525; 85,800; 43,256 | 7,058,927; 103,483; 52,936 |
| Services; instances; resources; dependencies; environments | 10; 100; 30; 22; 1 | the same |
| Rules; SLOs and their versions; alerts; alert transitions | 10; 3, 3; 4; 6 | 10; 3, 3; 4; 8 |
| Health snapshots; transitions; change events; audit records | 132; 216; 13; 14 | 132; 260; 13; 14 |
| Migrations applied | 19 | 21 (of the release's 21) |

Every platform table is counted, 39 of them (the result lists each); those whose rows come and go by design are left out: the
outbox's, expiring idempotency keys, leases and heartbeats.

- **Every check passed:**
  - `scripts/apply-clickhouse-access.sh` ran, and the load before and after the upgrade was sent;
  - nothing lost, and telemetry still flowing after the upgrade;
  - every migration the release has applied (21 of 21);
  - the twelve resource columns on all seven tables, and the old data read through them: a trace search through the upgraded API
    over the load before the upgrade (its tenant filter is `JuiceTenant` now, on parts written before the column) found a full
    page of traces;
  - Grafana's row policy on the tenant column (new after 1.2.0, where Grafana read every tenant);
  - `platform_monitor` reading its system tables;
  - the platform's own metrics arriving under the reserved tenant (627 points in 2 minutes), and none of them in the tenant's inventory;
  - every enabled worker passing since the upgrade (8 of 8, in the fourth run); `/health/platform` answering `ok`.
- **Time:** `docker compose up` to the stack up again: 39–73 s over the four runs (the image build cached or not); the checkout and
  `init.sh` before it take seconds. The Collector is down for part of it; senders retry, as for any Collector restart.
- **Not covered:** rolling back to 1.2.0 after the upgrade (the [deployment guide](deployment.md) says what an older build ignores);
  more than one API replica during the upgrade.

## Still to drill (Phase 10 milestone 4)

- **Retention:** short TTLs on a scratch run, then the real TTLs' disk budget.
- **Restore** on a fresh compose project (`tests/restore-drill.sh` restores in place today).
- **Disaster recovery:** lose the host, rebuild from the latest backups, measure RPO (≤ 24 h) and RTO (≤ 4 h).
