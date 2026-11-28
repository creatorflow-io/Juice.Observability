# Phase 10, milestone 1: Console scope and tenant context

**Status:** built 12 November; review rounds 1 and 2 fixed and confirmed (below); awaiting owner acceptance at the Phase 10 gate. The trace-ID index gap is accepted and documented (owner, 12 November). Part of the [Phase 10 plan](phase-10-plan.md) (P1, P2).

**Roadmap gate lines this milestone answers:**
- the Console doesn't duplicate general Grafana exploration without a product need;
- tenant context is preserved.

## Workflows, pages and journeys

The roadmap's Phase 10 Console workflows, the pages that serve them, and the browser journey that walks each one on the built image
(`console/e2e`). Every page in `OBSERVABILITY_ROUTES` is now walked by at least one journey.

| Roadmap workflow | Pages | Journey |
|---|---|---|
| **Topology** | Dependencies (`/topology`, an edge, a node) | `journey.mjs` (topology, topology-edge) |
| **Health** | Overview, Services and a service (health per environment), Resources and a resource (health, instances on a host) | `journey.mjs` (overview, services, service-payments); `inventory.mjs` (every resource type, a host's page) **new** |
| **Alerts and incidents** | Alerts and an alert (evidence, related changes, suggestions), Incidents and an incident, Rules, Silences, Notifications, Timeline | `journey.mjs` (alerts, rules, alert-related); `correlation.mjs`; `inventory.mjs` (Timeline, a service's timeline) **new** |
| **Enrollment** | Agents and an agent (enrollment tokens, re-enrolling, revoking) | `agents-console.mjs`; `agent-reenroll.mjs` |
| **Approvals** | Approvals; an agent's Requests | `approvals.mjs`; `remediation.mjs` |
| **Action history** | An agent's Requests and Commands (state, result, who asked and who decided) | `remediation.mjs`; `approvals.mjs`; `bundle.mjs` |
| SLOs (Phase 9) | SLOs and an SLO | `slos.mjs` |
| Capacity and load runs (X2) | A service's Capacity tab, Load runs | `journey.mjs` (capacity, load-runs) |

## General exploration: the product need (P1, owner, 12 November)

Traces, Logs and Metrics overlap with Grafana's Explore. They stay, on the product decision in the
[Console UI requirements §1](juice-observability-console-ui-requirements.md): Juice Observability must work without Grafana, Grafana is
optional, and the core investigation (a failing service → its error traces → the span → its logs and metrics → the dependency) must be
possible entirely in the Console. The owner confirmed it on 12 November: the Console works independently of Grafana.

The line Phase 10 holds: the Console's telemetry pages serve that investigation (scoped to a service, a trace, a span or a window, reached
from the domain pages). Phase 10 adds no general exploration: no dashboard builder, ad-hoc query language or Explore-style pages. Those
stay Grafana's, for customers who run it.

## Tenant context (P2)

One deployment serves one tenant (Phase 0, D1–D2): the API takes the tenant from the token, refuses any other in the path, and refuses a
database that belongs to another tenant. What can be shared is ClickHouse: the local stack's test Collector writes `local-test` telemetry
beside the deployment's tenant, and a customer could point two deployments' Collectors at one ClickHouse. So the checks are about what
reaches a person through the Console, the API and Grafana.

| Check | How | Result, 12 November |
|---|---|---|
| **Another tenant's telemetry never shows** | `e2e/tenant.mjs`: a probe service goes through the test Collector (tenant `local-test`), a control through the main one, both named after the run, each with server spans, a log record and a counter. The reads and pages where the probe would appear if scoping broke: the API's services, health, traces and metrics (every service), logs and search, and the Services, Traces, Logs and header-search pages. Each must also show the control, so it's known to work. Traces are narrowed to the run's operation and metrics to the last 15 minutes, and a cut answer fails, so other traffic can't push the probe out of view | The control everywhere; the probe nowhere |
| **The check can fail** | `PROBE_PORT=4318 node e2e/tenant.mjs` sends the probe to this tenant instead | Reported on all 6 of those API reads and all 4 pages; exits 1 |
| **The other reads** | The rest of what the Console reads (a service, its traces, metrics and stats, topology, dependencies, timeline, alerts): scoped to the control's ID, or to things the probe doesn't produce. Smoke checks: each answers and never names the probe, but can't prove scoping | All answer; the probe in none |
| **A user without a role on this tenant sees nothing** | `nora` (signed in, no platform role) opens 18 pages and deep links, and calls all 14 reads above. Each page must stay in the Console and make at least one refused API call, so a page that never loaded can't pass | Every API call 403, no tenant data on any page |
| **Grafana sees only this tenant** | `tests/verify.sh`: this run's `local-test` records are hidden from `grafana_reader`, it reads no other tenant in traces, metrics and logs, and every `otel` table with the tenant stamp has the policy | Passes; with the `otel_logs` policy dropped, 3 checks fail (Grafana then saw 24.3 M rows of other tenants' logs) |
| **Links out keep the context** | The Grafana link carries the service and the window; the tenant is enforced by ClickHouse (below), not by the link. Notification links (`Notifications:ConsoleUrl`) open this deployment's Console, where the API applies the signed-in user's tenant | By construction; the Grafana side is checked by `verify.sh` |

The API side was already covered by tests named for it: `ApiTests.Path_tenant_must_be_the_callers_tenant`,
`TelemetryApiTests.Another_tenants_telemetry_is_never_returned`, `DiscoveryTests.Telemetry_of_another_tenant_is_never_read_and_forged_tenants_are_restamped`,
`RuleEvaluatorTests.Rules_see_only_their_tenants_telemetry`, `IncidentApiTests.Another_tenants_incident_stays_out_of_reach`,
`ChangeApiTests.The_timeline_joins_changes_health_and_alerts_within_the_tenant` and
`NotificationFlowTests.Another_tenants_allowlist_opens_nothing_for_this_one`.

### Found and fixed: Grafana read every tenant

Grafana's ClickHouse user, `grafana_reader`, could read all of `otel.*`, and the Juice overview dashboard's queries don't filter by
tenant. On the local stack it saw three tenants: `local-dev` (14.6 M spans), `local-test` (27.9 M) and `other-tenant` (3,196, left by
tests). An "Open in Grafana" link for a service whose name another tenant also uses showed both tenants' data.

Fixed in ClickHouse, so it holds whatever Grafana asks: a row policy `grafana_tenant` on each table that carries the Collector's tenant stamp
(`ResourceAttributes['juice.platform_tenant_id']`) limits `grafana_reader` to the deployment's tenant (`clickhouse/init-users.sh`).

- **Every other user has an explicit unrestricted policy** (`all_tenants`: the Collector's writer, the platform's reader, admin), so
  nothing depends on ClickHouse's rule for users without one, and `clickhouse/access.xml` sets that rule to hide rows
  (`users_without_row_policies_can_read_rows` false), so a missing policy fails closed. Policies are created if missing, then
  altered in place, never replaced (a replace removes and re-adds, a moment with no policy). The platform's reader
  still reads every tenant: it filters per query, as the tests above show.
- **Not database-wide, and the trace-ID index isn't covered** (accepted, below): a policy on `otel.*` fails on
  `otel_traces_trace_id_ts`, the Collector's trace-ID index, which has no `ResourceAttributes`.
- **A new table is caught:** `verify.sh` fails if a table with the tenant stamp has no policy, for example after a Collector upgrade adds one.
- **Existing installs get it:** ClickHouse runs `init-users.sh` by itself only on an empty volume, so a `clickhouse-access` service
  runs it (idempotent) each time it starts, then idles healthy; the Collectors, the API and Grafana wait for it. An upgrade that only changes the
  script wouldn't restart that service, so the runbook's upgrade step runs `scripts/apply-clickhouse-access.sh` every time.
  - It idles rather than exits because `docker compose up --wait` fails on a container that exits, even with 0.
  - It clears its health marker first, so a restart isn't healthy before the script has run again.
  - Each run brings passwords, the profile's caps, grants and policies to what the script says (`CREATE … IF NOT EXISTS`, then
    `ALTER`), so a rotated password or a changed cap reaches an existing volume. A grant or policy taken out of the script stays
    until removed by hand.
  - It waits up to 120 s for ClickHouse on the network (on a fresh volume ClickHouse first runs on 127.0.0.1 only), and its
    healthcheck allows 150 s, so that wait isn't cut short.
  - Checked on a fresh volume, on the existing one, and starting the API alone after removing the policies.
  - The policy is new after 1.2.0: on 1.2.0, Grafana reads every tenant in a shared ClickHouse.
- **The tenant ID is checked** with the same rule as `scripts/init.sh` before it reaches SQL; a value like `x'; DROP` stops the script.
- **Cost:** the filter reads the tenant out of `ResourceAttributes` for each row Grafana scans. On a month of spans (42 M), a by-service
  p95 query took 0.27–0.30 s as Grafana against 0.05–0.08 s unfiltered; a log text search 0.15–0.18 s against 0.09–0.11 s. Far under
  Grafana's 50 s timeout. A materialized tenant column would be cheaper but changes tables the Collector owns; the scale tests
  (milestone 3) measure Grafana queries at higher volume and decide whether it's needed.

### Found and fixed: global search answered for another tenant's trace ID

Searching a trace ID looked it up in `otel_traces_trace_id_ts`, the exporter's per-trace time index, which carries no tenant.
A viewer could learn whether a trace ID existed in another tenant sharing the ClickHouse (not its content: opening it returned 404).
Search now does what the trace page already did: the index only narrows the time range, and the trace is a hit only if this
tenant has a span of it in `otel_traces`. Both share one helper (`TraceBoundsAsync`), whose comment says never to answer from
the index alone. `TelemetryApiTests.Another_tenants_telemetry_is_never_returned` now searches the other tenant's trace by ID and
by traceparent: it failed before the fix and passes after; the search for the tenant's own trace ID still finds it.

The trace page and search still make a second query when the index knows a trace ID, so their timing differs a little between another
tenant's trace and an unknown one. Accepted: it tells less than the index itself already tells Grafana (below).

### Accepted (owner, 12 November): Grafana can list every tenant's trace IDs

The trace-ID index `otel_traces_trace_id_ts` (filled by `otel_traces_trace_id_ts_mv`, which Grafana can read too) has no tenant column, and Grafana's trace view needs it: the ClickHouse plugin (4.22.1)
reads a trace's start and end from it before reading the spans (seen in ClickHouse's query log). So it can't take the row policy,
and hiding it from `grafana_reader` would break opening a trace in Grafana. What a Grafana user can do with it: list the trace IDs,
start and end times of every tenant in this ClickHouse (on the local stack, 2.26 M in a day, almost all `local-test`). No span
content, names or attributes.

It matters only where one ClickHouse is shared by more than one deployment. The options:

| Option | Closes it | Cost |
|---|---|---|
| **Accept and document** (chosen) | No | None; the docs say where the boundary stops |
| A Juice-owned tenant-stamped index, `(TraceId, Tenant, Start, End)`, filled by a materialized view from `otel_traces`, and the policy on it; the Collector's index taken from Grafana | Yes, if the plugin can be pointed at it (not checked: it names `<traces table>_trace_id_ts` itself) | A second write path on every span; a table the platform owns beside the Collector's |
| One ClickHouse per deployment as the supported layout | Yes, by layout | Customers who share one lose that option |

**Owner, 12 November: accept and document.** The deployment guide says to give each deployment its own ClickHouse unless this is acceptable.

## Review round 1, 12 November

| # | Finding | Fixed |
|---|---|---|
| 1 | Grafana can list other tenants' trace IDs and times from the trace-ID index; the README said the policy held for Explore | Stated in the README, `init-users.sh` and above; accepted by the owner |
| 2 | `init-users.sh` re-ran but changed nothing on an existing volume: `CREATE … IF NOT EXISTS` left old passwords and caps | Each user and the profile is `ALTER`ed after its create; the docs say what isn't removed |
| 3 | `tenant.mjs`: "traces, every service" read the newest 50 traces, so busy traffic could push the probe out and pass a broken filter | The read and the Traces page are narrowed to the run's operation |
| 4 | `tenant.mjs` sent the probe a counter but nothing looked for it | New leak read: metrics of every service in the last 15 minutes; a cut answer fails |
| 5 | `tenant.mjs`: nora's pages passed if they never loaded | Each must stay in the Console and make a refused API call |
| 6 | Search timing tells whether the index knows a trace ID | Accepted (above) |
| 7 | A table taken out of the policy list keeps its old policies | Said in `init-users.sh` and the README |
| 8 | `inventory.mjs` clicked the timeline's first link, which may not be an alert | Clicks the first alert link |

## Review round 2, 12 November

Round 1's fixes checked against the code and the live ClickHouse: correct and complete.

| # | Finding | Fixed |
|---|---|---|
| 1 | `CREATE ROW POLICY OR REPLACE` removes and re-adds a policy, a moment on every start in which `grafana_reader` has none and, by ClickHouse's default, reads every row | Each policy is created if missing, then `ALTER`ed in place; `clickhouse/access.xml` makes a user without a policy read nothing |
| 2 | `verify.sh` only checked rows were hidden, so a policy hiding everything (a typo in the tenant, the wrong user) passed | It checks each policy's filter and users, and that `grafana_reader` counts the same spans of this tenant in the last hour as admin (and that there are some) |
| 3 | The index's materialized view `otel_traces_trace_id_ts_mv` is readable too, and the docs didn't name it | Named in the README, `init-users.sh` and above |
| 4 | The Collectors didn't wait for `clickhouse-access`, so after a password rotation they could try the new password before it was set | They wait for it |

## Confirmation round, 12 November

No blocking issues: round 2's fixes checked against the code and the live ClickHouse (policies, `access.xml` in effect, the compose
dependency through `extends`, a policy on a table that doesn't exist yet on a fresh volume). Three small points fixed: the docs now
say the Collectors wait for `clickhouse-access` too; the runbook says step 2 already recreates ClickHouse on an upgrade from 1.2.0;
and the span-count check's window ends 5 minutes back, so a long span inserted between its two reads can't make it fail.

Tests after the last change: `TelemetryApiTests` 20/20, `tests/verify.sh` all checks (both new policy checks fail with a policy
set to `USING 0`), `e2e/tenant.mjs` and its self-test, `e2e/inventory.mjs`.
