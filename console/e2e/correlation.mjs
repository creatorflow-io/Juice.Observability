// Phase 9, milestone 3 through the browser: an alert's page lists the alerts suggested with it, each with its score and every piece of evidence
// with its weight, labelled as correlation. An operator dismisses one with a reason and accepts another, which merges the pair into an incident;
// the incident's page names the suggestion in its record and lists what is suggested with its alerts.
//   node e2e/correlation.mjs
// Needs the stack with the platform profile up and at least two suggested pairs among the firing alerts. Decisions are kept, so each run uses
// up two pairs: it says so and stops when too few are left.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });
const problems = [];
let n = 0;

async function token(user) {
  const res = await fetch('http://localhost:8080/realms/juice/protocol/openid-connect/token', {
    method: 'POST', body: new URLSearchParams({ grant_type: 'password', client_id: 'juice-platform-cli', username: user, password: env.KEYCLOAK_DEMO_PASSWORD }),
  });
  return (await res.json()).access_token;
}
const viewer = await token('victor');
async function api(path) {
  const res = await fetch(`${base}/api/v1${path}`, { headers: { Authorization: `Bearer ${viewer}` } });
  if (!res.ok) throw new Error(`${res.status} ${path}`);
  return res.json();
}
const tenant = (await api('/tenants')).items[0].id;
const firing = (await api(`/tenants/${tenant}/alerts?state=firing&limit=100`)).items;
const pairs = new Map();
for (const a of firing) {
  for (const s of (await api(`/tenants/${tenant}/alerts/${a.id}/suggestions`)).items) {
    const key = [s.alert.id, s.other.id].sort().join(':');
    if (!pairs.has(key)) pairs.set(key, s);
  }
}
console.log(`${firing.length} firing alerts, ${pairs.size} suggested pairs`);
for (const s of pairs.values()) console.log(`  ${s.score.toFixed(2)} ${s.alert.rule} ${s.alert.subjectKey} + ${s.other.rule} ${s.other.subjectKey}: ${s.evidence.map(e => e.code).join(', ')}`);
if (pairs.size < 2) {
  console.log('Too few suggested pairs left to dismiss one and accept another: needs fresh co-firing alerts.');
  process.exit(1);
}
// Dismiss the weakest, accept the strongest one neither of whose alerts is in an incident yet, so accepting opens one.
const ordered = [...pairs.values()].sort((a, b) => a.score - b.score);
const toAccept = [...ordered].reverse().find(s => !s.incidentId) ?? ordered[ordered.length - 1];
const toDismiss = ordered.find(s => s !== toAccept);

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
await page.goto(`${base}/`);
await page.waitForURL(/realms\/juice/, { timeout: 30000 });
await page.fill('#username', 'erin');
await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
await page.click('#kc-login');
await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });

const step = async (name, fn, section) => {
  try { await fn(); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
  await page.waitForTimeout(500);
  // The page scrolls inside the layout, so a full-page shot stops at the viewport: bring the step's section into it.
  if (section) await page.locator(section).first().scrollIntoViewIfNeeded().catch(() => {});
  await page.screenshot({ path: `${out}correlation-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const expect = (cond, what) => { if (!cond) throw new Error(what); };
const row = s => page.locator('[data-testid=alert-suggestions] [data-testid=suggestion]', { has: page.locator(`a[href*="${s.other.id}"]`) });

await step('alert', async () => {
  await page.goto(`${base}/alerts/${toDismiss.alert.id}`);
  await row(toDismiss).waitFor({ timeout: 15000 });
  const note = await page.locator('[data-testid=alert-suggestions] [data-testid=suggestions-note]').textContent();
  expect(note.includes('not a proven cause') && note.includes('Nothing is merged until someone accepts'), `note: ${note}`);
  const text = (await row(toDismiss).textContent()).replace(/\s+/g, ' ');
  expect(text.includes(toDismiss.score.toFixed(2)), `the score ${toDismiss.score.toFixed(2)} should show: ${text}`);
  for (const e of toDismiss.evidence) expect(text.includes(`(+${e.weight.toFixed(2)})`), `evidence ${e.code} with its weight: ${text}`);
  console.log(`  ${text.trim()}`);
}, '[data-testid=alert-suggestions]');

await step('dismiss', async () => {
  await row(toDismiss).locator('[data-testid=suggestion-dismiss]').click();
  const confirm = row(toDismiss).locator('[data-testid=suggestion-dismiss-confirm]');
  expect(await confirm.isDisabled(), 'dismissing without a reason should not be possible');
  await row(toDismiss).locator('[data-testid=suggestion-reason]').fill('e2e: unrelated, checked by hand');
  await confirm.click();
  await row(toDismiss).waitFor({ state: 'detached', timeout: 15000 });
  const left = (await api(`/tenants/${tenant}/alerts/${toDismiss.alert.id}/suggestions`)).items;
  expect(!left.some(s => s.other.id === toDismiss.other.id), 'a dismissed pair is not suggested again');
}, '[data-testid=alert-suggestions]');

let incident;
await step('accept', async () => {
  await page.goto(`${base}/alerts/${toAccept.alert.id}`);
  await row(toAccept).waitFor({ timeout: 15000 });
  const label = (await row(toAccept).locator('[data-testid=suggestion-accept]').textContent()).trim();
  expect(label === (toAccept.incidentId ? 'Add to its incident' : 'Merge into a new incident'), `accept reads: ${label}`);
  await row(toAccept).locator('[data-testid=suggestion-accept]').click();
  const merged = page.locator('[data-testid=alert-suggestion-merged] a');
  await merged.waitFor({ timeout: 15000 });
  incident = (await merged.getAttribute('href')).split('/').pop().split('?')[0];
  await row(toAccept).waitFor({ state: 'detached', timeout: 15000 });
}, '[data-testid=alert-suggestions]');

await step('incident', async () => {
  await openIncident(incident);
  const alerts = await page.locator('[data-testid=incident-alerts]').textContent();
  for (const a of [toAccept.alert, toAccept.other]) expect(alerts.includes(a.subjectKey), `the incident should hold ${a.subjectKey}`);
  const events = await page.locator('[data-testid=incident-events]').textContent();
  expect(events.includes('From a correlation suggestion, score'), `the record should name the suggestion: ${events.replace(/\s+/g, ' ')}`);
  await page.locator('[data-testid=incident-suggestions] [data-testid=suggestions-note]').waitFor();
  console.log(`  incident ${incident}: ${await page.locator('[data-testid=incident-suggestions] [data-testid=suggestion]').count()} more suggested`);
}, '[data-testid=incident-events]');

async function openIncident(id) {
  await page.goto(`${base}/incidents/${id}`);
  await page.locator('[data-testid=incident-alerts]').waitFor({ timeout: 15000 });
}

await browser.close();
console.log(problems.length ? `PROBLEMS:\n${problems.join('\n')}` : 'OK: no problems');
process.exit(problems.length ? 1 : 0);
