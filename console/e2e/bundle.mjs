// Phase 8, milestone 3 through the browser, on the local agent (deploy/local/agent.json lists /tmp/juice-demo-app.log and a syslog this
// container doesn't have): an operator asks for a diagnostic bundle, which runs without approval; its answer says what's in it, that a value
// was redacted and that syslog was left out; the operator downloads the zip, whose log excerpt has the password redacted. A viewer can't.
//   node e2e/bundle.mjs
// Needs the stack with the platform and agent profiles up, and an agent build with diagnostics.bundle (e2e/agent-custom.mjs installs one).
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { inflateRawSync } from 'node:zlib';
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

const write = spawnSync('docker', ['compose', 'exec', '-T', 'agent', 'sh', '-c',
  'printf "starting orders\\nconnecting Server=pg;Password=hunter2;Database=orders\\nlistening on 8080\\n" > /tmp/juice-demo-app.log'], { cwd: local, encoding: 'utf8' });
if (write.status !== 0) { console.log(`setup failed: ${write.stderr}`); process.exit(1); }

/** The entries of a zip, by name: enough of the format to read what the agent wrote (deflate or stored, no zip64). */
function unzip(buffer) {
  const files = {};
  let at = buffer.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  const count = buffer.readUInt16LE(at + 10);
  let entry = buffer.readUInt32LE(at + 16);
  for (let i = 0; i < count; i++) {
    const method = buffer.readUInt16LE(entry + 10), size = buffer.readUInt32LE(entry + 20), nameLength = buffer.readUInt16LE(entry + 28);
    const extra = buffer.readUInt16LE(entry + 30), comment = buffer.readUInt16LE(entry + 32), local = buffer.readUInt32LE(entry + 42);
    const name = buffer.toString('utf8', entry + 46, entry + 46 + nameLength);
    const data = local + 30 + buffer.readUInt16LE(local + 26) + buffer.readUInt16LE(local + 28);
    const raw = buffer.subarray(data, data + size);
    files[name] = (method === 8 ? inflateRawSync(raw) : raw).toString('utf8');
    entry += 46 + nameLength + extra + comment;
  }
  return files;
}

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
async function signIn(user) {
  const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 }, acceptDownloads: true })).newPage();
  page.on('pageerror', e => problems.push(`pageerror (${user}): ${e.message}`));
  page.on('console', m => { if (m.type() === 'error' && !/status of 403/.test(m.text())) problems.push(`console (${user}): ${m.text()}`); });
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400 && !(r.status() === 403 && user === 'victor')) problems.push(`http ${r.status()} (${user}) ${r.url()}`); });
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
  await page.screenshot({ path: `${out}bundle-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const expect = (cond, what) => { if (!cond) throw new Error(what); };
async function openAgent(page) {
  await page.goto(`${base}/agents`);
  await page.click(`[data-testid=agent-${hostname}]`);
  await page.waitForSelector('[data-testid=commands]');
}

const operator = await signIn('erin');
const viewer = await signIn('victor');
let commandId;

await step(operator, 'bundle-made', async () => {
  await openAgent(operator);
  await operator.click('[data-testid=command-action]');
  await operator.click('[data-testid="action-diagnostics.bundle"]');
  await operator.click('[data-testid=command-issue]');
  const row = operator.locator('[data-testid=commands] tbody tr', { hasText: 'diagnostics.bundle' }).first();
  await row.locator('[data-testid=command-state]').filter({ hasText: /Succeeded|Failed|Rejected/ }).waitFor({ timeout: 60000 });
  const text = await row.textContent();
  expect(text.includes('Succeeded') && /1 value redacted/.test(text), `the answer: ${text}`);
  expect(text.includes('syslog: missing'), `syslog isn't said to be missing: ${text}`);
  commandId = (await row.getAttribute('data-testid')).replace('command-', '');
});
await step(operator, 'bundle-downloaded', async () => {
  const [download] = await Promise.all([operator.waitForEvent('download'), operator.click(`[data-testid=bundle-download-${commandId}]`)]);
  expect(/^juice-bundle-.+\.zip$/.test(download.suggestedFilename()), `the file name: ${download.suggestedFilename()}`);
  const files = unzip(readFileSync(await download.path()));
  const log = Object.entries(files).find(([name]) => name.startsWith('logs/'))?.[1] ?? '';
  expect(log.includes('Password=[redacted]') && !log.includes('hunter2'), `the log excerpt: ${log}`);
  expect('host.json' in files && 'agent.json' in files && 'manifest.json' in files, `the entries: ${Object.keys(files)}`);
});
await step(viewer, 'viewer-cant-download', async () => {
  await openAgent(viewer);
  await viewer.click(`[data-testid=bundle-download-${commandId}]`);
  await viewer.locator(`[data-testid=bundle-error-${commandId}]`, { hasText: 'takes an operator or an admin' }).waitFor({ timeout: 10000 });
});

await browser.close();
console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
process.exit(problems.length ? 1 : 0);
