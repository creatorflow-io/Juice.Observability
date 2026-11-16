# Phase 10 acceptance: domain Console and scale hardening

**Status:** accepted by the owner 2026-11-17. Milestones 1–4 are done, each closed by the review loop (D3); this
document is milestone 5. Phase 10's 5× and 10× steps, and its drills, ran after Phase 11 on the owner's exception (13 November), so the
scale figures below are the platform as released in 1.3.0. [Plan](phase-10-plan.md).

## The gate

| Gate line | How it's met | Evidence |
|---|---|---|
| The Console doesn't duplicate general Grafana exploration without a product need | Each roadmap workflow (topology, health, alerts and incidents, enrollment, approvals, action history) maps to its pages and a browser journey; every Console route is walked. Traces, Logs and Metrics overlap with Grafana's Explore and stay on a recorded product need: the platform must work without Grafana, and the core investigation must be possible entirely in the Console (P1, owner, 12 November) | [Phase 10 Console](phase-10-console.md): the workflow table and the product need; `console/e2e` journeys |
| Tenant context is preserved | One deployment serves one tenant; one ClickHouse can hold several. Grafana read every tenant: fixed with ClickHouse row policies on every telemetry table (`grafana_tenant`, on the tenant column since 1.3.0), a fail-closed `access.xml`, and the `clickhouse-access` service; the global search's trace-ID lookup scoped to the tenant. Grafana listing other tenants' trace IDs through the trace index is accepted (owner, 12 November) | [Phase 10 Console](phase-10-console.md), Tenant context; `console/e2e/tenant.mjs`; `tests/verify.sh` (the row policies, checked to fail when they should); the upgrade drill (the policy on an upgraded 1.2.0 stack) |
| Scale tests document throughput, query mix, queue lag, resource use and cost assumptions | The harness (10 services × 10 instances, the query mix of P4, sampling every 10 s, one report a step) ran 1× and 3× in Phase 10, then 1×, 3×, 5×, 10× (baseline), the component ceilings, and the final rerun with 4× and 6× held out in Phase 11. Each step reports throughput per signal, the query mix's latencies, every worker's lag and the backlogs, CPU and memory per container, and disk a day per table. 1× meets every §9 target; every line holds through 5×; the first crossing is at 6× (a burst), the sustained limit at 10× (the Collector's metrics export). Cost as resources (P6): CPU, memory and disk at each step, and the disk a full 30/30/90-day retention needs at 1×, measured after merges: 17.5 GB a day, about 1.04 TB | [Scale tests](scale-tests.md) (the harness, Phase 10's steps, Phase 11's baseline, ceilings, fixes and final rerun); [drills](drills.md#the-disk-the-real-retention-needs); [deployment guide](deployment.md#sizing-and-scaling); raw data under `build/scale/` |

## The deliverables

- **A focused Console** for topology, health, alerts and incidents, enrollment, approvals and action history: milestone 1 (above), and
  since Phase 11 the Overview's platform panel and capacity view for operators.
- **Grafana kept for general telemetry exploration,** reading only the deployment's tenant.
- **Load and capacity:** the scale harness and the steps above; the capacity view, calibrated on the reference host.
- **The drills** ([drills](drills.md)), each on a fresh compose project on this machine (P8):
  - **Upgrade:** 1.2.0 → 1.3.0 with 10 minutes of the reference load (the plan's 1.1.0 → 1.2.0, moved to cover 1.3.0 by the owner's
    exception): nothing lost, every migration, the new pieces working on the old data. `compose up` to the stack up in 36–73 s over the runs.
  - **Retention:** short TTLs on a scratch project; every row deleted by ClickHouse's own TTL merges, none of any source left past
    its TTL. The real retention's disk: above.
  - **Restore on a fresh project, and disaster recovery:** the working stack (588 M telemetry rows) backed up as the schedule does,
    lost, and rebuilt on a fresh project from a clean checkout, `.env`, secrets and the backups: every table back, the platform
    answering, old traces found, new telemetry flowing. **RPO 5 s, RTO 145 s** against §9's 24 h and 4 h.

## Decisions the owner made in Phase 10

- P1: Traces, Logs and Metrics stay in the Console (12 November).
- P2: the trace-ID index gap accepted (12 November).
- P3: the steps find the limit; only 1× has a pass mark. P5, P8: this machine for every step and drill. P6: resources, no prices.
- Phase 11 split out (13 November), with the phase-order exception: Phase 10's 5×, 10× and drills after it.

## Open, for later phases

- **The Collector's metrics export** is the next limit on the reference host (about 95,000 points a second): to profile.
- **Not drilled:** rolling back a release after an upgrade; an upgrade with more than one API replica; a new host's own install in
  the RTO.
