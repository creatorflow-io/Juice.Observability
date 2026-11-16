// Scale-test telemetry (docs/phase-10-plan.md P3, milestone 2): a 10-service shop, 10 instances each on 20 hosts, sending OTLP protobuf
// (gzip) to a Collector at a multiple of the requirements §9 reference rates: 1,000 spans/s, 10,000 metric points/s, 1,000 logs/s.
//   node scale/generate.mjs --multiplier 1 --duration 1800 [--endpoint https://localhost:4318] [--out dir] [--workers n] [--stop-file path]
// It stops at --duration, or when --stop-file appears (a signal from Git Bash would kill it without its summary).
// What it models, so discovery, health, rules, SLOs and topology all have work:
// - traces that cross services (client span → server span, parent and child), with database and messaging spans, so dependencies
//   and resource edges are found; one log record per span, on the span's instance, with its trace and span IDs;
// - every instance exports 996 metric points per cycle (HTTP server/client and DB histograms, runtime gauges and sums, feature-flag
//   counters), every host 20 more (CPU, memory): 100,000 points per cycle, the cycle 10 s / multiplier. Cardinality stays fixed; the
//   rate grows with the multiplier;
// - a 15-minute cycle: payments fails 15 % of requests in minutes 3-8 (error-rate alerts fire and resolve), shipping is slow in minutes
//   9-14 (latency alerts); inventory-7 always fails 2 % (noise below every threshold).
// Prints a line every 10 s and, with --out, writes generator.csv and generator.json there.
import { Worker, isMainThread, parentPort, workerData } from 'node:worker_threads';
import { readFileSync, writeFileSync, appendFileSync, mkdirSync, existsSync } from 'node:fs';
import { request, Agent } from 'node:https';
import { gzipSync } from 'node:zlib';
import { randomFillSync } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import * as otlp from './otlp.mjs';

// The stack whose token and CA it sends with: this checkout's, or JUICE_LOCAL_DIR's (tests/upgrade-drill.sh sends to a stack checked out elsewhere).
const local = process.env.JUICE_LOCAL_DIR ? process.env.JUICE_LOCAL_DIR.replace(/[\/]?$/, '/') : fileURLToPath(new URL('../', import.meta.url));

// ─── the shop ───────────────────────────────────────────────────────────────

const SERVICES = ['gateway', 'catalog', 'cart', 'checkout', 'users', 'payments', 'inventory', 'shipping', 'notifications', 'search'];
const INSTANCES = 10, HOSTS = 20, NAMESPACE = 'shop', ENVIRONMENT = 'production';
// Each service's routes (server spans) and what a request to it calls: other services, a database, a topic.
const SHAPE = {
  gateway: { routes: ['GET /', 'GET /api/products', 'POST /api/cart', 'POST /api/checkout', 'GET /api/account'], ms: 4 },
  catalog: { routes: ['GET /products', 'GET /products/{id}', 'GET /categories', 'GET /products/{id}/reviews', 'GET /featured'], ms: 12, db: ['postgresql', 'catalog'], calls: [['inventory', 0.5], ['search', 0.3]] },
  cart: { routes: ['GET /cart', 'POST /cart/items', 'DELETE /cart/items/{id}', 'PUT /cart/items/{id}', 'POST /cart/merge'], ms: 6, db: ['redis', 'cart'], calls: [['inventory', 0.6]] },
  checkout: { routes: ['POST /checkout', 'POST /checkout/quote', 'GET /checkout/{id}', 'POST /checkout/{id}/confirm', 'POST /checkout/{id}/cancel'], ms: 18, db: ['postgresql', 'orders'], calls: [['payments', 1], ['inventory', 1], ['shipping', 0.8]], topic: 'orders' },
  users: { routes: ['GET /users/me', 'POST /sessions', 'GET /users/{id}/addresses', 'PUT /users/me', 'POST /users'], ms: 7, db: ['postgresql', 'users'] },
  payments: { routes: ['POST /payments', 'POST /payments/{id}/capture', 'POST /payments/{id}/refund', 'GET /payments/{id}', 'POST /payments/authorize'], ms: 35, db: ['postgresql', 'payments'] },
  inventory: { routes: ['GET /stock/{sku}', 'POST /reservations', 'DELETE /reservations/{id}', 'GET /stock', 'POST /stock/adjust'], ms: 5, db: ['postgresql', 'inventory'] },
  shipping: { routes: ['POST /quotes', 'POST /shipments', 'GET /shipments/{id}', 'GET /carriers', 'POST /labels'], ms: 20, db: ['postgresql', 'shipping'] },
  notifications: { routes: ['orders process', 'POST /emails', 'POST /sms', 'GET /templates', 'POST /push'], ms: 9, consumer: 'orders' },
  search: { routes: ['GET /search', 'GET /suggest', 'POST /index', 'GET /facets', 'GET /popular'], ms: 15, db: ['elasticsearch', 'products'] },
};
const ENTRY = [['catalog', 0.4], ['cart', 0.22], ['checkout', 0.13], ['users', 0.15], ['search', 0.1]];
const instanceKey = (s, n) => `${SERVICES[s]}-${n}`;
const hostOf = (s, n) => `host-${String((s * INSTANCES + n) % HOSTS).padStart(2, '0')}`;

// ─── main thread: workers, totals, report ───────────────────────────────────

if (isMainThread) {
  const args = Object.fromEntries(process.argv.slice(2).reduce((a, x, i, all) => (x.startsWith('--') ? [...a, [x.slice(2), all[i + 1]]] : a), []));
  const multiplier = Number(args.multiplier ?? 1), duration = Number(args.duration ?? 120);
  const endpoint = args.endpoint ?? 'https://localhost:4318';
  // About 1.5 workers per 1×, from 2 up to the host's cores less 4 (the VM shares them); --workers overrides it.
  const workers = Number(args.workers ?? Math.min(Math.max(4, (await import('node:os')).cpus().length - 4), Math.max(2, Math.ceil(multiplier * 1.5))));
  const out = args.out, stopFile = args['stop-file'];
  if (out) { mkdirSync(out, { recursive: true }); writeFileSync(`${out}/generator.csv`, 'time,spans,points,logs,requests,failed,refused,pending,cpu_pct\n'); }
  const token = readFileSync(`${local}secrets/otlp-tokens`, 'utf8').split(/\s/)[0];
  const ca = readFileSync(`${local}secrets/tls/ca.crt`);
  const started = Date.now();
  console.log(`scale load ×${multiplier} for ${duration} s to ${endpoint}: ${1000 * multiplier} spans/s, ${10000 * multiplier} points/s, ` +
    `${1000 * multiplier} logs/s; ${SERVICES.length} services × ${INSTANCES} instances on ${HOSTS} hosts; ${workers} workers`);

  const total = { spans: 0, points: 0, logs: 0, requests: 0, failed: 0, refused: 0, skipped: 0, skippedLocal: 0, pointsSkipped: 0, pending: 0, crashed: 0 };
  const pendingBy = new Map();
  const errors = new Map();
  let done = 0;
  const threads = Array.from({ length: workers }, (_, index) => {
    const w = new Worker(fileURLToPath(import.meta.url), { workerData: { index, workers, multiplier, duration, endpoint, token, ca, started } });
    w.on('message', m => {
      if (m.error) errors.set(m.error, (errors.get(m.error) ?? 0) + 1);
      else {
        for (const k of ['spans', 'points', 'logs', 'requests', 'failed', 'refused', 'skipped', 'skippedLocal', 'pointsSkipped']) total[k] += m[k] ?? 0;
        pendingBy.set(m.index, m.pending); total.pending = [...pendingBy.values()].reduce((a, b) => a + b, 0);
      }
    });
    // A crashed worker takes its share of the load with it: counted, and the run fails.
    w.on('error', e => { console.error(`worker ${index}: ${e.stack}`); total.crashed++; });
    w.on('exit', () => { if (++done === workers) finish(); });
    return w;
  });

  if (stopFile) { const watch = setInterval(() => { if (existsSync(stopFile)) { clearInterval(watch); for (const t of threads) t.postMessage('stop'); } }, 1000); watch.unref(); }
  let last = { ...total }, lastCpu = process.cpuUsage(), lastAt = Date.now();
  const cores = (await import('node:os')).cpus().length;
  const tick = setInterval(() => {
    const now = Date.now(), cpu = process.cpuUsage(lastCpu), secs = (now - lastAt) / 1000;
    const pct = ((cpu.user + cpu.system) / 1e6 / secs / cores) * 100;
    const d = Object.fromEntries(Object.keys(total).map(k => [k, total[k] - last[k]]));
    console.log(`${new Date(now).toISOString().slice(11, 19)} spans ${(d.spans / secs).toFixed(0)}/s, points ${(d.points / secs).toFixed(0)}/s, ` +
      `logs ${(d.logs / secs).toFixed(0)}/s, failed ${d.failed}, refused ${d.refused}, generator CPU ${pct.toFixed(1)} % of ${cores} cores`);
    if (out) appendFileSync(`${out}/generator.csv`, `${new Date(now).toISOString()},${d.spans},${d.points},${d.logs},${d.requests},${d.failed},${d.refused},${total.pending},${pct.toFixed(2)}\n`);
    last = { ...total }; lastCpu = process.cpuUsage(); lastAt = now;
  }, 10000);

  function finish() {
    clearInterval(tick);
    const secs = (Date.now() - started) / 1000;
    const summary = { multiplier, duration, endpoint, workers, seconds: Math.round(secs), ...total,
      rates: { spans: total.spans / secs, points: total.points / secs, logs: total.logs / secs }, errors: Object.fromEntries(errors) };
    console.log(`sent ${total.spans} spans, ${total.points} points, ${total.logs} logs in ${secs.toFixed(0)} s; ` +
      `${total.failed} requests failed, ${total.refused} refused, ${total.skipped} spans skipped under backpressure, ${total.skippedLocal} by a slow generator, ${total.pointsSkipped} points skipped, ${total.crashed} workers crashed${errors.size ? `; errors: ${JSON.stringify(summary.errors)}` : ''}`);
    if (out) writeFileSync(`${out}/generator.json`, JSON.stringify(summary, null, 2));
    process.exit(total.failed || total.refused || total.crashed || total.skipped || total.skippedLocal || total.pointsSkipped ? 1 : 0);
  }
} else {
  await work(workerData);
}

// ─── a worker: its share of requests and of instances ───────────────────────

async function work({ index, workers, multiplier, duration, endpoint, token, ca, started }) {
  const url = new URL(endpoint);
  const agent = new Agent({ keepAlive: true, maxSockets: 8, ca });
  // skipped: spans given up while the platform held requests up (backpressure); skippedLocal: while this worker was simply too slow (a
  // harness limit, not the platform's); pointsSkipped: metric cycles given up after falling a cycle behind.
  const stats = { spans: 0, points: 0, logs: 0, requests: 0, failed: 0, refused: 0, skipped: 0, skippedLocal: 0, pointsSkipped: 0, pending: 0 };
  // Counts since the last message; pending is the current level.
  const flushStats = () => { parentPort.postMessage({ index, ...stats }); for (const k of Object.keys(stats)) if (k !== 'pending') stats[k] = 0; };

  function send(path, body, counted) {
    const gz = gzipSync(body, { level: 1 });
    stats.pending++;
    return new Promise(resolve => {
      // Settled once, whichever comes first: the answer, a request error, or a response cut off after its headers.
      let settled = false;
      const settle = (ok, status, error) => {
        if (settled) return; settled = true; stats.pending--; stats.requests++;
        if (ok) { for (const [k, n] of Object.entries(counted)) stats[k] += n; }
        else if (status === 429 || status === 503) { stats.refused++; parentPort.postMessage({ error: `${path} ${status}` }); }
        else { stats.failed++; parentPort.postMessage({ error: `${path} ${error ?? status}` }); }
        resolve();
      };
      const req = request({ host: url.hostname, port: url.port, path, method: 'POST', agent, timeout: 30000, headers: {
        authorization: `Bearer ${token}`, 'content-type': 'application/x-protobuf', 'content-encoding': 'gzip', 'content-length': gz.length } }, res => {
        res.resume();
        res.on('end', () => settle(res.statusCode === 200, res.statusCode));
        res.on('error', e => settle(false, res.statusCode, e.code ?? e.message));
        res.on('close', () => settle(false, res.statusCode, 'response cut off'));
      });
      req.on('error', e => settle(false, 0, e.code ?? e.message));
      req.on('timeout', () => req.destroy(new Error('timeout')));
      req.end(gz);
    });
  }

  // IDs from a random pool, refilled as it runs out.
  let pool = Buffer.alloc(0), at = 0;
  const id = n => { if (at + n > pool.length) { pool = randomFillSync(Buffer.allocUnsafe(1 << 16)); at = 0; } const b = pool.subarray(at, at + n); at += n; return b; };
  let seed = (index + 1) * 2654435761 >>> 0;
  const rand = () => { seed ^= seed << 13; seed >>>= 0; seed ^= seed >>> 17; seed ^= seed << 5; seed >>>= 0; return seed / 4294967296; };
  const pick = list => { let r = rand(); for (const [x, p] of list) { if ((r -= p) <= 0) return x; } return list[list.length - 1][0]; };
  const ns = ms => BigInt(Math.round(ms * 1e6));
  const nowNs = () => BigInt(Date.now()) * 1000000n;

  // Resources: an instance per (service, n), a host per host name.
  const scope = otlp.scope('Shop.Telemetry', '2.3.1');
  const instRes = SERVICES.map((svc, s) => Array.from({ length: INSTANCES }, (_, n) => otlp.resource({
    'service.name': svc, 'service.namespace': NAMESPACE, 'service.instance.id': instanceKey(s, n), 'service.version': '2.3.1',
    'deployment.environment.name': ENVIRONMENT, 'host.name': hostOf(s, n), 'telemetry.sdk.language': 'dotnet', 'telemetry.sdk.name': 'opentelemetry' })));
  const hostRes = Array.from({ length: HOSTS }, (_, h) => otlp.resource({ 'host.name': `host-${String(h).padStart(2, '0')}`, 'os.type': 'linux' }));

  // The 15-minute cycle.
  const minute = () => Math.floor((Date.now() - started) / 60000) % 15;
  const errorRate = (svc, n) => (svc === 'payments' && minute() >= 3 && minute() < 8 ? 0.15 : svc === 'inventory' && n === 7 ? 0.02 : 0.002);
  const slow = svc => (svc === 'shipping' && minute() >= 9 ? 1500 : 0);

  // ── traces and logs ──
  const spanTarget = (1000 * multiplier) / workers;   // per second, this worker
  const bySpan = new Map(), byLog = new Map();        // "s/n" → spans, records
  let added = 0;   // spans added, for pacing
  const add = (map, s, n, item) => { const k = s * INSTANCES + n; let l = map.get(k); if (!l) map.set(k, l = []); l.push(item); if (map === bySpan) added++; };
  const SEVERITY = [[9, 'Information', 0.85], [13, 'Warning', 0.12], [5, 'Debug', 0.03]];

  /** One request to `svc` under `parent` (a client or producer span), at `start` ms; returns [end ms, failed]. */
  function serve(s, parent, traceId, start, kind = 2) {
    const svc = SERVICES[s], shape = SHAPE[svc], n = Math.floor(rand() * INSTANCES);
    const spanId = Buffer.from(id(8));
    const route = kind === 5 ? shape.routes[0] : shape.routes[Math.floor(rand() * shape.routes.length)];
    let t = start + 0.2;
    let failed = rand() < errorRate(svc, n);
    if (shape.db) {
      const d = shape.ms * (0.3 + rand() * 0.6);
      const op = route.startsWith('GET') ? 'SELECT' : 'INSERT';
      add(bySpan, s, n, { traceId, spanId: Buffer.from(id(8)), parentSpanId: spanId, name: `${op} ${shape.db[1]}`, kind: 3, start: ns(t), end: ns(t + d),
        attributes: otlp.attrs({ 'db.system.name': shape.db[0], 'db.namespace': shape.db[1], 'db.operation.name': op, 'server.address': `${shape.db[1]}-db`, 'server.port': 5432 }) });
      t += d;
    }
    for (const [callee, p] of shape.calls ?? []) {
      if (rand() >= p) continue;
      const c = SERVICES.indexOf(callee), clientId = Buffer.from(id(8));
      const [end, calleeFailed] = serve(c, clientId, traceId, t + 0.3);
      add(bySpan, s, n, { traceId, spanId: clientId, parentSpanId: spanId, name: `${SHAPE[callee].routes[0].split(' ')[0]}`, kind: 3, start: ns(t), end: ns(end + 0.3),
        attributes: otlp.attrs({ 'http.request.method': SHAPE[callee].routes[0].split(' ')[0], 'server.address': callee, 'server.port': 8080,
          'http.response.status_code': calleeFailed ? 500 : 200 }), error: calleeFailed ? 'Internal Server Error' : undefined });
      failed ||= calleeFailed && rand() < 0.5;
      t = end + 0.5;
    }
    if (shape.topic) {
      const producerId = Buffer.from(id(8));
      add(bySpan, s, n, { traceId, spanId: producerId, parentSpanId: spanId, name: `${shape.topic} publish`, kind: 4, start: ns(t), end: ns(t + 1),
        attributes: otlp.attrs({ 'messaging.system': 'kafka', 'messaging.destination.name': shape.topic, 'messaging.operation.type': 'publish' }) });
      serve(SERVICES.indexOf('notifications'), producerId, traceId, t + 2, 5);
      t += 1;
    }
    const end = t + shape.ms * (0.5 + rand()) + slow(svc) * (0.7 + rand() * 0.6);
    const status = failed ? 500 : 200;
    const [method, ...path] = route.split(' ');
    add(bySpan, s, n, { traceId, spanId, parentSpanId: parent, name: route, kind, start: ns(start), end: ns(end), error: failed ? 'Internal Server Error' : undefined,
      attributes: kind === 5 ? otlp.attrs({ 'messaging.system': 'kafka', 'messaging.destination.name': shape.consumer, 'messaging.operation.type': 'process' })
        : otlp.attrs({ 'http.request.method': method, 'http.route': path.join(' '), 'http.response.status_code': status, 'url.scheme': 'http' }) });
    return [end, failed];
  }

  function request1() {
    // Started 2.5 s ago, so a slow request has ended by now, as when an SDK exports it.
    const traceId = Buffer.from(id(16)), start = Date.now() - 2500;
    const before = added;
    const gw = Math.floor(rand() * INSTANCES), gwSpan = Buffer.from(id(8)), clientId = Buffer.from(id(8));
    const target = SERVICES.indexOf(pick(ENTRY));
    const [end, failed] = serve(target, clientId, traceId, start + 1);
    const route = SHAPE.gateway.routes[Math.floor(rand() * 5)], [method, path] = route.split(' ');
    add(bySpan, 0, gw, { traceId, spanId: clientId, parentSpanId: gwSpan, name: method, kind: 3, start: ns(start + 0.5), end: ns(end + 0.5),
      attributes: otlp.attrs({ 'http.request.method': method, 'server.address': SERVICES[target], 'server.port': 8080, 'http.response.status_code': failed ? 500 : 200 }),
      error: failed ? 'Internal Server Error' : undefined });
    add(bySpan, 0, gw, { traceId, spanId: gwSpan, name: route, kind: 2, start: ns(start), end: ns(end + 1), error: failed ? 'Internal Server Error' : undefined,
      attributes: otlp.attrs({ 'http.request.method': method, 'http.route': path, 'http.response.status_code': failed ? 500 : 200, 'url.scheme': 'https' }) });
    return added - before;
  }

  // One log record per span, on its instance: what a service writes while serving.
  function logsFor(spansByInstance) {
    for (const [k, spans] of spansByInstance) {
      const s = Math.floor(k / INSTANCES), n = k % INSTANCES, svc = SERVICES[s];
      for (const sp of spans) {
        const [sev, text] = sp.error ? [17, 'Error'] : (() => { let r = rand(); for (const [num, t, p] of SEVERITY) { if ((r -= p) <= 0) return [num, t]; } return [9, 'Information']; })();
        const body = sp.error ? `${svc}: ${sp.name} failed: upstream returned 500` : sp.kind === 3 ? `${svc}: called ${sp.name} in ${Number((sp.end - sp.start) / 1000000n)} ms`
          : `${svc}: handled ${sp.name} in ${Number((sp.end - sp.start) / 1000000n)} ms`;
        add(byLog, s, n, { time: sp.end, severity: sev, severityText: text, body, traceId: sp.traceId, spanId: sp.spanId,
          attributes: otlp.attrs({ 'log.logger': `Shop.${svc[0].toUpperCase()}${svc.slice(1)}`, 'event.category': sp.kind === 3 ? 'outbound' : 'request' }) });
      }
    }
  }

  async function flushTraces() {
    if (bySpan.size === 0) return;
    const spans = new Map(bySpan); bySpan.clear();
    logsFor(spans);
    const logs = new Map(byLog); byLog.clear();
    // Requests of at most ~2,000 items, as an SDK's batch processor would send.
    const batches = (map, key) => { const out = []; let cur = [], size = 0;
      for (const [k, items] of map) { cur.push({ resource: instRes[Math.floor(k / INSTANCES)][k % INSTANCES], scope, [key]: items }); size += items.length;
        if (size >= 2000) { out.push([cur, size]); cur = []; size = 0; } }
      if (cur.length) out.push([cur, size]); return out; };
    await Promise.all([
      ...batches(spans, 'spans').map(([g, n]) => send('/v1/traces', otlp.traces(g), { spans: n })),
      ...batches(logs, 'records').map(([g, n]) => send('/v1/logs', otlp.logs(g), { logs: n })),
    ]);
  }

  // ── metrics ──
  const mine = [];   // [s, n] instances this worker exports, and hosts
  for (let k = index; k < SERVICES.length * INSTANCES; k += workers) mine.push([Math.floor(k / INSTANCES), k % INSTANCES]);
  const myHosts = []; for (let h = index; h < HOSTS; h += workers) myHosts.push(h);
  const cycleMs = 10000 / multiplier;
  const bounds = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];
  const state = new Map();   // cumulative counters per series
  const cum = (key, inc) => { const v = (state.get(key) ?? 0) + inc; state.set(key, v); return v; };
  const hist = (key, n, meanS) => {
    const prev = state.get(key) ?? { count: 0, sum: 0, buckets: new Array(bounds.length + 1).fill(0) };
    for (let i = 0; i < n; i++) { const v = meanS * (0.3 + rand() * 1.4); prev.count++; prev.sum += v; let b = bounds.findIndex(x => v <= x); prev.buckets[b < 0 ? bounds.length : b]++; }
    state.set(key, prev); return { count: prev.count, sum: prev.sum, buckets: [...prev.buckets], bounds, min: meanS * 0.3, max: meanS * 1.7 };
  };
  const FLAGS = 244; // 4 variants each: with the series below, 996 points an instance

  function instanceMetrics(s, n) {
    const svc = SERVICES[s], shape = SHAPE[svc], key = `${s}/${n}`, perCycle = 3;   // observations per series per cycle; the cycle shrinks with the multiplier, so their rate grows with it
    const server = [], client = [], db = [];
    for (const route of shape.routes) for (const code of [200, 500]) {
      const [method, ...p] = route.split(' ');
      server.push({ attributes: otlp.attrs({ 'http.request.method': method, 'http.route': p.join(' '), 'http.response.status_code': code }),
        ...hist(`${key}/srv/${route}/${code}`, code === 200 ? perCycle : rand() < 0.1 ? 1 : 0, (shape.ms + slow(svc)) / 1000) });
    }
    for (const [callee] of shape.calls ?? []) client.push({ attributes: otlp.attrs({ 'server.address': callee, 'http.request.method': 'POST' }), ...hist(`${key}/cli/${callee}`, perCycle, SHAPE[callee].ms / 1000) });
    for (const op of ['SELECT', 'INSERT', 'UPDATE']) db.push({ attributes: otlp.attrs({ 'db.system.name': shape.db?.[0] ?? 'none', 'db.operation.name': op }), ...hist(`${key}/db/${op}`, perCycle, shape.ms / 3000) });
    const list = [
      { name: 'http.server.request.duration', unit: 's', type: 'histogram', points: server },
      { name: 'http.client.request.duration', unit: 's', type: 'histogram', points: client.length ? client : [{ attributes: [], ...hist(`${key}/cli/none`, 0, 0.01) }] },
      { name: 'db.client.operation.duration', unit: 's', type: 'histogram', points: db },
      { name: 'process.cpu.time', unit: 's', type: 'sum', monotonic: true, points: ['user', 'system'].map(m => ({ attributes: otlp.attrs({ 'cpu.mode': m }), value: cum(`${key}/cpu/${m}`, rand() * 0.2 * multiplier) })) },
      { name: 'process.memory.usage', unit: 'By', type: 'gauge', points: [{ attributes: [], value: 180e6 + rand() * 40e6 }] },
      { name: 'dotnet.gc.collections', unit: '{collection}', type: 'sum', monotonic: true, points: ['gen0', 'gen1', 'gen2'].map(g => ({ attributes: otlp.attrs({ 'dotnet.gc.heap.generation': g }), value: cum(`${key}/gc/${g}`, g === 'gen0' ? 3 : 1), int: true })) },
      { name: 'dotnet.gc.heap.total_allocated', unit: 'By', type: 'sum', monotonic: true, points: [{ attributes: [], value: cum(`${key}/alloc`, 5e6 * multiplier), int: true }] },
      { name: 'dotnet.thread_pool.queue.length', unit: '{work_item}', type: 'gauge', points: [{ attributes: [], value: Math.floor(rand() * 4), int: true }] },
      { name: 'dotnet.thread_pool.thread.count', unit: '{thread}', type: 'gauge', points: [{ attributes: [], value: 24 + Math.floor(rand() * 8), int: true }] },
      { name: 'kestrel.active_connections', unit: '{connection}', type: 'gauge', points: [{ attributes: [], value: 10 + Math.floor(rand() * 40), int: true }] },
    ];
    const used = list.reduce((a, m) => a + m.points.length, 0);
    const flagPoints = [];
    for (let f = 0; f < FLAGS && used + flagPoints.length < 996; f++) for (const v of ['on', 'off', 'control', 'treatment'])
      if (used + flagPoints.length < 996) flagPoints.push({ attributes: otlp.attrs({ 'feature_flag.key': `flag-${String(f).padStart(3, '0')}`, 'feature_flag.variant': v }), value: cum(`${key}/flag/${f}/${v}`, 1), int: true });
    list.push({ name: 'app.feature_flag.evaluations', unit: '{evaluation}', type: 'sum', monotonic: true, points: flagPoints });
    return list;
  }
  function hostMetrics(h) {
    const cpus = Array.from({ length: 8 }, (_, c) => ['user', 'system'].map(st => ({ attributes: otlp.attrs({ 'cpu.logical_number': c, 'cpu.mode': st }), value: rand() * 0.4 }))).flat();
    return [
      { name: 'system.cpu.utilization', unit: '1', type: 'gauge', points: cpus },
      { name: 'system.memory.usage', unit: 'By', type: 'sum', monotonic: false, points: ['used', 'free', 'cached', 'buffered'].map(st => ({ attributes: otlp.attrs({ 'system.memory.state': st }), value: (st === 'used' ? 9e9 : 2e9) + rand() * 1e9, int: true })) },
    ];
  }
  const pointCount = list => list.reduce((a, m) => a + m.points.length, 0);
  async function exportMetrics(group) {
    const time = nowNs(), start = BigInt(started) * 1000000n;
    const groups = [], counts = { points: 0 };
    for (const [s, n] of group.instances) { const m = instanceMetrics(s, n); counts.points += pointCount(m); groups.push({ resource: instRes[s][n], scope, start, time, metrics: m }); }
    for (const h of group.hosts) { const m = hostMetrics(h); counts.points += pointCount(m); groups.push({ resource: hostRes[h], scope: otlp.scope('hostmetrics'), start, time, metrics: m }); }
    // Requests of at most ~10 instances (about 10,000 points).
    for (let i = 0; i < groups.length; i += 10) {
      const part = groups.slice(i, i + 10);
      await send('/v1/metrics', otlp.metrics(part), { points: part.reduce((a, g) => a + pointCount(g.metrics), 0) });
    }
  }

  // ── the loop, every 100 ms, everything by the clock ──
  const end = started + duration * 1000;
  const inflight = new Set();
  const track = p => { inflight.add(p); p.finally(() => inflight.delete(p)); };
  const loopStart = Date.now();
  // Spans: as many as the time since the start calls for, so a slow tick or a backpressure wait is made up; more than 2 s behind,
  // the rest is given up and counted (skipped), so a stalled Collector doesn't get one huge burst.
  let spansMade = 0;
  // Flushes once a second, each worker at its own offset: SDKs' batch timers aren't aligned.
  let nextFlush = loopStart + (index * 1000) / workers;
  // Metrics: each instance and host on its own schedule, offset across the cycle, once every cycleMs exactly.
  const items = [...mine.map(inst => ({ inst })), ...myHosts.map(host => ({ host }))];
  items.forEach((it, k) => { it.next = loopStart + ((k + index / workers) * cycleMs) / items.length; });
  let statsAt = Date.now(), stopping = false;
  parentPort.on('message', m => { if (m === 'stop') stopping = true; });
  parentPort.unref();
  while (Date.now() < end && !stopping) {
    const tick = Date.now();
    if (tick >= nextFlush) { nextFlush += 1000; track(flushTraces()); }
    const due = (spanTarget * (tick + 100 - loopStart)) / 1000;
    if (due - spansMade > 2 * spanTarget) {
      stats[inflight.size > 150 ? 'skipped' : 'skippedLocal'] += Math.round(due - spansMade - 2 * spanTarget); spansMade = due - 2 * spanTarget;
    }
    while (spansMade < due) spansMade += request1();
    const ready = items.filter(it => it.next <= tick);
    if (ready.length) {
      // A cycle behind (a stall): skip ahead rather than send the missed cycles in one burst, and count what was skipped.
      for (const it of ready) { it.next += cycleMs; if (it.next <= tick) { const missed = Math.ceil((tick - it.next) / cycleMs); stats.pointsSkipped += missed * (it.inst ? 996 : 20); it.next += missed * cycleMs; } }
      track(exportMetrics({ instances: ready.filter(it => it.inst).map(it => it.inst), hosts: ready.filter(it => it.host !== undefined).map(it => it.host) }));
    }
    if (Date.now() - statsAt >= 2000) { flushStats(); statsAt = Date.now(); }
    if (inflight.size > 200) { await Promise.race(inflight); }   // backpressure: never more than 200 sends (a trace flush, a metrics export) in progress
    await new Promise(r => setTimeout(r, Math.max(0, 100 - (Date.now() - tick))));
  }
  await flushTraces();
  await Promise.all(inflight);
  flushStats();
  agent.destroy();
}
