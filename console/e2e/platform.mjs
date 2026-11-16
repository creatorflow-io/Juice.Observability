// Phase 11 milestone 6 through the browser: an operator's Overview has the Platform panel (workers, ingestion, ClickHouse, disk) and the
// capacity view with its label; a viewer's Overview has neither, and asks for nothing it would be refused. Then the health worker's lease
// is held from psql (the API's lease session ended, the advisory lock taken by a session that isn't a worker's, so no takeover ends it):
// the worker turns red, unheld, within its threshold (90 s) and a sample; released, it is green again.
//   node e2e/platform.mjs
// Needs the stack with the platform profile up. Screenshots land in e2e/shots/.
import { spawn } from 'node:child_process';
import { launch, signIn, steps, expect, finish } from './lib.mjs';

/** WorkerLease.KeyOf: FNV-1a over "juice.observability.worker:" + the worker, as a signed 64-bit number. */
function leaseKey(worker) {
  let hash = 14695981039346656037n;
  for (const c of 'juice.observability.worker:' + worker) hash = ((hash ^ BigInt(c.charCodeAt(0))) * 1099511628211n) & 0xFFFFFFFFFFFFFFFFn;
  return BigInt.asIntN(64, hash).toString();
}
const postgres = process.env.POSTGRES_CONTAINER ?? 'juice-observability-postgres-1';

const problems = [];
const browser = await launch();
const step = steps('platform', problems);

const operator = await signIn(browser, 'erin', problems);
const op = step(operator, 'operator');
await op('panel', async () => {
  await operator.waitForSelector('#jc-platform-title', { timeout: 30000 });
  // The first sample may still be on its way: the panel fills within one (15 s) plus the API's cache.
  await operator.waitForSelector('.jc-platform h3:has-text("Capacity")', { timeout: 45000 });
  const text = await operator.locator('.jc-platform').innerText();
  for (const part of ['workers', 'ingestion', 'clickhouse', 'disk', 'capacity', 'discovery']) expect(text.toLowerCase().includes(part), `the panel shows ${part}`);
  expect(/Calibrated on|Not calibrated/.test(text), 'the capacity view says what it is calibrated on, or that it is not');
  expect(/spans \d+(\.\d)?×|spans –/.test(text), 'the load is a multiple of the reference');
});

const viewer = await signIn(browser, 'victor', problems);
const view = step(viewer, 'viewer');
await view('no panel', async () => {
  await viewer.waitForSelector('h1:has-text("Overview")', { timeout: 30000 });
  await viewer.waitForTimeout(2000);
  expect(await viewer.locator('#jc-platform-title').count() === 0, 'a viewer has no Platform panel');
});

// The health worker held from psql: red (unheld), then green once let go.
const worker = 'health';
/** Waits until the worker's row in the panel has a badge of this class. */
const rowIs = cls => operator.waitForFunction(([w, c]) => [...document.querySelectorAll('.jc-platform .jc-list-row')].some(r =>
  r.querySelector('strong')?.textContent?.trim() === w && r.querySelector('.jc-badge')?.classList.contains(c)), [worker, cls], { timeout: 150000, polling: 2000 });
let psql;
await op('lease held from psql', async () => {
  psql = spawn('docker', ['exec', '-i', postgres, 'psql', '-U', 'juice', '-d', 'juice_observability', '-v', 'ON_ERROR_STOP=1'], { stdio: ['pipe', 'inherit', 'inherit'] });
  psql.stdin.write("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name LIKE 'juice-worker:" + worker + ":%';\n");
  psql.stdin.write('SELECT pg_advisory_lock(' + leaseKey(worker) + ');\n');
  await rowIs('jc-health-unhealthy');
  const state = await operator.evaluate(w => [...document.querySelectorAll('.jc-platform .jc-list-row')]
    .find(r => r.querySelector('strong')?.textContent?.trim() === w)?.querySelector('.jc-badge')?.textContent?.trim(), worker);
  expect(state === 'unheld', `the worker reads unheld (read ${state})`);
});
await op('lease released', async () => {
  psql.stdin.end();
  await new Promise(resolve => psql.exitCode === null ? psql.on('exit', resolve) : resolve());
  await rowIs('jc-health-healthy');
});
if (psql && psql.exitCode === null) psql.kill();

await browser.close();
finish(problems);
