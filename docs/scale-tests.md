# Scale tests

**Status:** milestone 2 (the harness) done 12 November: two review rounds and a confirmation round, then the 1× smoke run on the final
harness (below) met every target.
The 1×, 3×, 5× and 10× steps (milestone 3) next. Part of the [Phase 10 plan](phase-10-plan.md) (P3–P8, D1–D2). Raw data of every
run stays in `build/scale/<UTC time>-<multiplier>x/`.

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

