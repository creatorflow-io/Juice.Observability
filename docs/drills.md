# Drills

The operations rehearsed on this machine ([Phase 10 plan](phase-10-plan.md) milestone 4, D2; P8: each on a fresh compose project), each with its script and its
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
  ports checked whoever holds them; the fourth run passed those as well: `build/upgrade-drill-20261116T1246/result.md`. The confirmation round read the ports from the
  compose file, checked them before removing anything, counted each table on its own if the one query failed, and required all 8
  workers with each replica's row judged on its own; the fifth run passed: `build/upgrade-drill-20261116T1308/result.md`.
  Phase 10's milestone 4 review then moved the drills onto a shared `tests/drill-lib.sh`. A sixth attempt stopped before the upgrade:
  1.2.0's API crashed as it started (exit 139, Npgsql's GSS encryption probe, the crash 1.3.0 fixed), and 1.2.0 doesn't restart it, so
  the drill now starts it again on that exit code alone. The seventh run passed every check: `build/upgrade-drill-20261116T1556/result.md`.

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
  - every enabled worker passing since the upgrade (8 of 8 and no replica's row failing, in the fifth run); `/health/platform` answering `ok`.
- **Time:** `docker compose up` to the stack up again: 36–73 s over the runs (the image build cached or not); the checkout and
  `init.sh` before it take seconds. The Collector is down for part of it; senders retry, as for any Collector restart.
- **Not covered:** rolling back to 1.2.0 after the upgrade (the [deployment guide](deployment.md) says what an older build ignores);
  more than one API replica during the upgrade.

## Disaster recovery, and restore on a fresh project (16 November)

`deploy/local/tests/dr-drill.sh`: requirements §9's RPO ≤ 24 h and RTO ≤ 4 h, and Phase 10's restore on a fresh project (P8), in one
drill, since a restore onto a new host is what a disaster recovery is.
- **What it does:** the working stack's writers (the Collectors and the API) stop, its tables are counted, and it is backed up as the
  schedule does (`scripts/backup.sh`, `scripts/backup-telemetry.sh`). Then it is "lost": stopped, and not used again (its volumes are
  never touched). A fresh compose project (`juice-dr`, its own volumes and network) stands in for the new host, built from a clean
  checkout of this commit and what an operator keeps off the host: the two backups, `.env` and `secrets/`. It starts PostgreSQL and
  ClickHouse alone, restores both (`scripts/restore.sh`, `scripts/restore-telemetry.sh`) and counts them, then starts the rest
  (`up --build --wait`, `scripts/apply-clickhouse-access.sh`) and checks the platform.
- **Ran:** six times on 16 November, against the working stack (588 M telemetry rows, 6.7 GiB; 45 services). The first stopped before
  the rebuild: the script set `MSYS_NO_PATHCONV` for itself, and Git Bash then handed `git` an unconverted path; fixed. The second and
  third passed everything but the counts, short by 1,830 spans and 15 SLO buckets both times, and neither a loss:
  - **the spans:** the working stack's parts still held 1,830 rows a lightweight `DELETE` (a test's) had masked: `count()` sees
    62,600,670, the parts' totals said 62,602,500. A backup carries the rows that are there. The drill counts `count()` now;
  - **the SLO buckets:** 15 = 3 SLOs × the 5 minutes of `Slos:Lateness`, which the restored platform's SLO counter recounts as soon
    as it runs, with what those minutes hold. The drill counts the restored stores before the platform starts now.
- **The fourth run passed every check** (`build/dr-drill-20261116T1456`). The milestone's review then made the drill stricter: the
  restored counts must equal the backup's exactly (more is as wrong as less), the ports are checked before the working stack is
  touched, the search runs over the deployment's own tenant, and a failed count can never pass. The fifth run passed that too, and the
  sixth, on the final script, is the record (`build/dr-drill-20261116T1613/result.md`), every check passed:
  - every table back with exactly what it held at the backup: the 8 telemetry tables (the 7 signal tables and the trace index;
    62,600,670 spans, 588 M rows in all) and all 39 platform tables counted;
  - `/health/platform` answering `ok` on the new project; the services and their health back through the API (45); a trace search
    over the hour before the backup found a full page; new telemetry stored (60,044 spans in 3 minutes);
  - **RPO 5 s** (the last backup to the loss), **RTO 145 s** (the rebuild's start to the platform answering `ok`; the two restores
    81 s of it). The backups: 852 KB for the platform database, 4.2 GB for the telemetry. Over the runs, RTO 145–161 s.
- **What it means for §9:** the RPO a deployment has is its backup schedule's interval: daily backups meet ≤ 24 h. The RTO measured
  here is for this data size on this machine; copying the backups to the new host comes on top. Both are far inside their targets.
- **Not covered:** a new host's own install (Docker, the checkout), which the RTO doesn't include; restoring Grafana's dashboards
  (provisioned from the repository, so a checkout brings them back) and the agents' own state (on their hosts).

## Retention (16 November)

`deploy/local/tests/retention-drill.sh`: retention seen to delete, and the disk the real retention needs.
- **What it does:** a scratch compose project (`juice-retention`, its own volumes) from a clean checkout of this commit, with every
  signal's TTL set to 4 minutes in its `.env` (`JUICE_*_TTL`, as an operator sets them before the tables are created). The telemetry
  stack starts without the platform, the exporter creates its tables, and 2 minutes of the reference load go in. Then it waits.
  ClickHouse applies a TTL in a merge, at most every `merge_with_ttl_timeout` (4 hours by default) per partition: the drill lowers that
  to 60 s on its scratch tables, so it waits minutes, not hours. Nothing else forces the deletion.
- **Ran three times, every check passed each time.** The first (`build/retention-drill-20261116T1503`) watched the seven signal
  tables; the review added the trace index and made a failed count never read as gone; the third, on the final script, is the record
  (`build/retention-drill-20261116T1547/result.md`):
  - the exporter created all eight tables, the trace index included, with the configured TTL (`toDateTime(Timestamp) +
    toIntervalMinute(4)`; `TimeUnix` for the metrics, `Start` for the trace index);
  - 118,821 spans, as many logs, and about 1.2 M metric points stored;
  - every row of the load deleted by ClickHouse's own TTL merges 310 s after the load ended (421 s on the first run), the trace
    index included: the tables emptied one by one, each as its own TTL merge ran, in an order that differs between runs;
  - no row of any source left past its TTL, the Collector's own host and self metrics included, which kept arriving throughout.
- **On a real deployment** each partition is a UTC day, so a day's telemetry goes within `merge_with_ttl_timeout` (4 hours) after its
  last row passes the TTL: the disk holds the retention plus about a day.

### The disk the real retention needs

At the reference load (1×) and the reference retention (traces and logs 30 days, metrics 90), measured on the final rerun's scale
data after ClickHouse's merges (`juice-scale` volume, 588 M rows): bytes a row on disk, times the rows a day at 1× (the 1× step's).

| Table | Bytes a row | Rows a day at 1× | A day | Retention | At full retention |
|---|--:|--:|--:|--:|--:|
| `otel_traces` | 49.7 | 86.6 M | 4.3 GB | 30 d | 129 GB |
| `otel_logs` | 50.5 | 86.3 M | 4.4 GB | 30 d | 131 GB |
| `otel_traces_trace_id_ts` | 21.5 | 13.2 M | 0.28 GB | 30 d | 8.5 GB |
| `otel_metrics_sum` | 9.7 | 846 M | 8.2 GB | 90 d | 741 GB |
| `otel_metrics_histogram` | 19.5 | 12.7 M | 0.25 GB | 90 d | 22 GB |
| `otel_metrics_gauge` | 11.4 | 6.7 M | 0.08 GB | 90 d | 7 GB |
| **Total** | | | **17.5 GB** | | **about 1.04 TB** |

- **The scale reports' figure stands:** they scaled the first 30 minutes' parts to a day (1,020 GB at full retention at 1×), and the
  merged parts hold about the same bytes a row (49.7 against 50 for spans, 9.7 against 9.5 for metric sums), so "about 1 TB per 1×"
  in the [deployment guide](deployment.md#what-one-machine-carries) is the measured figure, not only an upper one.
- **Metric sums are 71 % of it,** at 90 days: shorter metrics retention is the largest lever (`ALTER TABLE … MODIFY TTL`).
- PostgreSQL's share is small: 0.08 GB a day at 1× (the 1× step); its SLO buckets are kept 35 days (`Slos:Retention`).
- The Overview's disk figure measures a deployment's own day from its last whole UTC partition, and its days until full.

## Dead-man: the notification pipeline's dispatcher stalls (26 November)

Proof point 10 of phase 5 (Juice.Notifications embedded, `Notifications:Pipeline` `new`): `DeadManDrill` in the test project, explicit
(`dotnet test --filter DeadManDrill -- xUnit.Explicit=on`), on the local stack.
- **What it does:** a host with the pipeline in `new`, the dead-man posting to a receiver on this machine's private address
  (`Notifications:DeadMan`, allowlisted, signed), and `DueThreshold` at its documented minimum, 2 minutes. An alert fires; the pipeline's
  webhook target answers 500 once, so the delivery comes due again for its retry. Before the retry is taken, another session holds the
  delivery's row (`FOR UPDATE`): the dispatcher claims with `SKIP LOCKED`, so it can't take it, and the row stays due and unleased. That is
  how a stuck dispatcher looks from the database, which is where the dead-man reads it (the pipeline's health, not its timers). Then the row
  is released.
- **Ran:** once, passed:

  | Time (UTC) | |
  |---|---|
  | 07:39:56 | alert fired; its request written |
  | 07:39:57 | dispatcher stalled: the delivery held, due for its retry |
  | 07:42:01 | dead-man arrived: `stalled`, "dispatcher due 2 min" |
  | 07:42:01 | dispatcher released |
  | 07:42:03 | the retry delivered; dead-man `resolved` |

  The alarm came at the threshold plus one pass (2 s), and each event was posted once.
- **Not drilled here:** the dispatcher stopped by a restart. The dead-man's state is in memory (by design), so a cause still standing
  after a restart is posted again by the new process, which then resolves it when it clears. A cause that clears while the host is down is
  never resolved: the new process didn't see it. The receiver's last word on it stays `firing` until it arises again.

## Not drilled

- Rolling back a release after an upgrade, and an upgrade with more than one API replica.
- A new host's own install (Docker, the checkout) in the RTO.
