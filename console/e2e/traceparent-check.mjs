// Opens a trace by the W3C traceparent an older Juice.Audit record holds, and by the global search box.
import { chromium } from 'playwright-core';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
const problems = [];
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
const check = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) problems.push(what); };

await page.goto(`${base}/traces?env=local&range=24h`);
await page.waitForURL(/realms\/juice/, { timeout: 30000 });
await page.fill('#username', 'erin'); await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD); await page.click('#kc-login');
await page.waitForSelector('table.jc-table tbody tr td a', { timeout: 30000 });
const traceId = (await page.getAttribute('table.jc-table tbody tr td a >> nth=0', 'href')).split('/traces/')[1].split('?')[0];
const traceParent = `00-${traceId}-0123456789abcdef-01`;

await page.goto(`${base}/traces/${traceParent}?env=local`);
await page.waitForSelector('.jc-waterfall .jc-wf-row', { timeout: 30000 });
check((await page.locator('h1.jc-mono').innerText()) === traceId, `trace page by traceparent shows ${traceId}`);
check(await page.locator('a:has-text("Logs of this trace")').getAttribute('href').then(h => h.includes(`trace=${traceId}`)), 'its logs link uses the plain id');

console.log('\nPROBLEMS:\n' + (problems.length ? problems.join('\n') : 'none'));
await browser.close();
process.exit(problems.length ? 1 : 0);
