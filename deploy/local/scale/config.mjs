// Writes the scale stack's rules.json, slos.json and notifications-provisioning.json (docs/phase-10-plan.md P4: 50 rules and 50 SLOs, as the Phase 9
// benchmark, here over the 10 services generate.mjs sends). Run it after changing the shop or the mix; the files are committed.
//   node scale/config.mjs
//   node scale/config.mjs --factor <k> --out <dir>   the workers ceiling (docs/phase-11-plan.md R19): k copies of the 50 rules and of
//                                                    the 50 SLOs (at most 200 SLOs, SloOptions.MaxSlos) in <dir>, not committed
//   ... --copy-from <n>   numbers the copies from n (default 2): keys the database hasn't seen, so their SLOs are new and count their
//                         30 days together (the burst of burn alerts that crashed the platform, docs/scale-tests.md)
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = fileURLToPath(new URL('./', import.meta.url));
const SERVICES = ['gateway', 'catalog', 'cart', 'checkout', 'users', 'payments', 'inventory', 'shipping', 'notifications', 'search'];
const service = s => `shop/${s}`, environment = 'production';
// Three routes of each service with their own availability SLO (the first three of generate.mjs SHAPE.routes).
const ROUTES = {
  gateway: ['GET /', 'GET /api/products', 'POST /api/cart'], catalog: ['GET /products', 'GET /products/{id}', 'GET /categories'],
  cart: ['GET /cart', 'POST /cart/items', 'DELETE /cart/items/{id}'], checkout: ['POST /checkout', 'POST /checkout/quote', 'GET /checkout/{id}'],
  users: ['GET /users/me', 'POST /sessions', 'GET /users/{id}/addresses'], payments: ['POST /payments', 'POST /payments/{id}/capture', 'POST /payments/{id}/refund'],
  inventory: ['GET /stock/{sku}', 'POST /reservations', 'DELETE /reservations/{id}'], shipping: ['POST /quotes', 'POST /shipments', 'GET /shipments/{id}'],
  notifications: ['orders process', 'POST /emails', 'POST /sms'], search: ['GET /search', 'GET /suggest', 'POST /index'],
};

// 50 rules: the stock tenant-wide ones (service-error-rate is what tests/breach-to-alert.sh waits for), and per service an error rate at
// two levels, p95 latency, and traffic stopped, plus a tighter latency rule on five fast services.
const rules = [
  { Key: 'service-error-rate', Name: 'Service error rate above 5 %', Template: 'ErrorRate', Severity: 'Critical', Threshold: 0.05, RecoveryThreshold: 0.02, MinimumSamples: 20, ForDuration: '00:02:00', RecoveryDuration: '00:03:00' },
  { Key: 'outbox-delivery-failures', Name: 'Outbox delivery failures above 10 %', Template: 'DeliveryFailureRate', Severity: 'Warning', Threshold: 0.1, RecoveryThreshold: 0.05, MinimumSamples: 10 },
  { Key: 'mediator-errors', Name: 'More than 5 mediator errors in 5 minutes', Template: 'MediatorErrors', Severity: 'Warning', Threshold: 5, RecoveryThreshold: 1 },
  { Key: 'consumer-failures', Name: 'More than 5 failed messages in 5 minutes', Template: 'ConsumerFailures', Severity: 'Warning', Threshold: 5, RecoveryThreshold: 1 },
  { Key: 'host-memory', Name: 'Host memory above 95 %', Template: 'HostMemory', Severity: 'Warning', Threshold: 0.95, RecoveryThreshold: 0.9, ForDuration: '00:05:00' },
];
for (const s of SERVICES) {
  const scoped = { Service: service(s), Environment: environment };
  rules.push(
    { Key: `${s}-errors`, Name: `${s}: error rate above 5 %`, Template: 'ErrorRate', Severity: 'Critical', Threshold: 0.05, RecoveryThreshold: 0.02, MinimumSamples: 20, ForDuration: '00:01:00', ...scoped },
    { Key: `${s}-errors-warning`, Name: `${s}: error rate above 1 %`, Template: 'ErrorRate', Severity: 'Warning', Threshold: 0.01, RecoveryThreshold: 0.005, MinimumSamples: 50, ForDuration: '00:05:00', ...scoped },
    { Key: `${s}-latency`, Name: `${s}: p95 latency above 1 s`, Template: 'LatencyP95', Severity: 'Warning', Threshold: 1000, RecoveryThreshold: 800, MinimumSamples: 20, ForDuration: '00:05:00', ...scoped },
    { Key: `${s}-traffic`, Name: `${s}: traffic stopped`, Template: 'ThroughputAbsent', Severity: 'Warning', Window: '00:10:00', Threshold: 0, ForDuration: '00:05:00', ...scoped });
}
for (const s of ['gateway', 'catalog', 'cart', 'users', 'inventory'])
  rules.push({ Key: `${s}-latency-fast`, Name: `${s}: p95 latency above 300 ms`, Template: 'LatencyP95', Severity: 'Info', Threshold: 300, RecoveryThreshold: 250, MinimumSamples: 20, ForDuration: '00:05:00', Service: service(s), Environment: environment });

// 50 SLOs: per service availability 99.5 % and latency 99 % under 300 ms, and three routes each at 99.9 %.
const slos = [];
for (const s of SERVICES) {
  slos.push({ Key: `${s}-availability`, Name: `${s} availability`, Sli: 'Availability', Service: service(s), Environment: environment, Target: 99.5 },
    { Key: `${s}-latency`, Name: `${s} latency`, Sli: 'Latency', Service: service(s), Environment: environment, Target: 99, ThresholdMs: 300 });
  ROUTES[s].forEach((route, i) => slos.push({ Key: `${s}-route-${i + 1}`, Name: `${s} ${route}`, Sli: 'Availability', Service: service(s), Environment: environment, Target: 99.9, Operations: [route] }));
}

// One signed webhook to the sink in compose.scale.yml, on the pinned scale network, so the notification is measured end to end: an integration
// with the sink as its target, a route for the alerts, and the sink's network in the allowlist. The file is keyed by the deployment's tenant Id
// (JUICE_PLATFORM_TENANT_ID in the environment or .env, else the local stacks' tenant), the only one the pipeline accepts.
const envFile = join(here, '..', '.env');
const tenantId = process.env.JUICE_PLATFORM_TENANT_ID
  ?? (existsSync(envFile) ? readFileSync(envFile, 'utf8').match(/^JUICE_PLATFORM_TENANT_ID=(S+)/m)?.[1] : undefined) ?? '0193f0aa-0000-7000-8000-0000000000a1';
const provisioning = {
  tenants: {
    [tenantId]: {
      integrations: { 'scale-sink': { channel: 'webhook', kind: 'webhook', secretRef: 'env:JUICE_WEBHOOK_SCALE', targets: { sink: { address: 'http://webhook-sink:8080/hook' } } } },
      routes: { alerts: { match: { type: 'observability.alert.*' }, targets: ['sink'] } },
      allowlist: [{ destination: '172.29.240.0/24', ports: [8080] }],
    },
  },
};

if (rules.length !== 50 || slos.length !== 50) throw new Error(`${rules.length} rules, ${slos.length} SLOs: expected 50 each`);
const note = 'Generated by scale/config.mjs for the scale stack (compose.scale.yml); edit that script, not this file.';
// The workers ceiling: copies 2..k of each rule and SLO under keys of their own, with the same query (template, service, operations)
// and so the same cost. The first copy is the committed set, service-error-rate included (the breach runs wait for it).
const arg = name => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : undefined; };
const factor = Number(arg('--factor') ?? 0), copyFrom = Number(arg('--copy-from') ?? 2);
if (factor) {
  const out = arg('--out');
  if (!Number.isInteger(factor) || factor < 1 || !out || !Number.isInteger(copyFrom) || copyFrom < 2) throw new Error('--factor <k ≥ 1> --out <dir> [--copy-from <n ≥ 2>]');
  const copies = (list, k) => [...list, ...Array.from({ length: k - 1 }, (_, i) =>
    list.map(d => ({ ...d, Key: `${d.Key}-c${i + copyFrom}`, Name: `${d.Name} (copy ${i + copyFrom})` }))).flat()];
  const manyRules = copies(rules, factor), manySlos = copies(slos, factor).slice(0, 200);
  const generated = `Generated by scale/config.mjs --factor ${factor}${copyFrom === 2 ? '' : ` --copy-from ${copyFrom}`} for the workers ceiling; not committed.`;
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, 'rules.json'), JSON.stringify({ '//': generated, Rules: { Definitions: manyRules } }, null, 2) + '\n');
  writeFileSync(join(out, 'slos.json'), JSON.stringify({ '//': generated, Slos: { Definitions: manySlos } }, null, 2) + '\n');
  console.log(`${manyRules.length} ${manySlos.length}`);
  process.exit(0);
}
writeFileSync(`${here}rules.json`, JSON.stringify({ '//': note, Rules: { Definitions: rules } }, null, 2) + '\n');
writeFileSync(`${here}slos.json`, JSON.stringify({ '//': note, Slos: { Definitions: slos } }, null, 2) + '\n');
writeFileSync(`${here}notifications-provisioning.json`, JSON.stringify(provisioning, null, 2) + '\n');
console.log(`wrote ${rules.length} rules, ${slos.length} SLOs, 1 webhook integration`);
