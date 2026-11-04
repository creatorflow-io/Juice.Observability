// Console requirements §21 acceptance: sign in through Keycloak, then the investigation journey, with no Grafana.
// Needs deploy/local up with the platform profile and demo traffic from e2e/demo-traffic.mjs (a failing payments-2).
//   BASE=http://localhost:5180 BROWSER="C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" node e2e/journey.mjs
// Screenshots land in e2e/shots/. Exits 1 on any failed step, browser error or failed API call.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:4200';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 950 } });
const problems = [];
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
let n = 0;
const shot = async name => { await page.waitForTimeout(1200); await page.screenshot({ path: `${out}${String(++n).padStart(2, '0')}-${name}.png` }); console.log('shot', name); };
const step = async (name, fn) => { try { await fn(); await shot(name); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); await shot(`${name}-FAILED`); } };

await step('signin', async () => {
  await page.goto(`${base}/services?env=production&range=15m`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', 'erin');
  await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });
  await page.waitForSelector('text=demo-shop/payments', { timeout: 30000 });
});
await step('overview', async () => { await page.goto(`${base}/?env=production&range=15m`); await page.waitForSelector('text=Firing alerts'); });
await step('services', async () => { await page.goto(`${base}/services?env=production&range=15m`); await page.waitForSelector('text=demo-shop/payments'); });
await step('service-payments', async () => { await page.click('text=demo-shop/payments'); await page.waitForSelector('text=Instances'); });
await step('operations', async () => { await page.click('role=tab[name="Operations"]'); await page.waitForSelector('text=POST /charge'); });
await step('error-traces', async () => { await page.click('text=error traces >> nth=0'); await page.waitForSelector('text=Root operation'); });
await step('trace-waterfall', async () => { await page.click('table.jc-table tbody tr td a >> nth=0'); await page.waitForSelector('.jc-waterfall .jc-wf-row'); });
await step('span-selected', async () => { await page.click('.jc-wf-row.jc-wf-error >> text=POST /charge'); await page.waitForSelector('text=Logs of this span'); await page.waitForSelector('text=pool exhausted', { timeout: 15000 }); });
await step('downstream-db', async () => {
  await page.click('.jc-wf-row >> text=INSERT charges'); await page.waitForSelector('text=Uses payments-db/payments');
  await page.click('text=Uses payments-db/payments'); await page.waitForSelector('text=Traces that reach it');
});
await step('metrics-around', async () => {
  await page.goBack(); await page.waitForSelector('.jc-waterfall');
  await page.click('.jc-wf-row.jc-wf-error >> text=POST /charge'); await page.click('text=Metrics around');
  await page.waitForSelector('text=payments.charges', { timeout: 20000 });
});
await step('logs-around', async () => { await page.click('role=tab[name="Logs"]'); await page.waitForSelector('text=pool exhausted'); });
await step('topology', async () => { await page.goto(`${base}/topology?env=production&range=15m`); await page.waitForSelector('.jc-graph canvas, .jc-graph svg, .jc-graph div', { timeout: 15000 }); await page.waitForTimeout(1500); });
await step('topology-edge', async () => {
  // From a service's Dependencies tab to the edge's evidence, as an investigator would.
  await page.goto(`${base}/services?env=production&range=15m`); await page.click('text=demo-shop/checkout');
  await page.click('role=tab[name="Dependencies"]'); await page.click('a:text-is("evidence") >> nth=0');
  await page.waitForSelector('text=Representative traces', { timeout: 20000 });
});
await step('global-search', async () => {
  await page.goto(`${base}/services?env=production&range=15m`); await page.waitForSelector('text=demo-shop/payments');
  await page.fill('.jc-lookup input', 'checkout'); await page.press('.jc-lookup input', 'Enter');
  await page.waitForURL(/\/services\/[0-9a-f-]{36}/); await page.waitForSelector('text=Instances');
});
await step('alerts', async () => { await page.goto(`${base}/alerts?env=production`); await page.waitForSelector('text=service-error-rate'); });
await step('rules', async () => { await page.goto(`${base}/rules`); await page.waitForSelector('text=Service error rate'); });

console.log('\nPROBLEMS:\n' + (problems.length ? [...new Set(problems)].join('\n') : 'none'));
await browser.close();
process.exit(problems.length ? 1 : 0);
