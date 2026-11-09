// Phase 8, milestone 1 through the browser: an operator asks for a Medium-risk action (an update to the version the agent already runs, which
// changes nothing on the host), it waits for an admin; the admin finds it on Approvals, can't reject it without saying why, and approves it;
// the agent runs it. A second request is rejected, with the reason.
//   node e2e/approvals.mjs
// Needs the stack with the platform and agent profiles up, the local agent online, and a package of the version it runs on offer
// (e2e/agent-custom.mjs leaves one). Screenshots land in e2e/shots/.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const hostname = process.env.JUICE_HOST_NAME ?? 'local-docker-host';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });
const problems = [];
let n = 0;

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
async function signIn(user) {
  const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
  page.on('pageerror', e => problems.push(`pageerror (${user}): ${e.message}`));
  page.on('console', m => { if (m.type() === 'error' && !/status of 400/.test(m.text())) problems.push(`console (${user}): ${m.text()}`); });
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} (${user}) ${r.url()}`); });
  await page.goto(`${base}/`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', user);
  await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });
  return page;
}
const step = async (page, name, fn) => {
  try { await fn(); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${out}approvals-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const expect = (cond, what) => { if (!cond) throw new Error(what); };

const operator = await signIn('erin');
const admin = await signIn('alice');
let version;

async function request(reason) {
  await operator.goto(`${base}/agents`);
  await operator.click(`[data-testid=agent-${hostname}]`);
  version = (await operator.locator('[data-testid=agent-version]').textContent()).trim();
  await operator.click('mat-select[name=action]');
  await operator.click('mat-option:has-text("Update the agent")');
  await operator.click('mat-select[name=version]');
  await operator.click(`mat-option:has-text("${version}")`);
  await operator.fill('[data-testid=request-reason]', reason);
  await operator.click('[data-testid=command-issue]');
  await operator.waitForSelector('[data-testid=command-issued] >> text=runs once approved');
}

await step(operator, 'request', async () => {
  await request('approvals e2e: reinstall the running version');
  await operator.locator('[data-testid=requests] tbody tr').first().locator('[data-testid=request-state] >> text=Waiting for approval').waitFor();
});
let id;
await step(admin, 'waiting', async () => {
  await admin.goto(`${base}/approvals`);
  const item = admin.locator('[data-testid^=approval-]', { hasText: 'reinstall the running version' }).first();
  await item.waitFor({ timeout: 15000 });
  id = (await item.getAttribute('data-testid')).replace('approval-', '');
  expect((await item.textContent()).includes(`version=${version}`), 'the parameters aren\'t shown');
});
await step(admin, 'reject-needs-a-reason', async () => {
  await admin.click(`[data-testid=reject-${id}]`);
  await admin.waitForSelector(`[data-testid=approval-error-${id}] >> text=Say why`);
});
await step(admin, 'approve', async () => {
  await admin.fill(`[data-testid=approval-comment-${id}]`, 'fine: same version');
  await admin.click(`[data-testid=approve-${id}]`);
  await admin.locator(`[data-testid=approval-${id}]`).waitFor({ state: 'detached', timeout: 15000 });
  await admin.locator('[data-testid=approvals-decided]', { hasText: 'Issued by alice' }).waitFor();
});
await step(operator, 'ran', async () => {
  await operator.reload();
  const command = operator.locator('[data-testid=commands] tbody tr', { hasText: 'agent.update' }).first();
  await command.locator('[data-testid=command-state]').filter({ hasText: /Succeeded|Failed|Rejected/ }).waitFor({ timeout: 120000 });
  const text = await command.textContent();
  expect(text.includes('Succeeded'), `the approved update: ${text}`);
  const request = operator.locator('[data-testid=requests] tbody tr').first();
  expect((await request.textContent()).includes('alice'), 'the request doesn\'t say who approved it');
});
await step(admin, 'reject', async () => {
  await request('approvals e2e: to be rejected');
  await admin.goto(`${base}/approvals`);
  const item = admin.locator('[data-testid^=approval-]', { hasText: 'to be rejected' }).first();
  await item.waitFor({ timeout: 15000 });
  const rejectId = (await item.getAttribute('data-testid')).replace('approval-', '');
  await admin.fill(`[data-testid=approval-comment-${rejectId}]`, 'not needed');
  await admin.click(`[data-testid=reject-${rejectId}]`);
  await admin.locator('[data-testid=approvals-decided]', { hasText: 'Rejected by alice' }).waitFor();
});

await browser.close();
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
