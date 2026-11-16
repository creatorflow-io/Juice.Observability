// The capacity view's calibration (docs/phase-11-plan.md R9, R10), from scale steps already run and analysed (components.mjs first):
//   node scale/calibration.mjs [--held-out 4,6] [--out <file>] <step dir>...
// For each step: whether every §9 line held (summary.json), and each resource's utilisation as the platform measures it live: ClickHouse's
// CPU (its own gauge, components.json), the Collector's inserts in flight (components.json), the export queue against its bound
// (collector.csv: the queues' sum, which the metrics queue is nearly all of), and each worker's pass time against its interval.
// The knee is a bracket from the calibration steps alone (the held-out steps are kept to check it): the last step every line held, and the
// first that crossed one. The view claims no more than that (owner, 16 November: the knee came from bursts, not a smooth curve).
// Writes deploy/local/capacity/calibration.json by default, which the compose stack mounts into the API (Capacity:CalibrationFile).
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
let heldOut = [], out = fileURLToPath(new URL('../capacity/calibration.json', import.meta.url));
const dirs = [];
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--held-out') heldOut = args[++i].split(',').map(Number);
  else if (args[i] === '--out') out = args[++i];
  else dirs.push(args[i]);
}
if (dirs.length === 0) { console.error('usage: node scale/calibration.mjs [--held-out 4,6] [--out <file>] <step dir>...'); process.exit(2); }

const json = f => JSON.parse(readFileSync(f, 'utf8'));
const QUEUE_BOUND = 200000;   // collector/config.yaml sending_queue.queue_size, per exporter
const quantile = (values, q) => { const a = [...values].sort((x, y) => x - y); return a.length ? a[Math.min(a.length - 1, Math.floor(q * a.length))] : null; };

const steps = dirs.map(dir => {
  const summary = json(join(dir, 'summary.json')), components = json(join(dir, 'components.json')), meta = summary.meta;
  if (!components.inserts) throw new Error(`${dir}: components.json has no inserts in flight; run components.mjs again`);
  const start = Date.parse(readFileSync(join(dir, 'start'), 'utf8').trim()), end = start + summary.window * 1000;
  const queue = readFileSync(join(dir, 'collector.csv'), 'utf8').trim().split('\n').slice(1).map(l => l.split(','))
    .filter(([t, m]) => m === 'otelcol_exporter_queue_size' && Date.parse(t) >= start && Date.parse(t) <= end).map(r => Number(r[2]));
  const cpu = components.gauges.find(g => g.name === 'juice.observability.clickhouse.cpu');
  const resources = {
    'clickhouse.cpu': { mean: cpu?.avg ?? null, max: cpu?.max ?? null, unit: 'cores' },
    'clickhouse.inserts_in_flight': { mean: components.inserts.mean, p99: components.inserts.p99, max: components.inserts.max, unit: 'inserts' },
    // No queue samples in the step's window (the Collector restarted, a scrape failed): no figure, never 0, which would make any fill red.
    'collector.queue': queue.length ? { p95: quantile(queue, 0.95) / QUEUE_BOUND, max: Math.max(...queue) / QUEUE_BOUND, unit: 'of the bound' } : { p95: null, max: null, unit: 'of the bound' },
  };
  // A worker's interval is the window over its passes (each runs on a timer); its utilisation is its mean pass over that.
  for (const p of components.passes) {
    if (p.passes > 0) resources[`worker.${p.worker}`] = { mean: p.mean_s / (components.seconds / p.passes), unit: 'of its interval' };
  }
  return {
    multiplier: meta.multiplier, heldOut: heldOut.includes(meta.multiplier),
    held: summary.targets.every(t => t.ok !== false), crossed: summary.targets.filter(t => t.ok === false).map(t => t.what),
    resources,
  };
}).sort((a, b) => a.multiplier - b.multiplier);

const meta = json(join(dirs[0], 'summary.json')).meta;
// ClickHouse's own core count (its automatic max_threads), which the platform reads live to match a calibration; the Docker VM's otherwise.
const clickhouseCores = json(join(dirs[0], 'components.json')).clickhouseCores ?? meta.docker_cpus;
const calibration = steps.filter(s => !s.heldOut);
const crossedAt = calibration.find(s => !s.held)?.multiplier ?? null;
const heldTo = calibration.filter(s => s.held && (crossedAt === null || s.multiplier < crossedAt)).at(-1)?.multiplier ?? null;
const file = {
  version: 1,
  created: new Date().toISOString(),
  commit: meta.commit,
  // The host the steps ran on: the view matches it by ClickHouse's cores (as ClickHouse counts them) and the memory it sees (the Docker
  // VM's, with Docker Desktop).
  host: { cores: clickhouseCores, memoryBytes: meta.docker_mem_bytes, hostCores: meta.host_cores, dockerCpus: meta.docker_cpus },
  // What drives the workers' cost besides the load: the scale stack's rules and SLOs (config.mjs), its shop's services (generate.mjs).
  mix: { rules: meta.rules, slos: meta.slos, services: 10, searchesPerSecond: meta.search_rate },
  // §9's reference load, per second, at 1×.
  reference: { spans: 1000, points: 10000, logs: 1000 },
  knee: { heldTo, crossedAt, crossed: calibration.find(s => s.multiplier === crossedAt)?.crossed ?? [] },
  steps,
};
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify(file, null, 2) + '\n');
console.log(`${out}: held to ${heldTo}×, crossed at ${crossedAt}× (calibration ${calibration.map(s => s.multiplier).join(', ')}; held out ${steps.filter(s => s.heldOut).map(s => `${s.multiplier}× ${s.held ? 'held' : 'crossed'}`).join(', ') || 'none'})`);
