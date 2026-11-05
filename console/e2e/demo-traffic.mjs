// Demo traffic for e2e/journey.mjs: demo-shop/checkout calls demo-shop/payments, whose instance payments-2 fails every
// charge. It goes to the main Collector, so it lands in the tenant the running API works on, as production telemetry.
//   node e2e/demo-traffic.mjs [seconds]     default 180; leave it running while the journey runs
import { request } from 'node:https';
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';

const local = path => new URL(`../../deploy/local/${path}`, import.meta.url);
const ca = readFileSync(local('secrets/tls/ca.crt'));
const token = readFileSync(local('secrets/otlp-tokens'), 'utf8').split(/\s/)[0];
const seconds = Number(process.argv[2] ?? 180);
const started = Date.now();
const [perBatch, everyMs] = [10, 5000];   // 2 requests per second

const hex = bytes => randomBytes(bytes).toString('hex');
const nanos = ms => `${BigInt(Math.round(ms)) * 1000000n}`;
const attributes = values => Object.entries(values).map(([key, value]) => ({ key, value: { stringValue: value } }));
const resource = (service, instance) => ({
  attributes: attributes({
    'service.name': service, 'service.namespace': 'demo-shop', 'service.instance.id': instance,
    'host.name': `${instance}-host`, 'deployment.environment.name': 'production',
  }),
});

function post(signal, body) {
  return new Promise((resolve, reject) => {
    const req = request(`https://localhost:4318/v1/${signal}`, {
      method: 'POST', ca, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    }, res => {
      res.resume();
      res.on('end', () => (res.statusCode === 200 ? resolve() : reject(new Error(`${signal}: HTTP ${res.statusCode}`))));
    });
    req.on('error', reject);
    req.end(JSON.stringify(body));
  });
}

const span = (trace, id, parent, name, kind, ok, at, ms, values) => ({
  traceId: trace, spanId: id, ...(parent ? { parentSpanId: parent } : {}), name, kind,
  startTimeUnixNano: nanos(at), endTimeUnixNano: nanos(at + ms), attributes: attributes(values), status: { code: ok ? 1 : 2 },
});
const log = (trace, id, at, severityText, severityNumber, body, values) => ({
  timeUnixNano: nanos(at), severityText, severityNumber, traceId: trace, spanId: id, body: { stringValue: body }, attributes: attributes(values),
});

const charges = { 'payments-1': 0, 'payments-2': 0 };
let sent = 0;

async function batch() {
  const now = Date.now();
  const spans = { checkout: [], 'payments-1': [], 'payments-2': [] };
  const logs = { checkout: [], 'payments-2': [] };
  for (let i = 0; i < perBatch; i++, sent++) {
    const instance = sent % 2 ? 'payments-2' : 'payments-1';
    const ok = instance === 'payments-1';
    const at = now - 2000 + i * 100;
    const [trace, root, call, charge] = [hex(16), hex(8), hex(8), hex(8)];
    spans.checkout.push(
      span(trace, root, null, 'POST /checkout', 2, ok, at, 40, { 'http.request.method': 'POST', 'http.route': '/checkout' }),
      span(trace, call, root, 'POST payments/charge', 3, ok, at + 5, 30, { 'http.request.method': 'POST', 'server.address': 'payments' }));
    spans[instance].push(
      span(trace, charge, call, 'POST /charge', 2, ok, at + 8, 24, { 'http.route': '/charge' }),
      span(trace, hex(8), charge, 'INSERT charges', 3, true, at + 10, 8, { 'db.system.name': 'postgresql', 'db.namespace': 'payments', 'server.address': 'payments-db' }));
    charges[instance]++;
    if (!ok) {
      logs['payments-2'].push(log(trace, charge, at + 30, 'Error', 17, 'Charge failed: card processor connection refused (pool exhausted)',
        { 'exception.type': 'System.Net.Sockets.SocketException' }));
      logs.checkout.push(log(trace, root, at + 35, 'Warning', 13, 'Payment failed for order; returning 502', {}));
    }
  }
  const instanceOf = key => (key === 'checkout' ? ['checkout', 'checkout-1'] : ['payments', key]);
  await post('traces', { resourceSpans: Object.entries(spans).filter(([, s]) => s.length).map(([key, s]) => ({ resource: resource(...instanceOf(key)), scopeSpans: [{ spans: s }] })) });
  await post('logs', { resourceLogs: Object.entries(logs).filter(([, l]) => l.length).map(([key, l]) => ({ resource: resource(...instanceOf(key)), scopeLogs: [{ logRecords: l }] })) });
  const cumulative = (name, unit, asDouble) => ({
    name, unit, sum: { aggregationTemporality: 2, isMonotonic: true, dataPoints: [{ asDouble, startTimeUnixNano: nanos(started), timeUnixNano: nanos(now) }] },
  });
  const level = (name, unit, asInt) => ({
    name, unit, sum: { aggregationTemporality: 2, isMonotonic: false, dataPoints: [{ asInt: `${asInt}`, startTimeUnixNano: nanos(started), timeUnixNano: nanos(now) }] },
  });
  const uptime = (now - started) / 1000;
  const runtime = key => [
    cumulative('dotnet.process.cpu.time', 's', uptime * (key === 'payments-2' ? 0.8 : 0.2)),
    level('dotnet.process.memory.working_set', 'By', key === 'payments-2' ? 420e6 : 180e6),
    cumulative('dotnet.gc.pause.time', 's', uptime * 0.01),
    // A level, but .NET reports it as a monotonic counter.
    { ...level('dotnet.thread_pool.queue.length', '{work_item}', key === 'payments-2' ? 40 : 0), sum: { ...level('', '', key === 'payments-2' ? 40 : 0).sum, isMonotonic: true } },
  ];
  await post('metrics', {
    resourceMetrics: [
      ...Object.entries(charges).map(([instance, value]) => ({
        resource: resource('payments', instance),
        scopeMetrics: [{ metrics: [{
          name: 'payments.charges', unit: '{charge}',
          sum: { aggregationTemporality: 2, isMonotonic: true, dataPoints: [{ asInt: `${value}`, startTimeUnixNano: nanos(started), timeUnixNano: nanos(now) }] },
        }, ...runtime(instance)] }],
      })),
      { resource: resource(...instanceOf('checkout')), scopeMetrics: [{ metrics: runtime('checkout') }] },
    ],
  });
}

while (Date.now() - started < seconds * 1000) {
  const at = Date.now();
  await batch();
  await new Promise(resolve => setTimeout(resolve, Math.max(0, everyMs - (Date.now() - at))));
}
console.log(`sent ${sent} checkouts over ${seconds} s`);
