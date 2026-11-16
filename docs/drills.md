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
- **Ran:** twice, from `api-1.2.0` (6b0d343) to 4bb88db, the working stack stopped for the ports. The first run's migration check
  read the wrong table name (the database's snake case history table), so it failed though the migrations had run (the heartbeats,
  written with the new column, said so); fixed, and run again: `build/upgrade-drill-20261116T1151/result.md`.

| Count | Before (1.2.0) | After (1.3.0, 2 min more) |
|---|--:|--:|
| Spans; logs | 600,027; 600,027 | 720,059; 720,059 |
| Metric points (sum, histogram, gauge) | 5,879,425; 85,800; 43,256 | 7,058,875; 103,515; 52,954 |
| Services; instances; resources; dependencies | 10; 100; 30; 22 | the same |
| Rules; alerts; health transitions; audit records | 10; 4; 217; 14 | 10; 4; 257; 14 |
| Migrations applied | 19 | 21 |

- **Every check passed:**
  - nothing lost, and telemetry still flowing after the upgrade;
  - every migration applied (the latest, `WorkerPassSeconds`);
  - the twelve resource columns on all seven tables, holding their keys on every span written before them (0 mismatches);
  - Grafana's row policy on the tenant column (new after 1.2.0, where Grafana read every tenant);
  - `platform_monitor` reading its system tables;
  - the platform's own metrics arriving under the reserved tenant (566 points in 2 minutes), and none of them in the tenant's inventory;
  - every worker's heartbeat written (8 of 8); `/health/platform` answering `ok`.
- **Time:** 52 s from checkout to the stack up again on the second run, 73 s on the first (the image build cached or not). The
  Collector is down for part of it; senders retry, as for any Collector restart.
- **Not covered:** rolling back to 1.2.0 after the upgrade (the [deployment guide](deployment.md) says what an older build ignores);
  more than one API replica during the upgrade.

## Still to drill (Phase 10 milestone 4)

- **Retention:** short TTLs on a scratch run, then the real TTLs' disk budget.
- **Restore** on a fresh compose project (`tests/restore-drill.sh` restores in place today).
- **Disaster recovery:** lose the host, rebuild from the latest backups, measure RPO (≤ 24 h) and RTO (≤ 4 h).
