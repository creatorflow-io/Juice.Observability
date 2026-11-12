# Phase 10 plan: domain Console and scale hardening

**Status:** approved by the owner 12 November; nothing built yet. The owner's decisions are marked **Owner**; the rest are mine, for review.
The owner's answers are at the end.

**Roadmap goal:** build proven domain workflows and scale the reference deployment with evidence.

**Roadmap deliverables:** a focused Console for topology, health, alerts and incidents, enrollment, approvals and action history; Grafana
kept for general telemetry exploration; load, retention, capacity, restore and upgrade drills, and disaster recovery.

**Roadmap gate:**
- the Console doesn't duplicate general Grafana exploration without a product need;
- tenant context is preserved;
- scale tests document throughput, query mix, queue lag, resource use and cost assumptions.

## Where Phase 10 starts

Most of the Console deliverable already exists, built phase by phase. Phase 10 is mostly evidence: checking what's there against the
roadmap, then measuring the deployment past the reference load and drilling the operations that haven't been rehearsed.

| Area | Already there | Not yet |
|---|---|---|
| **Console** | Overview, Services (with Capacity), Resources, Dependencies, Traces, Logs, Metrics, Alerts, Incidents, Rules, Notifications, Silences, Timeline, SLOs, Agents, Approvals, Load runs. Browser journeys for most (`console/e2e`) | No single map of roadmap workflow → page → journey; tenant context isn't checked page by page |
| **Load** | `tests/load.sh` at the §9 reference rates (1,000 spans/s, 10,000 points/s, 1,000 logs/s); 30-minute run in Phase 4 (freshness p95 1.37 s); the Phase 9 benchmark with 50 rules and 50 SLOs | Nothing above 1×. The load is **one service and one resource per signal**, not the 10 services and 100 instances §9 describes, so discovery, health and rule fan-out haven't been loaded |
| **Restore** | `tests/restore-drill.sh`: PostgreSQL and ClickHouse backup and restore (21 tables in 11 s) | Not on a fresh host; not after an upgrade |
| **Upgrade** | Migrations tested from empty, step by step (`MigrationTests`) | No drill from a released version with real data (1.1.0 → 1.2.0) |
| **Retention** | TTLs 30/30/90 days in the ClickHouse schema | Never seen to delete anything; no disk-per-day figure |
| **Disaster recovery** | RPO ≤ 24 h, RTO ≤ 4 h targets (§9); daily `backup.sh` | No full loss-of-host drill |

## Requirements and decisions

| # | Decision | By |
|---|---|---|
| P1 | **The Console's Traces, Logs and Metrics pages stay**, and the product need is recorded as the UI requirements' product decision (§1): Juice Observability must work without Grafana, and the core investigation must be possible entirely in the Console. Phase 10 adds **no new general exploration** (no dashboards builder, ad-hoc query language or Explore-style pages); those stay Grafana's. The Console keeps working on its own, independent of Grafana. | Owner |
| P2 | **Tenant context:** every Console page, deep link and API call keeps the tenant; tested with two tenants on one deployment (the API already scopes by tenant; this proves the Console and links don't lose it). | Mine |
| P3 | **Scale steps find the limit.** 1×, then 3×, 5× and 10× the §9 reference rates, 30 minutes each, with **10 services and 100 instances** generated (not one), continuing upward if 10× still holds. 1× must meet every §9 target (they are the reference deployment's acceptance thresholds); above 1× there is no pass mark: the steps find where the deployment stops meeting the targets, at what rate, and which part runs out first. | Owner |
| P4 | **Query mix during each step:** API reads at 20/s (inventory, health, alerts, SLOs), the 50 rules and 50 SLOs from the Phase 9 benchmark, one Console browser journey per minute, and trace and log searches at a fixed rate. The same mix at every step. | Mine |
| P5 | **Hardware:** this machine (24 cores, 64 GB) as the reference host, with the load generator on the same host and its share of CPU reported. Results are stated as "on a 24-core / 64 GB host", not as general limits. | Owner |
| P6 | **Cost assumptions** are stated as resources, not prices: vCPU, RAM, disk GB per day per signal at each step, and the disk a full 30/30/90-day retention needs. No prices. | Owner |
| P7 | **No new infrastructure in Phase 10 unless the 1× step misses a §9 target.** Then the roadmap's trigger table applies (Redis, JetStream, OpenSearch, object storage), with an ADR, operations plan, recovery design and cost rationale before anything is added. Results above 1× only inform. | Mine |
| P8 | **Drills run on a fresh compose project** (own volumes and network) on this machine, standing in for a new host. | Owner |
| D1 | **Targets are fixed before the first run** and written here (as in Phase 9), so results can't move them. | Mine |
| D2 | **Every result keeps its raw data** under `build/scale/` and a summary in `docs/scale-tests.md`; drills in `docs/drills.md`. | Mine |
| D3 | **Each milestone ends with the review loop** (two fix rounds and a confirmation round) before it's marked done. | Mine |

## Measures and targets (fixed before the first run)

Measured at every step. "Target" is the pass mark at 1×; above 1× it's the line whose crossing marks the limit.

| Measure | How | Target |
|---|---|---|
| **Throughput** | Collector accepted, refused and dropped per signal; rows written to ClickHouse | Nothing refused or dropped |
| **Ingestion freshness** | The `load.sh` probe | p95 ≤ 30 s |
| **API reads** | Inventory, health, alerts, SLOs at 20/s | p95 ≤ 500 ms |
| **Health freshness** | Evidence to visible state | p95 ≤ 60 s |
| **Rule breach-to-alert** | `breach-to-alert.sh` alongside | p95 ≤ 2 min after `ForDuration` |
| **Webhook dispatch** | First attempt after a transition | p95 ≤ 30 s |
| **Queue lag** | Collector queue size, rule and SLO evaluation lag, outbox and webhook backlog, discovery lag | Never above 1 min; no growing backlog |
| **Telemetry search** | Trace and log searches over the last hour | p95 reported (no §9 target) |
| **Resource use** | CPU and memory per container (`docker stats`, every 10 s), ClickHouse disk growth, PostgreSQL size | Reported; headroom stated |

## Milestones

| # | Milestone | Delivers | Evidence |
|---|---|---|---|
| 1 | **Console scope and tenant context** | A table mapping each roadmap workflow (topology, health, alerts and incidents, enrollment, approvals, action history) to its page and browser journey, gaps filled where a journey is missing; P1 recorded; a two-tenant check of every page, deep link and integration link (P2) | `docs/phase-10-console.md`; journeys pass on the built image; a deliberately broken tenant link is caught |
| 2 | **Scale harness** | `load.sh` takes a multiplier and generates 10 services × 10 instances with realistic attributes; a query-mix driver (P4); resource sampling; one report per run | A 1× smoke run that matches the Phase 4 figures |
| 3 | **Scale tests** | The 1×, 3×, 5×, 10× runs (P3, upward until a limit is found), the first limit and why, and the resource table (P6) | `docs/scale-tests.md` with raw data in `build/scale/`; if 1× misses a target, the fix or a P7 ADR |
| 4 | **Drills** | Upgrade 1.1.0 → 1.2.0 with data from a 1× run; retention (short TTLs on a scratch run, then the real TTLs' disk budget); restore on a fresh project; disaster recovery: lose the host, rebuild from the latest backups, measure RPO and RTO | `docs/drills.md`; scripts under `deploy/local/tests/`; RPO ≤ 24 h, RTO ≤ 4 h |
| 5 | **Gate** | `docs/phase-10-acceptance.md` mapping each gate line to its evidence | Owner acceptance |

Milestones 1 and 2 can run in parallel; 3 needs 2; 4 can start after 2 (its data comes from a 1× run).

## The owner's answers, 12 November

1. **Console scope (P1):** keep the Console working independently of Grafana; Traces, Logs and Metrics stay.
2. **Scale steps (P3):** the steps are there to find the limit; only 1× has a pass mark.
3. **Host (P5, P8):** this machine, for the scale tests and every drill.
4. **Cost (P6):** resources only, no prices.
