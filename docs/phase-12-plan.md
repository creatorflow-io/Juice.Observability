# Phase 12 plan: any OpenTelemetry metric

**Status:** approved by the owner, 26 November 2026, with the recommendations in §3. It follows the owner's request of the same day:
"Observability could work well with any software system that supports OpenTelemetry, all the rules … configurable depending on the real
metrics data". A check against the code (1.4.0) found that statement holds for traces, but not yet for arbitrary metrics or for hosts'
CPU and disks (§1).

**Goal:** any system that sends OpenTelemetry can be alerted on, whatever its language or framework: a rule can watch any metric it sends,
and every host's CPU, memory and disks are covered.

**Deliverables:**
1. **MetricThreshold**, a rule template over any OpenTelemetry metric (gauge, sum, histogram) with a fixed threshold, still a typed
   definition with no user SQL (D5).
2. **HostCpu** and **HostDisk** templates, with the Collector scrapers they need.
3. A **rule editor** in the Console for Admins, with a preview of what a rule would have said over the last 24 hours.
4. A non-.NET, non-Juice **sample service** that proves the claim.

**Gate:**
1. Proof points 1–6 (§6), each shown by a named test or a recorded run.
2. The sample service gets discovery, health, ErrorRate, LatencyP95, a MetricThreshold rule on its own metric and host rules, with no
   change beyond standard OpenTelemetry.
3. The 1× and 5× scale steps rerun with the new rules added, within the §9 targets.
4. `telemetry-requirements.md`, `deployment.md`, `scale-tests.md` and the roadmap updated.
5. No new infrastructure without a trigger-table row and an ADR.

---

## 1. Why: where 1.4.0 leaves it

| Area | Today | Gap |
|---|---|---|
| **Rule templates** | 8 typed templates, each a platform-owned ClickHouse query (`RuleTemplates.cs`). ErrorRate, LatencyP95 and ThroughputAbsent read Server and Consumer spans, so they work for any OpenTelemetry service. HostMemory reads `system.memory.usage`. MediatorErrors, ConsumerFailures and DeliveryFailureRate read the Juice signal pack | **No rule can watch an arbitrary metric** (a queue depth, a JVM GC pause, a business counter). Host rules cover memory only (D5, accepted 2026-11-05) |
| **Rule settings** | Threshold, recovery threshold, window, schedule, minimum samples, for and recovery durations, cooldown, severity, scope, through `POST/PUT /rules` and `rules.json`; the Console lists rules read-only | The Console can't edit rules |
| **Metric catalog** | `TelemetryEndpoints` lists metric names, types, units and descriptions per tenant, and validates names with `MetricNamePattern` | Nothing builds a rule from it |

---

## 2. Scope

**In:** the four deliverables, their API and Console changes, their tests, the sample service in `deploy/local/tests`, Collector
configuration for the `hostmetrics` `cpu` and `filesystem` scrapers, and the scale reruns.

**Out:**
- User-written queries of any kind (SQL, PromQL, expressions). D5 stays: definitions are typed and the platform owns every query.
- Rules over logs.
- Exponential histograms and summaries in MetricThreshold. They are stored, but no template reads them until a pilot needs one.
- Anything outside the public core (Architecture Foundation §2.2).

---

## 3. Owner decisions

| # | Question | Decision | Why |
|---|---|---|---|
| p12-d1 | **When?** | **Now, as Phase 12**, before any Cloud work. | It makes the product's first claim ("any OpenTelemetry system") true, and it changes no trust boundary. |
| p12-d2 | **How is a metric rule written?** | **Typed:** metric, aggregation, up to 5 attribute filters, subject, comparison and threshold. No expressions. | D5's safety holds: no injection, bounded cost, a query the platform tested. The Console builds the form from the metric catalog. |
| p12-d3 | **What does a metric rule alert on?** | **A service (default) or a host**, within the existing row limit per pass. | One alert per thing an operator owns. |
| p12-d4 | **A rule editor in the Console?** D5 made it read-only. | **Yes**, for Admins, through the same API and audit; Operators and Viewers read. Rules from files (`ManagedBy = config`) stay read-only in the Console and say so. | "Configurable" means configurable by the people who run it. The API already validates, versions (`DefinitionHash`) and audits. |

---

## 4. Requirements

| # | Requirement | By |
|---|---|---|
| R1 | **Definition** (in `RuleDefinition`, template `MetricThreshold`):<br>• `metric`: a name matching `MetricNamePattern`;<br>• `aggregation`: for a gauge `last`, `avg`, `max` or `min`; for a monotonic sum `rate` (per second) or `increase`; for a non-monotonic sum `last`; for a histogram `p50`, `p90`, `p95`, `p99`, `avg` or `rate` (of the count);<br>• `filters`: up to 5, each an attribute key and `equals` or `in` (string values, at most 20);<br>• `subject`: `service` or `host`;<br>• `comparison`: `above` or `below`;<br>• the existing threshold, window, for, recovery and cooldown fields. | p12-d2, p12-d3 |
| R2 | **Kind from the catalog.** The metric's type and unit come from the tenant's metric catalog. An aggregation that doesn't fit the type is refused with the reason. A metric not yet seen is accepted and shown as *Never observes*, as signal packs do. | Mine |
| R3 | **Queries.** One platform-owned query per type. The metric name, attribute keys and values are always parameters (`Attributes[{k:String}]`), never spliced into the text. Every query filters on the Collector-stamped tenant, is bounded by the window and the row limit, and runs under the workers' ClickHouse profile. | D5 |
| R4 | **Counters.** Rate and increase use the existing cumulative-counter rule (`CounterIncrease`): the increase from the series' last value before the window, a first point counted from zero, a reset treated as a restart, and a series with no point in the window left out (*no data*, never zero). Delta temporality is summed directly. | Mine |
| R5 | **Histograms.** Percentiles from explicit bucket counts, merged over the window (the increase of cumulative buckets) and interpolated linearly within the bucket. The answer states its bucket bounds. | Mine |
| R6 | **Evidence.** A firing alert carries the metric, aggregation, filters, value, unit and window. | Mine |
| R7 | **HostCpu**: utilisation from `system.cpu.utilization`, or from the rate of `system.cpu.time` (non-idle over all). **HostDisk**: used share per mount from `system.filesystem.usage` with `state`, or `system.filesystem.utilization`. Per host, with HostMemory's no-data rules. | Mine |
| R8 | **Collector**: the `hostmetrics` receiver enables the `cpu` and `filesystem` scrapers in every shipped configuration, excluding pseudo and read-only filesystems by default. | Mine |
| R9 | **Rule editor** (p12-d4): the form built from the metric catalog, with type and unit; a preview of what the rule would have said over the last 24 hours (a dry evaluation, read-only, bounded by the row limit); errors per field from the API; `If-Match` on the definition hash. | p12-d4 |
| R10 | **Extension seam:** the template's threshold is read through one interface, so that other threshold kinds can be added outside the core later without changing templates. | Mine |

---

## 5. Milestones

Each milestone ends with its tests, a commit and a tracker update. The review loop runs per milestone (code review after each, two
automatic fix rounds, a third that confirms), unless the owner says otherwise.

- **M1 MetricThreshold: gauges and sums** (R1–R4, R6, R10): definition, validation, the gauge and sum queries, counter semantics,
  *Never observes*, API, evidence. Tests: per aggregation on seeded ClickHouse data; counter resets and first points; injection tests on
  names, keys and values.
- **M2 Histograms** (R5): bucket percentiles with their bounds. Tests: against exact values from the same data.
- **M3 Hosts and the sample service** (R7, R8): HostCpu, HostDisk, Collector scrapers, and the non-.NET sample service in
  `deploy/local/tests`.
- **M4 Rule editor** (R9): the Console editor with the 24-hour preview. Journeys: create, edit and disable as Admin; read-only as Operator
  and Viewer; file-managed rules read-only.
- **M5 Scale and gate:** the 1× and 5× reruns with 50 MetricThreshold rules added; the sample-service walkthrough; docs; acceptance record.

---

## 6. Proof points and their tests

| # | Proof point | Test |
|---|---|---|
| 1 | A non-.NET, non-Juice service gets discovery, health, ErrorRate, LatencyP95, a MetricThreshold rule on its own metric and host rules, with standard OpenTelemetry only | sample-service walkthrough (M5) |
| 2 | A rule on any gauge, counter or histogram gives the right value; counter resets and first points don't fire falsely; no data is never zero | `MetricThresholdTests` |
| 3 | Nothing a user types reaches a query as text | injection tests; review that every query is parameterised |
| 4 | Histogram percentiles match exact values within the bucket bounds they report | `HistogramPercentileTests` |
| 5 | A rule changed in the Console is validated, versioned and audited like an API change; file-managed rules can't be changed | `RuleEditorTests`; M4 journeys |
| 6 | The new rules fit within the §9 targets at 1× and 5× | `scale-tests.md` rerun (M5) |

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Metric rules are expensive queries over large metric tables | Bounded windows and row limits, the workers' ClickHouse profile, a per-rule query time in the platform's metrics, and the M5 scale run |
| Histogram percentiles misread as exact | The bucket bounds are part of the value, and the Console shows them |
| The editor lets a rule be broken by accident | One API with validation, versions (`If-Match`) and audit; the 24-hour preview before saving |

---

## 8. Build board

| Item | Title | Milestone |
|---|---|---|
| p12-01 | MetricThreshold definition, validation, catalog check, threshold seam | M1 |
| p12-02 | Gauge and sum queries; counter semantics; evidence | M1 |
| p12-03 | Histogram percentiles | M2 |
| p12-04 | HostCpu, HostDisk; Collector scrapers | M3 |
| p12-05 | Non-.NET sample service | M3 |
| p12-06 | Console rule editor with the 24-hour preview | M4 |
| p12-g1 | Proof points 1–6; 1× and 5× reruns; docs and acceptance record | M5 |
