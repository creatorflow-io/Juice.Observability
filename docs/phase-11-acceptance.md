# Phase 11 acceptance: the platform's own health, limits and sizing

**Status:** accepted by the owner 2026-11-16, with the two decisions built differently from the plan (R2: no separate telemetry call;
R5: the Collector's own metrics scraped, not pushed; [plan](phase-11-plan.md), "As built"). Milestones 1–7 are done, each closed by the
review loop (two fix rounds and a confirmation round, R16). [Plan](phase-11-plan.md), [platform metrics](platform-metrics.md), [scale tests](scale-tests.md),
[deployment guide](deployment.md#sizing-and-scaling).

## The gate

| Gate line | How it's met | Evidence |
|---|---|---|
| 1. Every operational metric in requirements :254 is exported, each mapped to its source | The platform's meter, the framework's, ASP.NET Core, HttpClient, runtime, process and Npgsql metrics go to the Collector's platform pipeline under the reserved tenant `juice-platform`; the Collector's own metrics are scraped into it; ClickHouse is sampled every 15 s through `platform_monitor` | `docs/platform-metrics.md` (each :254 item to its instrument and source, and what a bad value means); `PlatformMetricsTests`, gate 1: every instrument the platform declares is listed there and goes through the exporter's pipeline; only allowlisted attribute keys are exported; nothing under the reserved tenant on a tenant page or in Grafana (milestone 1) |
| 2. The Overview shows each worker's state and the platform's utilisation against its calibrated limits, and keeps working while ClickHouse is slow or down | The Overview's Platform panel (operators and admins): each worker's state, last pass and how busy; backlogs; ingestion per signal and each export queue; ClickHouse CPU, inserts and reads by caller; disk. The capacity view sets each resource beside what the calibration steps measured where every line held and where one crossed. Everything comes from the heartbeats (PostgreSQL) and the last 15 s sample: no request reaches ClickHouse | `console/e2e/platform.mjs`: the panel, none for a viewer, and a worker turned red (`unheld`) when its lease was held from `psql`, green again after; `PlatformOverviewTests` (levels, labels, calibration); `…answers_at_once_with_the_workers_while_clickhouse_hangs` (gate 2's ClickHouse slow or down); the milestone 2 drill (the status with ClickHouse stopped, the Console still loading) |
| 3. The capacity view's predicted limit is checked against steps held out from its calibration | Calibration on 1×, 3×, 5×, 10×; 4× and 6× held out. The 6× crossing came from a burst, not a curve: interpolating any measure puts the knee near 9.5–10× (+60 %). The owner chose the bracket: held to 5×, crossed at 10×. Its lower bound against the measured first crossing at 6×: −17 %, within ±25 %; 4× held and 6× crossed, as it says. Chosen after seeing the data, and said so | [Scale tests](scale-tests.md), "The ±25 % check (R10)"; `deploy/local/capacity/calibration.json`; `…The_shipped_calibration_is_the_final_reruns_bracket…` |
| 4. The rerun steps show where the first limit moved and why | The first crossing moved from 5× (baseline: 20 M points refused, workers minutes behind, ClickHouse at 15 cores) to 6× (one 10-second burst, 0.012 % of points), and the sustained limit to 10× (5.8 % of points, with ClickHouse at 7 of 24 cores). Why: the platform's reads at 1× fell from 1.90 to 0.28 ClickHouse cores (searches bounded and newest-first, discovery's metrics cadence, health's remembered liveness, SLO reads narrower, the resource columns) and the Collector sends 16 inserts at once; the next limit is the metrics export's insert wait (70 → 206 ms), which caps it near 95,000 points a second | [Scale tests](scale-tests.md): "Phase 11 baseline", "Phase 11 fixes" §1–8 (each before and after), "Phase 11 final rerun" |
| 5. Each component's ceiling is measured on its own, with what limited it (R19) | Ingest: the metrics exporter, about 90,000 points a second (then 16 consumers: 100 % at 10×, 110,000 at 20×). ClickHouse queries: 8 to 16 searches a second at 1× ingest (then bounded: 16 a second degrades instead of collapsing). Workers: steady at 100 rules and 100 SLOs; a burst of alerts crashed the API (fixed: bounded handlers, the GSS probe off). PostgreSQL: watched in all, nowhere near a line | [Scale tests](scale-tests.md), "Phase 11 component ceilings" and fixes §1, §2, §7 |
| 6. `docs/deployment.md` says how to size and scale each part | What one machine carries at 1×–10×; what scales out (API replicas, delivery) and what doesn't (the six leased workers, with their switches); each worker's levers; ClickHouse and the Collector; each panel figure and what to do about it; how to calibrate on another machine | [Deployment guide](deployment.md#sizing-and-scaling) |
| 7. No new infrastructure without a trigger-table row and an ADR | None was added. A ClickHouse replica for searches, or sharding, has its trigger-table row (13 November, Q3); nothing reached it | [Roadmap](observability-platform-roadmap.md) trigger table |

## Release 1.3.0 (R17)

- **Version:** 1.3.0 (`Directory.Build.props`, the Console package), tagged `api-1.3.0` and `console-1.3.0` locally. Pushing the tags
  publishes it (`.github/workflows/release.yml`): the owner's step.
- **Upgrade from 1.2.0:** `scripts/init.sh` (adds `CLICKHOUSE_MONITOR_PASSWORD` and the platform's OTLP token), `docker compose
  --profile platform up -d --build --wait --remove-orphans`, `scripts/apply-clickhouse-access.sh` ([runbook](runbook.md#upgrade)). The migrations (heartbeats,
  their pass time) run on start; `clickhouse-access` adds `platform_monitor`, the row policies and the platform pipeline's users;
  `clickhouse-columns` adds the resource columns, with nothing rewritten.
- **Drill** ([drills](drills.md#upgrade-120--130-16-november)): `tests/upgrade-drill.sh` installed 1.2.0, gave it 10 minutes of the reference load, upgraded
  it in place to 83e55d5 and checked: nothing lost (599,970 spans and each of the 39 platform tables counted), every migration the
  release has applied (21 of 21), the resource columns on every table and the old traces found through them by the upgraded API,
  Grafana's row policy, `platform_monitor`, the platform pipeline, every worker's heartbeat, `/health/platform` ok. 58 s from
  `compose up` to the stack up again.

## Decisions the owner made in Phase 11

- Self-metrics on by default (R14 of revision 1), a separate platform pipeline under a reserved tenant (Q1).
- The ±25 % aim (R10) and its method: held-out steps (Q2).
- The trigger-table row for a ClickHouse replica or shards (Q3).
- Discovery reads the metric tables every 5 minutes (15 November).
- Resource attributes as columns: measured first, then all twelve the workers read (16 November).
- The capacity view's knee is the calibration's bracket (16 November).

## Open, for later phases

- **The Collector's export is the next limit** (near 95,000 points a second on the reference host): why the insert wait grows
  (ClickHouse's adaptive busy timeout, the exporter's batch sizes, or the Collector) is to profile.
- **ClickHouse workload scheduling** (R11 step 2) wasn't needed for the targets once the reads were fixed; kept for later.
- **Health's liveness reads** went from about 3 to about 5 a row with the review's 2-minute whole read: within the rerun's figures,
  but measured only on the next scale run.
- **Phase 10's other drills** (retention, restore on a fresh project, disaster recovery) and its gate (milestone 5) follow.
