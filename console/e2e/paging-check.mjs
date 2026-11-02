// Checks paging and the trace page's scrolling against the running stack (needs telemetry in "local" over the last 24 h).
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const out = fileURLToPath(new URL('./shots/paging/', import.meta.url));
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await browser.newPage({ viewport: { width: 1500, height: 900 } });
const problems = [];
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
const check = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) problems.push(what); };
const more = async (label) => {
  const before = await page.locator('table.jc-table tbody tr').count();
  await page.click(`button:has-text("${label}")`);
  await page.waitForFunction(n => document.querySelectorAll('table.jc-table tbody tr').length > n, before, { timeout: 20000 });
  return [before, await page.locator('table.jc-table tbody tr').count()];
};

await page.goto(`${base}/traces?env=local&range=24h`);
await page.waitForURL(/realms\/juice/, { timeout: 30000 });
await page.fill('#username', 'erin'); await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD); await page.click('#kc-login');
await page.waitForSelector('table.jc-table tbody tr', { timeout: 30000 });

// Traces: older pages append, and no trace appears twice.
let [a, b] = await more('Older traces');
[, b] = await more('Older traces');
const ids = await page.$$eval('table.jc-table tbody tr td a', as => as.map(x => x.getAttribute('href')));
check(b > a && new Set(ids).size === ids.length, `traces: ${a} → ${b} rows after two "Older traces", ${ids.length - new Set(ids).size} repeated`);
check(await page.locator('.jc-more').innerText().then(t => t.includes(`${b} traces`)), 'traces: the count under the table matches');
await page.screenshot({ path: `${out}01-traces.png` });

// Logs: "Older logs" appends 200 more.
await page.goto(`${base}/logs?env=local&range=24h`);
await page.waitForSelector('table.jc-table tbody tr', { timeout: 30000 });
[a, b] = await more('Older logs');
check(a === 200 && b === 400, `logs: ${a} → ${b} rows after "Older logs"`);

// Alerts: resolved alerts page through 100 at a time when there are more.
await page.goto(`${base}/alerts?state=resolved`);
await page.waitForSelector('table.jc-table tbody tr', { timeout: 30000 });
const resolved = await page.locator('table.jc-table tbody tr').count();
if (await page.locator('button:has-text("Load more")').count()) {
  [a, b] = await more('Load more');
  check(a === 100 && b > 100, `alerts: ${a} → ${b} resolved alerts after "Load more"`);
} else {
  console.log(`info alerts: ${resolved} resolved alerts, one page`);
}

// Trace page: only the panes scroll, not the layout as well.
await page.goto(`${base}/traces?env=local&range=24h&status=error`);
await page.click('table.jc-table tbody tr td a >> nth=0');
await page.waitForSelector('.jc-waterfall .jc-wf-row');
await page.click('.jc-wf-row >> nth=3');
await page.waitForSelector('.jc-span-detail');
await page.waitForTimeout(1500);
const scroll = await page.evaluate(() => {
  const content = document.querySelector('mat-sidenav-content');
  const detail = document.querySelector('.jc-span-detail');
  return {
    layout: content ? content.scrollHeight - content.clientHeight : -1,
    window: document.documentElement.scrollHeight - document.documentElement.clientHeight,
    detail: detail.scrollHeight > detail.clientHeight, detailBottom: Math.round(detail.getBoundingClientRect().bottom), viewport: innerHeight,
  };
});
check(scroll.layout <= 0 && scroll.window <= 0, `trace page: layout overflow ${scroll.layout}px, window overflow ${scroll.window}px`);
check(scroll.detailBottom <= scroll.viewport, `trace page: span pane ends at ${scroll.detailBottom}px of ${scroll.viewport}px, scrolls itself: ${scroll.detail}`);
await page.screenshot({ path: `${out}02-trace.png` });

console.log('\nPROBLEMS:\n' + (problems.length ? [...new Set(problems)].join('\n') : 'none'));
await browser.close();
process.exit(problems.length ? 1 : 0);
