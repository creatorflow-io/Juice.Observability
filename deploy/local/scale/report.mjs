// The report of one scale step (scale/run.sh): every measure of docs/phase-10-plan.md "Measures and targets", from the run's raw files.
//   node scale/report.mjs <run dir>     writes <run dir>/report.md and summary.json, prints the report, exits 1 if a target is missed
// Rules it keeps:
// - a measure with no data is missed, never passed (a dead worker or a crashed probe can't look like a pass);
// - targets are the plan's, fixed before the first run (D1). Queue lag is read as the owner set on 12 November: each worker at most 60 s
//   behind where its configuration says it should be (raw figures beside);
// - at 1× a target is met or missed; above 1× there is no pass mark (P3), so it is within or over the line, and the first line crossed
//   is the limit.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';

const dir = process.argv[2];
const read = f => (existsSync(`${dir}/${f}`) ? readFileSync(`${dir}/${f}`, 'utf8') : '');
const csv = f => { const [head, ...rows] = read(f).trim().split(/\r?\n/).filter(Boolean); if (!head) return [];
  const keys = head.split(','); return rows.map(r => Object.fromEntries(r.split(',').map((v, i) => [keys[i], v]))); };
const json = f => { try { return JSON.parse(read(f)); } catch { return null; } };
const num = v => (v === undefined || v === '' || v === null ? null : Number(v));
const known = list => list.filter(x => x !== null && !Number.isNaN(x));
const pct = (list, p) => { const s = known(list).sort((a, b) => a - b); return s.length ? s[Math.min(s.length - 1, Math.ceil(s.length * p) - 1)] : null; };
const max = list => pct(list, 1);
const mean = list => { const s = known(list); return s.length ? s.reduce((a, b) => a + b, 0) / s.length : null; };
const f = (x, d = 1) => (typeof x === 'string' ? x : x === null || x === undefined || Number.isNaN(x) ? '–' : Number(x).toLocaleString('en-US', { maximumFractionDigits: d, minimumFractionDigits: 0 }));
/** A judgement: true, false, or null (reported, no target). A target whose value is missing is false: "no data" is never a pass. */
const within = (value, line) => (value === null || value === undefined || typeof value === 'string' || Number.isNaN(value) ? false : value <= line);

const meta = json('meta.json') ?? {};
const m = meta.multiplier, secs = meta.seconds;
const start = read('start').trim(), end = read('end').trim();
const t0 = Date.parse(start), t1 = Date.parse(end);
const window = (t1 - t0) / 1000 || secs;
const inWindow = r => { const t = Date.parse(r.time); return t > t0 && t <= t1; };
const gen = json('generator.json');
const exits = Object.fromEntries(read('exits').trim().split(/\r?\n/).filter(Boolean).map(l => l.split(' ')).map(([k, v]) => [k, Number(v)]));
const verdict = ok => (ok === null ? 'reported' : m === 1 ? (ok ? '**met**' : '**missed**') : (ok ? 'within' : '**over the line**'));

// ── throughput ──
// Collector counters from the snapshot before the load (absent = 0), summing only increases, so a restart that resets them can't hide
// what was counted before it.
const before = csv('collector-before.csv');
const collector = csv('collector.csv');
function counted(name, base = true) {
  const rows = collector.filter(r => r.metric === name).sort((a, b) => Date.parse(a.time) - Date.parse(b.time));
  let prev = base ? num(before.find(r => r.metric === name)?.value) ?? 0 : rows.length ? num(rows[0].value) : 0, sum = 0;
  for (const r of rows) { const v = num(r.value); sum += v >= prev ? v - prev : v; prev = v; }
  return sum;
}
const signals = [['spans', 'spans'], ['metric_points', 'points'], ['log_records', 'logs']];
const stored = Object.fromEntries(csv('stored.csv').map(r => [r.signal, num(r.rows)]));
stored.points = ['points_gauge', 'points_sum', 'points_histogram'].some(k => stored[k] === null || stored[k] === undefined)
  ? null : stored.points_gauge + stored.points_sum + stored.points_histogram;
const nominal = { spans: 1000 * m * window, points: 10000 * m * window, logs: 1000 * m * window };
// What the generator sent in the window: its 10-second rows inside it.
const genRows = csv('generator.csv').filter(inWindow);
const sentInWindow = Object.fromEntries(['spans', 'points', 'logs'].map(k => [k, genRows.length ? genRows.reduce((a, r) => a + num(r[k]), 0) : null]));
const genWindow = genRows.length * 10;
const through = signals.map(([c, g]) => ({ signal: g, sent: sentInWindow[g], offered: sentInWindow[g] !== null && genWindow ? sentInWindow[g] / genWindow / (nominal[g] / window) : null,
  accepted: (() => { const rows = collector.filter(r => r.metric === `otelcol_receiver_accepted_${c}` && inWindow(r)).map(r => num(r.value)); return rows.length > 1 ? rows[rows.length - 1] - rows[0] : null; })(), refused: counted(`otelcol_receiver_refused_${c}`), sendFailed: counted(`otelcol_exporter_send_failed_${c}`),
  enqueueFailed: counted(`otelcol_exporter_enqueue_failed_${c}`), stored: stored[g], storedShare: stored[g] !== null && stored[g] !== undefined ? stored[g] / nominal[g] : null, nominal: nominal[g] }));
// What the receivers refused (the memory limiter's and a full export queue's alike) and what the exporters' sends lost for good. An
// exporter's enqueue failure is returned to its receiver, which counts the same points as refused, so it isn't added again (17 November:
// every point lost at 7×, 8× and 10× was in both). That holds while every exporter is synchronous behind its receiver (no batch
// processor or connector between, as now): an async stage would make enqueue failures a loss of their own.
const lost = through.reduce((a, t) => a + t.refused + t.sendFailed, 0);
const generatorOk = gen !== null && exits.generator === 0 && !gen.failed && !gen.refused && !gen.crashed && !gen.skipped && !gen.skippedLocal && !gen.pointsSkipped;
// The generator itself too slow (spans given up while few sends were in progress) or a worker crashed: the step says nothing about the platform.
const harnessLimit = gen !== null && (gen.skippedLocal > 0 || gen.crashed > 0);
const queue = collector.filter(r => r.metric === 'otelcol_exporter_queue_size').map(r => num(r.value));

// ── latencies ──
const fresh = csv('freshness.csv').map(r => num(r.ms));
const queries = json('queries.json') ?? {};
const journey = csv('journey.csv');
const breach = [...read('breach.log').matchAll(/(-?\d+) s after ForDuration/g)].map(x => Number(x[1]));
const breachMissed = (read('breach.log').match(/did not fire/g) ?? []).length;
const [whTotal, whAttempted, whP50, whP95, whMax, whDelivered, whUnplanned] = read('dispatch.csv').trim().split(',').map(num);
const transitions = num(read('transitions.csv').trim());
// Outbox deliveries created in the window and given up for good (run.sh): dropped, so a queue-lag miss; no file is no data.
const givenUp = existsSync(`${dir}/outbox.csv`) ? num(read('outbox.csv').trim()) : null;

// ── coverage: a measure sampled in too few of its 10 s slots can't be judged, so it's missed ──
const slots = Math.floor(window / 10);
const covered = rows => slots > 0 && rows.length >= 0.9 * slots;
const acceptedCovered = signals.every(([c]) => covered(collector.filter(r => r.metric === `otelcol_receiver_accepted_${c}` && inWindow(r))));

// ── lags and backlogs ──
const lag = csv('lag.csv').map(r => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, k === 'time' ? v : num(v)])));
// Ages of the oldest pending item are empty when nothing is pending: that is 0, not missing.
const col = (k, emptyIsZero = false) => lag.map(r => (r[k] === null && emptyIsZero ? 0 : r[k]));
// A backlog grows when the last full 15-minute load cycle (generate.mjs) averages more than the first by more than a noise floor: the
// same phase of the cycle compared, so a failure burst early in the window can't hide one later. Shorter windows aren't judged.
const CYCLE = 15 * 60;
const cycles = Math.floor(window / CYCLE);
// A cycle with under 90 % of its samples can't show growth: judged as growing (a miss), not as flat.
const inCycle = (t, i) => t > t0 + i * CYCLE * 1000 && t <= t0 + (i + 1) * CYCLE * 1000;
const cycleValues = (values, i) => values.filter((_, j) => inCycle(Date.parse(lag[j]?.time ?? ''), i));
const growing = (values, floor) => {
  if (cycles < 2) return null;
  const a = cycleValues(values, 0), b = cycleValues(values, cycles - 1);
  if (known(a).length < (0.9 * CYCLE) / 10 || known(b).length < (0.9 * CYCLE) / 10) return true;
  return mean(b) - mean(a) > floor;
};
const lagCovered = covered(lag.filter(inWindow));
const lags = [
  // what, column, configured delay (s), line beyond it (s; null = shown only), noise floor for growth, empty = 0
  ['Discovery: watermark behind now', 'discovery', 60, 60, 30, false, 'Discovery:Lag 60 s'],
  // Shown, not judged here: the plan's queue lag doesn't list health, which has its own freshness target.
  ['Health: oldest evaluation of a live subject', 'health_stale', 30, null, 30, false, 'Health:Interval 30 s; shown only, health freshness is its target'],
  ['Rules: oldest last evaluation of an open alert', 'alert_stale', 60, 60, 30, true, 'due every 60 s'],
  ['SLOs: past when the next minute became countable', 'slo_excess', 0, 60, 60, false, `its end + 10 s; a pass every 30 s; raw: counted-through ${f(max(col('slo_lag')))} s behind now at most`],
  ['Outbox: oldest undelivered', 'outbox_oldest', 0, 60, 30, true, ''],
  ['Notifications: oldest transition not yet planned', 'planning_oldest', 0, 60, 30, true, ''],
].map(([what, k, delay, line, floor, emptyIsZero, note]) => {
  const values = col(k, emptyIsZero), mx = max(values), excess = mx === null ? null : mx - delay;
  return { what, k, delay, line, p95: pct(values, 0.95), max: mx, excess, ok: line === null ? null : within(excess, line) && values.every(v => v !== null) && lagCovered,
    growing: line === null ? null : growing(values, floor), note };
});
const counts = [['Outbox: rows not yet published (retries included)', 'outbox_pending', 50], ['Outbox: failed, waiting for a retry', 'outbox_failed', 20],
  ['Notifications: transitions not yet planned', 'planning_pending', 20]]
  .map(([what, k, floor]) => ({ what, p95: pct(col(k), 0.95), max: max(col(k)), growing: growing(col(k), floor) }));
const queueRows = collector.filter(r => r.metric === 'otelcol_exporter_queue_size');
const queueGrowing = cycles < 2 ? null : (() => {
  const per = i => queueRows.filter(r => inCycle(Date.parse(r.time), i)).map(r => num(r.value));
  const a = per(0), b = per(cycles - 1);
  if (a.length < (0.9 * CYCLE) / 10 || b.length < (0.9 * CYCLE) / 10) return true;   // a cycle short of samples: growing, as the lags
  return mean(b) - mean(a) > 10000;
})();
const healthEvidence = col('health_evidence_p95');

// ── resources ──
const res = csv('resources.csv').filter(inWindow);
const containers = [...new Set(res.map(r => r.container))].sort();
const resources = containers.map(c => { const rows = res.filter(r => r.container === c);
  return { container: c.replace(/-1$/, ''), cpuMean: mean(rows.map(r => num(r.cpu_pct))), cpuMax: max(rows.map(r => num(r.cpu_pct))), memMax: max(rows.map(r => num(r.mem_bytes))) }; })
  .sort((a, b) => (b.cpuMean ?? 0) - (a.cpuMean ?? 0));
const genCpu = mean(genRows.map(r => num(r.cpu_pct)));
const cores = meta.docker_cpus ?? 24;

// ── storage: growth over the window, per day, and the full retention it implies (P6) ──
const storage = csv('storage.csv');
const times = [...new Set(storage.map(r => r.time))].sort();
const at = (t, table) => storage.find(r => r.time === t && r.table === table);
const RETENTION = { otel_traces: 30, otel_logs: 30, otel_traces_trace_id_ts: 30, otel_metrics_gauge: 90, otel_metrics_sum: 90, otel_metrics_histogram: 90,
  otel_metrics_exponential_histogram: 90, otel_metrics_summary: 90 };
const span = times.length > 1 ? (Date.parse(times[times.length - 1]) - Date.parse(times[0])) / 1000 : null;
const disk = Object.keys(RETENTION).concat(['postgres']).map(table => {
  const a = at(times[0], table), b = at(times[times.length - 1], table);
  if (!a || !b || !span) return null;
  const perDay = ((num(b.bytes) - num(a.bytes)) / span) * 86400;
  return { table, perDayGB: perDay / 1e9, rowsPerDay: ((num(b.rows) - num(a.rows)) / span) * 86400, retentionDays: RETENTION[table] ?? null,
    retainedGB: RETENTION[table] ? (perDay * RETENTION[table]) / 1e9 : null };
}).filter(Boolean);
const retainedTotal = disk.reduce((a, d) => a + (d.retainedGB ?? 0), 0);

// ── the measures ──
const pctOf = x => (x === null ? '–' : `${f(x * 100, 1)} %`);
const offeredOk = through.every(t => t.offered !== null && t.offered >= 0.98 && t.storedShare !== null && t.storedShare >= 0.98);
const healthP95 = pct(healthEvidence, 0.95);
const apiP95 = queries.api?.p95 ?? null, searchP95 = queries.search?.p95 ?? null;
const pageMs = journey.map(r => num(r.ms));
const growthNote = cycles < 2 ? 'growth not judged: the window is shorter than two 15-minute load cycles' : null;
const targets = [
  ['Throughput: the offered load reached, nothing refused or dropped', generatorOk && lost === 0 && offeredOk && acceptedCovered,
    `${gen === null ? 'no generator summary; ' : ''}${!generatorOk && gen ? `generator: exit ${exits.generator ?? '–'}, ${gen.failed} failed, ${gen.refused} refused, ${gen.skipped} spans skipped under backpressure, ${gen.skippedLocal ?? 0} by a slow generator, ${gen.pointsSkipped ?? 0} points skipped, ${gen.crashed} workers crashed; ` : ''}` +
    `${acceptedCovered ? '' : 'Collector counters missing for part of the window; '}${f(lost, 0)} lost in the Collector (refused, a full queue's included, or a send failed, from before the warm-up to after the drain); sent ${through.map(t => `${t.signal} ${pctOf(t.offered)}`).join(', ')} of nominal in the window, stored ${through.map(t => `${pctOf(t.storedShare)}`).join(', ')} (target ≥ 98 % each)`],
  ['Ingestion freshness p95 ≤ 30 s', within(pct(fresh, 0.95), 30000),
    `p95 ${f(pct(fresh, 0.95) / 1000, 2)} s, p50 ${f(pct(fresh, 0.5) / 1000, 2)} s, max ${f(max(fresh) / 1000, 2)} s (${fresh.length} probes)`],
  ['API reads p95 ≤ 500 ms (20/s)', within(apiP95, 500),
    queries.api ? `p95 ${f(apiP95)} ms, p50 ${f(queries.api.p50)} ms, max ${f(queries.api.max)} ms; ${queries.api.errors} of ${f(queries.api.count, 0)} reads failed (an unanswered read counts as slower than any target)` : 'no data'],
  ['Health freshness p95 ≤ 60 s', within(healthP95, 60) && healthEvidence.every(v => v !== null),
    `age of the evidence behind each live subject's visible state: p95 ${f(healthP95)} s, max ${f(max(healthEvidence))} s${healthEvidence.some(v => v === null) ? `; ${healthEvidence.filter(v => v === null).length} samples with no live subject` : ''}; at evaluation p95 ${f(pct(col('health_age_p95'), 0.95))} s`],
  ['Rule breach-to-alert p95 ≤ 2 min after ForDuration', breach.length > 0 && within(pct(breach, 0.95), 120) && breachMissed === 0,
    `${breach.length ? `p95 ${pct(breach, 0.95)} s over ${breach.length} runs (${breach.join(', ')} s)` : 'no runs'}${breachMissed ? `; ${breachMissed} did not fire` : ''}`],
  ['First dispatch p95 ≤ 30 s', whTotal > 0 && within(whP95, 30),
    whTotal ? `p95 ${f(whP95, 2)} s, p50 ${f(whP50, 2)} s, max ${f(whMax, 2)} s over ${whTotal} (${whAttempted} attempted, ${whDelivered} delivered, ${whUnplanned ?? 0} transitions not yet planned; the transition to the first delivery attempt; anything unattempted counts until the window's end; ${transitions} alert transitions)` : 'no notifying transitions in the window'],
  ['Queue lag: each worker ≤ 1 min past its configured delay; no growing backlog', lagCovered && givenUp === 0 && lags.every(l => l.ok !== false) && ![...lags, ...counts].some(l => l.growing) && !queueGrowing,
    `${lagCovered ? '' : `lag samples in only ${lag.filter(inWindow).length} of ${slots} slots; `}${givenUp === null ? 'no count of given-up outbox deliveries; ' : givenUp > 0 ? `${givenUp} outbox deliveries given up for good; ` : ''}${lags.filter(l => l.ok === false).map(l => `${l.what} ${f(l.excess)} s past`).join('; ') || 'every worker within 60 s of its configured delay'}; ` +
    `${growthNote ?? ([...lags, ...counts].filter(l => l.growing).map(l => l.what).join('; ') || 'no backlog growing')}; Collector export queue ${f(pct(queue, 0.5), 0)} items median, ${f(max(queue), 0)} max${queueGrowing ? ', **growing**' : ''}`],
  ['Telemetry search, last hour (no §9 target)', null,
    queries.search ? `p95 ${f(searchP95)} ms, p50 ${f(queries.search.p50)} ms, max ${f(queries.search.max)} ms; ${queries.search.errors} of ${f(queries.search.count, 0)} failed` : 'no data'],
  ['Console journey (no §9 target)', null,
    journey.length ? `page p95 ${f(pct(pageMs, 0.95), 0)} ms, max ${f(max(pageMs), 0)} ms; ${journey.filter(r => r.ok !== 'true').length} of ${journey.length} pages failed (navigation timed out, a page error or an API answer ≥ 400)` : 'no journey ran'],
];
// A component ceiling (docs/phase-11-plan.md R19) judges only the lines of what it runs; the others show, unjudged. Its own lines: the
// Collector's queue not growing (ingest backs up there before it refuses), and for the search ceiling a search p95 ≤ 2 s (no §9
// target exists; 2 s is the ceiling's line, Phase 11 milestone 4).
const ceiling = meta.ceiling || null;
if (ceiling) {
  const keep = { ingest: ['Throughput', 'Ingestion freshness'], search: ['Throughput', 'Ingestion freshness', 'API reads'],
    workers: ['Throughput', 'Ingestion freshness', 'Health freshness', 'Rule breach-to-alert', 'First dispatch', 'Queue lag'] }[ceiling] ?? [];
  for (const t of targets) if (t[1] !== null && !keep.some(k => t[0].startsWith(k))) { t[1] = null; t[2] = `not judged (ceiling ${ceiling}): ${t[2]}`; }
  targets.push(['Collector export queue not growing (ceiling line)', queueGrowing === null ? null : !queueGrowing,
    `${f(pct(queue, 0.5), 0)} items median, ${f(max(queue), 0)} max, sum of the exporters${queueGrowing === null ? '; growth not judged (window under two load cycles)' : queueGrowing ? ', growing' : ''}`]);
  if (ceiling === 'search') {
    const t = targets.find(x => x[0].startsWith('Telemetry search'));
    t[0] = 'Telemetry search p95 ≤ 2 s (ceiling line)'; t[1] = within(searchP95, 2000);
  }
}
const parts = Object.entries(exits).filter(([, v]) => v !== 0)
  .map(([k, v]) => k.endsWith('-restarts') ? `${k.slice(0, -'-restarts'.length)} crashed and restarted ${v}×` : `${k} exited ${v}`);

const ceilingTitle = { ingest: ' (workers reading ClickHouse off, no query mix, no breach runs)',
  search: ` at ${meta.search_rate} searches/s (workers health, rules, SLOs off, no breach runs)`,
  workers: ` at ${meta.rules} rules and ${meta.slos} SLOs (every worker on, no query mix)` }[ceiling] ?? '';
const md = `# Scale step ×${m}${ceiling ? `, ${ceiling} ceiling${ceilingTitle}` : ''}: ${new Date(t0).toISOString().slice(0, 16).replace('T', ' ')} UTC

Nominal ${f(1000 * m, 0)} spans/s, ${f(10000 * m, 0)} metric points/s, ${f(1000 * m, 0)} logs/s from 10 services × 10 instances on 20 hosts, measured for ${f(window / 60, 1)} minutes
after ${meta.warmup ?? 0} s of warm-up. Host: ${meta.host_cores} cores; Docker VM ${cores} vCPU, ${f((meta.docker_mem_bytes ?? 0) / 2 ** 30, 1)} GiB. Commit ${meta.commit}.
The load generator, on the same host, used ${f(genCpu, 1)} % of its ${meta.host_cores} cores.${parts.length ? `\n\n**Parts that failed:** ${parts.join(', ')} (see their logs).` : ''}
${harnessLimit ? '**Harness limit:** the generator itself fell behind (spans given up with few sends in progress) or a worker crashed, so this step says nothing about the platform: repeat it with more workers (`--workers`) or fewer other processes on the host.\n\n' : ''}${ceiling ? 'A component ceiling: no pass mark; the first line crossed is its ceiling.' : m === 1 ? 'At 1× every target is a pass mark (P3).' : 'Above 1× there is no pass mark (P3): each line is within or over, and the first crossed is the limit.'}

## Measures

| Measure | Result | |
|---|---|---|
${targets.map(([what, ok, detail]) => `| ${what} | ${detail} | ${verdict(ok)} |`).join('\n')}

## Throughput

| Signal | Sent in the window | Of nominal | Collector accepted | Refused | Export failed | Enqueue failed | Stored in the window | Of nominal |
|---|---|---|---|---|---|---|---|---|
${through.map(t => `| ${t.signal} | ${f(t.sent, 0)} | ${pctOf(t.offered)} | ${f(t.accepted, 0)} | ${f(t.refused, 0)} | ${f(t.sendFailed, 0)} | ${f(t.enqueueFailed, 0)} | ${f(t.stored, 0)} | ${pctOf(t.storedShare)} |`).join('\n')}

"Sent" is the generator's 10-second counts inside the window; "Accepted" the Collector's over the sampler's samples in the window, from every source (probes,
breach runs, the stack's host metrics); refused and failed are counted from before the warm-up. "Stored" is the shop's rows timestamped in the window.

## Lags and backlogs (sampled every 10 s)

| What | p95 | Max | Configured delay | Past it (line: 60 s) | Growing |
|---|---|---|---|---|---|
${lags.map(l => `| ${l.what}${l.note ? ` (${l.note})` : ''} | ${f(l.p95)} s | ${f(l.max)} s | ${l.delay} s | ${l.line === null ? `${f(l.excess)} s (not judged)` : `${f(l.excess)} s`} | ${l.growing === null ? '–' : l.growing ? '**yes**' : 'no'} |`).join('\n')}
${counts.map(c => `| ${c.what} | ${f(c.p95, 0)} | ${f(c.max, 0)} | | | ${c.growing === null ? '–' : c.growing ? '**yes**' : 'no'} |`).join('\n')}

${growthNote ? `${growthNote[0].toUpperCase()}${growthNote.slice(1)}.` : 'Growing: the last full 15-minute load cycle above the first by more than a noise floor.'}

## Resources in the window (docker stats every 10 s; CPU in % of one vCPU, ${cores} available)

| Container | CPU mean | CPU max | Memory max |
|---|---|---|---|
${resources.map(r => `| ${r.container} | ${f(r.cpuMean)} % | ${f(r.cpuMax)} % | ${f((r.memMax ?? 0) / 2 ** 20, 0)} MiB |`).join('\n')}
| load generator (host process) | ${f((genCpu ?? 0) * (meta.host_cores ?? 24))} % | | |

## Disk (P6)

| Table | GB per day | Rows per day | Retention | At full retention |
|---|---|---|---|---|
${disk.map(d => `| ${d.table} | ${f(d.perDayGB, 2)} | ${f(d.rowsPerDay, 0)} | ${d.retentionDays ? `${d.retentionDays} d` : '–'} | ${d.retainedGB !== null ? `${f(d.retainedGB, 1)} GB` : '–'} |`).join('\n')}
| **Total at full retention** | | | | **${f(retainedTotal, 1)} GB** |

Growth over ${f((span ?? 0) / 60, 0)} minutes, scaled to a day: compressed parts on disk before ClickHouse's later merges, so a rough upper figure.
`;
writeFileSync(`${dir}/report.md`, md);
writeFileSync(`${dir}/summary.json`, JSON.stringify({ meta, window, harnessLimit, lagCovered, acceptedCovered, targets: targets.map(([what, ok, detail]) => ({ what, ok, detail })), through, lags, counts, resources, genCpu, disk, retainedTotal, exits }, null, 2));
console.log(md);
process.exitCode = targets.some(([, ok]) => ok === false) || parts.length ? 1 : 0;
