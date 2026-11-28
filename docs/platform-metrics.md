# Platform metrics

What Juice Observability measures about itself (Phase 11 milestone 1, [plan](phase-11-plan.md) R1–R5).

## Where the metrics go

The platform exports its metrics over OTLP every 15 s to the Collector's **platform pipeline**:
- **The route:** receiver `otlp/platform` on port 4319, with a token of its own (`secrets/platform-otlp-token`). An application's token is
  refused there.
- **The tenant:** the pipeline stamps the reserved tenant `juice-platform` instead of the deployment's.
- **Why:** they never enter the tenant's own data. Discovery, health, rules, SLOs, the Console's tenant pages and Grafana's
  `grafana_tenant` policy all filter on the deployment's tenant (owner, Q1).
- **The reserved key:** a deployment can't take `juice-platform` as its own. `scripts/init.sh`, `clickhouse/init-users.sh` and the API at
  startup all refuse it. A test checks the API; `tests/verify.sh` checks `init-users.sh`.
- **The Collector's own metrics:** scraped from its `:8888` page every 15 s into the same pipeline. They report received, refused,
  exported and failed per signal, each exporter's queue against its capacity, and memory.
  - **Service name:** its own, `otelcol-contrib`, with its compose service as `service.instance.id`.
  - **Scraped, not pushed,** so the Collector needs no token or TLS connection to itself.
  - **The test Collector** (`--profile test`) shares the configuration, so its metrics are there too, as instance
    `otel-collector-test`. Leave it out when reading the deployment's Collector.
- **Where they land:** ClickHouse's `otel_metrics_*` tables, beside the tenant's, with
  `ResourceAttributes['juice.platform_tenant_id'] = 'juice-platform'`.
  - The platform's service name is `juice-observability-platform`.
  - Each replica is its `service.instance.id`, the container's host name.

**A meter provider of its own.** The platform's exporter isn't part of the host's OpenTelemetry setup. A host that uses OpenTelemetry for
its own metrics keeps its instruments, views and exporters, and neither side sees the other's metrics. The platform's allowlist applies only
to the platform's meters.

Settings, under `Telemetry` (all optional):

| Setting | Default | |
|---|---|---|
| `Telemetry:Enabled` | `true` | Off: nothing is exported (owner, R14: on by default) |
| `Telemetry:Otlp:Endpoint` | none | The platform receiver, such as `https://otel-collector:4319` |
| `Telemetry:Otlp:TokenFile` | none | A file holding the platform token; required |
| `Telemetry:Otlp:CaFile` | none | A CA certificate to trust for the receiver's TLS, besides the system's |
| `Telemetry:ExportInterval` | `00:00:15` | A .NET time span (`hh:mm:ss`), at least 1 s; `15` alone means 15 days |
| `ClickHouse:MonitorConnectionString` | none | The sampler's ClickHouse user, `platform_monitor`. Without it ClickHouse isn't sampled |
| `Telemetry:CollectorHealthUrl` | none | The Collector's health check, such as `http://otel-collector:13133/`. Without it the Collector isn't checked |

**Configuration problems stop the export, never the platform.** Nothing is exported, and the startup log says why
(`Platform metrics: ...`), when:
- a `Telemetry` value doesn't convert (`Telemetry:Enabled=yes`, `Telemetry:ExportInterval=15s`);
- the endpoint is missing or isn't an absolute http or https URL;
- the export interval is under 1 s;
- the token file is missing or can't be read;
- the CA file can't be read.

The exporter starts before the workers and stops after them, so a worker's last pass on shutdown is still exported. Without the monitor connection string, ClickHouse
isn't sampled. The compose stack (`docker-compose.yml`) and `scripts/run-api.sh` set all of them.

**A host that wants the platform's metrics in its own pipeline:**
1. Set `Telemetry:Enabled=false`.
2. Call `AddJuiceObservabilityMeters()` on its own `MeterProviderBuilder`.
3. Export them to the platform receiver, not to the tenant's.

**What is exported:**
- **Meters:**
  - the platform's own, `Juice.Observability`;
  - the Juice framework's, `Juice` and `delivery.metrics`;
  - ASP.NET Core (`Microsoft.AspNetCore.Hosting`), HttpClient (`System.Net.Http`), the .NET runtime (`System.Runtime`) and `Npgsql`, which
    .NET 10 and Npgsql publish themselves.
- **Attribute keys** are cut to an allowlist on every one of those meters' instruments (`PlatformTelemetry.AllowedKeys`, R3):
  - **Kept:** the platform's own fixed sets (`worker`, `caller`, `outcome`, `kind`, `state`, `user`, `disk`, `table`, `subject_type`,
    `template`, `queued`, `reason`) and the standard instruments' bounded keys (`http.request.method`, `http.response.status_code`,
    `http.route`, `error.type`, `gc.heap.generation`, `cpu.mode`, `db.client.connection.state`).
  - **Dropped:** everything else, such as a webhook receiver's host (`server.address`), URLs, and a connection pool's name (it comes
    from its connection string).
- **Histogram buckets:** the platform's own duration histograms use buckets from 5 ms to 5 min. The standard instruments keep their own.

## Requirements §9 against what measures it

Requirements line 254 asks for these. "Platform" is meter `Juice.Observability`; "Collector" is the scraped `otelcol_*` metrics.

| Requirement | Metric | Source |
|---|---|---|
| Received, exported, dropped telemetry | `otelcol_receiver_accepted_*`, `otelcol_receiver_refused_*`, `otelcol_exporter_sent_*`, `otelcol_exporter_send_failed_*`, `otelcol_exporter_enqueue_failed_*` (a series appears once it moves) | Collector |
| Collector queue and memory | `otelcol_exporter_queue_size` and `otelcol_exporter_queue_capacity` (per exporter); `otelcol_process_memory_rss` | Collector |
| ClickHouse query latency | `juice.observability.clickhouse.query.duration` {caller, outcome}, with `juice.observability.clickhouse.query.read_rows` and `juice.observability.clickhouse.query.read_bytes`; ClickHouse's own view, `juice.observability.clickhouse.server.query.p95` {user, kind} | Platform; sampler |
| ClickHouse insert latency | `juice.observability.clickhouse.server.query.p95{user=otel_writer, kind=Insert}`; and the count, `juice.observability.clickhouse.server.queries` | Sampler |
| PostgreSQL health | `juice.observability.postgres.up`, `juice.observability.postgres.sample.duration`, `juice.observability.postgres.size`; Npgsql's `db.client.*` | Sampler; Npgsql |
| Evaluation lag | `juice.observability.rules.lag`; `juice.observability.slos.burn_lag`; `juice.observability.discovery.watermark_lag`; `juice.observability.health.evidence_age`; each worker's `juice.observability.worker.since_last_pass` and `juice.observability.worker.pass.age` | Platform; sampler |
| Alert transitions | `juice.observability.alerts.transitions` {template, state} | Platform |
| API latency | `http.server.request.duration` {http.route, http.request.method, http.response.status_code} | ASP.NET Core |
| Availability (§9, measured) | `juice.observability.process.uptime`; `/health/ready` | Platform |

## The platform's own instruments

Meter `Juice.Observability`. A test checks that every instrument declared in code is in these tables, and that each goes through the
exporter.

### Workers

Each pass of discovery, health, SLO counting, notifications, action requests and the sampler is reported. Its outcome is
one of:
- `success`;
- `partial`: done, but part failed, such as some SLOs of many, or one store of the sampler's two;
- `failure`: including a pass in which every SLO, or every store, failed;
- `canceled`: the host stopping.

The rule scheduler has no single pass: it shows as busy slots instead.

| Instrument | Type, unit | Attributes | A bad value means |
|---|---|---|---|
| `juice.observability.worker.passes` | counter | worker, outcome | `failure` or `partial` rising: the worker's log says why |
| `juice.observability.worker.pass.duration` | histogram, s | worker | A pass near or past its interval: the worker can't keep up (R18) |
| `juice.observability.worker.pass.age` | gauge, s | worker | Growing: a pass is stuck, most likely in a query |
| `juice.observability.worker.since_last_pass` | gauge, s | worker | Past a few intervals: the worker stopped. Reported while the replica holds the lease (from its first pass after taking it), and always for the webhook dispatcher and the sampler, which run in every replica |
| `juice.observability.worker.lease` | gauge | worker | No replica at 1 for a worker: nobody runs it. Not reported for workers without a lease |
| `juice.observability.rules.busy` | gauge | – | At `rules.slots` with `rules.waiting` growing: raise `Rules:MaxConcurrency`, or find the slow template (`rules.duration`) |
| `juice.observability.rules.waiting` | gauge | – | Due evaluations waiting for a slot |
| `juice.observability.rules.slots` | gauge | – | `Rules:MaxConcurrency` |
| `juice.integration.runs` (`juice_integration_runs_total`) | counter | plugin, instance, outcome (`succeeded`, `partial`, `failed`) | `failed` rising for an instance: its state and last failure say why. `instance` is the instance id (N format), never its name |
| `juice.integration.failures` (`juice_integration_failures_total`) | counter | plugin, instance, reason (the failure kind: `connectivity`, `authentication`, `permission`, `ratelimited`, `configuration`, `remote`, `timeout`, `internal`) | `authentication`, `permission` or `configuration` means the instance is Failed and needs an admin; `timeout` rising: raise `Modules:integrations:RunTimeout` or fix the plugin |
| `juice.integration.freshness` (`juice_integration_freshness_seconds`) | gauge, s | plugin, instance | Past three times the instance's interval: it hasn't collected for a while (the Console shows it as stale). Kept in memory by the leased `integrations` worker, so it is reported by the replica that holds the lease, and a deleted instance's series ends with the next pass. At most `MaxInstances` (100) series per tenant |
| `juice.observability.discovery.runs` | counter | outcome | `failure` rising |
| `juice.observability.discovery.duration` | histogram, s | – | Near `Discovery:Interval` (30 s) |
| `juice.observability.discovery.created` | counter | kind | – |
| `juice.observability.discovery.truncated` | counter | kind | Rising: windows or values cut at their limits |
| `juice.observability.rules.truncated` | counter (`{cut}`) | template | Rising: a rule's query reached its row limit and left out its last subject, so that subject isn't evaluated. Counts cuts, not subjects (one per query that was cut) |
| `juice.observability.discovery.retirements` | counter | change (`retired`, `restored`) | Many `retired` at once: telemetry stopped arriving, not services going away |
| `juice.observability.discovery.watermark_lag` | gauge, s | – | Past `Discovery:Lag` (60 s) by a minute or more: discovery is behind |
| `juice.observability.health.evaluations` | counter | outcome | `failure` rising |
| `juice.observability.health.duration` | histogram, s | – | Near `Health:Interval` (30 s) |
| `juice.observability.health.evidence_age` | histogram, s | subject_type | Past a minute: health freshness at risk |
| `juice.observability.health.transitions` | counter | subject_type, state | – |
| `juice.observability.rules.evaluations` | counter | template, outcome | `failure` or `timeout` rising |
| `juice.observability.rules.duration` | histogram, s | – | Near a rule's schedule |
| `juice.observability.rules.lag` | histogram, s | template, queued | More than a few seconds: rules wait too long for a slot |
| `juice.observability.rules.skipped` | counter | reason | `overrun` rising: a rule is slower than its schedule |
| `juice.observability.rules.queued` | counter | template | Rises in a healthy deployment; the lag matters |
| `juice.observability.alerts.transitions` | counter | template, state | – |
| `juice.observability.slos.counted` | counter | outcome (`counted`, `idle`, `failed`) | `failed` rising |
| `juice.observability.slos.duration` | histogram, s | – | Near `Slos:Interval` (30 s) |
| `juice.observability.slos.burn_lag` | histogram, s | – | Past a minute: passes take longer than the minute they have |
| `juice.observability.notifications.held` | counter | outcome | `dropped` rising |
| `juice.observability.notifications.escalations` | counter | outcome | `unreachable` rising |
| `juice.observability.notifications.released` | counter | outcome | `dropped` rising: what the notification pipeline held under a silence and dropped after it |
| `juice.observability.notifications.refused` | counter | call, code | any: the notification pipeline refused a request the platform made; the log names it |
| `juice.observability.notifications.deadman` | counter | cause, event, outcome | any `firing` sent: the notification pipeline stopped keeping up (`stalled`, `failing`, `no-success`, `stuck-in-retry`, `unreadable`); `failed`: the alarm itself couldn't be posted |
| `juice.observability.process.uptime` | gauge, s | – | Resets: the process restarted |

### ClickHouse queries the platform makes

| Instrument | Type, unit | Attributes | A bad value means |
|---|---|---|---|
| `juice.observability.clickhouse.query.duration` | histogram, s | caller, outcome | One caller slow: its queries. Every caller slow: ClickHouse itself. `timeout` rising: queries cut by the caller's own deadline (`Rules:EvaluationTimeout`, `Slos:QueryTimeout`), ClickHouse too slow. `canceled`: the host or the request stopping. `busy`: an API query refused for load, by the API (`ClickHouse:Api:MaxConcurrent` taken for `QueueTimeout`) or by ClickHouse (its time, concurrency or memory limit), answered 503 "Busy, try again". `unavailable`: ClickHouse not reached, answered 503 |
| `juice.observability.clickhouse.api.running` | up-down counter | – | API queries running now on this replica. At `ClickHouse:Api:MaxConcurrent`, with `busy` rising: searches wait or are refused. Raise it only if ClickHouse has cores to spare, since ingest and the workers share them |
| `juice.observability.clickhouse.query.read_rows` | counter | caller | A caller reading far more than the others |
| `juice.observability.clickhouse.query.read_bytes` | counter, By | caller | Same |

**The `caller` attribute:**
- **Values:** a worker (`discovery`, `health`, `rules`, `slos`), or an API endpoint by its route template below the tenant, such as
  `api:/telemetry/traces`. It never carries a request's values. `other` means code that didn't name itself.
- **In ClickHouse:** the same caller is in `system.query_log` as `log_comment = 'juice:<caller>'`, the authoritative cross-check. The
  driver's read statistics come from a response header that can be partial for a streamed result. The sampler's own queries are there as
  `juice:sampler`, and aren't timed here.

### The sampler

Every 15 s, every replica samples the platform database and ClickHouse. Pages and probes read the last sample, never the stores.
- **Timeout:** each store has 10 s to answer, at the same time as the other. A store that doesn't answer is down for that sample, and its
  `up` gauge goes to 0.
- **Staleness:** a gauge shows only while its sample is under a minute old.
- **Replicas:** every replica samples the same stores. Read a figure from one replica, or take the max across them. Never sum across
  `service.instance.id`.

| Instrument | Type, unit | Attributes | A bad value means |
|---|---|---|---|
| `juice.observability.postgres.up` | gauge | – | 0: the database didn't answer the sample |
| `juice.observability.postgres.sample.duration` | gauge, s | – | Rising: the database is slow |
| `juice.observability.postgres.size` | gauge, By | – | Growth per day |
| `juice.observability.outbox.backlog` | gauge | state (`pending`, `retrying`) | `pending` growing: delivery is behind; `retrying`: a consumer keeps failing |
| `juice.observability.outbox.oldest_age` | gauge, s | – | Past a minute: notifications are late |
| `juice.observability.clickhouse.up` | gauge | – | 0: ClickHouse didn't answer the sampler |
| `juice.observability.collector.up` | gauge | – | 0: the Collector's health check (`Telemetry:CollectorHealthUrl`) didn't answer; absent when it isn't checked |
| `juice.observability.clickhouse.cpu` | gauge, cores | – | Near the cores it has: ClickHouse is the limit (Phase 10 scale tests) |
| `juice.observability.clickhouse.memory` | gauge, By | – | Near its memory |
| `juice.observability.clickhouse.running_queries` | gauge | user | Many at once for one user |
| `juice.observability.clickhouse.server.queries` | counter (from this process's start) | user, kind, outcome | Failures rising |
| `juice.observability.clickhouse.server.query.p95` | gauge, s (the last sample's window: 15 s, up to 5 min after a missed one) | user, kind | Rising for `platform_reader` (the platform) or `otel_writer` (inserts) |
| `juice.observability.clickhouse.disk.free` | gauge, By | disk | Falling toward what retention still needs |
| `juice.observability.clickhouse.disk.total` | gauge, By | disk | – |
| `juice.observability.clickhouse.table.size` | gauge, By | table | Growth per day, for the disk budget |
| `juice.observability.clickhouse.table.rows` | gauge | table | – |

**ClickHouse access:**
- **User:** `platform_monitor`.
- **Grants:** `SELECT` on `system.events`, `asynchronous_metrics`, `processes`, `query_log`, `disks` and `parts`, plus `SHOW TABLES ON otel.*`
  so `system.parts` lists the telemetry tables. It can't read a row of telemetry; `tests/verify.sh` checks that.
- **What it can see:** those system tables show every user's queries, query text included. The sampler reads only counts and timings.
  Users outside the platform's own (`otel_writer`, `platform_reader`, `grafana_reader`, `platform_monitor`) count as `other`, and kinds
  outside Select, Insert and AsyncInsertFlush as `other`.

**How each ClickHouse figure is read:**
- **CPU:** the change in ClickHouse's `OSCPUVirtualTimeMicroseconds` (the CPU time of its threads) over the wall time between samples.
  It's a lower bound: threads that keep no profile events aren't counted. On the local stack it read about two thirds of what
  `docker stats` showed for the container.
- **Query and insert timings:** from `system.query_log`, one window after another on ClickHouse's own clock, each ending 10 s back
  (ClickHouse flushes that log every 7.5 s).
  - **Missed windows:** a window that fails to read is read again next time, up to 5 minutes back.
  - **The count** is cumulative, so it reads right at any export interval.
  - **The p95** is the last window's (15 s, longer after a missed sample). For merged `other` groups it's the highest of their p95s, not a p95.

## The platform's status

Milestone 2 (plan R6, R7) builds the status from PostgreSQL, so it never waits on ClickHouse.

**Heartbeats.** Every 5 s each replica writes, in one statement, a row for every worker the platform has to `worker_heartbeats`. The
workers are action-requests, discovery, health, notifications, rules, sampler, slos. A row says:
- whether the worker is enabled in that replica (`*:Enabled`), and its interval;
- whether it runs under a lease, holds it, and since when;
- when the pass in progress started;
- when the last pass ended, and its outcome.
- how long its recent passes took, a moving mean (milestone 6: the panel's "how busy"; the 1.3.0 migration adds the column).

**Times:**
- **Whose clock:** every time in a row is the database's. The replica sends ages on its own monotonic clock, and the database subtracts
  them from its `now()`, so the replicas' clocks needn't agree.
- **Pruning:** rows of a replica not seen for an hour are pruned.
- **Workers without a single pass:**
  - The rule scheduler counts its 5 s tick. Its evaluations each have `Rules:EvaluationTimeout` (20 s), so a wedged one frees its slot.
- **A stop:** once the host starts stopping, the replica writes no more, so its last rows leave its workers enabled. A worker no other
  live replica runs then reads as `gone`, whatever order the host stops its services in. A worker whose loop dies without the host
  stopping stays enabled in a live row, and goes stale.
- **A fresh replica:** its first write waits a second for its workers to register. Until that write, for at most 30 s, its own missing
  row reads `starting`, not down, and so does a worker only a replica it may have replaced had enabled. A writer that hasn't
  succeeded 30 s after it started (after the host's own startup, migrations included) is `down`.

**How a worker is judged,** across the replicas that wrote within the last 30 s and have it enabled. Its threshold is 3 intervals,
never under 60 s.

| State | When | Degrades |
|---|---|---|
| `ok` | the holder (the replica that took the lease last) or, without a lease, every replica, passed within the threshold, or is in a pass shorter than it | no |
| `starting` | no pass yet, and every replica with it enabled registered it (a holder: took the lease) within the threshold. This includes a rolling upgrade from 1.2.0, whose holder writes no row, until a new replica takes the lease over at its first pass | no |
| `handing_over` | nobody holds the lease, but some replica, live or gone within the hour, passed within the threshold: a takeover, such as after a holder crashed and before its standby takes over | no |
| `off` | every live replica has it disabled | no |
| `stale` | the holder's last pass ended longer ago; or, without a lease, any replica's did | yes |
| `stuck` | a pass has run longer than the threshold | yes |
| `unheld` | nobody has held the lease for longer than the threshold | yes |
| `gone` | a replica gone within the hour had it enabled, and no live replica has: its replicas stopped. Not after a redeploy that switched it off: when a live replica with it disabled started after the old ones did, within 10 minutes either side of when they were last seen (a stop-then-start or a rolling redeploy, of every replica or only the worker replicas), it is `off`. The cost: a worker replica that crashes within 10 minutes of another replica starting reads `off` | yes |

**Dependencies and backlogs:**
- **The replica's last sample, read on every request:**
  - **Dependencies:** PostgreSQL, ClickHouse and the Collector, each `up`, `down`, `not_configured` or `unknown`.
  - **When the status shows a store down:** within one sample of it going down, at most 15 s plus the store's 10 s timeout.
  - **`unknown`:** the sample is older than a minute. That alone doesn't degrade, since the sampler's own heartbeat says whether it
    stopped.
- **`heartbeats`:** `down` when the replica answering has written but has no live row of its own. Its heartbeat writer is failing, so nobody's status can
  be trusted.
- **Backlogs** are `behind` when past their line:
  - **Outbox:** the most overdue message, 60 s. A new message counts from when it was written (a deferred one from when it falls due), a
    failed one from when its retry fell due, and a retry in progress from when it started.
  - **Discovery:** its watermark, at `Discovery:Lag` plus `Discovery:Interval` plus 60 s. It is `off` when discovery runs nowhere.

**The status** is `degraded` when any worker is stale, stuck, unheld or gone, any dependency is down, or any backlog is behind;
otherwise `ok`. The heartbeats are read at most every 15 s, so many requests cost one read.

**The endpoints** (both `Cache-Control: no-store`):
- **`/health/platform`**, anonymous: `{"status":"ok"}` with 200, or `{"status":"degraded"}` with 503, and nothing else. It's for alerting
  probes (an uptime checker, a load balancer's monitor), not a Kubernetes liveness or readiness probe. Every replica gives the same answer,
  so that would restart or remove them all at once. `/health/ready` stays the readiness probe (PostgreSQL only).
- **`/api/v1/platform/status`:** the whole report. That's each worker (state, holder, threshold, last pass, every live replica's row),
  each dependency, each backlog, and each replica with its version.
  - **Who can read it:** operators and admins of this deployment, so their tenant claim must name this deployment's tenant. One IdP may
    serve several deployments.

## The Overview's platform panel and the capacity view

Milestone 6 (plan R8, R9). `/api/v1/platform/overview`, for the same callers as the status, and the Console's Overview shows it to
operators and admins (the tenants list says `canOperate`, so a viewer's Console never asks). It is built from the status and the
replica's last sample, never a query of its own, and the Console reads it again every 15 s.

**The panel (R8).** Each figure is `ok`, `warn` or `bad` (green, amber, red):

| Figure | From | Amber | Red |
|---|---|---|---|
| Each worker: state, last pass, how busy | the heartbeats; how busy is its recent passes (a moving mean, each pass weighing a fifth) against its interval | 80 % busy | a bad state (stale, stuck, unheld, gone), or 100 % busy |
| Each backlog (outbox, discovery) | the status | – | `behind` |
| Each signal: received, refused, dropped a second | the Collector's metrics page (`Telemetry:CollectorMetricsUrl`), the tenant's receivers only; dropped by any exporter | – | anything refused or dropped since the last sample |
| Each export queue against its bound | the same page | 50 % | 90 % |
| ClickHouse CPU | the sampler's CPU, against `max_threads` (its cores) | 50 % of them (calibrated: the mean where every line held) | 80 % (calibrated: where one crossed) |
| The Collector's insert p95, and inserts in flight | `query_log`, `otel_writer`: the inserts' durations over the sample's window is how many ran at once | 200 ms | 350 ms (the final rerun: 109 ms at 5×, 243 ms at 6×, 347 ms at 10×; 17 November: 182 ms at 6×, which held, 362 ms at 7×, which crossed) |
| The platform's reads, p95 by caller | `query_log`, `platform_reader`, by `log_comment` | an API search past 500 ms (§9's API line) | past 5 s; the workers' have no line |
| Disk: used, free, a day, at retention, days until full | `system.disks`; each table's last whole UTC day's partition (or today's, scaled, once an hour has passed: an estimate) and its TTL. Days until full only while the tables still have more to grow, to reach their retention, than is free | under 30 days, or the retention needs more than the disk | under 7 days |

**The capacity view (R9): measured, labelled, not extrapolated.**
- **The calibration:** `scale/steps.sh` writes it after the steps (`scale/components.mjs`, then `scale/calibration.mjs`), with `HELD_OUT`
  naming the steps kept out to check it. The stack reads `capacity/calibration.json` (`Capacity:CalibrationFile`). It names its host
  (ClickHouse's cores and memory) and its mix (rules, SLOs, services, searches a second), and holds each step's resources as the
  platform measures them live: ClickHouse's CPU, the Collector's inserts in flight, the export queue (in the steps, the queues' sum,
  which the metrics queue is nearly all of), and each worker's pass time against its interval. Each is set against the steps' mean (the queue:
  its p95), while the live figure is one 15-second sample: a burst can colour one sample at any load.
- **The knee is a bracket,** from the calibration steps alone: the last step every line held, and the first that crossed one. The
  shipped calibration (the rerun with the driver's pool at 32, 17 November, 4× held out, [scale tests](scale-tests.md#rerun-with-the-drivers-pool-at-32-1-to-10-17-november)):
  held to 6×, crossed at 7× (16 November's, before the pool: held to 5×, crossed at 10×). The view claims no more than that: the knee
  comes from bursts, not from a curve the steps could be interpolated along (owner, 16 November).
- **The load** is a multiple of §9's reference per signal (1,000 spans, 10,000 points and 1,000 logs a second), from the Collector's
  received rate. **Headroom** is the step that held over today's load.
- **Each resource** shows now, and what it measured where every line held and where one crossed; amber and red at those.
- **Labels:** "Calibrated on 24 cores and 31.1 GiB, with 50 rules, 50 SLOs, 10 services and 2 searches a second". With no file, or a
  file for another machine (cores, or memory more than 10 % off), it shows utilisation only, against fixed shares, and says why. The
  mix is shown beside the calibration's, since rules, SLOs and services also drive cost.
- **Disk** is its own figure (days until full), not part of the headroom. No prices (Phase 10 P6).

## Volume

Measured on the local stack on 13 November, over 5 minutes. Gauges and cumulative sums are written for every series at every export
(5,760 a day at 15 s).

| Source | Series | Rows a day |
|---|---|---|
| The platform, one replica | about 175 | about 1.0 M |
| The deployment's Collector | about 66 | about 0.38 M |
| The test Collector (`--profile test` only) | about 94 | about 0.54 M |

- **Share of the load:** the platform and the deployment's Collector come to about 0.16 % of the §9 reference load's 860 M metric points a
  day. Each further API replica adds about 0.11 %.
- **Retention:** these rows are kept by the metrics tables' TTL (90 days), like the tenant's.
