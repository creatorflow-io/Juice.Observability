// The Agents pages through the browser: a viewer sees the agents, an operator runs each read-only diagnostic and is refused what's an admin's, the
// host's allowlist refuses what it doesn't permit, and an admin creates an enrollment token and reaches the revoke confirmation.
//
// Needs deploy/local up with the platform and agent profiles and an enrolled agent (node e2e/agent.mjs enrolls one).
//   node e2e/agents-console.mjs             everything but revoking
//   REVOKE=1 node e2e/agents-console.mjs    ...and revoke the agent at the end (it then has to enroll again)
//   BASE=http://<PUBLIC_HOST>:5180 node e2e/agents-console.mjs    over plain HTTP at the machine's address, where browsers withhold
//                                                                  secure-context APIs such as crypto.randomUUID and the clipboard
// Screenshots land in e2e/shots/. Exits 1 on any failed step, browser error or unexpected failed API call.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const hostname = process.env.JUICE_HOST_NAME ?? 'local-docker-host';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const problems = [];
// Refusals this script asks for on purpose: the page has to show them, so they aren't problems.
const expected = [];
let n = 0;
async function signIn(user) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1100 } });
  const page = await context.newPage();
  page.on('console', m => { if (m.type() === 'error' && !/status of 403/.test(m.text())) problems.push(`console (${user}): ${m.text()}`); });
  page.on('pageerror', e => problems.push(`pageerror (${user}): ${e.message}`));
  page.on('response', r => {
    if (!r.url().includes('/api/') || r.status() < 400) return;
    const i = expected.findIndex(e => e.user === user && e.status === r.status() && r.url().includes(e.path));
    if (i >= 0) expected.splice(i, 1); else problems.push(`http ${r.status()} (${user}) ${r.url()}`);
  });
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
  await page.screenshot({ path: `${out}agents-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const select = async (page, name, option) => { await page.click(`mat-select[name=${name}]`); await page.click(`mat-option:has-text("${option}")`); };
/** Runs the action now in the form and waits for the newest command to leave Pending and Delivered; returns its row. */
async function ask(page, action, fill = async () => {}) {
  await select(page, 'action', action);
  await fill();
  const before = await page.locator('[data-testid=commands] tbody tr').first().getAttribute('data-testid');
  await page.click('[data-testid=command-issue]');
  await page.waitForFunction(b => {
    const row = document.querySelector('[data-testid=commands] tbody tr');
    return row && row.getAttribute('data-testid') !== b;
  }, before, { timeout: 15000 });
  const row = page.locator('[data-testid=commands] tbody tr').first();
  await row.locator('[data-testid=command-state]').filter({ hasNotText: /Pending|Delivered/ }).waitFor({ timeout: 60000 });
  return row;
}
const expect = (cond, what) => { if (!cond) throw new Error(what); };

const viewer = await signIn('victor');
const operator = await signIn('erin');
const admin = await signIn('alice');

await step(viewer, 'list', async () => {
  await viewer.goto(`${base}/agents`);
  await viewer.waitForSelector(`[data-testid=agent-${hostname}]`);
  const row = viewer.locator('[data-testid=agents] tbody tr', { has: viewer.locator(`[data-testid=agent-${hostname}]`) });
  expect((await row.textContent()).includes('Online'), `${hostname} isn't shown online`);
  expect(await viewer.locator('nav, mat-sidenav, juice-layout').locator('text=Agents').count() > 0, 'no Agents entry in the menu');
});

await step(operator, 'host-summary', async () => {
  await operator.goto(`${base}/agents`);
  await operator.click(`[data-testid=agent-${hostname}]`);
  await operator.waitForSelector('[data-testid=agent-summary]');
  const row = await ask(operator, 'Host summary');
  const text = await row.textContent();
  expect(text.includes('Succeeded') && /CPUs/.test(text) && /up \d/.test(text), `host summary: ${text}`);
});
await step(operator, 'disk-usage', async () => {
  const row = await ask(operator, 'Disk usage', () => operator.fill('[data-testid=command-param-pathPrefix]', '/hostfs'));
  const text = await row.textContent();
  expect(text.includes('Succeeded') && /free of/.test(text) && text.includes('under /hostfs'), `disk usage: ${text}`);
  // A whole host has dozens of mounts: the fullest 8 show, the rest on request.
  const rows = () => row.locator('[data-testid=disk-result] tr').count();
  const toggle = row.locator('[data-testid^=disk-toggle-]');
  if (await toggle.count()) {
    expect(await rows() === 8, `${await rows()} filesystems shown before "Show all"`);
    await toggle.click();
    await operator.waitForFunction(() => document.querySelectorAll('[data-testid=commands] tbody tr:first-child [data-testid=disk-result] tr').length > 8);
    console.log(`  ${await rows()} filesystems in all`);
  }
});
await step(operator, 'tcp-reachable', async () => {
  const row = await ask(operator, 'TCP reachability', async () => { await operator.fill('[data-testid=command-param-host]', 'postgres'); await operator.fill('[data-testid=command-param-port]', '5432'); });
  const text = await row.textContent();
  expect(text.includes('Succeeded') && /Reachable at .* in \d+ ms/.test(text), `tcp: ${text}`);
});
await step(operator, 'refused-by-the-host', async () => {
  const row = await ask(operator, 'TCP reachability', async () => { await operator.fill('[data-testid=command-param-host]', 'example.com'); await operator.fill('[data-testid=command-param-port]', '443'); });
  const text = await row.textContent();
  expect(text.includes('Rejected') && text.includes('outside local bounds'), `refusal: ${text}`);
});
// The menu is what the agent reported: with the sample build installed (e2e/agent-custom.mjs), its custom action is in it, its path limited to
// the files the host lists. An agent without it says it doesn't report, or simply doesn't offer it.
await step(operator, 'custom-action', async () => {
  await operator.click('mat-select[name=action]');
  const offered = await operator.locator('[data-testid="action-sample.file_info"]').count();
  await operator.keyboard.press('Escape');
  if (!offered) {
    console.log(`  no custom action reported${await operator.locator('[data-testid=actions-not-reported]').count() ? ' (an agent from before reports)' : ''}`);
    return;
  }
  const row = await ask(operator, 'sample.file_info', async () => {
    await operator.click('[data-testid=command-param-path]');
    await operator.click('mat-option:has-text("/hostfs/etc/os-release")');
  });
  const text = await row.textContent();
  expect(text.includes('Succeeded') && /exists\s*true/.test(text) && /sizeBytes/.test(text), `custom: ${text}`);
  expect((await operator.locator('[data-testid=command-limits]').textContent()).includes('path: one of'), 'the host\'s limits aren\'t shown');
});
await step(operator, 'update-needs-admin', async () => {
  expected.push({ user: 'erin', status: 403, path: '/commands' });
  await select(operator, 'action', 'Update the agent');
  await operator.click('[data-testid=command-issue]');
  await operator.waitForSelector('[data-testid=command-error] >> text=needs the admin role');
});
await step(operator, 'token-needs-admin', async () => {
  expected.push({ user: 'erin', status: 403, path: '/enrollment-tokens' });
  await operator.goto(`${base}/agents`);
  await operator.click('[data-testid=enroll-create]');
  await operator.waitForSelector('[data-testid=enroll-error] >> text=needs the admin role');
});

await step(admin, 'enrollment-token', async () => {
  await admin.goto(`${base}/agents`);
  await admin.fill('[data-testid=enroll-purpose]', 'agents-console e2e, never used');
  await admin.fill('input[name=minutes]', '5');
  await admin.click('[data-testid=enroll-create]');
  const token = await admin.locator('[data-testid=enroll-token-value]').textContent({ timeout: 10000 });
  expect(/^\S{30,}$/.test(token.trim()), `token: ${token}`);
  // Over plain HTTP at an address there's no clipboard API: the token is selected instead.
  await admin.click('[data-testid=enroll-copy]');
  await admin.waitForSelector('[data-testid=enroll-copy] >> text=/Copied|Selected/');
});
await step(admin, 'update-offers-packages', async () => {
  await admin.click(`[data-testid=agent-${hostname}]`);
  await admin.waitForSelector('[data-testid=agent-summary]');
  await select(admin, 'action', 'Update the agent');
  await admin.click('mat-select[name=version]');
  await admin.waitForTimeout(500);
  const options = await admin.locator('mat-option').count();
  const none = await admin.locator('text=No signed packages are offered').count();
  await admin.keyboard.press('Escape');
  expect(options > 0 || none > 0, 'neither packages nor the note that there are none');
  console.log(`  ${options} package(s) offered`);
});
await step(admin, 'revoke-asks-first', async () => {
  await admin.fill('[data-testid=revoke-reason]', 'agents-console e2e');
  await admin.click('[data-testid=revoke]');
  await admin.waitForSelector('[data-testid=revoke-confirm]');
  if (process.env.REVOKE === '1') {
    await admin.click('[data-testid=revoke-confirm]');
    await admin.waitForSelector('[data-testid=agent-revoked]');
  } else {
    await admin.click('button:has-text("Cancel")');
    await admin.locator('[data-testid=revoke-confirm]').waitFor({ state: 'detached', timeout: 5000 });
    await admin.locator('[data-testid=revoke]').waitFor();
  }
});

await browser.close();
for (const e of expected) problems.push(`expected a ${e.status} for ${e.user} on ${e.path}, which never came`);
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
