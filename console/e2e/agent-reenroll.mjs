// Bringing a revoked agent back, through the browser, on the local stack: an admin revokes the local agent, creates a token for its host on
// the revoked agent's page, and the token goes into the agent's token file (deploy/local/secrets/agent-enrollment-token, mounted into the
// container). The agent sets its old identity aside and enrolls again by itself; the revoked agent's page then links to the new one.
//   node e2e/agent-reenroll.mjs
// Needs the stack with the platform and agent profiles up and the local agent enrolled and online, running a build from 9 November or later
// (node e2e/agent-custom.mjs installs one). The old agent stays revoked: that's the point.
import { chromium } from 'playwright-core';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const local = new URL('../../deploy/local/', import.meta.url);
const env = Object.fromEntries(readFileSync(new URL('.env', local), 'utf8').split('\n').filter(l => l.includes('=') && !l.startsWith('#'))
  .map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const hostname = process.env.JUICE_HOST_NAME ?? 'local-docker-host';
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });
const problems = [];
let n = 0;

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
await page.goto(`${base}/`);
await page.waitForURL(/realms\/juice/, { timeout: 30000 });
await page.fill('#username', 'alice');
await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
await page.click('#kc-login');
await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });

const step = async (name, fn) => {
  try { await fn(); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${out}reenroll-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};

let oldUrl;
await step('revoke', async () => {
  await page.goto(`${base}/agents`);
  await page.click(`[data-testid=agent-${hostname}]`);
  await page.waitForSelector('[data-testid=agent-summary]');
  oldUrl = page.url();
  await page.fill('[data-testid=revoke-reason]', 'e2e: bring it back with a new token');
  await page.click('[data-testid=revoke]');
  await page.click('[data-testid=revoke-confirm]');
  await page.waitForSelector('[data-testid=agent-revoked]');
});
await step('token', async () => {
  await page.click('[data-testid=reenroll-create]');
  const token = (await page.locator('[data-testid=reenroll-token-value]').textContent({ timeout: 10000 })).trim();
  // As a person with access to the host would: the new token replaces the used one in the agent's token file.
  writeFileSync(new URL('secrets/agent-enrollment-token', local), token);
});
await step('enrolled-again', async () => {
  const started = Date.now();
  for (let i = 0; i < 120; i++) {
    await page.goto(oldUrl);
    await page.waitForSelector('[data-testid=agent-summary]');
    if (await page.locator('[data-testid=agent-successor]').count()) break;
    await page.waitForTimeout(1000);
  }
  await page.locator('[data-testid=agent-successor]').waitFor({ timeout: 1000 });
  console.log(`  enrolled again ${Math.round((Date.now() - started) / 1000)} s after the token was in place`);
  await page.click('[data-testid=agent-successor] a');
  await page.waitForSelector('[data-testid=agent-state] >> text=Online', { timeout: 60000 });
});

await browser.close();
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
