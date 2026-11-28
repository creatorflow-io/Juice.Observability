// Integration core I1 through the browser: integration instances and their health, with the test-only plugin `sample.jobs`.
//   1. alice (admin) opens Integrations, creates an instance from the plugin's form (the secret as a reference, never a value), validates it
//      (the report shows), and enables it. It runs and reports Enabled.
//   2. The plugin is made to fail (the instance's `outcome` field is changed, which returns it to Configured; it is validated and enabled again):
//      the instance goes Degraded, then Failed after Modules:integrations:FailedAfter runs, and an `integration-health` alert shows in Alerts.
//   3. alice disables it: the alert resolves. The instance's plugin text is shown as text, and the secret's value is nowhere on the page.
//   4. victor (viewer) sees the list and the instance page read-only: no New instance, no Edit, no Enable; its API mutation is refused (403).
//   5. alice deletes the (Disabled) instance.
//   node e2e/integrations.mjs                 (BASE=http://localhost:5180 by default)
// Needs the stack up with the test module `sample` (plugin `sample.jobs`: config field `outcome` = Succeeded | Failed, secret `token`) compiled in
// and Modules:integrations:FailedAfter small enough to reach Failed within the wait below (3 by default) with a short interval (MinInterval 60 s
// is the floor; set Modules:integrations:PollInterval low for the stack). The secret's env var must exist on the API host, named by SECRET_REF.
// Exits 0 with "skipped" when the stack (or deploy/local/.env) isn't there, or has no integration plugin.
//   SECRET_REF=env:JUICE_INTEGRATION_SAMPLE_TOKEN PLUGIN_KEY=sample.jobs FAIL_WAIT_MS=420000 node e2e/integrations.mjs
import { randomBytes } from 'node:crypto';
const skip = reason => { console.log(`skipped: ${reason}`); process.exit(0); };
let lib;
try { lib = await import('./lib.mjs'); } catch (e) { skip(`the local stack's deploy/local/.env can't be read (${e.code ?? e.message})`); }
const { launch, signIn, tokenFor, steps, expect, finish, base, keycloak } = lib;
const reachable = async url => { try { return (await fetch(url, { signal: AbortSignal.timeout(4000) })).status < 500; } catch { return false; } };
if (!await reachable(`${keycloak}/realms/juice`) || !await reachable(base)) skip(`the local stack or the Console at ${base} isn't up`);

const PLUGIN_KEY = process.env.PLUGIN_KEY ?? 'sample.jobs';
const SECRET_REF = process.env.SECRET_REF ?? 'env:JUICE_INTEGRATION_SAMPLE_TOKEN';
const FAIL_WAIT_MS = Number(process.env.FAIL_WAIT_MS ?? 420000);
const run = randomBytes(3).toString('hex');
const name = `e2e-jobs-${run}`;
const problems = [];
const expected = [];     // refusals the journey causes on purpose (the viewer's mutation), so they aren't problems
const onError = e => { if (/http 403 \S*\/integrations\/instances/.test(e) || /Failed to load resource.*status of 403/.test(e)) expected.push(e); else problems.push(e); };
const step = steps('integrations', problems);

const tokens = {};
const api = async (user, method, path, body) => {
  tokens[user] ??= await tokenFor(user);
  const r = await fetch(`${base}/api/v1${path}`, { method, headers: { Authorization: `Bearer ${tokens[user]}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch { /* not JSON */ }
  return { status: r.status, body: json, text };
};
const tenant = (await api('alice', 'GET', '/tenants')).body.items[0].id;
const root = `/tenants/${tenant}/integrations`;
const plugins = (await api('alice', 'GET', `${root}/plugins`)).body;
if (!Array.isArray(plugins) || !plugins.some(p => p.key === PLUGIN_KEY)) skip(`the stack has no ${PLUGIN_KEY} integration plugin`);
const instance = async () => (await api('alice', 'GET', `${root}/instances?limit=200`)).body.items.find(i => i.name === name);
const waitFor = async (what, test, ms = 120000) => {
  const until = Date.now() + ms;
  for (;;) {
    const v = await test();
    if (v) return v;
    if (Date.now() > until) throw new Error(`timed out waiting for ${what}`);
    await new Promise(r => setTimeout(r, 3000));
  }
};
const firingAlerts = async () => ((await api('alice', 'GET', `/tenants/${tenant}/alerts?state=firing&limit=200`)).body?.items ?? []).filter(a => a.rule === 'integration-health');

const browser = await launch();
try {
  const alice = await signIn(browser, 'alice', problems, onError);
  const as = step(alice, 'alice');

  await as('create', async () => {
    await alice.goto(`${base}/integrations`);
    await alice.locator('[data-testid=integration-new]').waitFor({ timeout: 20000 });
    await alice.click('[data-testid=integration-new]');
    await alice.locator('[data-testid=integration-form]').waitFor();
    await alice.fill('[data-testid=if-name]', name);
    await alice.fill('[data-testid="if-config.outcome"]', 'Succeeded').catch(async () => alice.selectOption('[data-testid="if-config.outcome"]', 'Succeeded'));
    const secret = alice.locator('[data-testid^="if-secret."]').first();
    expect(await secret.getAttribute('type') === 'text', 'the secret is asked for as a reference in a text input');
    await secret.fill(SECRET_REF);
    expect(await alice.locator('input[type=password]').count() === 0, 'no password input on the form');
    await alice.click('[data-testid=integration-save]');
    await alice.waitForURL(/integrations\/[0-9a-f-]{36}/, { timeout: 20000 });
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Configured' }).waitFor();
  });
  await as('validate', async () => {
    await alice.click('[data-testid=integration-validate]');
    await alice.locator('[data-testid=integration-report]').waitFor({ timeout: 40000 });
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Validated' }).waitFor({ timeout: 20000 });
    expect(await alice.locator('[data-testid=integration-check]').count() > 0, 'the validation report lists its checks');
  });
  await as('enable', async () => {
    await alice.click('[data-testid=integration-enable]');
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Enabled' }).waitFor({ timeout: 20000 });
    const i = await waitFor('the first successful run', async () => { const x = await instance(); return x?.lastSuccessAt ? x : null; });
    expect(i.state === 'Enabled', `state after a good run: ${i.state}`);
  });
  await as('secret-value-not-shown', async () => {
    const body = await alice.locator('body').innerText();
    expect(!/canary/i.test(body), 'a secret value must never reach the page');
    expect(body.includes(SECRET_REF), 'the reference (not the value) is shown to the admin');
  });

  await as('force-failure', async () => {
    await alice.click('[data-testid=integration-edit]');
    await alice.locator('[data-testid=integration-form]').waitFor();
    await alice.fill('[data-testid="if-config.outcome"]', 'Failed').catch(async () => alice.selectOption('[data-testid="if-config.outcome"]', 'Failed'));
    await alice.click('[data-testid=integration-save]');
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Configured' }).waitFor({ timeout: 20000 });
    await alice.click('[data-testid=integration-validate]');
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Validated' }).waitFor({ timeout: 40000 });
    await alice.click('[data-testid=integration-enable]');
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Enabled' }).waitFor({ timeout: 20000 });
    const failed = await waitFor('the instance to reach Failed', async () => { const x = await instance(); return x?.state === 'Failed' ? x : null; }, FAIL_WAIT_MS);
    expect(failed.health.consecutiveFailures >= 1, 'failures are counted');
    await alice.reload();
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Failed' }).waitFor({ timeout: 20000 });
    await alice.locator('[data-testid=integration-failure]').waitFor();
  });
  await as('alert-shows', async () => {
    const found = await waitFor('the integration-health alert', async () => (await firingAlerts()).length > 0);
    expect(found, 'an integration-health alert fires');
    await alice.goto(`${base}/alerts`);
    await alice.getByText('integration-health').first().waitFor({ timeout: 20000 });
  });
  await as('disable-resolves-alert', async () => {
    const i = await instance();
    await alice.goto(`${base}/integrations/${i.id}`);
    await alice.click('[data-testid=integration-disable]');
    await alice.locator('[data-testid=integration-state]').filter({ hasText: 'Disabled' }).waitFor({ timeout: 20000 });
    await waitFor('the alert to resolve', async () => (await firingAlerts()).length === 0, 90000);
  });

  const victor = await signIn(browser, 'victor', problems, onError);
  const vs = step(victor, 'victor');
  await vs('read-only', async () => {
    await victor.goto(`${base}/integrations`);
    await victor.locator('[data-testid=integrations]').waitFor({ timeout: 20000 });
    expect(await victor.locator('[data-testid=integration-new]').count() === 0, 'a viewer has no New instance');
    await victor.locator(`[data-testid=integration-link]`, { hasText: name }).click();
    await victor.locator('[data-testid=integration-health]').waitFor({ timeout: 20000 });
    for (const id of ['integration-edit', 'integration-validate', 'integration-enable', 'integration-disable', 'integration-delete'])
      expect(await victor.locator(`[data-testid=${id}]`).count() === 0, `a viewer has no ${id}`);
    const body = await victor.locator('body').innerText();
    expect(!body.includes(SECRET_REF), 'a viewer is not shown the secret references');
    const i = await instance();
    const refused = await api('victor', 'POST', `${root}/instances/${i.id}/enable`, {});
    expect(refused.status === 403, `a viewer's enable is refused: ${refused.status}`);
  });

  await as('delete', async () => {
    const i = await instance();
    await alice.goto(`${base}/integrations/${i.id}`);
    await alice.click('[data-testid=integration-delete]');
    await alice.click('[data-testid=integration-delete-confirm]');
    await alice.waitForURL(/\/integrations$/, { timeout: 20000 });
    expect(!(await instance()), 'the instance is gone');
  });
} finally {
  // Leave nothing behind when a step failed midway: disable, then delete.
  try {
    const left = await instance();
    if (left) {
      await api('alice', 'POST', `${root}/instances/${left.id}/disable`, {});
      const fresh = (await api('alice', 'GET', `${root}/instances/${left.id}`)).body;
      await fetch(`${base}/api/v1${root}/instances/${left.id}`, { method: 'DELETE', headers: { Authorization: `Bearer ${tokens['alice']}`, 'If-Match': `"${fresh.etag}"` } });
    }
  } catch { /* best effort */ }
  await browser.close();
}
if (expected.length) console.log(`expected refusals: ${expected.length}`);
finish(problems);
