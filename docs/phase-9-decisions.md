# Phase 9 decision records

**Status:** approved 2026-11-11: the owner followed the recommendations (DR-1 to DR-4). DR-1 rests on the benchmark in
[the Phase 9 plan](phase-9-plan.md#benchmark-method-and-targets-fixed-10-november-before-the-first-run); DR-2 records R11.

| # | Decision | Recommendation | Decision |
|---|---|---|---|
| DR-1 | Streaming evaluation | **Not introduced.** Scheduled evaluation meets every target it can be held to; the one it misses, streaming wouldn't fix | **Approved** 2026-11-11 |
| DR-2 | Anomaly detection | **Deferred** until there are 90 days of SLO buckets and 20 reviewed incidents | **Approved** 2026-11-11 |
| DR-3 | Fast-burn detection target | **Keep the 2-minute settle; set the target to p95 ≤ 4 minutes** (option B below) | **Approved** 2026-11-11 |
| DR-4 | Reading SLO status at the 200-SLO limit | **Compute the statuses once per window minute and share them** (option A below) | **Approved** 2026-11-11 |

## DR-1: Streaming evaluation is not introduced

**Context.** R10: streaming only if a benchmark shows scheduled queries can't meet an explicit requirement, against targets fixed before it
runs. The roadmap gate adds that any new stateful component needs an approved ADR, an operations plan, a recovery design and a cost
rationale.

**What was measured.** `deploy/local/tests/streaming-benchmark.sh`, 10 November 17:23–18:17 UTC: the reference load (1,000 spans/s,
10,000 metric points/s, 1,000 logs/s through the test Collector, for 55 minutes) and, beside it, the whole platform with 50 rules and
50 SLOs on 50 services, 20 of which break one after another. Workstation: Windows 11, Docker Desktop, the `deploy/local` stack. The
reference load itself held: ingestion freshness p95 1.4 s, nothing refused or dropped.

| Measure | n | p50 | p95 | max | Target | Result |
|---|---|---|---|---|---|---|
| Rule breach-to-alert, after `ForDuration` | 20 | 52 s | 82 s | 82 s | p95 ≤ 2 min | **Met** |
| SLO fast-burn detection, after the rate is crossed | 20 | 142 s | 172 s | 172 s | p95 ≤ 2 min | **Missed** (DR-3) |
| First webhook dispatch | 61 | 1.1 s | 1.7 s | 1.7 s | p95 ≤ 30 s | **Met** |
| Rule evaluation lag | 1,950 | 5.0 s | 5.1 s | 5.3 s | never above 1 min | **Met** |
| SLO burn evaluation lag | 39 | 2.1 s | 5.9 s | 6.0 s | never above 1 min | **Met** |
| `GET slos`, 50 SLOs with 30 days, 20/s under the reference load | 1,200 | 121 ms | 175 ms | 351 ms | p95 ≤ 500 ms | **Met**, no errors |
| `GET slos`, 200 SLOs, 20/s | 1,200 | 5.8 s | 9.3 s | 11.6 s | p95 ≤ 500 ms | **Missed** (DR-4), 601 errors |

Rule evaluations took p95 0.09 s; SLO counting passes p50 5 s, p95 16 s.

**What the benchmark found, and what was fixed inside scheduled evaluation** (each smoke run is 2 breaks, the same harness):

| Found | Cause | Fix | Before | After |
|---|---|---|---|---|
| Rule lag grew without bound, 780 s after 15 minutes | The scheduler started at most `MaxConcurrency` (4) rules per 5-second tick, so at most 48 a minute however quick each was: 50 one-minute rules could never keep up | Every due rule starts on its tick and waits for a worker, so a freed worker starts the next at once (`RuleScheduler`) | max 780 s | max 5.3 s |
| `GET slos` failed or took 20 s at 20 requests/s | Each read summed every minute of 30 days for every SLO (2.2 million rows at 50 SLOs), twice | Hourly and daily totals kept by the counter with the buckets (`slo_hours`, `slo_days`); a window reads its whole days, the hours of its partial days, and the minutes of its partial first and last hours. One read serves the page and the burn windows | p95 20 s, 964 of 1,200 failed | p95 175 ms, none failed |
| The same read, slow at some times of day | The minute stretches were ORed in one WHERE, which PostgreSQL read as every minute of every SLO (465 ms) | One SELECT per stretch, each an index range, combined with UNION | 340 ms a read | 21 ms a read |
| SLO burn judging up to 64 s late | Counting passes slowed by the read overload above | The same fixes | max 64 s | max 6 s |

The plan's remedy for the read was daily totals plus the minutes since (milestone 1, round 3). Daily totals alone brought it to p95 1.8 s
with no failures, still over 500 ms, so hourly totals were added beside them. No new component: two tables in the existing PostgreSQL,
written by the existing counter in its own transaction.

**Why streaming isn't the answer to the one miss.** Fast-burn detection takes 2½ to 3 minutes after the burn crosses its rate. Almost
all of that is by design, not evaluation speed: windows end on whole minutes, two minutes before now (`SloReader.Settle`), so telemetry
still arriving isn't read as missing; then the next pass (every 30 s) judges. Counting passes take about 5 s (p95 16 s) and burn judging
is at most 6 s late. A streaming evaluator would face the same choice of how long to wait for late telemetry, so it would gain nothing unless that
wait is shortened, which scheduled evaluation can do as well (DR-3).

**Decision.** No streaming evaluator and no new stateful component. Revisit if a later benchmark at a larger reference load misses a
target that isn't explained by a deliberate wait.

## DR-2: Anomaly detection is deferred

**Context.** R11: no anomaly detection until there's enough telemetry and incident history.

**Decision.** Not in this phase. Revisit when there are at least **90 days of SLO buckets** and **20 reviewed incidents** (accepted or
dismissed correlation suggestions count toward that, milestone 3 keeps every decision with its evidence). Those are the data a detector
would be tuned and judged against: without them it can't be told from noise, and an alert people learn to ignore is worse than none.

**Until then.** Fixed-threshold rules, multiwindow burn alerts and correlation suggestions cover the cases. The decisions kept by
correlation suggestions are the first labelled history.

## DR-3: The fast-burn detection target

The fast-burn target (p95 ≤ 2 minutes after the rate is crossed) is missed: 127 to 172 s over 20 burns, p95 172 s. The target was set in
this phase's plan, not by §9.

Where the time goes, for a burn crossing during minute M: the minute is in the window once the window's end passes M+1, which is Settle +
1 minute after M; then the next pass (every 30 s, about 10 s long) judges. So detection is (Settle + 0 to 1 minute) + up to 40 s:

| Option | What changes | Detection (from the arithmetic above) | Cost |
|---|---|---|---|
| **B. Keep the settle, set the target to p95 ≤ 4 minutes** (recommended) | The target | 2 to 3⅔ minutes, as measured | None in code |
| A. Settle 1 minute | Windows end one minute before now, not two | 1 to 2⅔ minutes: likely still over 2 at p95 | A just-ended minute is judged before all its telemetry is in more often; it is recounted for 5 minutes anyway, but a burn can be judged on a partial minute |
| A0. No settle | Windows end at the last whole minute | 0 to 1⅔ minutes: meets 2 | The window's newest minute is often not yet counted when judged, so windows read partly unknown and burn alerts hold: the problem the settle was added for (milestone 1) |
| C. Streaming | A stateful evaluator beside the counter | depends on its own wait | An ADR, operations, recovery and cost (the gate), and the same late-telemetry choice |

Recommendation: B. A fast burn is paged within 4 minutes of the rate being crossed, which is within the fast-burn window's own 5 minutes,
and the settle keeps burn alerts from holding on minutes still being counted. A is worth a benchmark run if the owner wants to shave a
minute; A0 and C are not recommended.

## DR-4: Reading SLO status at the 200-SLO limit

At 50 SLOs (the reference) the list reads in p95 175 ms at 20 requests a second. At the 200-SLO limit one read takes about 215 ms on its
own (21 ms at 50: measured one at a time on a fresh database, 15 reads each), so 20 a second is about four cores of PostgreSQL on this
workstation, beside the reference load: requests queue, p95 9.3 s, and half fail once the connection pool runs out.

The statuses change only when the windows' end moves, once a minute (or when a late minute is recounted). Options:

| Option | What changes | Cost |
|---|---|---|
| **A. Compute once per window minute** (recommended) | The list's statuses are computed once for each window end and shared by every request in that minute, per replica | A status can be up to a minute behind a late minute's recount; the windows already move by the minute. A small in-memory cache, no new component |
| B. Lower the limit to 100 SLOs | `SloOptions.MaxSlos` | Fewer SLOs per deployment; about 100 ms a read, still four times the 50-SLO cost |
| C. Leave it | Nothing | At 200 SLOs, the list page is slow when many people open it at once |

The 50-SLO reference, which the plan's target names, is met. Recommendation: A, as a follow-up change with its own benchmark run.
