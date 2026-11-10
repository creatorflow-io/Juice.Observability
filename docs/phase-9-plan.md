# Phase 9 plan: SLOs, alert correlation, and selective streaming

**Status:** in progress, milestone 1 of 4 done. The owner's requirements and decisions are marked **Owner**; the rest are mine, for review.

**Roadmap gate:**
- a benchmark shows scheduled queries can't meet an explicit requirement before streaming is introduced;
- any new stateful component has an approved ADR, an operations plan, a recovery design and a cost rationale.

## Requirements and decisions

| # | Decision | By |
|---|---|---|
| R1 | **SLOs are defined in a configuration file**, validated and audited, as rules are. The API and Console show them read-only. | Owner |
| R2 | **Versioned, with audit history and effective dates.** Every change makes a new version; nothing is edited in place. | Owner |
| R3 | **An extensible SLI model.** Availability and Latency now. Room for Business Success Rate, Message Processing Success, Queue Latency and Data Freshness later. | Owner |
| R4 | **Technical and business SLOs are kept apart.** | Owner |
| R5 | **Request eligibility is defined explicitly:** HTTP 4xx, client cancellations, health checks and retries. | Owner |
| R6 | **Error budgets** over a **30-day rolling window**, with consumption and burn rates. | Owner |
| R7 | **Pre-aggregated** into time buckets and histograms; raw traces aren't queried again and again. | Owner |
| R8 | **Missing telemetry is never success.** `InsufficientData` and `Unknown` states. | Owner |
| R9 | **Correlation suggests, people decide.** Co-firing alerts are suggested as one incident, with evidence and a weighted score. An operator accepts or dismisses; nothing merges on its own. | Owner |
| R10 | **Streaming only if the benchmark says so,** against targets set before it runs. | Owner |
| R11 | **No anomaly detection yet,** until there's enough telemetry and incident history. | Owner |
| D1 | **Versions and effective dates.** The file names each SLO by `Key`. A changed definition becomes version n+1, effective from its `EffectiveFrom` (default: when it's loaded; a future date waits). Each minute is counted under the version effective at that minute, so a target change doesn't rewrite the past. A key the file drops is retired, not deleted. `slo.create`, `slo.version` and `slo.retire` are audited and recorded as configuration changes on the timeline. | Mine |
| D2 | **SLI kinds.** Each kind is one class that counts a minute's events as eligible, good, bad and excluded (and why). `Availability` and `Latency` are implemented. `BusinessSuccess`, `MessageProcessing`, `QueueLatency` and `DataFreshness` are reserved names that the file refuses with "not supported yet", so adding one later is a new class and no change to budgets, alerts or storage. | Mine |
| D3 | **Category: `Technical` or `Business`.** It's set per SLO and fixed by its kind: Availability and Latency are Technical; the business kinds will be Business. The API filters by it, the Console shows them in separate sections, and their burn alerts carry it, so a webhook can route them differently. No Business kind is implemented yet, so the Business section will say so. | Mine |
| D4 | **Retention.** Minute buckets are kept 35 days: the 30-day window plus margin. | Mine |

## Request eligibility (R5)

What a request is: a span of kind `Server` (HTTP and RPC) or `Consumer` (messages) of the SLO's service in its environment, optionally only
the operations the SLO lists (`http.route` or span name). Each SLO can change any default below.

| Case | Default | Why | Setting |
|---|---|---|---|
| **Health checks:** `/health`, `/healthz`, `/ready`, `/live`, `/metrics` and the paths under them (route or path) | **Excluded** | Probes aren't users; they'd inflate the good count | `ExcludeHealthChecks`; `ExcludeOperations` adds patterns |
| **5xx**, or a span with status `Error` | **Bad** | The service failed | none |
| **429 Too Many Requests**, **503** | **Bad** | The service refused work it should have taken | none |
| **Other 4xx** (400, 401, 403, 404, 409, 422) | **Eligible, good** | The client's mistake; the service answered correctly. Counted, so the total stays honest | `ClientErrors: Good` (default), `Bad` or `Exclude` |
| **408 and 499** (client gave up or closed the connection) | **Excluded** | Nobody was waiting for the answer; we can't tell whether it would have been good | `Cancellations: Exclude` (default) or `Bad` |
| **Retries** | **Each attempt counts as its own request** | The service sees attempts, not the client's intent; each was served well or badly. A client's retry that succeeds after a failure leaves one bad and one good | none |
| **Consumer spans** | Bad when `juice.dispatch.result = failure` or status `Error`; good otherwise | Juice's own outcome | none |
| **Latency SLI** | Only eligible requests that succeeded; good when at or under the threshold | A failure's speed means nothing: Availability covers it | `ThresholdMs` |

Every excluded request is counted by reason, so the Console shows what was left out and why.

## Pre-aggregation (R7)

- **A worker** reads, once a minute per SLO, the closed minutes since its last pass. That's one bounded ClickHouse query, grouped by
  minute. It writes **one bucket per SLO version and minute** to PostgreSQL (`slo_buckets`):
  - counts of eligible, good and bad;
  - excluded counts by reason;
  - a **latency histogram** with fixed boundaries (log-spaced, 1 ms to 60 s, about 40 buckets), so any threshold reads from the histogram
    without querying traces again.
- **Late telemetry:** the last 5 minutes (`Slos:Lateness`) are counted again on each pass, then sealed.
- **Budgets and burn rates** are sums over buckets, all in PostgreSQL. A 30-day window is about 43,000 rows per SLO, read through an
  index.
- **Why not a ClickHouse materialized view:** eligibility is per SLO and versioned, so a view would have to keep every dimension
  eligibility might use. And the budget math sits beside the alert lifecycle in PostgreSQL. This adds no new stateful component: it uses
  the PostgreSQL and the worker lease the platform already has.

## Missing telemetry (R8)

- **A minute with no requests** is empty: no good, no bad. It's never counted as good.
- **A minute that couldn't be read** (ClickHouse down, timeout) isn't stored, so it's `Unknown`. The counter doesn't move past it, and
  reads it again on every pass until it succeeds.
- **A minute the tenant sent no telemetry in at all**, no spans and no metrics, is a gap in the pipeline, not a quiet minute: it isn't
  stored, and stays `Unknown`. This holds for an outage in the middle of the window too, not only after the newest telemetry. Both
  checks are per SLO service (its spans and its metrics), so one service stopping shows while the rest of the tenant sends.
- **Coverage** is the share of a window's minutes that were counted, from the SLO's first counted minute on: before the tenant's
  telemetry started there was nothing to miss, so a new deployment isn't `Unknown` for its first month.
- **SLO state:**
  - `Unknown` when more than 10% of the window's minutes are `Unknown`;
  - `InsufficientData` when the window has fewer eligible requests than `MinimumEvents` (default 1,000 for 30 days, and a minimum per
    burn window);
  - otherwise `Met`, `AtRisk` (over 75% of the budget used) or `Exhausted`.
- **Burn alerts never fire, and never resolve, on `InsufficientData` or `Unknown`.** They show it, as rules show `NoData` today.

## Budgets and burn rates (R6)

- **Target** t (for example 99.5%); **budget** = (1 − t) × eligible requests in the last 30 days; **used** = bad ÷ budget.
- **Burn rate over a window** = (bad ÷ eligible in that window) ÷ (1 − t). 1 means the budget runs out exactly at the end of the 30 days.
- **Alerts** (milestone 2), each needing both windows over the rate:

  | Alert | Rate | Long window | Short window | Severity |
  |---|---|---|---|---|
  | Fast burn | 14.4 | 1 h | 5 min | Critical |
  | Slow burn | 6 | 6 h | 30 min | Warning |
  | Slow burn | 1 | 3 days | 6 h | Warning |

- They go through the existing alert lifecycle, so notifications, silences, escalations and incidents work unchanged. Each carries the
  SLO's key, version, category and the windows' numbers as evidence.

## Correlation suggestions (R9)

- **Candidates:** alerts firing within 10 minutes of each other in the same environment.
- **Score:** the sum of the evidence's weights, at most 1. A pair at 0.5 or more is suggested.

  | Evidence | Weight |
  |---|---|
  | One depends directly on the other (observed dependency) | 0.40 |
  | Same host | 0.30 |
  | A change on either, or on a shared dependency, in the 30 minutes before | 0.30 |
  | Fired within 2 minutes of each other (sliding to 0 at 10 minutes) | up to 0.20 |
  | A shared downstream dependency that is itself unhealthy | 0.20 |

- **A suggestion lists every piece of evidence with its weight,** and is labelled correlation, not cause.
- **Accept:** an operator merges the alerts into one incident (a new one, or the open incident one of them is already in).
- **Dismiss:** recorded with a reason, and not suggested again for that pair while both alerts are open.
- Both are audited. **Nothing merges automatically.** The weights are a starting point. With no incident history to tune them yet, every
  decision is kept, to tune them later.

## Streaming (R10)

- **Targets, fixed before the run** (requirements §9 and this phase):
  - rule breach-to-alert p95 ≤ 2 minutes;
  - SLO fast-burn detection p95 ≤ 2 minutes after the short window's rate is crossed;
  - first webhook dispatch p95 ≤ 30 seconds;
  - evaluation lag never above one schedule interval.
- **Reference load:** the 30-minute reference telemetry rate, with 50 rules and 50 SLOs evaluated together.
- **Outcome:** if scheduled evaluation meets every target, the decision record says so with the numbers, and nothing is added. If one is
  missed, an ADR is needed before anything streams.

## Anomaly detection (R11)

Not in this phase. Revisit when there are at least 90 days of SLO buckets and 20 reviewed incidents (accepted or dismissed suggestions
count toward that). Those are the data any detector would need to be tuned and judged against.

## Milestones

1. **SLOs and budgets:**
   - the file with versions, effective dates and audit;
   - the SLI kinds and eligibility;
   - the bucket worker with histograms;
   - `Unknown` and `InsufficientData`;
   - budgets and burn rates in the API (`GET slos`, `GET slos/{key}` with history and versions);
   - a Console SLO page with Technical and Business apart.
2. **Burn-rate alerts:** the three alerts through the alert lifecycle, with evidence, and never on missing data.
3. **Correlation suggestions:** scoring with evidence, then accept and dismiss, audited; on the alert and incident pages.
4. **The streaming benchmark** and its decision record, and the record deferring anomaly detection. It also measures reading SLO
   status: `GET slos` sums each SLO's 30 days of minute buckets on every request (about 43,000 rows per SLO, 8.6 million at the
   200-SLO limit). If that misses its target at reference load, daily totals plus the minutes since replace it (milestone 1 review,
   round 3).

Each milestone ends with its tests, a mutation check, browser checks on the local stack and an acceptance record, as in Phase 8.

## Milestone 1: done, 9 November

- **Module:** `src/Juice.Observability.Slos`:
  - definitions, validation and the SLI kinds;
  - the provisioner (versions, effective dates, retire and restore, audit and timeline);
  - the counter (one ClickHouse query per SLO and version stretch, shared by SLOs on the same requests; minute buckets with a 27-slot
    histogram; per-service gap detection from all seven telemetry tables; the lateness recount; passes that narrow after a timeout;
    retention);
  - budgets and burn rates.
- **Storage:** `slos`, `slo_versions` and `slo_buckets` in PostgreSQL. Each bucket also stores its SLI's own total and good, scored
  under the version in effect for that minute, so budget sums are plain SQL. A minute without a row is unknown.
- **API:** `GET slos` (`category=Technical|Business`, `retired=true`) and `GET slos/{key}`: state, attainment, budget, the five burn
  windows, days, what counts, and versions.
- **Console:** an SLOs page with Technical and Business apart (Business says why it's empty), and an SLO page.
- **Local stack:** `deploy/local/slos.json` with three SLOs on the demo shop. Locally, `Slos__MaxMinutesPerPass` is 10080, so a new SLO
  counts its 30 days in five passes.

| Requirement | Evidence |
|---|---|
| Versions, audit, effective dates (R2) | `SloTests.A_changed_definition_is_a_new_version_from_its_effective_date_and_audited`: a future-dated version waits for its date, a backdated one is refused, changing the SLI needs a new key, retire and restore, `slo.*` audit and timeline entries |
| Extensible SLIs, technical and business apart (R3, R4) | Availability and Latency are classes of one interface; the four reserved kinds are refused; the API filters by category and the Console keeps two sections |
| Eligibility (R5) | `SloTests.Requests_are_counted_as_the_eligibility_rules_say`: real spans through the Collector. Health checks excluded, 404 good, 499 excluded, 5xx, 429, a failed dispatch and a 503 bad, latency over successes only, an operation filter, and the stricter policies |
| Pre-aggregation (R7) | The same test: one query, minute buckets, histogram-scored latency. Locally, 30 days of three SLOs counted in five passes |
| Missing telemetry (R8) | `Without_recent_telemetry_nothing_is_counted_and_the_minutes_stay_unknown` (real ClickHouse); `Minutes_after_the_newest_telemetry_stay_unknown`; `Missing_data_is_never_success`; the API test reads an uncounted window as `Unknown`, with no budget |
| Budgets and burn rates (R6) | `A_burn_rate_of_one_spends_the_budget_exactly_and_each_version_keeps_its_own_target`; `The_budget_is_met_at_risk_or_exhausted` |
| Through the browser | `console/e2e/slos.mjs`, 9 November: the demo shop's three SLOs under Technical, Business empty and saying why, an overspent budget worded as such, the burn windows, what counts, and the versions |
| Independent review, 10 November | Three rounds of `code-review` plus a confirmation round on the last fixes: 32 findings fixed (one false alarm, one moved to milestone 4). Among them: gaps detected per service from all seven telemetry tables, pending versions that can be withdrawn or re-dated, `EffectiveFrom` stored as UTC, passes that narrow after a timeout but never below `Lateness`, a service move that waits for its date and keeps recounting the minutes before it, one unreadable SLO not stopping the rest. 605 tests pass; `slos.mjs`, `approvals.mjs` and `agents-console.mjs` pass on the rebuilt stack |
| Mutation check | 18 of 18 caught (before the review; each review fix has its own test): health checks counted, client 4xx bad by default, 499 counted, 429 not bad, failed dispatch ignored, latency over failures, empty minutes not stored, the watermark ignored or missing, unknown minutes ignored, too few events ignored, one target for every version, no recount of recent minutes, a backdated version, dropped keys not retired, the newest version regardless of date, the at-risk threshold, reserved kinds accepted |

Not yet: burn-rate alerts (milestone 2). The demo shop fails half its charges, so locally every SLO reads Exhausted. That is expected
with this traffic, not a defect.
