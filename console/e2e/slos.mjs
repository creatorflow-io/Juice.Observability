// Phase 9, milestone 1 through the browser: the SLOs page lists deploy/local/slos.json's SLOs under Technical, with the Business section empty
// and saying why; each shows its state, 30-day attainment, budget and burn. An SLO's page shows the budget in words, the burn windows, its days,
// what counts as a request, and its versions.
//   node e2e/slos.mjs
// Needs the stack with the platform profile up, deploy/local/slos.json loaded, and demo traffic in the last 30 days (e2e/demo-traffic.mjs).
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

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
await page.goto(`${base}/`);
await page.waitForURL(/realms\/juice/, { timeout: 30000 });
await page.fill('#username', 'victor');
await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
await page.click('#kc-login');
await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });

const step = async (name, fn) => {
  try { await fn(); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${out}slos-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const expect = (cond, what) => { if (!cond) throw new Error(what); };
const STATES = ['Met', 'At risk', 'Budget exhausted', 'Not enough data', 'Unknown'];

await step('list', async () => {
  await page.goto(`${base}/slos`);
  for (const key of ['checkout-availability', 'checkout-latency', 'payments-availability']) {
    const row = page.locator(`[data-testid=slos-technical] [data-testid=slo-${key}]`);
    await row.waitFor({ timeout: 15000 });
    const state = (await row.locator('[data-testid=slo-state]').textContent()).trim();
    expect(STATES.includes(state), `${key}'s state: ${state}`);
    console.log(`  ${key}: ${state} | ${(await row.textContent()).replace(/\s+/g, ' ').trim()}`);
  }
  expect((await page.locator('[data-testid=slos-business]').textContent()).includes('No business SLOs yet'), 'the business section should say none exist yet');
  for (const left of await page.locator('[data-testid=slo-budget-left]').allTextContents()) expect(!/-\d/.test(left), `a budget left reads negative: ${left}`);
});
await step('detail', async () => {
  await page.click('[data-testid=slo-payments-availability] a');
  await page.locator('[data-testid=slo-budget]').waitFor();
  for (const window of ['5m', '30m', '1h', '6h', '3d']) await page.locator(`[data-testid=slo-burn-${window}]`).waitFor();
  const eligibility = await page.locator('[data-testid=slo-eligibility]').textContent();
  expect(eligibility.includes('/health/*') && eligibility.includes('408 and 499'), `what counts: ${eligibility}`);
  // Exactly one version is in effect, whichever the file is on now.
  await page.locator('[data-testid^=slo-version-]', { hasText: 'Effective' }).first().waitFor();
  expect(await page.locator('[data-testid^=slo-version-]', { hasText: 'Effective' }).count() === 1, 'one version should be in effect');
  console.log(`  budget: ${(await page.locator('[data-testid=slo-budget]').textContent()).replace(/\s+/g, ' ').trim()}`);
  // Burn alerts: the section explains them, and lists any there are, each linking to its alert.
  const alerts = await page.locator('[data-testid=slo-alerts]').textContent();
  expect(alerts.includes('14.4×') && alerts.includes('holds the alert'), `burn alerts: ${alerts}`);
  console.log(`  alerts: ${alerts.replace(/\s+/g, ' ').trim().slice(0, 300)}`);
});
await step('burn-rules', async () => {
  // The platform's three burn rules are on the rules page, marked as its own, with no way to change them.
  await page.goto(`${base}/rules`);
  const table = page.locator('[data-testid=rules]');
  await table.locator('tr', { hasText: 'slo-fast-burn' }).waitFor();
  for (const key of ['slo-fast-burn', 'slo-slow-burn', 'slo-slow-burn-3d']) {
    const row = table.locator('tr', { hasText: key }).first();
    expect((await row.textContent()).includes("the platform's, for SLOs"), `${key} should be marked as the platform's`);
    expect(await page.locator(`[data-testid=rule-edit-${key}]`).count() === 0, `${key} should have no Change button`);
  }
});

await browser.close();
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
