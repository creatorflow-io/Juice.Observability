// Writes the scale stack's rules.json, slos.json and webhooks.json (docs/phase-10-plan.md P4: 50 rules and 50 SLOs, as the Phase 9
// benchmark, here over the 10 services generate.mjs sends). Run it after changing the shop or the mix; the files are committed.
//   node scale/config.mjs
import { writeFileSync } from 'node:fs';
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

// One signed webhook to the sink in compose.scale.yml, on the pinned scale network, so dispatch delay is measured end to end.
const webhooks = {
  Webhooks: {
    AllowHttpToAllowlisted: true,
    Endpoints: [{ Key: 'scale-sink', Kind: 'Webhook', Url: 'http://webhook-sink:8080/hook', SecretRef: 'env:JUICE_WEBHOOK_SCALE' }],
    Allowlist: [{ Destination: '172.29.240.0/24', Ports: [8080] }],
  },
};

if (rules.length !== 50 || slos.length !== 50) throw new Error(`${rules.length} rules, ${slos.length} SLOs: expected 50 each`);
const note = 'Generated by scale/config.mjs for the scale stack (compose.scale.yml); edit that script, not this file.';
writeFileSync(`${here}rules.json`, JSON.stringify({ '//': note, Rules: { Definitions: rules } }, null, 2) + '\n');
writeFileSync(`${here}slos.json`, JSON.stringify({ '//': note, Slos: { Definitions: slos } }, null, 2) + '\n');
writeFileSync(`${here}webhooks.json`, JSON.stringify({ '//': note, ...webhooks }, null, 2) + '\n');
console.log(`wrote ${rules.length} rules, ${slos.length} SLOs, ${webhooks.Webhooks.Endpoints.length} webhook`);
