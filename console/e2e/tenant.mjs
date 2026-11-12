// Phase 10, milestone 1: tenant context through the Console (docs/phase-10-console.md, plan P2). One deployment serves one tenant, but
// one ClickHouse can hold more than one tenant's telemetry: the test Collector writes local-test beside the deployment's tenant. This run
// sends a probe service through the test Collector (tenant local-test) and a control service through the main one (the deployment's
// tenant), both named after the run, with the same kind of telemetry: server spans, a log record, a counter. Then:
//   1. the API reads and pages where the probe would show if tenant scoping broke ("can leak") must show the control and never the
//      probe; the other reads the Console makes are smoke checks (scoped to the control's ID, or to things the probe doesn't produce:
//      alerts, changes, dependency edges), so they still must answer and never name the probe, but they don't prove scoping;
//   2. nora, signed in but with no platform role, gets no data on any page or deep link: the API refuses her.
//   node e2e/tenant.mjs
//   PROBE_PORT=4318 node e2e/tenant.mjs    self-test: the probe goes to this tenant, and every "can leak" check must report it
// Needs deploy/local up with the platform and test profiles (docker compose --profile platform --profile test up -d --wait).
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { base, env, expect, finish, launch, local, signIn, steps, tokenFor } from './lib.mjs';

const run = randomBytes(4).toString('hex');
const probe = `tenant-probe-${run}`;     // local-test: must never be seen
const control = `tenant-control-${run}`; // the deployment's tenant: must be seen
const problems = [];
const step = steps('tenant', problems);

// ─── Telemetry for both services ────────────────────────────────────────────

const token = readFileSync(`${local}secrets/otlp-tokens`, 'utf8').split(/\s/)[0];
const hex = bytes => randomBytes(bytes).toString('hex');
function send(port, service, signal, body) {
  // The response body goes to stdout and is dropped here (/dev/null doesn't exist for a Windows curl); errors never carry the command
  // line, which holds the OTLP token.
  let status;
  try {
    status = execFileSync('curl', ['-s', '-m', '30', '-w', '\n%{http_code}', '--ssl-no-revoke', '--cacert', `${local}secrets/tls/ca.crt`,
      '-H', `Authorization: Bearer ${token}`, '-H', 'Content-Type: application/json', '--data-binary', '@-', `https://localhost:${port}/v1/${signal}`],
      { input: JSON.stringify(body), stdio: ['pipe', 'pipe', 'ignore'] }).toString().trim().split('\n').pop();
  } catch (e) {
    throw new Error(`${service} ${signal}: curl exited ${e.status}`);
  }
  if (status !== '200') throw new Error(`${service} ${signal} → ${status}`);
}
function telemetry(port, service) {
  const resource = { attributes: [{ key: 'service.name', value: { stringValue: service } }, { key: 'service.instance.id', value: { stringValue: `${service}-1` } },
    { key: 'deployment.environment.name', value: { stringValue: 'local' } }] };
  const now = BigInt(Date.now()) * 1000000n;
  const spans = Array.from({ length: 20 }, (_, i) => ({ traceId: hex(16), spanId: hex(8), name: `GET /tenant/${run}`, kind: 2,
    startTimeUnixNano: String(now - BigInt(i) * 1000000n), endTimeUnixNano: String(now - BigInt(i) * 1000000n + 5000000n), status: { code: 1 },
    attributes: [{ key: 'http.route', value: { stringValue: `/tenant/${run}` } }, { key: 'http.response.status_code', value: { intValue: 200 } }] }));
  send(port, service, 'traces', { resourceSpans: [{ resource, scopeSpans: [{ scope: { name: 'tenant-check' }, spans }] }] });
  send(port, service, 'logs', { resourceLogs: [{ resource, scopeLogs: [{ scope: { name: 'tenant-check' }, logRecords: [{ timeUnixNano: String(now),
    severityNumber: 9, severityText: 'Information', body: { stringValue: `tenant check ${run} from ${service}` } }] }] }] });
  // The metric is named after its service, so the control's proves the read works and the probe's would be a leak (the read
  // "metrics, every service" below).
  send(port, service, 'metrics', { resourceMetrics: [{ resource, scopeMetrics: [{ scope: { name: 'tenant-check' }, metrics: [{ name: `tenant.check.${service}`,
    sum: { aggregationTemporality: 2, isMonotonic: true, dataPoints: [{ asInt: '1', startTimeUnixNano: String(now), timeUnixNano: String(now) }] } }] }] }] });
}
// PROBE_PORT=4318 sends the probe to the deployment's own tenant instead: a self-test of this check, which must then fail.
const probePort = Number(process.env.PROBE_PORT ?? 14318);
const sendBoth = () => { telemetry(probePort, probe); telemetry(4318, control); };

// ─── API ────────────────────────────────────────────────────────────────────

// API calls as a user. Tokens live 5 minutes and a slow run (discovery, then health) can outlast one, so a 401 gets a new token
// and one retry.
const apiAs = user => {
  let bearer = null;
  const get = async path => fetch(`${base}/api/v1${path}`, { headers: { authorization: `Bearer ${bearer ??= await tokenFor(user)}` } });
  return async path => {
    let r = await get(path);
    if (r.status === 401) { bearer = null; r = await get(path); }
    const text = await r.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* a proxy's HTML error page, say: the status says what happened */ }
    return { status: r.status, text, json };
  };
};
// Every page of a cursor-paged list, as the Console reads it (api.service.ts all(): 200 a page, 25 pages at most): the items, the
// joined text (to look for names anywhere in the answers) and the last status.
async function all(api, path) {
  const items = [];
  let text = '', cursor = null, status = 200;
  for (let i = 0; i < 25; i++) {
    const r = await api(`${path}${path.includes('?') ? '&' : '?'}limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
    status = r.status; text += r.text;
    if (r.status !== 200 || !r.json) break;
    items.push(...(r.json.items ?? []));
    if (!(cursor = r.json.nextCursor)) break;
  }
  return { status, text, items };
}

const victor = apiAs('victor');
const tenants = await victor('/tenants');
const tenant = tenants.json?.items?.[0]?.id;
if (!tenant) { problems.push(`victor has no tenant: ${tenants.status} ${tenants.text.slice(0, 200)}`); finish(problems); }
console.log(`run ${run}: probe ${probe} → ${probePort === 4318 ? 'THIS tenant (self-test)' : 'local-test'}, control ${control} → ${env.JUICE_PLATFORM_TENANT_ID}`);

// Discovery reads every 30 s; send until the control is a service (150 s at most). The probe is sent alongside, so it has had the same
// chances to be (wrongly) discovered. Without the control nothing below can tell anything, so the check stops there.
const deadline = Date.now() + 150000;
let controlId = null;
do {
  sendBoth();
  await new Promise(r => setTimeout(r, 10000));
  const services = await all(victor, `/tenants/${tenant}/resources?type=service`);
  controlId = services.items.find(s => s.displayName === control || s.key?.includes(control))?.id ?? null;
} while (!controlId && Date.now() < deadline);
if (!controlId) { problems.push(`the control service ${control} was never discovered in 150 s: the check can't tell anything, so it stops`); finish(problems); }
sendBoth();
await new Promise(r => setTimeout(r, 5000));

// 1. The reads the Console makes, as victor (viewer). "leak": the probe would be in the answer if tenant scoping broke (the self-test
// shows each of these failing), and the control must be, so the read is known to work. "smoke": answers and never names the probe,
// but can't show it either way.
// The traces and metrics reads are narrowed to this run (its operation, read page by page; the last 15 minutes, where the run's metric
// names are among few), so other traffic can't push the probe out of an answer that still shows the control.
const operation = `GET /tenant/${run}`;
const since = new Date(Date.now() - 15 * 60000).toISOString();
const reads = [
  ['services', `/resources?type=service`, 'leak', true],
  ['health', `/health`, 'leak', true],
  ['traces, every service', `/telemetry/traces?operation=${encodeURIComponent(operation)}`, 'leak', true],
  ['metrics, every service', `/telemetry/metrics?from=${encodeURIComponent(since)}`, 'leak'],
  ['logs by text', `/telemetry/logs?q=${run}`, 'leak'],
  ['search', `/search?q=${run}`, 'leak'],
  ['service detail', `/resources/${controlId}`, 'smoke'],
  ['traces of the control', `/telemetry/traces?serviceId=${controlId}`, 'smoke'],
  ['metrics of the control', `/telemetry/metrics?serviceId=${controlId}`, 'smoke'],
  ['service stats', `/telemetry/service-stats?serviceId=${controlId}`, 'smoke'],
  ['topology', `/topology`, 'smoke'],
  ['dependencies', `/telemetry/dependencies`, 'smoke'],
  ['timeline', `/timeline`, 'smoke'],
  ['alerts', `/alerts?limit=200`, 'smoke'],
];
// Which reads show the control by name: every "leak" read, and the control's own reads.
const namesControl = new Set(['services', 'health', 'traces, every service', 'metrics, every service', 'logs by text', 'search', 'service detail',
  'traces of the control', 'metrics of the control']);
// A new service gets a health entry once the health worker has judged it, which can take a while after discovery: a read that should
// name the control is retried until it does (2 minutes at most), so a slow worker isn't mistaken for a read that proves nothing.
const read = async (path, paged) => (paged ? all(victor, `/tenants/${tenant}${path}`) : victor(`/tenants/${tenant}${path}`));
for (const [what, path, kind, paged] of reads) {
  let r = await read(path, paged);
  for (const until = Date.now() + 120000; namesControl.has(what) && r.status === 200 && !r.text.includes(control) && Date.now() < until;) {
    await new Promise(res => setTimeout(res, 10000));
    sendBoth();
    r = await read(path, paged);
  }
  const seen = r.text.includes(control), leaked = r.text.includes(probe);
  console.log(`  api ${kind} ${what}: ${r.status}${namesControl.has(what) ? `, control ${seen ? 'seen' : 'MISSING'}` : ''}${leaked ? ', PROBE LEAKED' : ''}`);
  if (r.status !== 200) problems.push(`api ${what}: ${r.status} ${r.text.slice(0, 200)}`);
  if (leaked) problems.push(`api ${what}: the other tenant's ${probe} is in the answer`);
  if (namesControl.has(what) && !seen) problems.push(`api ${what}: the control ${control} is missing, so this read proves nothing`);
  // A cut answer could have dropped the probe while keeping the control.
  if (kind === 'leak' && !paged && r.json?.truncated) problems.push(`api ${what}: the answer was cut, so it can't show the probe is absent`);
}

// ─── Console ────────────────────────────────────────────────────────────────

const browser = await launch();
const body = page => page.locator('body').innerText();
// A page shows the control (so its data has loaded) and never the probe.
async function shows(page, url) {
  await page.goto(`${base}${url}`);
  await page.getByText(control, { exact: false }).first().waitFor({ timeout: 20000 });
  expect(!(await body(page)).includes(probe), `${url} shows the other tenant's ${probe}`);
}

// victor's pages where the probe would show: the self-test fails each of them.
const victorPage = await signIn(browser, 'victor', problems);
const v = step(victorPage, 'victor');
await v('services', () => shows(victorPage, '/services'));
await v('traces-all', () => shows(victorPage, `/traces?op=${encodeURIComponent(operation)}`));
await v('logs', () => shows(victorPage, `/logs?q=${run}`));
await v('search', async () => {
  // The Console's global lookup (.jc-lookup, as journey.mjs uses it): it asks /search and opens the first hit. The control's name
  // opens a page about the control, so the lookup works; the probe's name must open nothing that shows the probe.
  const lookup = async q => {
    await victorPage.goto(`${base}/services`);
    await victorPage.fill('.jc-lookup input', q);
    await victorPage.press('.jc-lookup input', 'Enter');
  };
  await lookup(control);
  await victorPage.waitForURL(u => !u.pathname.endsWith('/services'), { timeout: 20000 });
  await victorPage.getByText(control).first().waitFor({ timeout: 20000 });
  // With no hit the lookup stays and says `Nothing found for "<query>"`, echoing what was typed: that line is the only place the
  // probe's name may appear. A hit opens a page instead.
  await lookup(probe);
  const nothing = `Nothing found for "${probe}".`;
  await Promise.race([
    victorPage.getByText(nothing).waitFor({ timeout: 20000 }),
    victorPage.waitForURL(u => !u.pathname.endsWith('/services'), { timeout: 20000 }),
  ]).catch(() => {});
  expect(new URL(victorPage.url()).pathname.endsWith('/services'), `the lookup of ${probe} opened ${victorPage.url()}`);
  expect(await victorPage.getByText(nothing).count() > 0, `the lookup of ${probe} didn't say it found nothing`);
  expect(!(await body(victorPage)).replace(nothing, '').includes(probe), `the lookup of ${probe} shows it somewhere`);
});
await v('service', () => shows(victorPage, `/services/${controlId}`));

// 2. nora: signed in, no role on this tenant. Every page and deep link: she stays in the Console (not sent back to sign-in), the page
// asks the API and is refused, and nothing of the tenant shows. A page that didn't load at all would fail the first two.
const refusals = [];
const noraPage = await signIn(browser, 'nora', problems, e => refusals.push(e));
const r = step(noraPage, 'nora');
const deepLinks = ['/', '/services', `/services/${controlId}`, `/traces?service=${controlId}`, `/logs?q=${run}`, `/metrics?service=${controlId}`, '/topology', '/resources',
  '/alerts', '/incidents', '/rules', '/notifications', '/silences', '/slos', '/agents', '/approvals', '/timeline', '/load-runs'];
for (const link of deepLinks) {
  await r(link === '/' ? 'overview' : link.slice(1).split(/[?/]/)[0], async () => {
    const before = refusals.filter(e => e.includes(' http 403 ')).length;
    await noraPage.goto(`${base}${link}`);
    await noraPage.waitForLoadState('networkidle');
    expect(noraPage.url().startsWith(base), `${link} left the Console for ${noraPage.url()}`);
    expect(refusals.filter(e => e.includes(' http 403 ')).length > before, `${link} made no refused API call, so it shows nothing either way`);
    const text = await body(noraPage);
    expect(!text.includes(control) && !text.includes(run), `${link} shows the tenant's data to nora`);
  });
}
const noraApi = apiAs('nora');
for (const [what, path] of reads) {
  const res = await noraApi(`/tenants/${tenant}${path}`);
  if (res.status !== 403) problems.push(`nora api ${what}: ${res.status}, expected 403`);
  if (res.text.includes(control)) problems.push(`nora api ${what}: the tenant's data is in the answer`);
}
const tenantsForNora = await noraApi('/tenants');
console.log(`  nora: /tenants → ${tenantsForNora.status}; ${refusals.filter(e => e.includes('http')).length} API calls refused in the browser`);
if (tenantsForNora.status === 200 && tenantsForNora.json?.items?.length) problems.push(`nora is given a tenant: ${tenantsForNora.text.slice(0, 200)}`);

await browser.close();
finish(problems);
