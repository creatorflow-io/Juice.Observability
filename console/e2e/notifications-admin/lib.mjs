// Shared by the notification pages' journeys (phase 5, M48): Juice Console's Alerts › Notifications against a host that embeds the
// notification pipeline. They are the Notifications Console's 4c journeys (deliveries, configure, secrets, no-access) run as the
// platform's own roles: alice (admin) configures, erin (operator) reads and retries, victor (viewer) sees nothing.
//
// The host: BASE (default http://localhost:5180), the pipeline in `new` (Notifications:Pipeline=new), Notifications:Delivery:
// AllowHttpToAllowlisted=true (the receiver is plain HTTP on this machine), its tenant TENANT (default the platform tenant of
// deploy/local/.env), and NOTIFICATIONS_SECRET_E2E_HOOK in its environment (the webhook's signing secret, HOOK_SECRET here). Alerts come
// from telemetry: OTLP (default the main Collector, https://localhost:4318), which stamps the host's tenant; STACK_SECRETS names the
// stack's secrets folder when this checkout isn't the one scripts/init.sh ran in.
//
// A host of its own beside the stack's (a branch not yet in the image), on the integration tests' tenant so its alerts stay out of the
// running platform's: run it on :4200 (a redirect the Console's client allows) with Platform:TenantKey set to that tenant and a scratch
// database, and run the journeys with BASE=http://localhost:4200 TENANT=<that tenant> OTLP=https://localhost:14318 (the test Collector)
// TOKEN_CLIENT=juice-platform-test SPLIT_TENANT=1.
import { request } from 'node:https';
import { createServer } from 'node:http';
import { networkInterfaces } from 'node:os';
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { env, local, base, keycloak } from '../lib.mjs';

export { env, base };

/**
 * A token for `user` (password grant). TOKEN_CLIENT names the client: `juice-platform-cli` (the default) carries the platform tenant's claim,
 * `juice-platform-test` the integration tests' tenant's, for a host on that tenant.
 */
export async function tokenFor(user) {
  const r = await fetch(`${keycloak}/realms/juice/protocol/openid-connect/token`, { method: 'POST', body: new URLSearchParams({ grant_type: 'password',
    client_id: process.env.TOKEN_CLIENT ?? 'juice-platform-cli', username: user, password: env.KEYCLOAK_DEMO_PASSWORD }) });
  if (!r.ok) throw new Error(`token for ${user}: ${r.status}`);
  return (await r.json()).access_token;
}
export const tenant = process.env.TENANT ?? env.JUICE_PLATFORM_TENANT_ID;
export const hookSecret = process.env.HOOK_SECRET ?? 'm48-e2e-signing-secret-0123456789abcdef';
export const hookSecretRef = 'env:NOTIFICATIONS_SECRET_E2E_HOOK';
/** Where the Console mounts the notification pages, and the tenant's pages under it. */
export const pages = '/alerts/notifications';
export const tenantPage = path => `${pages}/tenants/${tenant}${path ? `/${path}` : ''}`;
/** The admin API (Juice.Notifications.Admin under /notifications) and the platform's own. */
export const adminPath = path => `/notifications/api/v1/tenants/${tenant}/${path}`;
/** The platform API names its tenant by its own record's id (GET /api/v1/tenants), the pipeline by the tenant key's. */
let platformTenant;
export async function platformPath(path) {
  platformTenant ??= (await must('alice', 'GET', '/api/v1/tenants')).items[0].id;
  return `/api/v1/tenants/${platformTenant}/${path}`;
}

const tokens = {};
/** A call as `user` that never throws: its status, text and JSON (null when none). */
export async function as(user, method, path, body) {
  tokens[user] ??= await tokenFor(user);
  const r = await fetch(`${base}${path}`, { method, headers: { authorization: `Bearer ${tokens[user]}`, 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  return { status: r.status, text, json };
}

/** A call as `user` that must succeed (or answer one of `allow`). */
export async function must(user, method, path, body, { allow = [] } = {}) {
  const r = await as(user, method, path, body);
  if (r.status >= 400 && !allow.includes(r.status)) throw new Error(`${user} ${method} ${path}: ${r.status} ${r.text}`);
  return r.json;
}

export async function until(read, done, seconds = 30) {
  const end = Date.now() + seconds * 1000;
  let value;
  do {
    value = await read();
    if (done(value)) return value;
    await new Promise(r => setTimeout(r, 1000));
  } while (Date.now() < end);
  return value;
}

/**
 * A webhook receiver on this machine's private IPv4 address (the pipeline never sends to loopback): answers 404 while `failing`, and keeps
 * each request's `webhook-id` (the delivery's id), timestamp, signature and body.
 */
export async function receiver() {
  const address = Object.values(networkInterfaces()).flat()
    .find(a => a && a.family === 'IPv4' && !a.internal && /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(a.address))?.address;
  if (!address) throw new Error('no private IPv4 address on this machine for the webhook receiver');
  const state = { failing: false, received: [] };
  const server = createServer((req, res) => {
    let body = '';
    req.on('data', d => body += d).on('end', () => {
      state.received.push({ id: req.headers['webhook-id'], timestamp: req.headers['webhook-timestamp'], signature: req.headers['webhook-signature'],
        body, status: state.failing ? 404 : 200 });
      res.writeHead(state.failing ? 404 : 200).end();
    });
  });
  await new Promise(resolve => server.listen(0, '0.0.0.0', resolve));
  const port = server.address().port;
  return Object.assign(state, { address, port, url: `http://${address}:${port}/hook`, close: () => new Promise(r => server.close(r)) });
}

/** The destination allowlist entry, integration, target and route (to the alert types) that send to `hook`, made as alice through the API. */
export async function webhookSetup(hook, run) {
  const keys = { integration: `e2e-hooks-${run}`, target: `e2e-hook-${run}`, route: `e2e-alerts-${run}`, routes: [] };
  const listed = await must('alice', 'GET', adminPath('allowlist'));
  const existing = listed.find(a => a.destination === hook.address || a.destination === `${hook.address}/32`);
  await must('alice', 'PUT', adminPath('allowlist'), { destination: existing?.destination ?? hook.address,
    ports: [...new Set([...(existing?.ports ?? []), hook.port])] });
  await must('alice', 'PUT', adminPath(`integrations/${keys.integration}`), { channel: 'webhook', kind: 'webhook', secretRef: hookSecretRef });
  await must('alice', 'PUT', adminPath(`integrations/${keys.integration}/targets/${keys.target}`), { address: hook.url });
  await routeAlerts(keys, run);
  return keys;
}

/**
 * A route from the firing alerts of the rule {@link fireAlert}(`ruleRun`) makes to the setup's target: only this run's alerts, so another
 * run's rule, firing or resolving meanwhile, sends nothing here.
 */
export async function routeAlerts(keys, ruleRun) {
  const route = `e2e-alerts-${ruleRun}`;
  await must('alice', 'PUT', adminPath(`routes/${route}`), { match: { type: 'observability.alert.firing', labels: { rule: `e2e-${ruleRun}` } },
    targets: [keys.target] });
  keys.routes.push(route);
}

/** The setup {@link webhookSetup} made stops sending: its route removed and its integration disabled (one that has sent can't be deleted). */
export async function webhookTeardown(keys) {
  for (const route of keys.routes) await must('alice', 'DELETE', adminPath(`routes/${route}`), undefined, { allow: [404] });
  await must('alice', 'PUT', adminPath(`integrations/${keys.integration}`), { channel: 'webhook', kind: 'webhook', secretRef: hookSecretRef, enabled: false },
    { allow: [404] });
}

// ─── Alerts from telemetry ────────────────────────────────────────────────

const otlp = process.env.OTLP ?? 'https://localhost:4318';
// The stack's secrets (scripts/init.sh writes them in the checkout that runs it): STACK_SECRETS for another checkout's.
const secrets = process.env.STACK_SECRETS ?? `${local}secrets/`;
let otlpAuth;
const otlpSecrets = () => (otlpAuth ??= { ca: readFileSync(`${secrets}tls/ca.crt`), token: readFileSync(`${secrets}otlp-tokens`, 'utf8').split(/\s/)[0] });
const hex = bytes => randomBytes(bytes).toString('hex');
const nanos = ms => `${BigInt(Math.round(ms)) * 1000000n}`;
const attributes = values => Object.entries(values).map(([key, value]) => ({ key, value: { stringValue: value } }));

function post(signal, body) {
  return new Promise((resolve, reject) => {
    const { ca, token } = otlpSecrets();
    const req = request(`${otlp}/v1/${signal}`, { method: 'POST', ca, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' } },
      res => { res.resume(); res.on('end', () => (res.statusCode === 200 ? resolve() : reject(new Error(`${signal}: HTTP ${res.statusCode}`)))); });
    req.on('error', reject);
    req.end(JSON.stringify(body));
  });
}

/** Failing server spans of `e2e-m48/<service>` (production), now: what an ErrorRate rule on the service fires on. */
export async function failingTraffic(service, count = 20) {
  const now = Date.now();
  const spans = Array.from({ length: count }, (_, i) => ({ traceId: hex(16), spanId: hex(8), name: 'POST /charge', kind: 2,
    startTimeUnixNano: nanos(now - 5000 + i * 100), endTimeUnixNano: nanos(now - 5000 + i * 100 + 20), attributes: attributes({ 'http.route': '/charge' }),
    status: { code: 2 } }));
  await post('traces', { resourceSpans: [{ resource: { attributes: attributes({ 'service.name': service, 'service.namespace': 'e2e-m48',
    'service.instance.id': `${service}-1`, 'deployment.environment.name': 'production' }) }, scopeSpans: [{ spans }] }] });
}

/**
 * An alert of its own: an ErrorRate rule `e2e-<run>` on `e2e-m48/<service>` (made as alice), and failing traffic until it fires. Its
 * notification is `observability.alert.firing`, labelled with the rule, which {@link webhookSetup}'s route takes. The alert's id.
 */
export async function fireAlert(run, service = `svc-${run}`) {
  const key = `e2e-${run}`;
  await must('alice', 'POST', await platformPath('rules'), { key, name: `E2E ${run}`, template: 'ErrorRate', severity: 'Critical', service: `e2e-m48/${service}`,
    threshold: 0.05, forDuration: '00:00:00' }, { allow: [409] });
  const alert = await until(async () => {
    await failingTraffic(service);
    const alerts = await must('alice', 'GET', await platformPath(`alerts?rule=${key}`));
    return (alerts.items ?? alerts).find(a => a.rule === key && a.state === 'Firing');
  }, a => a, 120);
  if (!alert) throw new Error(`the rule ${key} didn't fire in two minutes`);
  return alert.id;
}


// ─── The browser ──────────────────────────────────────────────────────────

import { signIn as platformSignIn } from '../lib.mjs';
export { launch, steps, expect, finish } from '../lib.mjs';

/**
 * A page signed in as `user`, at `path`. Page errors go to `problems`, failed calls to `refused` (a journey that expects some sorts them).
 * SPLIT_TENANT=1, for a host on the integration tests' tenant whose browser sign-in still carries the platform tenant's claim: the
 * platform's own pages' calls it refuses for that reason (403 under /api/v1) are left out; the notification pages' never are.
 */
export async function signIn(browser, user, problems, refused, path = pages) {
  const split = process.env.SPLIT_TENANT === '1';
  const page = await platformSignIn(browser, user, problems, e => {
    if (split && / http 403 .*\/api\/v1\//.test(e) && !e.includes('/notifications/api/')) return;
    if (split && /console: Failed to load resource: the server responded with a status of 403/.test(e)) return;
    refused.push(e);
  });
  if (path !== '/') await page.goto(`${base}${path}`);
  return page;
}

/** The ids of the delivery rows the page shows. */
export const rowIds = page => page.getByTestId('delivery-row').evaluateAll(rs => rs.map(r => r.getAttribute('data-id')));

/**
 * Whether `text` holds `secret`: as written, as a JSON string escapes it (`+` for `+`, `\/`), or URL-encoded. A secret with such
 * characters (a base64 key) would otherwise be missed in an API answer.
 */
export function holds(text, secret) {
  const unescaped = text.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16))).replace(/\\\//g, '/');
  return [text, unescaped].some(t => t.includes(secret) || t.includes(encodeURIComponent(secret)));
}

/**
 * Every answer `page` gets from the host from now on, with its body. `read()` waits for the bodies still being read, then gives the answers
 * and those of the API whose body couldn't be read (a check's blind spot).
 */
export function collectAnswers(page) {
  const answers = [];
  const reading = [];
  page.on('response', r => {
    if (!r.url().startsWith(base)) return;
    const answer = { url: r.url(), status: r.status(), text: null };
    answers.push(answer);
    if (r.status() === 204 || (r.status() >= 300 && r.status() < 400)) {
      answer.text = '';
      return;
    }
    reading.push(r.text().then(t => { answer.text = t; }, () => {}));
  });
  return {
    async read() {
      await Promise.all(reading);
      return { answers, unread: answers.filter(a => a.text === null && a.url.includes('/api/')) };
    },
  };
}
