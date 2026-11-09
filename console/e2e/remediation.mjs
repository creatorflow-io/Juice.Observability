// Phase 8, milestone 2 through the browser, on the local agent (deploy/local/agent.json lists /tmp/juice-demo-*.log and juice-demo.service):
// an operator's cleanup dry run needs no approval and deletes nothing; the real cleanup waits for an admin, then deletes only the old file.
// A restart aimed at the host's health is refused while the host is healthy (or its health is stale): there's nothing to fix. Asked without
// aiming, it waits for an admin and fails on the agent with "systemctl not found", since its container has no systemd.
//   node e2e/remediation.mjs
// Needs the stack with the platform and agent profiles up, and an agent build with the remediations (e2e/agent-custom.mjs installs one).
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const hostname = process.env.JUICE_HOST_NAME ?? 'local-docker-host';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
const local = fileURLToPath(new URL('../../deploy/local/', import.meta.url));
mkdirSync(out, { recursive: true });
const problems = [];
let n = 0;

/** A shell command inside the agent's container, as the agent's own user. */
function inAgent(command) {
  const run = spawnSync('docker', ['compose', 'exec', '-T', 'agent', 'sh', '-c', command], { cwd: local, encoding: 'utf8' });
  return { ok: run.status === 0, out: (run.stdout ?? '') + (run.stderr ?? '') };
}
const old = '/tmp/juice-demo-old.log';
const recent = '/tmp/juice-demo-recent.log';
const threeDaysAgo = new Date(Date.now() - 3 * 86400_000).toISOString().slice(0, 19).replace('T', ' ');
const setup = inAgent(`echo old > ${old} && touch -d "${threeDaysAgo}" ${old} && echo new > ${recent}`);
if (!setup.ok) { console.log(`setup failed: ${setup.out}`); process.exit(1); }

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
async function signIn(user) {
  const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
  page.on('pageerror', e => problems.push(`pageerror (${user}): ${e.message}`));
  page.on('console', m => { if (m.type() === 'error' && !/status of (400|409)/.test(m.text())) problems.push(`console (${user}): ${m.text()}`); });
  // A refused precondition is a 409 this test expects.
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400 && !(r.status() === 409 && r.url().includes('/action-requests'))) problems.push(`http ${r.status()} (${user}) ${r.url()}`); });
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
  await page.screenshot({ path: `${out}remediation-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const expect = (cond, what) => { if (!cond) throw new Error(what); };

const operator = await signIn('erin');
const admin = await signIn('alice');

async function openAgent(page) {
  await page.goto(`${base}/agents`);
  await page.click(`[data-testid=agent-${hostname}]`);
  await page.waitForSelector('[data-testid=command-action]');
}
async function choose(page, action) {
  await page.click('[data-testid=command-action]');
  await page.click(`[data-testid="action-${action}"]`);
}
async function pick(page, parameter, value) {
  await page.click(`[data-testid=command-param-${parameter}]`);
  await page.click(`mat-option:has-text("${value}")`);
}
/** The newest command of an action, once it has an outcome. */
async function outcome(page, action) {
  const row = page.locator('[data-testid=commands] tbody tr', { hasText: action }).first();
  await row.locator('[data-testid=command-state]').filter({ hasText: /Succeeded|Failed|Rejected/ }).waitFor({ timeout: 60000 });
  return row.textContent();
}
async function approveLatest(reasonText, comment) {
  await admin.goto(`${base}/approvals`);
  const item = admin.locator('[data-testid^=approval-]', { hasText: reasonText }).first();
  await item.waitFor({ timeout: 15000 });
  const id = (await item.getAttribute('data-testid')).replace('approval-', '');
  await admin.fill(`[data-testid=approval-comment-${id}]`, comment);
  await admin.click(`[data-testid=approve-${id}]`);
  await admin.locator(`[data-testid=approval-${id}]`).waitFor({ state: 'detached', timeout: 15000 });
}

await step(operator, 'dry-run-needs-no-approval', async () => {
  await openAgent(operator);
  await choose(operator, 'files.cleanup');
  await pick(operator, 'folder', '/tmp');
  await operator.click('[data-testid=command-param-dryRun]');
  await operator.fill('[data-testid=request-reason]', 'remediation e2e: what would go');
  await operator.click('[data-testid=command-issue]');
  await operator.waitForSelector('[data-testid=command-issued] >> text=Needs no approval');
  const text = await outcome(operator, 'files.cleanup');
  expect(/filesRemoved\s*1/.test(text) && /dryRun\s*true/.test(text), `the dry run's answer: ${text}`);
  expect(inAgent(`test -f ${old}`).ok, 'the dry run deleted the old file');
});
await step(operator, 'cleanup-waits-for-an-admin', async () => {
  await operator.click('[data-testid=command-param-dryRun]');
  await operator.fill('[data-testid=request-reason]', 'remediation e2e: clean the demo logs');
  await operator.click('[data-testid=command-issue]');
  await operator.waitForSelector('[data-testid=command-issued] >> text=runs once approved');
});
await step(admin, 'cleanup-approved', async () => approveLatest('clean the demo logs', 'old demo logs only'));
await step(operator, 'cleanup-ran', async () => {
  await openAgent(operator);
  const text = await outcome(operator, 'files.cleanup');
  expect(/filesRemoved\s*1/.test(text) && /dryRun\s*false/.test(text), `the cleanup's answer: ${text}`);
  expect(!inAgent(`test -f ${old}`).ok, 'the old file is still there');
  expect(inAgent(`test -f ${recent}`).ok, 'the recent file was deleted');
});
await step(operator, 'restart-refused-on-healthy-host', async () => {
  await choose(operator, 'service.restart');
  await pick(operator, 'service', 'juice-demo.service');
  await operator.fill('[data-testid=request-reason]', 'remediation e2e: aimed at the host');
  await operator.click('[data-testid=request-watch-host]');
  await operator.click('[data-testid=command-issue]');
  const error = operator.locator('[data-testid=command-error]');
  await error.waitFor();
  const text = await error.textContent();
  expect(/precondition_failed|stale_evidence/.test(text), `the refusal: ${text}`);
});
await step(operator, 'restart-requested', async () => {
  await operator.click('[data-testid=request-watch-host]');
  await operator.fill('[data-testid=request-reason]', 'remediation e2e: restart the demo service');
  await operator.click('[data-testid=command-issue]');
  await operator.waitForSelector('[data-testid=command-issued] >> text=runs once approved');
  const request = operator.locator('[data-testid=requests] tbody tr').first();
  expect((await request.textContent()).includes('High risk'), 'a restart is High risk');
});
await step(admin, 'restart-approved', async () => approveLatest('restart the demo service', 'second person: not the requester'));
await step(operator, 'restart-failed-without-systemd', async () => {
  await openAgent(operator);
  const text = await outcome(operator, 'service.restart');
  expect(text.includes('Failed') && text.includes('systemctl not found'), `the restart's answer: ${text}`);
});

inAgent(`rm -f ${recent}`);
await browser.close();
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
