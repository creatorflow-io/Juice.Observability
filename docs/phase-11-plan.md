# Phase 11 plan: platform self-observability, capacity and performance

**Status:** approved by the owner 13 November (revision 2, revised after an independent review against the code, then edited by the owner).
Revision 3, approved the same day after milestone 2, turns the rest of the phase toward the data path's throughput (the Collector,
ClickHouse, PostgreSQL), adds component ceilings (R19), and moves the Overview after the fixes (see "The owner's answer on revision 3").
- The owner answered four questions on revision 1 and Q1–Q3 on revision 2 (end of this file).
- Decisions marked **Owner** are the owner's; the rest are mine, for review.
- The owner split this out of Phase 10 on 13 November, to be done carefully, as a phase of its own: "this time is for optimizing the
  platform performance itself, also metrics and health; I need to see the platform working indexes on the Overview and estimate the
  capacity. How can I deploy platform if itself a bottleneck of system?"

**Goal:** the platform shows how it is working and how close it is to its measured limits, its own limits move up with evidence, and an
operator knows how to size and scale it.

**Focus (revision 3):** the data path's throughput. Telemetry has to get in (Collector → ClickHouse inserts) before anything reads it,
and the workers and searches are slow mainly when ClickHouse is busy. So the steps are reported per component, each component's own
ceiling is measured apart from the others, and the fixes are chosen from those measurements.

**Deliverables:**
- the platform's own metrics exported, and the Collector's and ClickHouse's (done);
- worker heartbeats and a platform health check (done);
- the scale steps with each component's throughput, and each component's ceiling measured on its own;
- ingest-path tuning, ClickHouse workload isolation and measured query fixes, PostgreSQL where it shows;
- the scale steps rerun before and after;
- platform status and a capacity view on the Console Overview, from the measured limits;
- a sizing and scaling guide.

**Gate:**
1. Every operational metric in requirements :254 is exported, each mapped to its source in `docs/platform-metrics.md`.
2. The Overview shows each worker's state and the platform's utilisation against its calibrated limits, and keeps working while
   ClickHouse is slow or down.
3. The capacity view's predicted limit is checked against steps held out from its calibration.
4. The rerun steps show where the first limit moved and why.
5. Each component's ceiling (ingest, ClickHouse queries, workers, PostgreSQL) is measured on its own, with what limited it (R19).
6. `docs/deployment.md` says how to size and scale each part.
7. No new infrastructure without a trigger-table row and an ADR.

## Why: where Phase 10 left it

The Phase 10 scale steps ([scale-tests.md](scale-tests.md), milestone 3) met every §9 target at 1×. At 3×:
- the SLO worker made no pass for about 60 s, and its lag crossed the line once;
- the rules pass due at the same time finished 12–21 s late, within its line.

The cause couldn't be measured: the platform has 21 instruments of its own, but exports none of them.

| Area | Today | Gap |
|---|---|---|
| **Own metrics** | 21 instruments under meter `Juice.Observability`: discovery, health and SLO pass durations; rule duration, lag, skips and queueing; SLO burn lag; webhook dispatch delay. The Juice framework's meters (`juice.messaging.*`, `juice.outbox.*`) are in the process too | **Nothing is exported:** no OpenTelemetry SDK is referenced. The Collector's own metrics are a Prometheus page on `:8888` that nothing reads. Requirements :254 asks for received, exported and dropped telemetry; the Collector's queue and memory; ClickHouse insert and query latency; PostgreSQL health; evaluation lag; alert transitions; webhook retries; and API latency. **Not measured at all:** API request duration, ClickHouse query time and rows read, ClickHouse insert latency, the outbox and webhook backlog, discovery watermark lag, the notification, sweeper and dispatcher passes, the lease holder, uptime |
| **Health endpoints** | `/health/live` (checks nothing) and `/health/ready` (PostgreSQL connects) | No worker staleness and no ClickHouse check. A replica whose workers stopped still reports ready |
| **Overview** | Service health tiles, firing alerts and services needing attention, all from PostgreSQL | Nothing about the platform itself |
| **Capacity** | Services → Capacity fits cores and memory against load for an application's service (`CapacityEstimate`) | Nothing for the platform |
| **ClickHouse use** | Every worker and every Console search use one connection string and one user, `platform_reader`. Its `bounded_reader` profile is shared with Grafana: `readonly = 2`, `max_execution_time` 60 s, `max_result_rows` 1 M, `max_memory_usage` 4 GB, each already pinned with a `MAX` constraint. Nothing limits rows read. ClickHouse 26.8; driver ClickHouse.Driver 1.5.0, which supports per-query settings and returns query statistics | No priority or share between workers and interactive searches, and no per-query settings. Every query reads the raw exporter tables (schema from the exporter's `create_schema`). The tenant lives only inside the `ResourceAttributes` map |
| **Workers** | One replica at a time under a PostgreSQL lease: discovery, health, rules, SLO counting, notifications, action requests. The webhook dispatcher and outbox delivery run in every replica. SLO counting goes one SLO after another; rules run 4 at a time | A worker's limit is its pass time against its interval, and nobody can see it. A leased worker can't scale out. `deployment.md` lists only three of the six leased workers |

## Requirements and decisions

### Self-telemetry

| # | Decision | By |
|---|---|---|
| R1 | **The platform's telemetry stays out of the tenant's data.** The Collector stamps every record it receives with the deployment's tenant, so the platform's own metrics would otherwise appear in the tenant's inventory as a service with instances. Health, service-wide rules and SLOs would then evaluate it, and it would show in Console counts, topology, Grafana and the tenant's disk figures. Instead the Collector gets a separate platform pipeline: its own OTLP receiver and token, stamping a reserved tenant value `juice-platform`. Discovery, health, rules, SLOs and Grafana's `grafana_tenant` policy filter on the deployment's tenant, so they never see it. The Overview's platform view reads it, and a test checks that it never shows on a tenant page. The added volume is stated (series × export interval). | Owner (Q1) |
| R2 | **Wiring.** One extension, `AddJuiceObservabilityTelemetry()`, is called by the reference host and documented for customer hosts. It configures the .NET OpenTelemetry SDK: the platform's meter, the Juice framework's meters, ASP.NET Core, HttpClient, runtime, process (uptime, for requirements :251) and Npgsql metrics, exported over OTLP every 15 s. Config: `Telemetry:Otlp:Endpoint`, a token file, an optional CA file and the export interval. A host that already configures OpenTelemetry can add the platform's meters to its own provider instead. **"On by default" (R14)** means it's enabled unless `Telemetry:Enabled=false`, and exports wherever an endpoint is configured. The shipped compose, `init.sh` and the deployment templates configure it. With no endpoint it logs once at startup and exports nothing. | Mine |
| R3 | **Bounded, safe dimensions.** A View allowlists the attribute keys of every exported instrument. The standard instrumentations otherwise carry `server.address` (webhook receivers' hosts), `dns.question.name` and connection-string-derived pool names. The replica is the `service.instance.id` resource attribute, not a dimension. New instruments use `juice.observability.*` names (requirements :254). The milestone 1 test checks that only allowlisted keys are exported. | Mine |
| R4 | **New instruments:**<ul><li>ClickHouse query duration, rows and bytes read, per caller (each worker and each API telemetry endpoint). Read from the driver's query statistics, with `system.query_log` as the authoritative cross-check, keyed by `log_comment`. The summary header can be partial for streamed results;</li><li>for every worker, a pass start, end, duration and outcome;</li><li>discovery watermark lag;</li><li>the outbox and webhook backlog and their oldest age;</li><li>the lease holder, per worker;</li><li>the rule scheduler's busy evaluation slots.</li></ul> | Mine |
| R5 | **The Collector and ClickHouse report too.** The Collector pushes its own telemetry (`service.telemetry` with an OTLP periodic reader) into the platform pipeline: received, refused, exported and failed per signal, each exporter's queue against its own 200,000 bound, and memory. ClickHouse is sampled by the platform every 15 s, server-side, never per page view, through a narrow `platform_monitor` user: CPU and memory (`system.asynchronous_metrics`), running queries (`system.processes`), query and insert latency per user (`system.query_log`, `otel_writer` for inserts), and disk used and free (`system.disks`, `system.parts`). That user is granted only those system tables. They show other users' queries, so it's documented, and used only for counts and timings, never query text. | Mine |

### Status and health

| # | Decision | By |
|---|---|---|
| R6 | **Heartbeats in PostgreSQL**, so status doesn't depend on ClickHouse.<ul><li>**Rows:** one row per (worker, replica), upserted at each pass start and end, so a pass stuck in a query shows. Replicas not seen for an hour are pruned.</li><li>**Staleness** is judged per worker across replicas, so a lease-standby replica isn't stale. A worker disabled everywhere (`*:Enabled=false`) is "off", not stale.</li><li>**Workers without passes:** the rule scheduler, the webhook dispatcher and outbox delivery have no single pass. They're shown by busy slots out of their concurrency and by backlog age, not by pass time against interval.</li><li>**Upgrades:** a 1.2.0 holder writes no heartbeat, so during a rolling upgrade that worker shows "unknown" for at most one interval.</li></ul> | Mine |
| R7 | **`/health/platform`** (owner, R4 of revision 1). `/health/ready` stays PostgreSQL only.<ul><li>**Anonymous answer:** status only, `ok` or `degraded`, with HTTP 503 when degraded.</li><li>**Operator role:** the details, per worker and per dependency.</li><li>**Cost:** served from a cached sample taken every 15 s, so a probe never fans out to ClickHouse or the Collector.</li><li>**Stale threshold:** max(3 × interval, 60 s).</li><li>**Use:** documented for alerting probes, not as a Kubernetes liveness or readiness probe. Every replica returns the same answer, so it would restart or remove them all at once.</li></ul> | Owner, Mine (details) |

### Overview and capacity

| # | Decision | By |
|---|---|---|
| R8 | **Overview, "Platform" panel**, for operators.<ul><li>**Workers:** for each, state, last pass and utilisation (pass time against interval, or busy slots), and lag past its configured delay.</li><li>**Ingestion:** received, refused and dropped per signal, and each export queue against its bound.</li><li>**ClickHouse:** CPU, running queries, and query and insert latency p95 by caller.</li><li>**Disk:** used and free, the steady-state need at the configured retention, and the days until full.</li></ul> Each figure is green, amber or red against the scale steps' lines. Everything comes from the heartbeats and the 15 s server-side samples. | Mine |
| R9 | **Capacity view: measured, labelled, not extrapolated.**<ul><li>**Current load:** a multiple of the §9 reference, per signal (spans, points, logs).</li><li>**Utilisation and knee:** each resource's utilisation (each worker, ClickHouse CPU, each Collector queue), each beside the knee where the calibration steps first crossed a line.</li><li>**Calibration file:** written by `steps.sh`. It names its host and its mix (rules, SLOs, services, search rate). Rules, SLOs and services also drive cost, so they're shown next to the load.</li><li>**Labels:** the view says "calibrated on <host>, <mix>". Without a calibration for the host it's running on, it shows utilisation only, and says so.</li><li>**Disk:** a figure of its own (days until full), not part of the headroom.</li><li>**From measurement (revision 3):** built after the fixes (milestone 6), from the final rerun and the ceilings, so it is never provisional.</li></ul> No prices (Phase 10 P6). | Mine |
| R10 | **The aim** (owner, revision 1):<ul><li>1× meets every §9 target;</li><li>every line holds at 3×;</li><li>the next limit is named with its measured cause;</li><li>the capacity view's predicted knee is within ±25 % of the measured one.</li></ul> **How the ±25 % is checked:** calibration on 1×, 3×, 5× and 10×, then finer steps held out near the knee (for example 4× and 6×), on the reference host and mix. The check isn't a claim about other hardware. The 3× aim goes beyond Phase 10 P7 ("results above 1× only inform"). It's a Phase 11 aim, not a change to Phase 10. | Owner (aim; method, Q2) |

### Performance

| # | Decision | By |
|---|---|---|
| R11 | **Workload isolation in ClickHouse, in three steps, each measured.**<ol><li>**Per-query settings** on the existing user, through the driver: a `log_comment` naming the caller, and tighter `max_execution_time` and `max_threads` for searches. The profile's three existing limits are already pinned with `MAX` constraints (`init-users.sh`). Any limit added here, such as `max_threads` or a rows-read limit, gets the same constraint, because `readonly = 2` lets a client raise an unpinned setting, Grafana included.</li><li>**ClickHouse workload scheduling** (`CREATE WORKLOAD`, with weights and priority, selected by the `workload` setting). This gives the workers a CPU share. `priority` alone only ranks queries running at the same time, and would starve searches under a steady worker load.</li><li>**Separate users** (`platform_worker`, `platform_search`), only if a per-user concurrency cap is needed. Each new user is added to the `all_tenants` row policy and to `verify.sh`: without a policy, `access.xml` makes a user silently read nothing. The code falls back to `ClickHouse:ConnectionString` when the new ones are unset. `init.sh` adds missing secrets to an existing `.env`, the compose access service passes them, and `deployment.md` gets the upgrade SQL.</li></ol> A refused or timed-out search shows in the Console as "busy, try again", and the harness counts it as failed. | Mine |
| R12a | **Ingest-path tuning, chosen from the ceilings (R19), not guessed.** Candidates, to confirm or drop:<ul><li>**Collector:** the batch processor's size and timeout, the exporter's consumers and queue, `memory_limiter`; which of them bounds the received rate;</li><li>**ClickHouse inserts:** rows per insert, parts created per second and merge backlog ("too many parts"), `async_insert` against the Collector's batching, compression codecs and TTL merges on the exporter tables;</li><li>**ClickHouse tables:** `ORDER BY` and skip indexes on the exporter tables. The exporter owns their schema (`create_schema`), so a change there means creating the tables ourselves, an upgrade cost for 1.3.0, stated with its gain.</li></ul> | Mine |
| R12b | **PostgreSQL, where it shows.** Its load (state, leases, heartbeats, outbox, notifications) is small at 1×. It gets fixes (indexes, pool sizes, statement batching) only if the steps or ceilings show it near a line: transactions, connections against the pool, lock waits, outbox age. | Mine |
| R12 | **Query fixes are chosen from measurement, not guessed.** Candidates from the code, to confirm or drop:<ul><li>SLO counting reads raw spans one SLO after another, and re-reads the last 5–6 minutes each pass (batch by service);</li><li>rule templates that read the window plus 1 h;</li><li>trace search's second scan over ±1 h;</li><li>`/dependencies`, up to 8 scans per request.</li></ul> A materialised tenant column is a later candidate: in a single-tenant deployment it saves only the map reads, and adding it to existing data is a heavy mutation, an upgrade cost for 1.3.0, on tables the exporter owns. Each change ships with its before and after on the same step. | Mine |
| R13 | **No new infrastructure** (Phase 10 P7 carries over). If the fixes can't meet R10, the roadmap's trigger table applies, with an ADR first. A ClickHouse replica for searches, or sharding, is new infrastructure: its trigger-table row was added on 13 November (Q3). | Mine |

### Measurement and process

| # | Decision | By |
|---|---|---|
| R14 | **Baseline** (owner, R11 of revision 1). Phase 10's 5× and 10× run here, after milestone 1, on a fresh scale stack: `stack.sh destroy`, then `WARMUP=300 scale/steps.sh --through 1 3 5 10`. **Reported per component** (revision 3): the Collector's received, exported and refused rows per second and its queue fill; ClickHouse insert rows per second and latency, parts and merges, CPU, query time by caller; PostgreSQL transactions per second, connections and outbox age. | Owner |
| R19 | **Component ceilings, each measured on its own** (revision 3), on the same host and scale stack, each raised in steps until a line is crossed, the cause recorded:<ul><li>**Ingest:** every worker off (`*:Enabled=false`) and no query mix; the generator's multiplier raised until the Collector refuses or its queue grows, or ClickHouse inserts fall behind. The ceiling is rows per second per signal. The generator's own sent rate is checked against its target, so a limit in the generator isn't read as the platform's.</li><li>**ClickHouse queries:** ingest held at 1×, workers off; the query mix's concurrency raised until a search's p95 crosses its line.</li><li>**Workers:** ingest at 1×, no query mix; the rule and SLO counts raised until a worker's pass time reaches its interval or its lag crosses its line.</li><li>**PostgreSQL:** watched in all of the above; a test of its own only if it nears a line.</li></ul> The ceilings say which part bends first, and by how much each must grow for the next step. | Mine |
| R15 | **Same harness, same host and same target source** as Phase 10 (P4, P5). The targets are still read from PostgreSQL, as in Phase 10, so the figures compare. The new metrics are used for causes. | Mine |
| R16 | **Each milestone ends with the review loop** (two fix rounds and a confirmation round), as Phase 10 D3. | Mine |
| R17 | **Release 1.3.0** (owner, R14 of revision 1: self-metrics on by default).<ul><li>**Upgrade from 1.2.0:** the heartbeat migration; the platform pipeline and token in the Collector; the `platform_monitor` user and its grants; any users R11 adds.</li><li>**Where the steps go:** `init.sh` and `clickhouse-access` for the compose stack, and `deployment.md` for customer installs.</li><li>**Drill:** Phase 10's upgrade drill (milestone 4) covers 1.2.0 → 1.3.0.</li></ul> | Owner, Mine (steps) |
| R18 | **Sizing and scaling guide** in `docs/deployment.md`, the answer to "how can I deploy the platform if it's the bottleneck". What scales out:<ul><li>the API, with more replicas;</li><li>webhook and outbox delivery, which run in every replica.</li></ul> What doesn't: each leased worker runs on one replica. Its levers are:<ul><li>its own settings (`Rules:MaxConcurrency`, `Slos:Interval` and `MaxMinutesPerPass`, rule and SLO counts);</li><li>separating worker replicas from API replicas (`*:Enabled`);</li><li>the fixes in R12.</li></ul> ClickHouse scales up on one node: CPU per 1× of load, from the steps. Isolation (R11) protects the workers from searches. The Collector is one instance, with its queue bounds. Each Overview figure (R8) maps to its action. The ceilings come from milestones 3–5 (revision 3: each component's own ceiling, R19). | Mine |

### As built (milestone 1, 13 November)

Two decisions above were built differently, for the reasons given, and are put to the owner at the gate:
- **R2:** there is no separate `AddJuiceObservabilityTelemetry()` call.
  - **How it's wired:** `AddJuiceObservability` adds the export itself (`AddPlatformTelemetry`), so a customer host still makes the
    same three calls, and on by default needs no fourth.
  - **For a host with its own OpenTelemetry:** `AddJuiceObservabilityMeters()` on its own pipeline.
  - **Separation:** the export has a meter provider of its own, so it never changes the host's.
- **R5:** the Collector's own metrics are scraped from its `:8888` page into the platform pipeline (`prometheus/self`), not pushed by
  an OTLP reader in `service.telemetry`, so the Collector needs no token or TLS connection to itself.

[Platform metrics](platform-metrics.md) describes both.

As built in milestone 2 (13 November), after its review:
- **R6, upgrades:** there is no `unknown` worker state. A worker whose 1.2.0 holder writes no row reads `starting` until a new
  replica takes the lease over at its first pass, which is how the lease works.
- **R6, the rule scheduler:** it counts its tick. Each evaluation has `Rules:EvaluationTimeout`, so busy slots are reported in the
  metrics (`rules.busy`), not in the status.
- **R6, the webhook dispatcher:** each batch is a pass.
- **R6, a worker no live replica runs:** `gone` when a replica gone within the hour had it enabled, so a stopped worker replica is never
  read as switched off.
- **R6, the rows:** they are written every 5 s from each worker's in-process state, not at each pass start and end. A pass start
  shows within 5 s.
- **R6, outbox delivery:** it is judged by how overdue its most overdue message is, not by busy slots. The framework's delivery has no
  slots the platform can read.
- **Milestone 2 drill:** the status shows a store down within one sample (15 s) plus the store's timeout (10 s), not within 15 s.
- **Milestone 2 drill, run 13 November on the working stack** (ClickHouse stopped three times, 20–60 s each):
  - **Status:** degraded, with `clickhouse: down` and `/health/platform` 503, after 4 s and 19 s, depending on where the sample cycle stood.
  - **Recovery:** `ok` 7–32 s after ClickHouse started.
  - **Console:** the page, `/health/ready`, and the Overview's health, alerts and resources (PostgreSQL) answered 200 throughout, in under 10 ms.
  - **Gap, for milestone 5:** trace search answers 500 after about 4 s while ClickHouse is down, not a clear "unavailable, try again".
  - **The operator's tenant:** the detailed status needs the deployment's tenant claim. The test client `juice-platform-test` stamps
    `local-test`, so erin through it gets 403 on the working stack, as designed. Through `juice-platform-cli` (the deployment's tenant) it answers.

## Milestones

| # | Milestone | Delivers | Evidence |
|---|---|---|---|
| 1 | **Self-metrics** | R1–R5; `docs/platform-metrics.md`, mapping every requirements :254 item to its instrument and source, with what a bad value means; runbook entries using the exported names | Unit tests that drive each instrument, event-driven ones included; on the local stack, every instrument in ClickHouse within two export intervals (30 s); only allowlisted attribute keys exported; nothing under the reserved tenant on any tenant page or in Grafana |
| 2 | **Heartbeats and health** | R6, R7: the heartbeat migration, a platform status endpoint (operator role), `/health/platform` | API tests (stale worker, standby replica, disabled worker, ClickHouse down, Collector down); a local drill: stop ClickHouse, then check that the status says so within 25 s (see "As built") and the Console still loads |
| 3 | **Baseline steps** | R14: 1×, 3×, 5×, 10× with the self-metrics, reported per component, the cause of each crossed line measured | A Phase 11 baseline section in `docs/scale-tests.md`; raw data under `build/scale/` |
| 4 | **Component ceilings** | R19: ingest, ClickHouse queries, workers, PostgreSQL, each on its own; the harness switches that make them (workers off, query mix off or raised, rule and SLO counts) | A ceilings section in `docs/scale-tests.md`: each ceiling, its limiting resource, and the generator's sent rate against its target |
| 5 | **Fixes** | R12a (ingest path), R11 step by step, R12 one change at a time, R12b if PostgreSQL shows; each with its step or ceiling rerun | Before and after per change; a final rerun against R10, with held-out steps for the ±25 % check |
| 6 | **Overview panel and capacity view** | R8, R9: the panel, and the calibration file written by `steps.sh` from the final rerun and the ceilings | A Playwright journey: the panel is present, and a worker turns red when its lease is held from `psql`. `WorkerLease.KeyOf` gives the key, and the takeover won't end a session that isn't a worker's. A unit test for the view from a known calibration |
| 7 | **Guide, gate and release** | R18, with each component's ceiling and lever; `deployment.md` corrected (all six leased workers); `docs/phase-11-acceptance.md`; the 1.3.0 upgrade path | Owner acceptance |

- **Order (revision 3):** 1 → 2 → 3 → 4 → 5 → 6 → 7. Milestones 1–7 are done; accepted by the owner 16 November
  ([acceptance](phase-11-acceptance.md)).
- **The baseline (3) waits for the metrics (1),** so it measures causes, not only symptoms.
- **The ceilings (4) follow the baseline,** which shows which component to push first.
- **The Overview (6) comes after the fixes,** so its limits are the measured ones, not a provisional calibration rewritten later. The
  steps are watched through Grafana and the platform status meanwhile.
- **Milestones 3–5 run on a fresh scale stack,** which needs the owner's permission to stop the working stack each time.

## Phase 10 meanwhile

Phase 10's milestone 3 closes on 1× and 3×, and its 5× and 10× move here (R14). Milestones 4 (drills) and 5 (gate) wait until Phase 11
is done, so the upgrade drill covers 1.2.0 → 1.3.0 as well.

That goes against the roadmap rule "Dependencies must follow the phase order". It is recorded as an owner exception in the roadmap (Q3, 13 November).

## The owner's answer on revision 3, 13 November

After milestone 2 the owner asked whether Phase 11 should focus on the ClickHouse, Collector and PostgreSQL throughput instead of the
platform itself. Agreed ("ok"): the rest of the phase is reordered to baseline → component ceilings → fixes → Overview → guide, R19
(component ceilings), R12a (ingest path) and R12b (PostgreSQL) are added, and the baseline is reported per component. Milestones 1
and 2 stay as built.

## The owner's answers on revision 2, 13 November

1. **Q1:** the reserved tenant: the platform's telemetry stays out of the tenant's inventory, evaluations and Grafana.
2. **Q2:** held-out steps (4×, 6×) on the reference host and mix; other hardware shows utilisation only, labelled "not calibrated for this host".
3. **Q3:** both roadmap changes recorded: the phase-order exception, and a trigger row for a ClickHouse replica or sharding.

## The questions asked on revision 2

- **Q1 (R1):** keep the platform's own telemetry under a reserved tenant, out of the tenant's inventory, evaluations and Grafana? Or let
  it appear as a discovered service, watched by its own health, rules and SLOs, with a documented policy?
- **Q2 (R10):** check the ±25 % on held-out finer steps (4×, 6×) on the reference host and mix only, and label the capacity view "not
  calibrated" on other hardware?
- **Q3 (R13, phase order):** record two roadmap changes:
  - an owner exception, so Phase 11 can run before Phase 10's milestones 4–5;
  - a trigger-table row for a ClickHouse replica or sharding: a measured search-or-worker limit that isolation and query fixes
    couldn't move, an ADR, an operations plan and a recovery design.

## The owner's answers on revision 1, 13 November

1. **Health (R7):** a separate `/health/platform` for workers, ClickHouse and the Collector; `/health/ready` stays PostgreSQL only.
2. **Aim (R10):** 1× meets every §9 target, every line holds at 3×, the next limit is named with its measured cause, and the capacity
   estimate is within ±25 % of the measured limit.
3. **Baseline (R14):** Phase 10's 5× and 10× move here, run once the self-metrics exist, on a fresh stack.
4. **Release (R17):** self-metrics export on by default in 1.3.0, and can be turned off.
