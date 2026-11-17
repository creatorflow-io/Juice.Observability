# Scale tests

**Status:** milestone 2 (the harness) done 12 November: two review rounds and a confirmation round, then the 1× smoke run on the final
harness (below) met every target.
Milestone 3 (the steps) run 12 November: 1× met every target, 3× crossed the SLO-lag line once (one sample, 64 s against 60 s; cause not measured), so the driver stopped there; 5× and 10× moved to the Phase 11 baseline. Part of the [Phase 10 plan](phase-10-plan.md) (P3–P8, D1–D2). Raw data of every
run stays in `build/scale/<UTC time>-<multiplier>x/`.
Phase 11 milestone 5 (the fixes) done 16 November: the final rerun (below) held every line through 5×, then two review rounds
and a confirmation round (28 findings, 20 fixed, the rest kept with their reasons in the tracker).

## The harness (milestone 2)

Everything is under `deploy/local/scale/` and runs on this machine (P5) against a stack of its own (P8). The plan named `load.sh` for
this; `tests/load.sh` stays as it was (one resource per signal, the Phase 4 reference), and the scale load is `scale/generate.mjs`.

| Part | What it does |
|---|---|
| `stack.sh up \| down \| destroy` | The local stack as compose project `juice-scale`: own volumes and network, the scale rules, SLOs and a webhook sink (`compose.scale.yml`). It uses the working stack's host ports, so it refuses to start while that one runs. `destroy` removes its volumes, so the next `up` starts from empty |
| `generate.mjs` | The load: a 10-service shop, 10 instances each on 20 hosts, OTLP protobuf with gzip through the main Collector into the deployment's tenant, at a multiple of 1,000 spans/s, 10,000 metric points/s and 1,000 logs/s (below) |
| `config.mjs` | Writes the stack's 50 rules, 50 SLOs and webhook (`rules.json`, `slos.json`, `webhooks.json`) |
| `query-mix.mjs` | The query mix (P4), the same at every step |
| `sample.sh` | Every 10 s: CPU and memory per container, the platform's lags and backlogs, the Collector's counters; storage every 5 minutes |
| `run.sh <multiplier> [seconds]` | One step: 3 minutes of warm-up, then a measured window of exactly `seconds` (1,800 by default) with the generator, query mix, samplers, ingestion-freshness probe and breach-to-alert runs; the load runs on until the last breach run is over, then the Collector drains; `report.md` at the end. Ctrl+C stops every part and ends the run |
| `report.mjs` | The report of one step: each measure against its target, throughput per signal, lags, resources, disk per day and at full retention. Exits 1 if a target is missed |

### The load

- **Shape.** gateway → catalog, cart, checkout, users, search; checkout → payments, inventory, shipping and a Kafka topic that
  notifications consumes; catalog and cart → inventory, catalog → search. Every service but gateway and notifications has a database
  (PostgreSQL, Redis or Elasticsearch). So discovery finds 10 services, 100 instances, 20 hosts, 12 service dependencies, 8
  databases and a topic.
- **Spans** are client → server pairs across services with their database and messaging spans; **one log record per span**, on the
  span's instance, with its trace and span IDs. Spans and logs grow with the multiplier as more requests.
- **Metrics:** every instance exports 996 points per cycle, every host 20: HTTP server, client and database histograms, runtime
  gauges and sums, and feature-flag counters (most of the points, so 98 % land in `otel_metrics_sum`). 100,000 points a cycle; the
  cycle is 10 s at 1× and 10 s ÷ multiplier above it. Cardinality stays the same at every step; the rate grows.
- **By the clock:** spans are made as the time since the start calls for (a slow tick is made up; more than 2 s behind, the rest is
  given up and counted as skipped, which fails the step); every instance and host exports on its own schedule, offset across the
  cycle; each worker flushes once a second at its own offset, as SDK batch timers aren't aligned.
- **Failures on a 15-minute cycle:** payments fails 15 % of requests in minutes 3–8 (error-rate alerts fire, then resolve), shipping
  is slow in minutes 9–14 (latency alerts), inventory-7 always fails 2 % (below every threshold). So rules have transitions and the
  webhook has deliveries to time at every step.
- **Protobuf, not JSON:** the Collector parses what SDKs send. The encoder is `otlp.mjs` (opentelemetry-proto v1 field numbers, checked
  field by field in review). A 40-second run into the scratch tenant on 12 November stored every span, log and point sent, with 100
  instances, 20 hosts, the 12 edges and the histogram buckets.

### The query mix (P4)

As `erin` (reads only; not `victor`, whom the breach runs sign in as: Keycloak locks a user out for a minute after two password
sign-ins less than a second apart): API reads at 20/s, open loop (applications, resources, health, alerts, firing alerts, topology, rules, SLOs, an
SLO, unhealthy services, a service, its health); a trace search (a service) and a log search (`failed`) over the last hour, one each a
second; and once a minute a signed-in browser opens Overview, Services, a service, Alerts, SLOs and a service's Traces. A page fails if
it doesn't load in 30 s, throws a page error or gets an API answer of 400 or more. Rule breach-to-alert runs alongside
(`tests/breach-to-alert.sh`, one run after another while one can still finish in the window). The token is renewed on a timer, so no
timed read waits for Keycloak.

### How each measure is taken

The platform exports no metrics of its own (its instruments are in-process only), so lags are read from its database. A measure with
no data, or too little, is a miss, never a pass. Every database, ClickHouse and `docker stats` call gives up after 20 s, so a stalled
stack leaves a missing sample rather than a hung run.

| Measure | Target (plan) | Source |
|---|---|---|
| Throughput | Nothing refused or dropped | The generator's sent counts in the window, at least 98 % of nominal for each signal; the shop's rows in ClickHouse in the window, at least 98 %, counted after the Collector's queue has drained; the main Collector's refused, export-failed and enqueue-failed counters from a snapshot before the warm-up to one after the drain (summing increases, so a restart can't hide them), and its accepted counters present for 90 % of the window; and the generator itself: no failed, refused or skipped sends, no crashed worker, exit 0. Spans the generator gave up while few sends were in progress mark the step a harness limit (repeat it with more workers), not a platform result |
| Ingestion freshness | p95 ≤ 30 s | A log record every 10 s, timed until ClickHouse returns it over HTTP; 60 s without it counts as 60 s |
| API reads | p95 ≤ 500 ms | `query-mix.mjs`; a read with no answer or an error answer counts as slower than any target |
| Health freshness | p95 ≤ 60 s | Every 10 s, the p95 over live subjects (not Offline or Unknown) of now − `last_evidence_at`: how old the evidence behind what's visible is, so a stalled health worker shows |
| Rule breach-to-alert | p95 ≤ 2 min after `ForDuration` | `tests/breach-to-alert.sh` |
| Webhook dispatch | p95 ≤ 30 s | `first_attempt_at − created_at` (the transition's commit) for deliveries planned in the window up to 30 s before its end; one never attempted, and a transition that notifies (to Firing, or from it) whose notifications aren't planned yet, count until the window's end |
| Queue lag | ≤ 1 min past each worker's configured delay (owner, 12 November); no growing backlog | Discovery watermark behind now (configured 60 s); oldest last evaluation of an open alert (due every 60 s); the oldest SLO's `counted_through` (its creation, if not yet counted) past the moment its next minute became countable (the minute's end + 10 s, `SloCounter.Range`); the oldest outbox row still to be delivered (not yet tried, in progress, or failed with a retry due); the oldest pending webhook delivery; and no outbox delivery created in the window given up for good. Lag samples are needed in 90 % of the window's 10 s slots. "Growing": the last full 15-minute load cycle averages more than the first by more than a noise floor (a cycle short of samples counts as growing), so only steps of 30 minutes or more judge it. Failed outbox rows waiting for a retry, retrying deliveries (backoff by design) and the Collector's export queue are judged on growth only; the oldest live health evaluation is shown only (health has its own target) |
| Telemetry search | reported | `query-mix.mjs` |
| Resource use | reported | `docker stats` every 10 s; the generator's own CPU (`process.cpuUsage`); ClickHouse bytes and rows per table (`system.parts`) and the PostgreSQL size every 5 minutes |
| Cost (P6) | resources, no prices | Disk growth per table scaled to a day, and to 30 days (traces, logs) and 90 days (metrics) |

At 1× each target is met or missed (P3); above 1× each line is within or over, and the first crossed is the limit.

**Host:** 24 cores, 64 GB; Docker Desktop gives its VM 24 vCPU and 31 GiB, so results are stated for that VM. The generator runs on
the host, outside the VM, and shares its CPUs.

## Review round 1, 12 November

The OTLP encoding, the service keys and attributes discovery reads, SLO operations and the compose override were checked against
opentelemetry-proto v1 and the platform's code: correct. What was fixed:

| # | Finding | Fixed |
|---|---|---|
| 1 | A missing percentile compared as `null <= 60`, true in JavaScript: a stopped health worker or an API that never answered read **met** | A measure with no data is missed |
| 2 | A crashed generator, query mix, webhook planner or breach script read "reported", and the step exited 0 | Missing data is missed; each part's exit status is recorded and a failed one fails the step |
| 3 | Nothing checked the offered load reached nominal; the generator dropped span shortfalls each second, rounded the metric cycle (−17 % at 40×), and a crashed worker exited 0 | Sent and stored ≥ 98 % of nominal per signal is part of the throughput target; spans and metrics are paced by the clock; crashed workers fail the run |
| 4 | Collector failure counters are absent until non-zero, and reset on a restart | Counted from a snapshot before the load, summing increases |
| 5 | Webhook p95 skipped deliveries never attempted | They count until the window's end |
| 6 | Failed reads left the API percentile; browser errors weren't checked; token renewal ran inside timed reads | Failed reads count as slower than any target; a page with a browser error fails; the token is renewed on a timer |
| 7 | The lag lines weren't the plan's; searches covered 15 minutes, not the plan's hour | The owner's reading of "1 min" recorded in the plan and applied; searches cover the last hour |
| 8 | "Growing" compared quarters across the 15-minute failure cycle, so a burst in the first quarter hid growth | Compares the first and last full cycles; not judged on shorter windows |
| 9 | Health samples ignored stalls over 10 minutes or between evaluations, and counted non-live subjects | Health freshness is now − evidence time over live subjects; staleness over live subjects without a time filter |
| 10 | The window ended when the slowest part finished, not when the load did | The window is exactly the step's length; every part has that deadline; the load runs until the last breach run is over |
| 11 | No cleanup on interrupt; a response cut off after its headers could hang a worker | A trap stops every part; a send settles on its answer, an error, or the connection closing |
| 12 | `meta.json` called uncommitted code a clean commit | Any change in the repository marks the commit `+changes` |
| 13 | All workers flushed on the same second boundary | Each at its own offset |
| 14 | The freshness probe polled through `docker exec` (about 1 s each) and timed out after 300 polls, not 60 s | Polls ClickHouse over HTTP, with a 60 s deadline |
| 15 | The generator's pending count only ever grew | It's the current level |
| 16 | The docs said every service has a database, and cited a check with no record | Corrected above |

## Review round 2, 12 November

Round 1's fixes held, except where below.

| # | Finding | Fixed |
|---|---|---|
| 1 | A fast error answer (a 401 after a failed token renewal, a 503) counted as a fast read | It counts as slower than any target, as no answer does |
| 2 | Outbox lag counted only rows never attempted (`processed_on` is set on every attempt); webhook dispatch left out transitions not yet planned | Pending is "not published or skipped", retries included, failed rows shown apart; unplanned transitions count until the window's end |
| 3 | SLO lag used discovery's watermark, a minute early, and moved in minute steps, so a real two-minute excess read 60 s | Measured past the next minute's countable moment (its end + 10 s), continuously |
| 4 | Missing samples (a stalled PostgreSQL, an unreachable Collector port) were judged as if complete | 90 % of slots needed for lags and Collector counters, and per cycle for growth; short is a miss |
| 5 | Ctrl+C ran the trap and carried on; a kill didn't reach a breach run in progress | An interrupt ends the run; the breach loop passes the stop on |
| 6 | Stored rows and Collector counters were read before the pipeline drained (a false miss on stored, a false pass on late export failures) | The run waits for the export queue to empty and the stored rows to settle (6 minutes at most), then takes the final counters |
| 7 | `pg`, `ch` and `docker stats` had no timeouts | 20 s each |
| 8 | Spans the generator gave up for its own slowness read as a platform miss; workers capped at 12; a stalled metric schedule caught up in one burst; histogram observations grew with the multiplier squared | Told apart (backpressure or harness limit, which invalidates the step); workers up to the host's cores less 4; a schedule a cycle behind skips ahead and counts it; observations grow linearly |
| 9 | Health staleness was judged as a queue lag, which the plan doesn't list | Shown only; health freshness is its target |
| 10 | Docs claims (failed reads, SLO watermark, outbox, counter window, deadlines) | Corrected above |

## Confirmation round, 12 November

Not confirmed: one blocking issue and two medium ones, all fixed. A second confirmation round found nothing blocking. Its one note: on a
stack just made with `stack.sh destroy` and `up`, each new SLO first counts 30 days in about five 30-second passes, which fits the
3-minute warm-up only just; give the first step after a `destroy` `WARMUP=300`.

| # | Finding | Fixed |
|---|---|---|
| 1 | **Blocking:** webhook dispatch counted every unplanned transition as waiting, but only one to or from Firing notifies: Pending ones never get planned, so the target would miss on every run | Only transitions that notify count |
| 2 | A delivery given up for good (Failed, no next attempt) counted as pending forever, failing queue lag on every later step | Pending is "still to be delivered"; one created in the window and given up is counted apart and misses queue lag (a dropped notification) |
| 3 | With no counted SLO, `greatest(0, NULL)` read 0, a pass | Empty stays empty (a miss); an SLO never counted ages from its creation |
| 4 | Health growth still judged; accepted counted past the window; a short cycle of queue samples read as flat; two timed-out queries could end the drain early | Health shown only; accepted in the window only; a short cycle is growing; an empty answer never counts as settled |

## 1× smoke run, first attempt (superseded)

The first 10-minute smoke run (`build/scale/20261112T0945-1x/`) met every target as the report then judged them. Review round 1 showed
that report could pass a step with missing data (above), so its verdicts don't count; the run is repeated on the fixed harness below.
Its raw figures were in line with Phase 4's: freshness p95 1.50 s, API p95 14 ms, breach-to-alert 20 s and 63 s, webhook first
dispatch p95 1.89 s, nothing lost.

## 1× smoke run on the round-1 harness (superseded)

Judged by the report as it stood after review round 1, whose gaps round 2 found; kept for its figures. `build/scale/20261112T1038-1x/report.md`: 10 minutes measured after 3 of warm-up, every part exited 0. A run between this and the
first (`20261112T1023-1x`) was judged a miss by the fixed report, as it should have been: the query mix couldn't sign in (Keycloak's
quick-login lockout, above), so API reads had no data. Against Phase 4 ([MVP acceptance](mvp-acceptance.md): one service and one
resource per signal, 7 rules):

| Measure | Target | Phase 4 | 1× smoke | |
|---|---|---|---|---|
| Throughput | nothing refused or dropped | 0 lost | sent 100.4 % spans, 100.1 % points, 100.4 % logs of nominal; stored 100 % of each; 0 refused or failed in the Collector or the generator | met |
| Ingestion freshness | p95 ≤ 30 s | p95 1.39 s | p95 1.46 s, max 1.60 s (54 probes) | met |
| API reads at 20/s | p95 ≤ 500 ms | p95 6.8 ms | p95 43 ms, max 308 ms, 0 of 11,976 failed | met |
| Health freshness | p95 ≤ 60 s | 10–11 s (at evaluation) | evidence behind the visible state p95 44 s; at evaluation p95 12.4 s | met |
| Rule breach-to-alert | p95 ≤ 2 min after `ForDuration` | p95 59 s (3 runs) | 34, 59, 63 s | met |
| Webhook first dispatch | p95 ≤ 30 s | bound 2 s + send | p95 1.11 s (18 deliveries, all delivered) | met |
| Queue lag | ≤ 1 min past each configured delay | not measured | discovery 34 s past its 60 s, health 4 s past its 30 s, rules on time, SLOs one minute step, outbox and webhooks empty; growth not judged (10 minutes) | met |
| Telemetry search, last hour | reported | – | p95 542 ms, max 1.9 s (1,198 searches) | |
| Console journey | reported | – | page p95 898 ms, 0 of 54 pages failed | |

Phase 4's figures hold under the heavier load; health freshness is now measured as the age of what's visible (Phase 4 measured the
age at evaluation, 12.4 s here).

**Resources at 1×** (24 vCPU VM): ClickHouse 4.0 vCPU mean (21.5 peak), 2.9 GiB; Collector 0.26 vCPU; platform API 0.22 vCPU,
228 MiB; PostgreSQL 0.14 vCPU. The generator used 0.5 % of the host's cores. ClickHouse used 1.2 vCPU in the first attempt, whose
searches covered 15 minutes: the plan's hour-long trace and log searches, two a second, are most of its work.

**Disk, first look (P6):** about 16 GB a day, 948 GB at full retention, 90 days of metrics most of it (864 M points a day). A
10-minute figure before ClickHouse's later merges, so an upper bound; milestone 3's 30-minute steps give the figures for the cost table.

## 1× smoke run on the final harness, 12 November (milestone 2 evidence)

`build/scale/20261112T1116-1x/report.md`: on a stack just reset with `stack.sh destroy`, 10 minutes measured after 3 of warm-up;
every part exited 0; the Collector drained in 12 s after the load. Against Phase 4 ([MVP acceptance](mvp-acceptance.md)):

| Measure | Target | Phase 4 | 1× smoke | |
|---|---|---|---|---|
| Throughput | nothing refused or dropped | 0 lost | sent 100.1 % spans, 100 % points, 100.1 % logs of nominal; stored 100 % of each; 0 refused or failed in the Collector or the generator | met |
| Ingestion freshness | p95 ≤ 30 s | p95 1.39 s | p95 1.62 s, max 1.76 s (54 probes) | met |
| API reads at 20/s | p95 ≤ 500 ms | p95 6.8 ms | p95 17 ms, max 150 ms, 0 of 11,964 failed | met |
| Health freshness | p95 ≤ 60 s | 10–11 s (at evaluation) | evidence behind the visible state p95 35.8 s; at evaluation p95 12.6 s | met |
| Rule breach-to-alert | p95 ≤ 2 min after `ForDuration` | p95 59 s (3 runs) | 35, 59, 57 s | met |
| Webhook first dispatch | p95 ≤ 30 s | bound 2 s + send | p95 1.99 s (59 deliveries, all delivered; none waiting to be planned) | met |
| Queue lag | ≤ 1 min past each configured delay | not measured | discovery 24 s past its 60 s; rules on time; SLOs 8 s past their countable minute; outbox and webhooks empty, nothing given up; growth not judged (10 minutes) | met |
| Telemetry search, last hour | reported | – | p95 167 ms, max 1.1 s (1,197 searches) | |
| Console journey | reported | – | page p95 778 ms, 0 of 54 pages failed | |

The Phase 4 figures hold under 100 instances, 50 rules, 50 SLOs and the query mix. The step began on time after the reset: the SLOs
were 8 s past at most, so the 3-minute warm-up was enough this time.

**Resources at 1×** (24 vCPU VM): ClickHouse 1.4 vCPU mean (4.3 peak), 2.4 GiB; Collector 0.21 vCPU; platform API 0.17 vCPU, 238 MiB;
PostgreSQL 0.07 vCPU. The generator used 0.4 % of the host's cores.

**Search cost grows with the data in the hour.** Here the last hour held 13 minutes of data (a fresh stack); in the round-1 run it held
about 40 minutes, and ClickHouse used 4.0 vCPU with search p95 542 ms. A 30-minute step after its warm-up searches a full hour, so
milestone 3 will see the second figure or more, and searches are the likeliest first limit.

**Disk, first look (P6):** about 16 GB a day, about 1 TB at full retention, 90 days of metrics most of it (864 M points a day). A
10-minute figure before ClickHouse's later merges, so an upper bound; milestone 3's 30-minute steps give the figures for the cost table.


## Milestone 3: the scale steps, 12 November

`deploy/local/scale/steps.sh --restore` ran the steps back to back on one scale stack:
- 30 minutes measured after 3 minutes of warm-up each;
- every part exited 0;
- the Collector drained in 11–12 s after each load.

The driver stopped after the first step that crossed a line: **3× crossed one, so 5× and 10× did not run.** The plan (P3, milestone 3)
asks for 1×, 3×, 5× and 10× and a resource table at each step, so this is short of the deliverable. The missing steps moved to Phase 11 (below).
- **Reports:** `build/scale/20261112T1147-1x/report.md` and `build/scale/20261112T1221-3x/report.md`.
- **Driver log:** `build/scale/steps-20261112T1147.log`.

That log is from the first version of `steps.sh`. Review then changed the script:
- a clean-up trap that also stops a running step;
- harness problems told apart from platform results;
- `--through`;
- exit codes.

The changed script was checked against stub stacks, not rerun on the real one.

**The steps didn't start on empty storage.** The stack wasn't reset with `stack.sh destroy` first:
- 1× ran on volumes that still held the 11:16 smoke run;
- 3× followed 1×, so its last-hour searches covered partly 1×-rate data (3×-rate rows outnumbered them from about 12:32).

The 3× search and ClickHouse figures are therefore lower than a 3× steady state would give. At 1× the milestone 2 expectation
(ClickHouse 4.0 vCPU and search p95 542 ms, once the hour fills) didn't hold: 2.65 vCPU and 332 ms, with about 20 minutes of data in
the searched hour at the window's start and about 45 at its end (search means flat at 98–126 ms).

| Measure | Target (line above 1×) | 1× | 1× verdict | 3× | 3× verdict |
|---|---|---|---|---|---|
| Throughput | nothing refused or dropped | sent 100.1 / 100 / 100.1 %, stored 100 % of each, 0 lost | met | sent 100.1 / 100 / 100.1 %, stored 100 % of each, 0 lost | within |
| Ingestion freshness | p95 ≤ 30 s | p95 1.63 s (160 probes) | met | p95 2.27 s (156 probes) | within |
| API reads at 20/s | p95 ≤ 500 ms | p95 20.8 ms, 0 of 35,966 failed | met | p95 53.4 ms, 0 of 35,975 failed | within |
| Health freshness | p95 ≤ 60 s | p95 40.1 s | met | p95 47.4 s | within |
| Rule breach-to-alert | p95 ≤ 2 min after `ForDuration` | p95 66 s (9 runs) | met | p95 62 s (9 runs) | within |
| Webhook first dispatch | p95 ≤ 30 s | p95 1.93 s (37, all delivered) | met | p95 1.91 s (48, all delivered) | within |
| Queue lag | ≤ 1 min past each configured delay, no growing backlog | discovery 32 s past, rules 0.2 s, SLOs 33 s; nothing growing, nothing given up | met | discovery 40 s, rules 25 s, **SLOs 64 s**; nothing growing, nothing given up | **over** |
| Telemetry search, last hour | reported | p95 332 ms, max 1.6 s (3,597, none failed) | – | p95 2.78 s, max 8.3 s (3,598, none failed) | – |
| Console journey | reported | page p95 850 ms (174 pages, none failed) | – | page p95 1.69 s (174 pages, none failed) | – |

**1× meets every §9 target** over 30 minutes and two full 15-minute failure cycles. So P7 doesn't apply, and nothing new is added.

### The first line crossed at 3×: the SLO lag, once

**What crossed.** At 12:48:13 the oldest SLO was 64 s past the moment its next minute became countable, against a 60 s line. That
was 1 of 177 samples in the window: the samples either side were 52.9 s (12:48:02) and 12.0 s (12:48:22), so the lag was over the
line for under 20 s. No SLO pass was seen for about 60 s: the lag climbed steadily from 1.5 s at 12:47:11.

**3× is slower than 1× all through the window, not only at that sample.**
- Samples over 30 s: 47 of 177 at 3×, against 12 of 179 at 1× (whose highest was 33 s).
- The SLO lag p95: 41.9 s at 3×, against 30.4 s at 1×.
- Each minute's highest sample crept from 35 s to 44 s by 12:41. It fell to 36 s, was 47–50 s over 12:44–12:46 and 42 s at 12:47, then hit the 64 s.
- After that it fell back: 47, 39, 30, 32 and 42 s.

The report's growth test (last 15-minute cycle against the first) found no growing backlog.

**The rules worker ran late at the same time, but didn't stop.**
- Between its passes (due every 60 s) the oldest open alert's last evaluation always climbs 10 s per 10 s sample. In most minutes its
  per-minute highest was 55–62 s.
- Around the crossing it went 31 → 42 → 50 → 61 → 72 s (12:47:32–12:48:13), then 20.8 s at 12:48:22. So the pass due at 12:48:01
  started on time and finished 12–21 s late.
- It also ran late at 12:37 (81 s) and 12:42 (85 s) with no SLO stall (the SLO lag's highest in those minutes: 41 and 36 s).
- Its line allows 60 s past its due time, so it stayed within.

**The cause isn't measured.** The platform exports no metrics of its own, so pass durations, ClickHouse query times and the process's
own state weren't recorded. Two explanations fit:
- **ClickHouse query contention.** Every worker and every Console search reads ClickHouse as the same user, `platform_reader`, with
  no priority between them.
  - ClickHouse went from about 5 vCPU to 8–11 vCPU from about 12:32, with dips to 7–8 at 12:43–12:45 and 12:50, and a peak of
    21.5 of the VM's 24 vCPU.
  - Last-hour searches ran 8 times slower than at 1× (p95 2.78 s against 0.33 s). By clock 10-minute bucket (the first and last
    partial), the search mean was 439, 617, 822 and 559 ms.
  - **For:**
    - 12:47 had the highest full-minute ClickHouse mean of the window, 11.4 vCPU.
    - The window's two slowest searches (8.35 s at 12:46:44, 8.08 s at 12:47:44) came as the SLO stall began.
  - **Against:** at 12:48:09 ClickHouse used 10.8 vCPU, not a peak; the lag fell in the last minutes while ClickHouse stayed at 8–11; and searches of 4–8 s came in most minutes, mostly without a late pass.
- **A stall in the platform process.** Both workers run in it, along with the API, on a shared connection pool and runtime.
  **Against:** the API's reads, served by the same process, stayed fast throughout (10 s means of 19–39 ms, a maximum of 285 ms over
  12:47–12:48:30).

On this evidence ClickHouse contention is the likelier, but it isn't measured.

So the first limit is **the SLO worker's lag, with the cause not yet measured**. [Phase 11](phase-11-plan.md) measures it with the
platform's own metrics and reruns the steps.

**Margins at 3×:**

| Area | At 3× | Line or bound | At 1× | Left at 3× |
|---|---|---|---|---|
| Health freshness | p95 47.4 s | 60 s | 40.1 s | 12.6 s |
| Discovery | 40.2 s past its delay | 60 s | 31.7 s | 19.8 s |
| Rules | 24.8 s past due | 60 s | 0.2 s | 35.2 s |
| Collector export queues | max 88,391 items, median 15,984 (three exporters summed) | 200,000 per exporter; beyond that data is dropped, a throughput loss | max 11,047, median 6,045 | not judged, growth only |
| Collector CPU | 0.95 vCPU | – | 0.20 vCPU | – |
| Ingestion freshness, API reads, webhooks, outbox | p95 2.27 s, 53 ms, 1.91 s, empty | 30 s, 500 ms, 30 s | 1.63 s, 21 ms, 1.93 s | wide |

- **Health freshness** had the narrowest relative margin.
- **The Collector's queues and CPU** grew faster than the load: queue max 8×, CPU 4.7×.

**Possible fixes, not made in Phase 10.** Results above 1× only inform (P7). None of these needs new infrastructure.
- Give the workers' ClickHouse queries their own user and settings profile, with priority over interactive searches.
- Bound searches under load.
- Export the platform's own meters, so a pass and its queries can be timed.

### Resources and disk (P6), for the steps run

Docker VM 24 vCPU, 31.1 GiB. CPU is in vCPU: the window's mean, then its peak. Memory is the window's maximum. The harness's own
containers (`webhook-sink`, `clickhouse-access`) are left out.

| | 1× | 3× |
|---|---|---|
| ClickHouse | 2.65 vCPU, peak 18.7; 2.9 GiB | 8.45 vCPU, peak 21.5; 5.5 GiB |
| OpenTelemetry Collector | 0.20 vCPU, peak 0.48; 121 MiB | 0.95 vCPU, peak 1.99; 246 MiB |
| Platform API (all workers) | 0.16 vCPU, peak 0.49; 228 MiB | 0.29 vCPU, peak 0.58; 211 MiB |
| PostgreSQL | 0.08 vCPU, peak 0.21; 67 MiB | 0.15 vCPU, peak 0.40; 79 MiB |
| Keycloak | 0.01 vCPU, peak 0.14; 1.0 GiB | 0.02 vCPU, peak 0.21; 1.2 GiB |
| **Headroom on the 24 vCPU / 31.1 GiB VM** | 20.9 vCPU mean, 4.8 at ClickHouse's peak; 27 GiB | 14.1 vCPU mean, 1.1 at ClickHouse's peak; 24 GiB |
| **Table data per day:** traces (with the trace-ID index) | 4.5 GB | 13.3 GB |
| logs | 4.2 GB | 12.6 GB |
| metrics | 8.1 GB | 24.9 GB |
| PostgreSQL | 0.07 GB | 0.06 GB |
| **Table data at full retention** (30 d traces and logs, 90 d metrics) | **987 GB** | **3,019 GB** |

- **The headroom row** is the VM's 24 vCPU and 31.1 GiB, less every container's mean CPU (or ClickHouse's peak) and maximum memory.
- **The disk figures are a table-data estimate, not a disk size.**
  - They take 30 minutes of growth in the six otel tables' active parts, scaled to a day. That's before ClickHouse's later merges,
    so the data itself is likely overstated.
  - They leave out:
    - ClickHouse's system log tables;
    - the free space merges need;
    - PostgreSQL's growth (it has no retention, and 0.06–0.07 GB a day from 30 minutes is noisy);
    - backups.
  - Size a disk well above them.
- **Table data scaled with load,** 3.06× for 3×, though that's only two points. Metrics are about three quarters of it at full
  retention: the sum table alone is 700 GB at 1×, 846 M points a day kept for 90 days. Keeping metrics 30 days instead of 90 brings the
  1× estimate to about 500 GB.
- **CPU is ClickHouse.** At 1× the rest of the stack used under half a vCPU together.
- **The harness on the host:** the generator process used 0.3 % of the host's 24 cores at 1× and 1.6 % at 3×, with no harness limit
  at either step. The other harness parts weren't measured: the query mix, the Chromium journey, the samplers and the breach tests.

### 5× and 10×: moved to Phase 11

The plan's milestone 3 lists them. The owner moved them to the [Phase 11](phase-11-plan.md) baseline on 13 November (R11). That run
happens once the platform exports its own metrics, so each crossed line has a measured cause. It goes on a fresh scale stack
(`stack.sh destroy`) with `WARMUP=300 scale/steps.sh --through 1 3 5 10` (the first step after a reset needs the longer warm-up,
see the confirmation round above). Phase 10's milestone 3 closes on 1× and 3×.

## Phase 11 baseline: 1×, 3×, 5×, 10×, 13 November

Phase 11 milestone 3 ([plan](phase-11-plan.md) R14, revision 3), on a fresh scale stack:
- **Run:** `stack.sh destroy`, then `WARMUP=300 scale/steps.sh --through 1 3 5 10`, commit 2da8d59 (clean), 5 minutes of warm-up and 30 measured per step.
- **Host:** 24 cores; Docker VM with 24 vCPU and 31 GiB.
- **Reports:** `build/scale/20261113T0732-1x`, `…0807-3x`, `…0843-5x`, `…0919-10x` (`report.md`).
- **Per-component figures:** `components.json` in each step's directory, and `build/scale/components-p11-baseline.md`. They come from
  `scale/components.mjs`, which reads the scale stack's ClickHouse after the steps:
  - `system.query_log` by user and by caller (`log_comment`), CPU included;
  - `system.part_log`;
  - `system.asynchronous_metric_log`;
  - the platform's own metrics under the reserved tenant.

| Measure | Line | 1× | 3× | 5× | 10× |
|---|---|---|---|---|---|
| Throughput (stored, spans / points / logs) | ≥ 98 % each, nothing lost | 100 / 100 / 100 %, 0 lost | 100 / 100 / 100 %, 0 lost | 100 / **77.9** / 100 %, **20.1 M points refused** | 99 / **17.1** / 100 %, **164 M points refused** |
| Ingestion freshness p95 | ≤ 30 s | 1.45 s | 1.96 s | 3.28 s | 15.2 s |
| API reads p95 (20/s) | ≤ 500 ms | 24.8 ms | 54 ms | 89.7 ms | 86.6 ms |
| Health freshness p95 | ≤ 60 s | 39.2 s | 47.1 s | **65 s** | **265 s** |
| Rule breach-to-alert p95 | ≤ 2 min after `ForDuration` | 62 s | 70 s | 65 s | 119 s |
| Webhook first dispatch p95 | ≤ 30 s | 1.92 s | 2 s | 2 s | 1.67 s |
| Queue lag, past each configured delay | ≤ 60 s, nothing growing | within | within | **discovery 148 s, rules 136 s, SLOs 248 s (growing); Collector queue growing** | **discovery 1,264 s, rules 527 s, SLOs 1,268 s** |
| Telemetry search p95 (reported) | – | 292 ms | 2.6 s | 10.6 s | 46.4 s, 73 of 3,595 failed |
| Console page p95 (reported) | – | 696 ms | 1.26 s | 5.8 s | 30 s, 10 of 174 failed |

- **1× meets every §9 target. Every line holds at 3×,** this time: Phase 10's single SLO-lag crossing didn't recur.
- **5× is the first step that crosses,** on throughput, health freshness and queue lag. At 10× everything that reads ClickHouse is
  minutes behind.

### Per component

CPU is in cores, from docker stats unless marked. ClickHouse's share is taken from `system.query_log`. A query counts whole in the
window it ended in, and failed queries are included: a timeout still spent its CPU.

| | 1× | 3× | 5× | 10× |
|---|---|---|---|---|
| **Docker VM CPU busy, user + system (24 vCPU, from ClickHouse's `/proc` view)** | 4.6 | 11.8 | 19.2 | 21.6 |
| Docker VM load average | 4.8 | 27.7 | 66.8 | 127.7 |
| **ClickHouse container CPU, mean** | 2.5 | 8.3 | 15.1 | 16.0 |
| …the platform's reads (`platform_reader`) | 1.90 | 6.98 | 11.23 | 12.34 |
| …inserts (`otel_writer`, async flushes included) | 0.09 | 0.34 | 0.71 | 0.62 |
| …not in `query_log` (background work, presumably mostly merges, which belong to ingest; the remainder of two methods) | 0.5 | 1.0 | 3.1 | 3.0 |
| Rows written per second | 12,200 | 36,500 | 49,600 | 38,500 |
| Insert p50, the Collector's wait (async insert) | 206 ms | 155 ms | 302 ms | 695 ms |
| Async insert flush p95 | 36 ms | 169 ms | 557 ms | 1,212 ms |
| Rows read per second by the platform | 3.2 M | 12.1 M | 20.8 M | 32.1 M |
| Most parts in one partition | 15 | 31 | 53 | 56 |
| **Collector CPU, mean** | 0.2 | 0.8 | 2.3 | 3.8 |
| Collector export queues, max (sum of 3 exporters, bound 3 × 200,000) | 10,056 | 161,850 | 238,354 | 595,653 |
| **PostgreSQL CPU, mean** | 0.09 | 0.13 | 0.24 | 0.24 |
| PostgreSQL commands per second (platform, client side), mean time | 133, 0.9 ms | 132, 1.8 ms | 127, 3.9 ms | 118, 4.1 ms |
| **Platform API CPU, mean** | 0.18 | 0.27 | 0.46 | 0.46 |

The platform's reads by caller: CPU in cores, the mean rows each query read, and the query count where it fell:

| Caller | 1× | 3× | 5× | 10× |
|---|---|---|---|---|
| discovery | 0.53 (4.5 M rows) | 1.86 (14.5 M) | 2.57 (18.4 M) | 0.81 (23.8 M; 41 queries, most cut at 60 s) |
| SLO counting | 0.52 (0.4 M) | 2.01 (1.2 M) | 1.56 (1.6 M; 1,927 queries) | 0.26 (1.6 M; 269 queries) |
| health | 0.30 (2.4 M) | 0.98 (7.2 M) | 1.25 (8.2 M) | 0.59 (6.9 M; 99 queries) |
| trace search | 0.23 (0.4 M) | 1.25 (1.5 M) | 4.08 (3.4 M) | 7.60 (6.4 M) |
| log search | 0.25 (1.2 M) | 0.68 (5.7 M) | 1.33 (12.6 M) | 2.41 (23.8 M) |
| rules | 0.07 (0.1 M) | 0.20 (0.3 M) | 0.41 (0.6 M) | 0.63 (1.0 M) |

The searches run at the same rate at every step, about 1,800 of each in the window. The workers' queries fall as their passes slow.

Each worker's mean pass time against its interval, in seconds, from the platform's own histogram (the SDK's max covers the
process's life, so it isn't given):

| Worker (interval) | 1× | 3× | 5× | 10× |
|---|---|---|---|---|
| discovery (30) | 1.8 | 7.8 | 22.5 | 64.7 (27 passes) |
| health (30) | 1.5 | 5.7 | 14.0 | 44.6 (38 passes) |
| SLO counting (30) | 5.8 | 16.3 | 53.6 (32 passes) | 390 (4 passes) |

### The cause of the first limit: what is measured, and what is a hypothesis

**Measured: the reads use most of ClickHouse, and their cost grows with the load.**
- **The host's CPU is near full from 5× on.** The Docker VM is busy on about 19 of its 24 vCPU at 5× and 22 at 10×. ClickHouse
  holds 15–16 of them. It is not pinned at 24, but it shares a nearly full machine with the Collector and the harness.
- **The platform's reads take 11–12 of ClickHouse's cores at 5× and 10×.** Inserts take under one. The rest, about 3, isn't in `query_log`; background
  merges, which belong to ingest, are the likely part.
- **Every reader scans a window whose row count grows with the load.** Discovery reads 18 M rows a query at 5×, health 8 M, log
  search 13 M and trace search 3.4 M.
- **The workers fall behind their interval.** SLO counting passes its 30 s interval at 5×. Discovery and health are well past theirs
  at 10× (65 s and 45 s against 30 s).

**Measured: ingest loses metric points first.**
- **Inserts slow down.** The Collector's insert wait rises from about 200 ms to 695 ms at 10×, and flushes from 36 ms to 1.2 s.
- **The queues fill.** At 5× the Collector refuses metric points, about four fifths of the rows, while spans and logs still get
  through. At 10× all three exporter queues reach their bound at peak (sum 595,653 of 600,000): 164 M points, 171,413 spans and 4,706 logs are refused.
- **Fewer rows are written at 10× than at 5×:** 38,500 a second against 49,600. The reads' CPU barely changes between the two (11.2
  and 12.3 cores), so what slowed inserts further isn't shown by these figures.
- **Parts are not the limit:** at most 56 parts in one partition, against ClickHouse's default of 300 before it delays inserts.

**Hypothesis, for milestone 4 to test: ingest is capped by the exporter as much as by ClickHouse's CPU.**
- **What caps the exporter.** Each exporter has 4 consumers sending batches of up to 20,000 items (`collector/config.yaml`), and
  each insert waits for ClickHouse's async-insert flush. That wait is about 200 ms even at 1×, where the flush itself takes 7 ms. So
  throughput is bounded by consumers × batch ÷ insert wait, and a slower ClickHouse lowers that bound.
- **CPU starvation and that bound fit the data equally well.**
- **The test:** milestone 4's ingest-only run, with the workers and the query mix off, tells them apart.

**Not near a limit:**
- **The Collector:** 2.3–3.8 cores.
- **The platform API:** under half a core.
- **The generator:** 3.2 % of the host at 5×.
- **PostgreSQL:** a quarter of a core, about 120–130 commands a second.
  - **Its connections:** at most one in use at any sampled moment. At 10× that connection is busy about half of each second
    (118 commands × 4.1 ms).
  - **The ceilings:** it doesn't limit the platform here; the ceilings (R19) check it.

**Still to measure, in milestone 4 (component ceilings, R19):**
- ingest alone;
- the workers alone;
- the searches alone, each against its own line.

**For the fixes (milestone 5):**
- **R12:** the readers' row counts above: discovery, SLO counting, health, and the trace and log searches' scans.
- **R11:** a CPU share for inserts and workers against searches.
- **R12a:** the exporter's consumers, batch and async-insert settings, if milestone 4 confirms the hypothesis.
- **The Collector's overflow:** a refused point is lost, while a slower export only delays it.

**Not met by this baseline:**
- **R10, "every line holds at 3×":** met.
- **R10, the ±25 % capacity check:** needs the final rerun.

**Seen during the run:** trace search answers 500 while ClickHouse is down (milestone 2 drill). It goes with the "busy, try again"
work in R11.

## Phase 11 component ceilings, 13–14 November

Phase 11 milestone 4 ([plan](phase-11-plan.md) R19). Each ceiling runs on the scale stack with one part of the platform under load and
the rest off or held at 1×, raised in steps until a line is crossed:
- **Harness:** `CEILING=ingest|search|workers scale/steps.sh` (commits b3c6720, f74a813 and 124a8ad). The overlay
  `scale/compose.ceiling-<name>.yml` sets which workers run. `report.mjs` judges only the lines of what the ceiling runs and adds its
  own lines: the Collector's export queue not growing, and for searches a p95 of at most 2 s. There is no §9 target for searches, so 2 s is the ceiling's
  line, not a requirement.
- **Host:** as the baseline, 24 cores and a Docker VM with 24 vCPU and 31 GiB, 5 minutes of warm-up and 30 measured per step.
  The `dev-infra` and `juice-notifications` stacks were stopped during the runs to free memory. A first attempt on 13 November was
  stopped by Claude Code for low host memory during the ingest 10× step. Its 5× step had finished and is kept. Its 10× step
  (`20261113T1125-ingest-10x`) is void: steps.sh flagged it as a harness problem (lag samples missing). The 10× step was run again.
- **Reports:** `build/scale/20261113T1050-ingest-5x`, `20261114T0058-ingest-10x`, `20261114T0135-search-2per-s` to
  `…0321-search-16per-s`, and the workers steps below. Each report gives its commit: b3c6720 for the ingest and search steps, the
  ingest 5× clean, the rest `+changes`. The changes were docs only: the tracker for ingest 10× and the searches, which ran before the
  workers mode was written. Then this section as well for the workers steps after f74a813.
- **Per-component figures:** for the search steps, `components.json` in each step and `build/scale/components-p11-ceilings.md`.
  The ingest steps' ClickHouse was removed for the next ceiling before `components.mjs` read it. Their figures come from each
  report (docker stats and the Collector's own counters).

### Ingest: the Collector's metrics exporter, at about 90,000 points a second

Every worker that reads ClickHouse is off (discovery, health, rules, SLO counting), with no query mix and no breach runs. The step
is the generator's multiplier.

| | Baseline 5× (everything on) | Ingest 5× | Ingest 10× |
|---|---|---|---|
| Stored, spans / points / logs | 100 / 77.9 / 100 % | 100 / 100 / 100 %, 0 lost | 100 / **90.3** / 100 %, **20.3 M points refused** |
| Ingestion freshness p95 | 3.28 s | 1.39 s | 0.79 s |
| Metric points the exporter sent, per second | – | 50,024 | **90,315** (offered about 100,000) |
| Collector export queues (sum of 3), median / max (report) | – / 238,354 | 16,436 / 36,567 | **194,456 / 209,476** |
| ClickHouse CPU, mean | 15.1 | 1.2 | 2.3 |
| Collector CPU, mean | 2.3 | 0.8 | 2.1 |

- **5× holds whole without the reads.** Nothing is lost and ClickHouse uses 1.2 cores, against 15 in the baseline at 5×. So the
  baseline's 5× loss came from the reads crowding out inserts, not from the ingest path.
- **10× crosses on metric points only.** Spans and logs are all stored at 10,000 a second each. The metrics exporter sends a
  steady 90,000 points a second, and the queues' sum stays near one exporter's 200,000 bound all the window. Only the sum is
  sampled. Spans and logs refuse nothing, so their queues were under their bound, and the metrics queue, the one refusing, is
  inferred to hold most of it. Past the bound the Collector refuses about a tenth of the points (`block_on_overflow: false`).
- **ClickHouse's CPU is not the limit at 10×:** 2.3 cores of 24, the VM far from full.
- **What bounds the exporter, measured and not:** its rate stays flat while its queue is full and ClickHouse's CPU is mostly idle.
  The likely bound is the exporter's pipeline: 4 consumers, batches of up to 20,000 points, each waiting for its async-insert
  flush on ClickHouse. The consumers and batches are in `collector/config.yaml`. Async insert is the exporter's own default, not
  set there, and shows as `AsyncInsertFlush` in `query_log`. 90,000 points a second is what 4 × 20,000 gives at about 0.9 s an insert. That
  fits, but this run didn't measure the insert wait: `query_log` wasn't read before the stack was removed. So the wait could
  equally be on ClickHouse's side (the flush), and it is the hypothesis for R12a. The baseline's hypothesis ("the exporter bounds
  ingest as much as ClickHouse's CPU") is narrowed: at 5× the limit was the CPU, taken by the reads; at 10× without the reads it
  is in the insert path, not the CPU.
- **The ceiling:** between 5× and 10× for metric points (50,000 held, 90,000 a second the most sent). Spans and logs are above 10×;
  their own ceiling wasn't reached, since the steps stop at the first line crossed.

### Searches: between 8 and 16 a second, at 1× ingest

Health, rules and SLO counting off; discovery on (the searches use the services it finds), ingest at 1×, no breach runs. The
step is searches a second, half traces and half logs, each over the last hour. The 20 API reads a second and the Console journey
run as in the full mix.

| Searches a second | 2 | 4 | 8 | 16 |
|---|---|---|---|---|
| Search p50 / p95 | 54 / 168 ms | 83 / 225 ms | 102 / 614 ms | 2.3 s / **no answer**, **6,028 of 28,785 failed** |
| Trace search p95 / mean rows read | 176 ms / 0.37 M | 239 ms / 0.90 M | 890 ms / 1.20 M | 60.9 s / 1.11 M |
| Log search p95 / mean rows read | 66 ms / 1.24 M | 89 ms / 3.27 M | 373 ms / 3.88 M | 60.1 s / 3.93 M |
| ClickHouse CPU, mean (`platform_reader` from `query_log`) | 1.8 (1.02) | 2.1 (2.40) | 5.8 (6.10) | **18.9 (16.69)** |
| ClickHouse queries running at once, max | 5 | 5 | 8 | **428** |
| ClickHouse memory, max | 2.3 GiB | 2.8 GiB | 3.5 GiB | **22.8 GiB** |
| API reads p95 (20/s, PostgreSQL) | 10.9 ms | 15.3 ms | 30 ms | 67.9 ms |
| Console page p95 | 645 ms | 739 ms | 1.7 s | **30 s, 12 of 174 failed** |
| Ingest | all stored | all stored | all stored | **points 64.8 % stored; 7.6 M refused (points 7.3 M, spans 0.2 M, logs 0.1 M); freshness p95 at the probe's 60 s cap** |
| Discovery: pass mean / watermark past its delay | 1.2 s / within | 1.5 s / within | 2.9 s / within | **21.3 s / 882 s** |

ClickHouse's CPU is docker stats' mean; the reads' share is from `query_log`, which counts a query whole in the window it ended,
so the two can differ by a few tenths (at 4 a second the share is above the mean).

- **8 a second holds, 16 collapses.** At 8 the p95 is 614 ms and ClickHouse uses about 6 cores. At 16 the searches' p50 is 2.3 s
  and most of the slow ones run to ClickHouse's 60 s limit. 5,218 queries time out (code 159), and 145 are refused for too many
  queries at once (202).
- **The collapse looks like a pile-up that then uses up the cores.** At 8 a second a trace search costs about 0.7 CPU-seconds and a
  log search about 0.5. At that cost sixteen a second would want about 10 cores, and 24 were there. But nothing limits how many
  searches run at once: the query mix sends on schedule whether the last ones answered or not, as impatient users would. The
  queries running at once go from 8 to 428 and ClickHouse's memory from 3.5 to 22.8 GiB. A trace search's cost rises to about 1.3
  CPU-seconds, and ClickHouse ends up on 19 cores on average (max 22), with the VM's load average at 105. So the cores did run
  short, but only once the searches had piled up. Unbounded concurrency is the likely trigger, not shown directly.
- **The pile-up reaches ingest and discovery.** At 16 a second inserts wait up to 38 s, the Collector refuses about a third
  of the points the generator offered (7.3 M of 21.6 M), and discovery falls 15 minutes behind. Searches and ingest share ClickHouse with nothing between them: the R11 case.
- **Each search reads a lot for one hour at 1×.** A log search reads 3.9 M rows, a trace search 1.2 M. At 2 and 4 a second the
  rows read are lower because the stack started empty: the last hour wasn't full until about 02:35, in the 4 a second step. So 8
  and 16 a second are the steady-state figures.
- **PostgreSQL-backed API reads stay fast throughout:** 68 ms p95 at 16 a second, with ClickHouse saturated.
- **The ceiling:** between 8 and 16 searches a second at 1× ingest, on this host. The likely cause: unbounded concurrency on a
  scan of a few million rows a search.

### Workers: steady at 100 rules and 100 SLOs; the API crashed on bursts of alerts, at 100 and at 200

Every worker on, ingest at 1×, no query mix, breach runs on. The step is k copies of the scale set's 50 rules and 50 SLOs
(`scale/config.mjs --factor k`; SLOs stop at 200, `SloOptions.MaxSlos`). Each copy has the same query as its original, so it has the same cost.
`run.sh` writes the files and recreates `platform-api` before the step, since rules and SLOs load at startup.

| Copies (rules / SLOs) | 1 (50 / 50) | 2 (100 / 100) | 4 (200 / 200) |
|---|---|---|---|
| Health freshness p95 | 38.9 s | 41.8 s | **`platform-api` down** |
| Rule breach-to-alert p95 | 64 s (9 runs) | 67 s (9 runs) | **4 runs, none fired** |
| Webhook first dispatch p95 | 1.5 s (101) | 1.7 s (194) | **none** |
| SLO lag past its next countable minute, max | 29.9 s | 10.3 s | **1,973 s** |
| Rules: oldest evaluation of an open alert, past due | 0.1 s | 0 s | **1,918 s** |
| ClickHouse CPU, mean | 1.1 | 1.0 | 0.4 (ingest only) |
| PostgreSQL CPU, mean | 0.02 | 0.03 | 0.01 |

- **Runs:** `build/scale/20261114T0403-workers-1x`, `…0542-workers-2x` and `…0618-workers-4x`, commits f74a813 and 124a8ad.
- **The 1× step:** its report marks Throughput missed and flags a harness limit: the generator fell 898 spans (0.04 %) behind its
  schedule, and steps.sh stopped there. Everything was stored and every other line held. It is kept as the reference, with that
  caveat. The next run removed its ClickHouse, so it has no `components.json`.
- **A first 2× attempt** (`…0441-workers-2x`) is void: `platform-api` crashed before its window (below), and nothing then
  recorded why.
- **Per component at 2×** (`build/scale/components-p11-workers.md`):
  - **ClickHouse:** the reads use 1.2 cores (discovery 0.48, SLO counting 0.40, health 0.25, rules 0.08), and the 100 rules make 3,000 queries in the window.
  - **Worker passes:** mean SLO counting pass 4.0 s, discovery 1.3 s, health 1.1 s, against 30 s intervals.
  - **PostgreSQL:** 41 commands a second.
- **In steady state the workers are far from their ceiling.** Doubling the rules and SLOs keeps every line near its 1× value. SLO
  counting's mean pass is 4.0 s, under a seventh of its 30 s interval.

**The crash, three times, each soon after a start.** `platform-api` stopped:
- in the first 2× attempt (100 rules and SLOs), at an unknown time before the window. It kept no log or exit code; it was
  missing from every docker stats sample;
- in a reproduction by hand on that step's data straight after (100 and 100), about 15 s after starting, with exit code 139
  (`docker compose ps`);
- in the 4× step (200 and 200), 2 min 24 s after starting: exit 139, `oom=false` (`platform-api.state`).

139 is a segmentation fault (signal 11), and in the 4× step not an out-of-memory kill.

The last two came just after a burst of alerts the new SLOs opened together: 62 burn alerts fired at once in the reproduction,
and 102 in the 4× step.
- **In the 4× step's log** (`build/scale/20261114T0618-workers-4x/platform-api.log`), the process faults right after the first error
  that follows the burst (the dump, below, shows the fault was elsewhere). EF Core reports a failure using the connection to PostgreSQL, and the next lines are the runtime's `createdump`:
  `Crashing thread 00b3 signal 11`. While the dump is being written, the rest of that failure is logged: `53300: sorry, too many
  clients already`, in an `AlertTransitionedConsumer` handler planning notifications (`PlanNotificationsHandler`,
  `WebhookDispatcher.cs:184`), one event logged at four layers.
- **Measured:** the burst, the refused connection, the segfault, and nothing restarting it. `platform-api` has no restart policy in
  `docker-compose.yml` or `compose.scale.yml` (Compose's default is none), so every worker stays down for the rest of the step:
  the 1,900 s lags above.
- **Inferred, to confirm in milestone 5:** each alert transition is handled concurrently, each handler holding its own connection
  in a transaction. PostgreSQL's `max_connections` defaults to 100. Npgsql's pool also defaults to 100, and the worker leases
  hold connections of their own outside it (`WorkerLease.cs:49`, `Pooling = false`, one per leased worker). So the pool at its
  limit plus the leases can ask for more than the server allows, and a large enough burst exhausts it. Nothing in the platform bounds that. Steady state uses one connection at a sampled
  moment, every 15 s, which the burst falls between.
- **Why the process segfaults, from the core dump** (read in milestone 5: `dotnet-dump` in an Alpine SDK container, since the
  image's runtime is a musl build). The fault isn't in the refused connection's error path. The crashing thread (`0xb3`) was
  planning notifications (`NotificationPlan.Plan`), opening a new physical connection. Npgsql's default `GSS Encryption Mode`
  (`Prefer`) first asks the native GSSAPI library for a Kerberos context, and the thread faulted inside that call
  (`Interop+NetSecurityNative.InitSecContext`). A second thread (`0xb5`) was in the same call at the same moment, and a third
  (`0xb6`) was throwing the 53300 refusal. Both come from the burst opening many new connections at once. Steady state reuses
  pooled connections, so it rarely reaches that call, and two at once even less often. That would explain why the crash
  didn't repeat on demand. The platform doesn't use Kerberos.
- **Confirmed in milestone 5:** the alert-transition handlers run unbounded. The Juice local channel starts a task per message
  and sets no limit unless `LocalChannelOptions.MaxConcurrency` is set, which it wasn't.
- **Not reproduced on demand.** Thirteen more starts by hand, after the reproduction, didn't crash. They ran on the same data
  without the generator: platform-api restarted twice, recreated with 150 to 400 rules four times, and the whole stack restarted
  seven times. None was logged to a file. In the one start whose log was read, the SLO burn alerts line was "0 fired, 2 resolved".
- **What it means for the ceiling:** the workers' ceiling at 4× isn't measured, and the 2× step held only on its second try. What bends first is not a worker's pass but the
  alert burst on PostgreSQL's connections. A deployment that adds many SLOs at once, or an incident that opens many alerts at once,
  would meet it.

### What the ceilings say for the fixes (milestone 5)

- **The crash first.** Bound the alert-transition handlers' concurrency, or their connections (a pool `Maximum Pool Size` under
  the server's limit), so a burst queues instead of failing. Find the segfault from the dump. Restart a crashed platform: the
  compose file and the deployment guide.
- **R11: bound the searches.** A cap on searches running at once (per user in ClickHouse, `max_concurrent_queries_for_user`,
  and in the API, answering "busy, try again" past it) should turn the 16 a second collapse into slower or refused searches,
  and leave ingest and the workers alone. To verify in milestone 5 by rerunning the step. Inserts and workers need their own share.
- **R12: smaller search scans.** 3.9 M rows for a one-hour log search at 1×, and 1.2 M for traces: the time and service filters
  don't narrow what ClickHouse reads enough.
- **R12a: the metrics exporter.** More consumers, or larger batches, and the insert wait measured, against the 90,000 points a
  second it reaches now. Spans and logs have headroom.
- **R12b, PostgreSQL:** its CPU and command rate are nowhere near a line: API reads under 70 ms p95, under a quarter of a core.
  Its connections are, in a burst: see the crash above.

## Phase 11 fixes (milestone 5), from 15 November

One change at a time, each with its step rerun.

### 1. The crash on bursts of alerts

**What the dump showed** (above, under the workers ceiling): the fault was in Npgsql's GSS encryption probe, on new physical
connections opened during the burst. It was not in the 53300 error path.

**The changes:**
- **Alert handling bounded.** The local channel handles at most 8 messages at once (`Messaging:Local:MaxConcurrency`,
  `PlatformExtensions.DefaultLocalConcurrency`). Before, a burst of 102 transitions started 102 handlers together.
- **The GSS probe off.** `GSS Encryption Mode` is `Disable` unless the connection string sets it
  (`PlatformDbContext.WithoutGssProbe`, used by the context and so by the worker leases).
- **A crashed API restarts.** `platform-api` has `restart: unless-stopped`. The harness records `restarts=` in
  `platform-api.state`, and a restart during a step is a part that failed ("crashed and restarted N×").

**Verification, the workers ceiling at 4× (200 rules, 200 SLOs), twice:**

| Step | What changed | The burst | Result |
|---|---|---|---|
| `20261114T2340-workers-4x` | the concurrency bound alone | none: the 200 SLOs were already in the database from the crashed step ("200 configured, 0 changed"), so nothing counted 30 days at once | every line held; no restart, no 53300. Not a test of the fix |
| `20261115T0019-workers-4x` | both fixes; `COPY_FROM=5`, so 150 of the 200 SLOs were new to the database | "200 configured, 300 changed" (150 new, 150 retired); one pass resolved 142 burn alerts, then 24 fired, 4, 4, and 24 resolved | every line held; no restart, no 53300, no fault |

In the second step:
- health freshness p95 40.7 s;
- rule breach-to-alert p95 63 s;
- webhook first dispatch p95 1.81 s over 207 deliveries, from 378 alert transitions;
- every worker within 60 s of its delay;
- at most 1 outbox row and 24 webhook deliveries waiting.

**What this does and doesn't show.** The burst went through without a fault, but the crash never reproduced on demand before
the fixes either (thirteen starts by hand). So this is evidence, not proof. Neither cause is left in place:
- the bound keeps a burst from opening 100 connections together;
- with the probe off, the native call that faulted isn't made at all.

The first step, at 4×, also showed the first sample after a restart on kept volumes: discovery 5,007 s behind for 20 s while it
caught up a watermark from the previous session. That is an artefact of the start, not lag under load.

**Also fixed:** `SloTests.The_api_shows_slos_by_category_and_an_uncounted_window_as_unknown_Async` failed in the first hour after
midnight UTC, because the last counted hour then starts the day before. The test now dates the older hour from the day the last
hour starts in. A time-of-day test bug, not the platform's.

### 2. Searches bounded (R11, step 1)

**The changes:**
- **At most 8 API queries at once per replica.** The API's ClickHouse queries (searches and the Console's telemetry pages) take
  a slot from `ClickHouse:Api:MaxConcurrent` (8). A query waits up to `ClickHouse:Api:QueueTimeout` (5 s) for one, then is
  refused. The workers' queries don't take slots.
- **Tighter limits per API query:** `max_execution_time` 20 s (the profile allows 60) and `max_threads` 2. So one replica's
  searches use at most about 16 of ClickHouse's cores.
- **Busy is a 503, not a 500.** No slot in time, or ClickHouse cutting a query for time, for too many queries or for memory
  (codes 159, 202, 241): 503 "Busy, try again" with `Retry-After: 5`. ClickHouse out of reach: 503 "Telemetry store
  unavailable" with `Retry-After: 15` (the milestone 2 gap: this was a 500).
- **Seen in metrics:** `juice.observability.clickhouse.api.running`, and the query outcomes `busy` and `unavailable`.

**The searches ceiling again, 8 and 16 a second** (`20261115T0202-search-8per-s`, `20261115T0235-search-16per-s`), against
milestone 4:

| | 8 a second, before | 8 a second, bounded | 16 a second, before | 16 a second, bounded |
|---|---|---|---|---|
| Search p50 / p95 | 102 / 614 ms | 125 / 314 ms | 2.3 s / no answer | 5.4 s / no answer |
| Searches failed | 0 | 0 | 6,028 of 28,785 (ClickHouse timeouts and refusals) | 3,308 of 28,777, all 503 "busy" after 5.0–5.1 s |
| Searches answered a second | 8 | 8 | 12.6 | 14.1 |
| ClickHouse CPU, mean / max | 5.8 / – | 3.3 / 20.2 | 18.9 / 22 | 15.7 / 21.8 |
| ClickHouse memory, max | 3.5 GiB | 2.9 GiB | 22.8 GiB | 3.7 GiB |
| Console page p95 | 1.7 s | 757 ms | 30 s, 12 of 174 failed | 6.3 s, 16 of 174 failed |
| API reads p95 (PostgreSQL) | 30 ms | 17.7 ms | 67.9 ms | 53.7 ms |
| Ingest | all stored | all stored | **points 64.8 % stored, 7.6 M refused** | **all stored, 0 refused**; freshness p95 1.74 s |
| Discovery watermark past its delay, max | within | 31.5 s | 882 s | 33.6 s |

- **16 a second now degrades instead of collapsing.** The searches queue for a slot and wait. The answered ones took 5.3–6.0 s
  (p50–p95): about 5 s waiting and under a second running. One in nine was refused with a 503 at the queue timeout.
- **Ingest and discovery are no longer touched.** Every point was stored, nothing was refused, and ClickHouse's memory stayed
  under 4 GiB. Before, a third of the points were lost.
- **8 a second got faster:** p95 314 ms against 614 ms, on about half the cores. With two threads a query, the scans contend less.
- **One replica's search capacity is about 14 a second here** (8 slots, about 0.55 s a search running). Beyond that, searches
  wait and then are refused. More capacity needs another API replica, with its own 8 slots, on a ClickHouse with the cores for it.
  Their 16 cores each are the sizing figure (`MaxConcurrent × MaxThreads`).
- **Not measured here:** ClickHouse's queries running at once, since the harness doesn't sample `system.processes`. The bound
  allows at most 8 from the API, plus the workers' queries (off in this ceiling).

**Step 2 (workload scheduling in ClickHouse) is deferred.** Step 1 kept ingest whole at twice the search rate that broke it.
What's left is the workers' share of the cores while searches fill their 16. That shows only in the full mix with every worker on,
so the final rerun checks it. If the workers lag there, step 2 comes in.

### 3. Searches read the newest slice first (R12)

**Measured first, on the 16 a second step's data** (each query alone, `max_threads` 2). A log search for `failed` over the last
hour read every log in it, 3.63 M rows and 0.35 CPU-seconds, to return the newest 51. A trace search for one service read 1.66 M
rows and 0.47 CPU-seconds: 0.67 M to find the matching traces, the rest fetching their spans over the window ± 1 h.

**The changes:**
- **Newest slice first.** Trace and log searches read the newest 5 minutes of the window (from the cursor's position on a later
  page), then 1 hour, 12 hours and the whole window, until a page is full
  (`ClickHouseTelemetrySource.QueryNewestFirstAsync`). The answer is the same as one read of the whole window: when a slice holds
  a full page, nothing older can rank above it. A search that matches nothing reads about 8 % more than before. A search holds
  one query slot (R11) for all its slices. The trace search still fetches the matched traces' spans over the window ± 1 h.
- **The logs table's first key column bounded.** The exporter orders `otel_logs` by `(toStartOfFiveMinutes(Timestamp),
  ServiceName, Timestamp)`, and ClickHouse doesn't derive the first column's bound from a bound on `Timestamp`. So a slice was
  cheap only while the newest minutes sat in small parts that their own time range pruned. The log search now bounds
  `toStartOfFiveMinutes(Timestamp)` too.

Measured alone on the same data: a log slice 0.42 M rows and 0.09 CPU-seconds; a trace search 0.52 M rows and 0.16. Bounding the
trace search's span fetch by `otel_traces_trace_id_ts` instead read more rows (1.6 M), so it was dropped.

**The searches ceiling, 8 and 16 a second:**

| | 8, R11 | 8, slices | 8, slices + key bound | 16, R11 | 16, slices | 16, slices + key bound |
|---|---|---|---|---|---|---|
| Step | `20261115T0202` | `20261115T0626` | `20261115T0739` | `20261115T0235` | `20261115T0659` | `20261115T0813` |
| Search p50 / p95 | 125 / 314 ms | 55 / 138 ms | 69 / 119 ms | 5.4 s / no answer | 76 ms / no answer | **78 / 148 ms** |
| Trace / log search p95 | 316 / 312 ms | 101 / 147 ms | 129 / 68 ms | 6.0 / 5.7 s | 5.2 / 6.5 s | 176 / 85 ms |
| Refused (503) | 0 | 0 | 0 | 3,308 of 28,777 | 1,716 of 28,777 | **0 of 28,777** |
| ClickHouse CPU, mean | 3.3 | 1.7 | 1.6 | 15.7 | 5.0 | **3.1** |
| ClickHouse memory, max | 2.9 GiB | 2.5 GiB | 2.5 GiB | 3.7 GiB | 3.5 GiB | 3.0 GiB |
| Console page p95 | 757 ms | 671 ms | 720 ms | 6.3 s, 16 failed | 854 ms, 6 failed | 739 ms, none failed |
| Ingest | all stored | all stored | all stored | all stored | all stored | all stored |

- **16 a second now meets the 2 s line, with no refusals,** on about 3 of the 24 cores: the step where milestone 4's searches
  collapsed. The ceiling is above 16 a second. The steps stop at 16, so how far above isn't measured.
- **Slices alone left a 3¾-minute episode at 16 a second** (07:27:45–07:31:30), with all 1,716 refusals. From `query_log`: in it,
  every log search read 3.05–3.13 M rows and 2.9 CPU-seconds in one query, against 0.4–0.6 M before. Its part had just been merged
  into one of 10.3 M rows, covering the day up to 07:26. Replayed on that data, the same slice read 3.18 M rows without the key
  bound and 0.53 M with it.
- **With the key bound, a merge didn't show.** A 2.5 M-row logs merge came at 08:40, inside the 16 a second step, and log slices
  read at most 0.46 M rows throughout (0.37–0.40 M mean every 5 minutes).

**Next candidate, a worker query:** discovery's instance pass reads about 21.8 M rows and 24 CPU-seconds each time, across traces,
logs and metrics over its 10-minute window, about 0.8 of a core all the time at 1×. It isn't a search, and is measured on its own.

### 4. Discovery reads the metric tables every 5 minutes (R12)

**Measured first** (`query_log`, the 16 a second step `20261115T0813`): discovery's resource read ran every 30 s over its 10½-minute
window, 13.6 M rows and 15.5 CPU-seconds a pass, about half a core. Split by table on one window, `otel_metrics_sum` was 17.1 M of
the rows and 16.1 of the CPU-seconds. Each metric point was read about 20 times. The cost is the `ResourceAttributes` map: the
same rows without it cost 0.12 CPU-seconds, the tenant filter alone 8.3, and grouping by the whole map before extracting still 10.1.
A bound on the table's `toStartOfHour(TimeUnix)` key column pruned nothing. So no rewrite of the query helps. The levers are reading
the metric rows less often, or materialised resource columns on the exporter's tables (a schema change, deferred).

**The change (the owner's choice of the two):** discovery reads spans and logs every pass, as before, and the metric tables on their
own checkpoint (`telemetry-metrics`) once `Discovery:MetricsInterval` (5 min) has passed since it. A resource seen only through
metrics appears, and its last-seen time moves, up to 5 minutes later: inside the 15 minutes before an instance goes inactive.
Reconciling never moves a first-seen time later or a last-seen time earlier, so the two checkpoints can't undo each other.

**The searches ceiling at 8 a second again** (`20261115T0904`, against `20261115T0739`), from `query_log`, as a share of the
30-minute window:

| | Before | After |
|---|---|---|
| Discovery's resource read | 60 passes, 12.4 M rows each, 0.447 cores | spans and logs: 60 passes, 1.7 M rows, 0.057 cores; metrics: 6 passes, 9.9 M rows, 0.037 cores |
| Discovery, all its queries | 0.52 cores | 0.16 cores |
| `platform_reader`, every query (searches included) | 1.34 cores | 0.98 cores |
| ClickHouse container CPU, mean (docker stats) | 1.56 cores | 1.58 cores |
| Search p95; discovery past its delay, max | 119 ms; 29.5 s | 119 ms; 30.3 s |

- **Discovery's queries cost a third of what they did,** and the reader's queries in all 0.36 cores less.
- **The container's CPU didn't move,** so that saving didn't show in docker stats. The rest of ClickHouse's CPU (about 0.2 cores
  before, 0.55 after) isn't attributed by what the harness records. Merges were similar in both windows (439 M and 564 M rows
  merged). So the R12 claim is the measured query cost, not the container's total.

### 5. Health remembers liveness across passes (R12)

**Measured first** (`query_log`, the workers ceiling at 4×, `20261115T0019`): health's liveness read cost 0.30 cores, 7.5 M rows
and 9.1 CPU-seconds every 30 s. It reads every table, metrics included, over the whole 5-minute evidence window, so each row was
read about 10 times. Health uses only each resource's latest last-seen time from it.

**The change:** the health runner keeps each resource's latest observation across passes. A pass reads from the previous pass's
end less `Health:LivenessOverlap` (1 min), merges, and drops what has left the window. A runner with nothing remembered (the
first pass, or after a restart or a change of leader) reads the whole window, as before. The trade-off: a row stored more than
a minute after its own time is missed until a runner starts without memory. Before, it counted while it was inside the window. (Changed in the milestone 5 review: with ingestion minutes behind, every row arrived before the read range and live
instances would have gone Offline. The read now also reaches back as far as the newest row remembered trails the previous end, and the review's second round
added a whole-window read at least every `Health:LivenessFullRead`, 2 minutes, for one service whose rows are all late, such as
long spans, which are written when they end. That raises the liveness reads from about 3 a row to about 5, still half of the 10
before; the figures below were measured without it, and the next scale run measures it.)

**Workers ceiling at 4× again** (`20261115T1006`, the same 200 rules and 200 SLOs as `20261115T0019`), with discovery's 5-minute
metrics cadence (section 4) also in place. From `query_log`, as a share of the 30-minute window:

| `platform_reader` queries | Before | After |
|---|---|---|
| Discovery: resource read | 60 passes, 13.3 M rows each, 0.517 cores | spans and logs 60 passes, 1.7 M rows, 0.064 cores; metrics 6 passes, 8.9 M rows, 0.038 cores |
| Discovery: links and resource uses | 0.067 cores | 0.078 cores |
| Health: liveness | 60 passes, 7.5 M rows each, 0.302 cores | 60 passes, 2.65 M rows each, 0.105 cores |
| Health: requests and memory | 0.015 cores | 0.012 cores |
| Rules | 0.326 cores | 0.275 cores |
| SLO counting | 0.61 cores | 0.38 cores (see below) |
| **All of them** | **1.84 cores** | **0.95 cores** |

| Step result | Before | After |
|---|---|---|
| ClickHouse container CPU, mean / max (docker stats) | 1.77 / 12.1 cores | **0.87 / 7.3 cores** |
| Health freshness p95 | 40.7 s | 40.7 s |
| Rule breach-to-alert p95 | 63 s | 67 s |
| Webhook first dispatch p95 | 1.81 s over 207 | 1.9 s over 159 |
| Every worker within 60 s of its delay; no backlog growing | yes | yes |
| Ingest | all stored | all stored |
| API restarts | 0 | 0 |

- **ClickHouse's CPU halved with every worker on,** and the drop in the container (0.90 cores) matches the drop in the reader's
  queries (0.89). Section 4's 8 a second run, where the container didn't move, ran without health, rules or SLOs.
- **Discovery and health together went from 0.90 to 0.30 cores.**
- **The SLO figure isn't a like-for-like change.** In the before step, 150 of the 200 SLOs were new to the database and counted 30
  days at once (the burst it was run to test). Here they were already counted. Rules and SLO counting are unchanged by R12 so far.
- **The workers' lines hold as before.** Breach-to-alert and webhook figures differ with the breach runs' timing, not their cost.

### 6. SLO counting: the newest telemetry and the minutes with telemetry, read narrower (R12)

**Measured first** (`query_log`, `20261115T1006`): counting itself was cheap, 2,400 queries and 0.058 cores. The cost was in the two
"is the service sending" reads, each over every table, the metric tables included:
- the watermark, each service's newest telemetry over the 15-minute lookback: 600 queries, 1.3 M rows each, 0.206 cores;
- presence, the minutes with any telemetry in the stretch counted: 600 queries, 0.68 M rows each, 0.118 cores.

On one service, a 2-minute watermark read cost 0.13 CPU-seconds against 0.80 for 15 minutes; on `otel_metrics_sum`, 0.05 against
0.65.

**The changes** (both give the same answer as before):
- **Watermark newest first.** The newest 2 minutes are read first, and the whole lookback only when they hold nothing. Anything in
  them is newer than anything older.
- **Presence from spans and logs first.** The metric tables are read only when spans and logs leave a minute of the stretch
  uncovered. A service taking requests has spans every minute.

A new test covers a service quiet for 5 minutes (still counted) and a minute with only metrics (still present, counted as quiet).

**Workers ceiling at 4× again** (`20261115T1129`, the same set as `20261115T1006`):

| `platform_reader` queries (`query_log`) | Before | After |
|---|---|---|
| SLO watermark | 600, 1.3 M rows each, 0.206 cores | 600, 0.38 M rows each, 0.040 cores |
| SLO presence | 600, 0.68 M rows each, 0.118 cores | spans and logs 600, 0.16 M rows, 0.013 cores; metrics 40, 0.3 M rows, 0.002 cores |
| SLO counting | 2,400, 0.058 cores | 2,400, 0.056 cores |
| **SLO worker, all** | **0.382 cores** | **0.111 cores** |
| Rules | 0.275 cores | 0.178 cores (unchanged code: the run's breach timing) |
| **Every reader query** | **0.955 cores** | **0.569 cores** |

| Step result | Before | After |
|---|---|---|
| ClickHouse container CPU (docker stats, 10 s samples): median / p90 / p99 | 0.47 / 2.29 / 4.43 cores | 0.42 / 1.85 / 7.96 cores |
| Mean | 0.87 cores | 1.07 cores |
| Health freshness p95; rule breach-to-alert p95; webhook first dispatch p95 | 40.7 s; 67 s; 1.9 s | 41.2 s; 62 s; 1.92 s |
| Every worker within 60 s of its delay; ingest all stored; API restarts | yes; yes; 0 | yes; yes; 0 |

- **The SLO worker reads at a third of its cost,** and the reader's queries in all at 60 %.
- **The container's mean went up, not down.** Its median and p90 fell, but a few 10-second samples in the top 1 % reached 8–12
  cores, and they lift the mean. Merges don't explain them (383 M rows merged against 317 M, ClickHouse busy merging 26 % and 24 % of
  the time). The query log, which counts a query when it ends, doesn't place them either. Across the three 4× runs the container's
  median went 0.51, 0.47, 0.42 cores and its p90 6.24, 2.29, 1.85 cores. So R12's claim is the queries' measured cost. Container
  bursts are left to the final rerun.

### 7. The Collector sends metrics with 16 inserts at once, not 4 (R12a)

**Measured first** (`query_log`, ingest ceiling at 10×, `20261115T1230`, the same config as milestone 4): the metrics exporter sends
each batch, about 5,650 points, as one insert per metric table, and each insert returns only once ClickHouse's async insert has
flushed it: about 210 ms for the gauge and histogram tables and 260 ms for the sum table (p50), almost all of it the busy timeout
(the flush itself takes about 10 ms at 1×). With 4 consumers the sum table had 3.75 inserts in flight on average, so the exporter
was waiting on ClickHouse nearly all the time, at 15 batches a second: 84,600 points/s stored of 100,000. ClickHouse's container
used 1.8 cores of 24.

**The change:** `num_consumers` 4 → 16 on every exporter (`collector/config.yaml`). Async insert gathers concurrent inserts into one
part, so the part count doesn't grow with it.

| Ingest ceiling | 10×, 4 consumers (`20261115T1230`) | 10×, 16 consumers (`20261115T1304`) | 20×, 16 consumers (`20261115T1338`) |
|---|---|---|---|
| Points stored of nominal | **86.5 %** (53.3 M lost) | **100 %**, 0 lost | **55.1 %** (357 M lost) |
| Spans, logs stored | 100 %, 100 % | 100 %, 100 % | 100 %, 100 % |
| Metric points inserted per second (`query_log`) | 86,500 | 100,000 | 110,100 |
| Metric batches per second; sum-table insert p50 / p95 | 15.0; 262 / 282 ms | 17.3; 219 / 278 ms | 19.0; 226 / 307 ms |
| Sum-table inserts in flight, mean | 3.75 (of 4) | 3.70 (of 16) | 4.42 (of 16) |
| Collector export queues, median / max (bound 3 × 200,000) | 195,452 / 212,070 | 36,514 / 65,798 | 201,343 / 213,274 |
| Ingestion freshness p95 | 0.79 s | 0.79 s | 0.63 s |
| ClickHouse container CPU, mean / max | 1.8 / 4.0 cores | 1.9 / 4.4 | 3.1 / 7.4 |
| Collector container CPU, mean / max | 1.7 / 2.2 cores | 1.5 / 2.0 | 3.1 / 4.6 |

- **10× now holds:** every point stored, the queue at a sixth of its bound and draining, at the same ClickHouse cost.
- **At 20× the limit has moved into the Collector.** The metrics exporter sends about 110,000 points/s with 16 consumers, but only
  4.4 of them are inserting at a time on average; ClickHouse uses 3.1 of 24 cores and the inserts are no slower. The consumers wait
  on something before the insert, in the Collector. Not measured: where. Its CPU is 3.1 cores on a host with room, so one serial
  stage (the queue's batcher, or turning OTLP into rows) is the likely candidate, a hypothesis for a profile of the Collector at 20×.
- **The ingest ceiling is now between 10× and 20×,** about 110,000 metric points/s (with 10,000 spans and logs/s at 10×, 20,000 at
  20×) on one Collector, against about 85,000 before.

### 8. Resource attributes as columns: measured, not shipped (R12)

**Asked by the owner** after R12a: would a tenant column be cheaper than filtering on `ResourceAttributes['juice.platform_tenant_id']`?

**Method:** one hour of the workers 4× run's data (11:00–12:10, `20261115T1129`) copied into a scratch database on the scale volume,
and 380 of that run's own worker queries replayed, sampled at random from `query_log` (150 rules, 150 SLO counting, 40 discovery,
40 health), each once to warm and once measured, in four variants on the same copy:
- **A:** as now, every key read from the map;
- **B:** the tenant from a `LowCardinality(String)` column materialised from the map;
- **C:** B, plus `service.namespace` and `deployment.environment.name`;
- **D:** C, plus every other key the workers read (instance, host name and id, pid, version, container, three Kubernetes keys).

Results were the same in every variant (sorted output hashed per query), except four rules' p95: ClickHouse's `quantile` is
approximate and depends on read order (1,360 against 1,409 ms on one; the counts the same). A second run of A cost the same as the
first (382 CPU-seconds both).

| CPU-seconds, ClickHouse (`query_log`) | A: map | B: tenant column | C: + namespace, environment | D: every key |
|---|---|---|---|---|
| Rules (150) | 4.9 | 4.0 | 1.2 | 1.2 (−75 %) |
| SLO counting (150) | 41.2 | 34.5 | 5.9 | 6.1 (−85 %) |
| Discovery (40) | 52.0 | 29.8 | 25.6 | 19.2 (−63 %) |
| Health (40) | 283.7 | 172.4 | 161.0 | 80.0 (−72 %) |
| **All 380** | **381.8** | **240.8 (−37 %)** | **193.7 (−49 %)** | **106.5 (−72 %)** |

- **The cost is each key looked up, row by row, not only decompressing the map.** The tenant column alone saves 37 %, even in
  discovery and health, which read the map for other keys anyway. Rules and SLOs repeat the environment key three or four times a
  query (trimmed and lowered), which is why C saves them three quarters and more.
- **The columns are nearly free to store:** about 90 KiB each on the copy's 19.5 M sum rows, against 128 MiB for the map.
- **No backfill is needed for the workers.** Parts written before a column existed compute it from the map when read, so they cost
  what they cost today; the workers read only the last 5–15 minutes, all in new parts within minutes of the upgrade.
- **Caveats.** The copy was merged into one part per table, so time-bounded queries prune less than on live data: health read 7.5 M
  rows a query here against 2.65 M live. The percentages are the comparison; the absolute CPU-seconds aren't the live cost. The cost
  at insert (each key looked up once per row) isn't measured yet, nor are the searches (they filter on the tenant and namespace).

**The cost at insert** (owner, 16 November: measure it before shipping). Ingest ceiling at 10×, 16 consumers, run twice back to back on
the same data: as now (`20261116T0141`), then with the 12 columns of D added to the seven telemetry tables as `LowCardinality(String)
MATERIALIZED` (`20261116T0216`). The columns were dropped again afterwards. `query_log` and `part_log`, the 30-minute windows:

| ClickHouse, ingest 10× | Without | With 12 columns | Change |
|---|---|---|---|
| Inserts (`otel_writer`), CPU | 0.231 cores | 0.522 cores | +0.29 |
| Async insert flushes, CPU | 0.378 cores | 0.459 cores | +0.08 |
| Merges, CPU (1,184 M and 1,235 M rows merged) | 1.27 cores | 1.61 cores | +0.34 (+22 % a row merged) |
| Container CPU (docker stats): mean / median / p90 | 2.41 / 1.90 / 3.98 cores | 3.13 / 2.61 / 4.15 cores | +0.72 mean |
| Sum-table insert p50 / p95; its flush p50 | 224 / 313 ms; 45 ms | 235 / 317 ms; 58 ms | +11 ms |
| New parts written | 3.98 GB | 4.15 GB | +4 % |
| Points stored; freshness p95 | 98.6 %; 0.89 s | 98.3 %; 0.97 s | – |

- **The columns cost about 0.7 cores at 10× ingest:** half at insert (each key looked up once a row), half in merges (12 more
  columns to rewrite). Insert latency barely moves.
- **Against what they save on reads, at the workers 4× run's 1× ingest:** about 0.07 cores at insert, scaled from 10×, against 72 %
  of the 0.57 cores the worker queries cost there (`20261115T1129`), about 0.4. Both sides grow with ingest, since the workers read
  rows in proportion to it.
- **Both runs lost about 1.5 % of points,** where the 15 November 16-consumer run lost none: the baseline was also flagged a harness
  limit (the generator fell behind, 230 spans given up). The loss is the same without and with the columns, so it says nothing
  about them; the larger scale data (merges of up to 58–68 M rows) and the host are the candidates, not measured.

**Shipped** (owner, 16 November: every key the workers read). `clickhouse/resource-columns.sh` adds the twelve columns of D on every
start (the `clickhouse-columns` service, after the Collector has created its tables; the API and Grafana wait for it); every
platform query reads them (`ResourceColumns`), Grafana's `grafana_tenant` policy filters on `JuiceTenant`, and only the Console's
free attribute filter still reads the map. Tests check the script against `ResourceColumns`, that no query in `src` reads a listed key
from the map, and that each column holds its key on the running stack; `tests/verify.sh` checks the columns on every table. Applied
to the working stack's existing tables (a metadata change); a fresh install was simulated (the script waits for tables created
later). Its effect on the workers is for the final rerun to show.

## Phase 11 final rerun: 1×, 3×, 4×, 5×, 6×, 10×, 16 November

All of milestone 5 (fixes 1–8 above) against the 13 November baseline, on a fresh scale stack again:
- **Run:** `stack.sh destroy`, then `WARMUP=300 scale/steps.sh --restore --through 1 3 4 5 6 10`, commit 38e136a, 5 minutes of
  warm-up and 30 measured per step. 4× and 6× are held out for the capacity view's ±25 % check (R10); 1, 3, 5 and 10 calibrate it.
- **Reports:** `build/scale/20261116T0442-1x`, `…0517-3x`, `…0553-4x`, `…0628-5x`, `…0704-6x`, `…0739-10x` (`report.md`).
- **Per-component figures:** `components.json` in each step, and `build/scale/m5-final-components.md`.

| Measure | Line | 1× | 3× | 4× | 5× | 6× | 10× |
|---|---|---|---|---|---|---|---|
| Throughput (stored, spans / points / logs) | ≥ 98 % each, nothing lost | 100 / 100 / 100 %, 0 lost | 100 / 100 / 100 %, 0 lost | 100 / 100 / 100 %, 0 lost | 100 / 100 / 100 %, 0 lost | 100 / 100 / 100 %, **12,948 points dropped** (once, 10 s) | 100 / **94.2** / 100 %, **10.5 M points dropped** |
| Ingestion freshness p95 | ≤ 30 s | 1.37 s | 1.35 s | 1.4 s | 1.41 s | 1.58 s | 1.35 s |
| API reads p95 (20/s) | ≤ 500 ms | 10.6 ms | 11.3 ms | 12.9 ms | 16 ms | 38.8 ms | 33.9 ms |
| Health freshness p95 | ≤ 60 s | 37.9 s | 38.1 s | 38.8 s | 40.4 s | 39.7 s | 42 s |
| Rule breach-to-alert p95 | ≤ 2 min after `ForDuration` | 67 s | 64 s | 66 s | 65 s | 65 s | 65 s |
| Webhook first dispatch p95 | ≤ 30 s | 0.56 s | 1.98 s | 1.99 s | 1.96 s | 1.96 s | 1.83 s |
| Queue lag, past each configured delay | ≤ 60 s, nothing growing | within | within | within | within | within | within; **Collector queue growing** |
| Telemetry search p95 (reported) | – | 62 ms | 126 ms | 154 ms | 230 ms | 817 ms | 1.14 s |
| Console page p95 (reported) | – | 654 ms | 708 ms | 769 ms | 888 ms | 1.18 s | 1.43 s |
| ClickHouse CPU, mean / max (docker stats) | – | 0.76 / 8.5 | 1.75 / 13.8 | 2.68 / 18.3 | 3.16 / 16.2 | 5.46 / 21.4 | 7.06 / 18.7 |

- **1× meets every §9 target, and every line holds at 3×** (R10), and at 4× and 5×. 5× is where the baseline first crossed (20 M
  points refused, workers minutes behind); it now runs with every worker in line and ClickHouse at about a fifth of the CPU.
- **6× crosses only "nothing lost":** one 10-second sample in 30 minutes, 0.012 % of the points, while everything else stayed in.
- **10× crosses on throughput**, sustained: 5.8 % of points dropped and the Collector's queue growing. Every worker, freshness and
  read is still in line (in the baseline they were 9–21 minutes behind).

**Against the baseline,** from `query_log` (CPU cores in the window, the platform's reads and the Collector's inserts):

| | 1× before → after | 3× | 5× | 10× |
|---|---|---|---|---|
| Platform reads (`platform_reader`), CPU | 1.90 → 0.28 (−85 %) | 6.98 → 0.72 (−90 %) | 11.23 → 1.29 (−89 %) | 12.34 (saturated) → 3.08 |
| Rows read a second | 3.2 M → 1.2 M | 12.1 M → 3.3 M | 20.8 M → 6.1 M | 32.1 M → 10.8 M |
| ClickHouse container CPU, mean | 2.49 → 0.76 | 8.29 → 1.75 | 15.1 → 3.16 | 15.96 → 7.06 |
| Telemetry search p95 | 292 ms → 62 ms | 2.6 s → 126 ms | 10.6 s → 230 ms | 46.4 s → 1.14 s |

The rows read are what the owner asked about on 16 November (3 M a second at 1×): 1.2 M now, of which the synthetic search mix (one
log and one trace search a second) is about half (0.6 M a second at 1×, `juice:api:/telemetry/*`). The workers read the other 0.6 M (SLOs 0.25 M, discovery 0.17 M, health 0.1 M, rules 0.09 M).
Fixes 2–6 and 8 each took a share; this run measures them together, the sections above one at a time.

**The ~1.5 % point loss of the two ingest ceiling runs (§8) didn't recur:** 0 lost at 1–5× with the full mix. It belonged to that
harness, where the generator fell behind too.

### The next limit: the Collector's metrics export, in bursts at 6× and sustained at 10×

- **At 6×, a burst.** At 07:27:05 ClickHouse went from 2.5 to 10–16 cores for about 40 seconds (`metric_log`, 5-second steps), with
  16–23 queries running at once: a 25-second merge of 24.7 M `otel_metrics_sum` rows and a 21-second one of traces, the 16 inserts,
  and a discovery pass and searches at the same moment (discovery's four queries in that minute took 41 CPU-seconds). Inserts slowed
  (p95 243 ms at 6× against 109 ms at 5×); the export queue went from 95,000 to 196,000 items in one sample, its bound is 200,000,
  and 12,948 points were dropped (`otelcol_exporter_enqueue_failed_metric_points`). It drained within 30 seconds.
- **At 10×, sustained,** and not for ClickHouse's CPU: it averaged 7.1 of 24 cores. Insert latency did: p50 13 ms at 1×, about 70 ms
  from 3× to 6×, **206 ms at 10×**, with flushes fewer and larger (27.6 a second at 6×, 17.9 at 10×). Each of the exporter's 16
  inserts waits for its flush, so the metrics export tops out near the 94 % that was stored: about 95,000 points a second. The
  ingest ceiling (§7) found the same order, 110,000 a second at 20× with only 4.4 of 16 inserts in flight. Why the wait grows
  (ClickHouse's adaptive async-insert busy timeout, the batch sizes the exporter sends, or the Collector itself) is not measured yet:
  it's the Collector profile queued after milestone 5.

### The ±25 % check (R10)

The check is on the capacity view's predicted knee (milestone 6), which doesn't exist yet; this run is its data. A first look, by a
straight line through ClickHouse's mean CPU at 1, 3, 5 and 10× (0.714 cores per × − 0.21): 4× predicted 2.65 cores, measured 2.68
(−1 %); 6× predicted 4.08, measured 5.46 (−25 %, at the edge). The knee itself isn't a CPU knee: ClickHouse would run out of cores
near 34× on that line, but the first line crossed at 6× (a burst) and the sustained one at 10× (insert wait). So the capacity view has
to model the export path (inserts in flight × insert latency against the queue's bound) and the burst headroom, not just CPU.

**The check, done in milestone 6** (the capacity view, [platform metrics](platform-metrics.md#the-overviews-platform-panel-and-the-capacity-view)):
- **The mechanism.** The Collector's inserts in flight (their durations over the time, `query_log`, 10-second buckets), mean / p99 /
  max: 1× 0.12 / 0.14 / 0.16, 3× 1.16 / 1.37 / 1.63, 4× 1.62 / 2.10 / 2.67, 5× 2.09 / 2.82 / 3.59, **6× 3.31 / 10.54 / 11.17**,
  **10× 10.30 / 10.78 / 10.84**. At 10× every bucket sits near 10.5: the export path saturates there, not at the 16 consumers. At 6×
  the burst reached the same ceiling for its 40 seconds, while the mean stayed at a third of it.
- **A curve doesn't predict it.** Interpolating any of the step measures across 1, 3, 5 and 10× puts the knee near 9.5–10×: the queue's
  p95 or p99, ClickHouse's CPU p99 against its cores, inserts in flight against ~10. That is +60 % against the measured first crossing at
  6×. The burst isn't on the curve: ClickHouse's CPU p90 went from 4.5 cores at 5× to 13.3 at 6×, and the steps run one after another
  on growing data, so a later step also meets larger merges.
- **What the view claims instead** (owner, 16 November, on these figures): the bracket. The last calibration step every line held and the
  first that crossed one: **held to 5×, crossed at 10×**. Its lower bound, 5×, against the measured first crossing, 6×: **−17 %, within
  ±25 %.** The held-out steps fall where the bracket says: 4× held, 6× crossed. Chosen after seeing the data, and said so here. It is a
  coarser claim than a curve, and an honest one: the platform held at 5× and is not claimed to hold anywhere above it.
- **The calibration file** is `deploy/local/capacity/calibration.json`, written by `scale/calibration.mjs --held-out 4,6` from these six
  steps (`scale/steps.sh` writes one after every run). Each resource in it is measured as the platform measures it live, so the view
  sets today's figure beside the step that held and the step that crossed.

## Phase 11 follow-ups, 16–17 November

The two items Phase 11 left open ([acceptance](phase-11-acceptance.md)): why the Collector's metrics export tops out, and what the
review's whole liveness read every 2 minutes costs.

### The export's limit was the ClickHouse driver's connection pool

The ingest ceiling at 20× (`CEILING=ingest scale/steps.sh 20`, every worker off, no query mix), where the export saturates on its own,
one change at a time to `collector/config.yaml` (restored after each run):

| At 20× ingest (200,000 points/s offered) | Points stored | ClickHouse CPU, mean | Collector CPU, mean | Inserts in flight, mean / p99 | Run |
|---|--:|--:|--:|--:|---|
| A: as 1.3.0 ships (async inserts, 16 consumers) | 55.1 % | 3.1 cores | 3.1 | – | `20261115T1338-ingest-20x` |
| B: the exporter's `async_insert: false` | 89.7 % | 13.5 cores | 7.3 | 10.9 / 11.2 | `20261116T2308-ingest-20x` |
| C: async inserts, 32 consumers | 50.7 % | 3.3 cores | 3.1 | 11.1 / 11.3 | `20261116T2345-ingest-20x` |
| **E: async inserts, the driver's pool at 32 (`connection_params: max_open_conns`)** | **99.6 %** | **8.1 cores** | 5.6 | 20.5 / 30.3 | `20261117T0100-ingest-20x` |

- **The cap:** inserts in flight sat at about 11 whatever the consumers (B and C), the ClickHouse Go driver's default pool: clickhouse-go's own
  default of 10 open connections (5 idle), not `database/sql`'s, which has no limit. More consumers only queue for a connection (C, no better than A). Turning async
  inserts off (B) made each insert faster (p50 88 ms against 213), so the same 11 slots carried more, at four times ClickHouse's CPU
  (each insert a part of its own: up to 85 parts in a partition, against 38).
- **The fix (E), shipped:** `max_open_conns: "32"`, async inserts kept. About 200,000 points a second at the ingest ceiling, twice
  1.3.0's 110,000; ClickHouse's CPU follows the data it now stores, at a lower cost a point than B's. Up to 30 inserts in flight
  (p99) and at most 64 parts in a partition. At 20× it is at its new limit (0.5 % of points dropped, the queue's median 71,000), so
  20× is about where it stands. With the full mix, a rerun of the steps measures where the knee moved: the next section, once it has run. The pool is the cap again at
  20× (30 of 32 in flight, p99): an export inserts its metric types' tables at once, so the pool goes up with the consumers. Idle
  connections are kept as many (`max_idle_conns`, the driver's default 5), so a busy pool isn't redialled; each exporter has its own,
  up to 96 connections in all.
- Per-component figures: `build/scale/export-experiment-components.md`.

### What health's whole liveness read every 2 minutes costs

A full-mix 1× step on the current code (`20261117T0021-1x`, every §9 target met) against the final rerun's 1× (`20261116T0442-1x`,
before the review's whole read): health's queries went from 0.026 to 0.038 ClickHouse cores (+46 %), from about 3 reads a row to
about 5, as estimated; health freshness p95 40 s (line 60 s). The step ran straight after the 20× runs, whose data was still inside the
rules' and discovery's longer windows (an hour and more), so their figures there (rules 0.19 cores, reading 12 times the rows) say
nothing about 1×; health's 5-minute window had cleared, and its figure stands.
