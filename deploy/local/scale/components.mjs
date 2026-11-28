// Per-component figures for scale steps (docs/phase-11-plan.md R14, revision 3), read after the steps from the scale stack's ClickHouse:
// its own system logs (query_log, part_log, asynchronous_metric_log) and the platform's self-metrics (reserved tenant juice-platform).
// The Collector's counters and the containers' CPU are in each step's collector.csv and resources.csv already (report.mjs).
//   node scale/components.mjs <step dir>...      writes <step dir>/components.json and prints a Markdown summary
// Needs the scale stack's ClickHouse on localhost:8123, or at CLICKHOUSE_URL (scale/stack.sh up, or `dc up -d --wait clickhouse` alone after the steps).
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const env = Object.fromEntries(readFileSync(process.env.JUICE_ENV_FILE ?? new URL('../.env', import.meta.url), 'utf8').split(/\r?\n/)
  .filter(l => /^[A-Z_]+=/.test(l)).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));
const auth = 'Basic ' + Buffer.from(`admin:${env.CLICKHOUSE_ADMIN_PASSWORD}`).toString('base64');

async function q(sql) {
  const r = await fetch(`${process.env.CLICKHOUSE_URL ?? 'http://localhost:8123'}/?default_format=JSON`, { method: 'POST', headers: { Authorization: auth }, body: sql });
  const text = await r.text();
  if (!r.ok) throw new Error(`${r.status}: ${text.slice(0, 300)}\n${sql}`);
  return JSON.parse(text).data;
}

const n = v => (v === null || v === undefined || v === '' ? null : Number(v));
const f = (v, d = 1) => (v === null || Number.isNaN(v) ? '–' : Number(v).toFixed(d));

for (const dir of process.argv.slice(2)) {
  const meta = JSON.parse(readFileSync(join(dir, 'meta.json'), 'utf8'));
  const start = readFileSync(join(dir, 'start'), 'utf8').trim().replace('T', ' ').replace('Z', '');
  const end = readFileSync(join(dir, 'end'), 'utf8').trim().replace('T', ' ').replace('Z', '');
  const secs = (Date.parse(`${end}Z`) - Date.parse(`${start}Z`)) / 1000;
  const win = `event_time BETWEEN toDateTime('${start}', 'UTC') AND toDateTime('${end}', 'UTC')`;
  const cpu = `ProfileEvents['OSCPUVirtualTimeMicroseconds'] / 1e6`;

  // ClickHouse work by user and kind: rate, rows written and read, latency, and the CPU seconds each spent (its share of the server).
  // Queries that failed while running are included (a timeout spent its CPU too). A query counts whole in the window it ended in.
  const byUser = await q(`SELECT user, toString(query_kind) AS kind, count() AS queries, count() / ${secs} AS per_s,
      sum(written_rows) / ${secs} AS written_rows_s, sum(read_rows) / ${secs} AS read_rows_s,
      quantile(0.5)(query_duration_ms) AS p50_ms, quantile(0.95)(query_duration_ms) AS p95_ms, max(query_duration_ms) AS max_ms,
      sum(${cpu}) / ${secs} AS cpu_cores
    FROM system.query_log WHERE type IN ('QueryFinish', 'ExceptionWhileProcessing') AND ${win} GROUP BY user, kind ORDER BY cpu_cores DESC`);
  const failures = await q(`SELECT user, toString(query_kind) AS kind, exception_code AS code, count() AS n
    FROM system.query_log WHERE type IN ('ExceptionBeforeStart', 'ExceptionWhileProcessing') AND ${win} GROUP BY user, kind, code ORDER BY n DESC`);
  // The platform's reads by caller (log_comment = 'juice:<caller>'), the authoritative view of which worker or page costs what.
  const byCaller = await q(`SELECT if(log_comment = '', '(none)', log_comment) AS caller, count() AS queries,
      quantile(0.5)(query_duration_ms) AS p50_ms, quantile(0.95)(query_duration_ms) AS p95_ms, max(query_duration_ms) AS max_ms,
      avg(read_rows) AS avg_read_rows, sum(read_bytes) / ${secs} AS read_bytes_s, sum(${cpu}) / ${secs} AS cpu_cores,
      max(memory_usage) AS max_memory
    FROM system.query_log WHERE type IN ('QueryFinish', 'ExceptionWhileProcessing') AND user = 'platform_reader' AND ${win} GROUP BY caller ORDER BY cpu_cores DESC`);
  // Parts: how many inserts made, how many merges ran and how long, and the most parts any partition held (the "too many parts" road).
  const parts = await q(`SELECT table, countIf(event_type = 'NewPart') / ${secs} AS new_parts_s, countIf(event_type = 'MergeParts') / ${secs} AS merges_s,
      avgIf(duration_ms, event_type = 'MergeParts') AS merge_avg_ms, sumIf(rows, event_type = 'NewPart') / greatest(countIf(event_type = 'NewPart'), 1) AS rows_per_part
    FROM system.part_log WHERE database = 'otel' AND ${win} GROUP BY table ORDER BY new_parts_s DESC`);
  const server = await q(`SELECT metric, avg(value) AS avg, max(value) AS max FROM system.asynchronous_metric_log
    WHERE ${win} AND metric IN ('MaxPartCountForPartition', 'OSUserTimeNormalized', 'OSSystemTimeNormalized', 'OSIOWaitTimeNormalized',
      'MemoryResident', 'LoadAverage1', 'NumberOfCPUCores') GROUP BY metric ORDER BY metric`);

  // The platform's self-metrics (tenant juice-platform): worker pass time against interval, and PostgreSQL's client side (Npgsql).
  const tenant = `ResourceAttributes['juice.platform_tenant_id'] = 'juice-platform'`;
  const mwin = `TimeUnix BETWEEN toDateTime64('${start}', 9, 'UTC') AND toDateTime64('${end}', 9, 'UTC')`;
  // Cumulative histograms: a series' growth over the window, Sum over Count, is its mean in the window (no max: the SDK's Max runs from
  // the process's start, not the window's). Assumes no process restart inside the window.
  const passes = await q(`SELECT Attributes['worker'] AS worker, sum(dc) AS passes, sum(ds) / greatest(sum(dc), 1) AS mean_s FROM (
      SELECT Attributes, max(Count) - min(Count) AS dc, max(Sum) - min(Sum) AS ds FROM otel.otel_metrics_histogram
      WHERE ${tenant} AND MetricName = 'juice.observability.worker.pass.duration' AND ${mwin}
      GROUP BY Attributes, ResourceAttributes['service.instance.id']) GROUP BY worker ORDER BY worker`);
  const metricNames = await q(`SELECT DISTINCT MetricName AS name FROM merge('otel', '^otel_metrics_') WHERE ${tenant} AND ${mwin}
      AND (MetricName LIKE 'db.client%' OR MetricName LIKE 'juice.observability.postgres%' OR MetricName LIKE 'juice.observability.outbox%')`);
  const pgOps = await q(`SELECT sum(dc) / ${secs} AS ops_s, sum(ds) / greatest(sum(dc), 1) * 1000 AS mean_ms FROM (
      SELECT max(Count) - min(Count) AS dc, max(Sum) - min(Sum) AS ds FROM otel.otel_metrics_histogram
      WHERE ${tenant} AND MetricName = 'db.client.operation.duration' AND ${mwin}
      GROUP BY Attributes, ResourceAttributes['service.instance.id'])`);
  const pgConn = await q(`SELECT state, max(v) AS max, avg(v) AS avg FROM (
      SELECT Attributes['db.client.connection.state'] AS state, toStartOfInterval(TimeUnix, INTERVAL 15 SECOND) AS t, sum(Value) AS v FROM otel.otel_metrics_sum
      WHERE ${tenant} AND MetricName = 'db.client.connection.count' AND ${mwin} GROUP BY state, t) GROUP BY state`);
  const gauges = await q(`SELECT MetricName AS name, avg(Value) AS avg, max(Value) AS max FROM otel.otel_metrics_gauge
      WHERE ${tenant} AND ${mwin} AND MetricName IN ('juice.observability.outbox.oldest_age',
        'juice.observability.discovery.watermark_lag', 'juice.observability.postgres.sample.duration', 'juice.observability.clickhouse.cpu',
        'juice.observability.clickhouse.running_queries', 'juice.observability.rules.busy') GROUP BY name ORDER BY name`);

  // The Collector's inserts in flight (their durations over the time): on average, and in 10-second buckets, where the final rerun found the
  // export saturating (docs/scale-tests.md). The platform's sampler measures the same, every 15 s (PlatformSampler, InsertsInFlight).
  const [inserts] = await q(`SELECT sum(f) / ${secs / 10} AS mean, quantile(0.99)(f) AS p99, max(f) AS max FROM (
      SELECT toStartOfInterval(event_time, INTERVAL 10 SECOND) AS b, sum(query_duration_ms) / 10000 AS f FROM system.query_log
      WHERE type = 'QueryFinish' AND user = 'otel_writer' AND query_kind = 'Insert' AND ${win} GROUP BY b)`);
  // ClickHouse's own count of its cores (its automatic max_threads), which the platform's capacity view matches a calibration by.
  const [{ cores }] = await q(`SELECT toFloat64(getSetting('max_threads')) AS cores`);
  const out = { multiplier: meta.multiplier, start, end, seconds: secs, byUser, failures, byCaller, parts, server, passes, postgres: { ops: pgOps, connections: pgConn, metrics: metricNames.map(m => m.name) }, gauges, inserts, clickhouseCores: cores };
  writeFileSync(join(dir, 'components.json'), JSON.stringify(out, null, 2));

  const lines = [`### ${meta.multiplier}× (${start} – ${end} UTC, ${secs} s)`, '',
    '**ClickHouse by user and kind**', '', '| User | Kind | Queries/s | Rows written/s | Rows read/s | p50 ms | p95 ms | Max ms | CPU cores |', '|---|---|--:|--:|--:|--:|--:|--:|--:|',
    ...byUser.map(r => `| ${r.user} | ${r.kind} | ${f(n(r.per_s), 2)} | ${f(n(r.written_rows_s), 0)} | ${f(n(r.read_rows_s), 0)} | ${f(n(r.p50_ms), 0)} | ${f(n(r.p95_ms), 0)} | ${f(n(r.max_ms), 0)} | ${f(n(r.cpu_cores), 2)} |`),
    '', failures.length ? `**Failed queries:** ${failures.map(r => `${r.user} ${r.kind} code ${r.code} ×${r.n}`).join('; ')}` : '**Failed queries:** none', '',
    '**The platform\'s reads by caller**', '', '| Caller | Queries | p50 ms | p95 ms | Max ms | Rows read (avg) | CPU cores |', '|---|--:|--:|--:|--:|--:|--:|',
    ...byCaller.map(r => `| ${r.caller} | ${r.queries} | ${f(n(r.p50_ms), 0)} | ${f(n(r.p95_ms), 0)} | ${f(n(r.max_ms), 0)} | ${f(n(r.avg_read_rows), 0)} | ${f(n(r.cpu_cores), 2)} |`),
    '', '**Parts**', '', '| Table | New parts/s | Rows/part | Merges/s | Merge avg ms |', '|---|--:|--:|--:|--:|',
    ...parts.map(r => `| ${r.table} | ${f(n(r.new_parts_s), 2)} | ${f(n(r.rows_per_part), 0)} | ${f(n(r.merges_s), 2)} | ${f(n(r.merge_avg_ms), 0)} |`),
    '', `**Server:** ${server.map(r => `${r.metric} avg ${f(n(r.avg), 2)} max ${f(n(r.max), 2)}`).join('; ')}`,
    '', `**Worker passes (mean s, passes):** ${passes.map(r => `${r.worker} ${f(n(r.mean_s), 2)} (${r.passes})`).join('; ') || 'none'}`,
    `**PostgreSQL (client side):** ${pgOps.map(r => `${f(n(r.ops_s), 1)} commands/s, mean ${f(n(r.mean_ms), 2)} ms`).join('')}; connections ${pgConn.map(r => `${r.state} max ${f(n(r.max), 0)}`).join(', ') || '–'}`,
    `**Inserts in flight (mean / p99 / max of 10 s):** ${f(n(inserts.mean), 2)} / ${f(n(inserts.p99), 2)} / ${f(n(inserts.max), 2)}`,
    `**Platform gauges (avg / max):** ${gauges.map(r => `${r.name.replace('juice.observability.', '')} ${f(n(r.avg), 2)}/${f(n(r.max), 2)}`).join('; ')}`, ''];
  console.log(lines.join('\n'));
}
