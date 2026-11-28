// Phase 12, milestone 4 through the browser: the rule editor and its 24-hour preview.
//   1. alice (admin) builds a MetricThreshold rule on system.memory.usage with subject Host, previews it (at least one subject), sees a field
//      error land under its own field, saves, changes the threshold (a preview goes stale), disables it, and the API shows each change.
//   2. erin (operator) looks at the rule and previews it, with no Save and no New. 3. victor (viewer) only looks: no Preview, and the API refuses his.
//   4. A rule from the rules file (host-cpu) shows the banner, has every field disabled, and the API refuses a change with 409, even for alice.
//   5. A preview for another tenant's route is refused (403). A burst of previews ends in 429, which the form says in one sentence.
//   6. At 1440 and 390 px the form and the preview have no horizontal overflow.
//   node e2e/rule-editor.mjs                 (BASE=http://localhost:5180 by default)
// There is no rule DELETE API: each run leaves one disabled `e2e-mem-*` rule behind.
// Needs the stack with the platform profile up, a build of the Console that has the editor, and system.memory.usage reported by the host Collector
// (the `host` profile) in the last 24 hours. Exits 0 with "skipped" when the stack (or deploy/local/.env) isn't there.
import { randomBytes } from 'node:crypto';
const skip = reason => { console.log(`skipped: ${reason}`); process.exit(0); };
let lib;
try { lib = await import('./lib.mjs'); } catch (e) { skip(`the local stack's deploy/local/.env can't be read (${e.code ?? e.message})`); }
const { launch, signIn, tokenFor, steps, expect, finish, base, keycloak } = lib;
const reachable = async url => { try { return (await fetch(url, { signal: AbortSignal.timeout(4000) })).status < 500; } catch { return false; } };
if (!await reachable(`${keycloak}/realms/juice`) || !await reachable(base)) skip(`the local stack or the Console at ${base} isn't up`);

const run = randomBytes(3).toString('hex');
const key = `e2e-mem-${run}`;
const METRIC = 'system.memory.usage';
const problems = [];
const expected = [];            // refusals the journeys cause on purpose (field errors, 403, 409, 429), so they aren't problems
// Only the refusals the journeys cause on purpose (a field error, a viewer's or other tenant's preview, a config rule's PUT, a burst) are expected.
const onError = who => e => { if (/http (400|403|409|429) \S*\/rules(\/preview)?$|Failed to load resource.*status of (400|403|409|429)/.test(e)) expected.push(e); else problems.push(e); };
const step = steps('rule-editor', problems);

// ─── The API beside the browser ─────────────────────────────────────────────

const tokens = {};
const api = async (user, method, path, body, headers = {}) => {
  tokens[user] ??= await tokenFor(user);
  const r = await fetch(`${base}/api/v1${path}`, { method, headers: { Authorization: `Bearer ${tokens[user]}`, 'Content-Type': 'application/json', ...headers },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch { /* not JSON */ }
  return { status: r.status, body: json, text, headers: r.headers };
};
const tenant = (await api('alice', 'GET', '/tenants')).body.items[0].id;
const rules = `/tenants/${tenant}/rules`;
/** The journey's rule as the API lists it (there is no read of one rule), or null. */
const getRule = async () => {
  for (let cursor = null, pages = 0; pages < 50; pages++) {
    const page = (await api('alice', 'GET', `${rules}?limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)).body;
    const found = (page?.items ?? []).find(r => r.key === key);
    if (found) return found;
    if (!(cursor = page?.nextCursor)) return null;
  }
  return null;
};

// ─── Helpers for the form ───────────────────────────────────────────────────

const form = page => page.getByTestId('rule-form');
const pick = async (page, name, option) => {
  await form(page).locator(`mat-select[name=${name}]`).click();
  await page.locator('mat-option', { hasText: new RegExp(`^\\s*${option}\\s*$`) }).first().click();
};
const fill = (page, name, value) => form(page).locator(`input[name=${name}]`).fill(String(value));
const fieldError = (page, field) => form(page).locator(`[data-testid=rule-field-error][data-field=${field}]`);
const noOverflow = async (page, what) => {
  const o = await page.evaluate(() => {
    const el = document.querySelector('[data-testid=rule-form]');
    const wide = [...el.querySelectorAll('.jc-preview, .jc-preview *')].filter(e => e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).overflowX === 'visible' && e.getBoundingClientRect().right > innerWidth + 1);
    return { page: document.documentElement.scrollWidth - innerWidth, form: el.scrollWidth - el.clientWidth, wide: wide.length };
  });
  expect(o.page <= 1 && o.form <= 1 && o.wide === 0, `${what}: horizontal overflow ${JSON.stringify(o)}`);
};
/** The memory metric must be in the tenant's catalog, or the rest says nothing. */
const catalog = (await api('alice', 'GET', `/tenants/${tenant}/telemetry/metrics`)).body;
if (!(catalog?.items ?? catalog ?? []).some?.(m => m.name === METRIC)) {
  problems.push(`${METRIC} is not in the catalog: start the host Collector (docker compose --profile host up -d) and wait a minute`);
  finish(problems);
}

const browser = await launch();

// ─── 1. alice, the admin ────────────────────────────────────────────────────

const alice = await signIn(browser, 'alice', problems, onError('alice'));
{
  const s = step(alice, 'alice');
  await s('rules-list', async () => {
    await alice.goto(`${base}/rules`);
    await alice.getByTestId('rules').waitFor({ timeout: 15000 });
    await alice.getByTestId('rule-new').waitFor();
    await alice.getByTestId('rule-view-host-cpu').waitFor();
  });
  await s('new-metric-rule', async () => {
    await alice.getByTestId('rule-new').click();
    await form(alice).waitFor();
    await fill(alice, 'key', key);
    await fill(alice, 'name', `E2E memory ${run}`);
    await pick(alice, 'template', 'MetricThreshold');
    await alice.getByTestId('rule-metric').fill(METRIC);
    await alice.getByTestId('rule-kind').waitFor({ timeout: 10000 });
    const options = (await alice.getByTestId('rule-aggregation').getAttribute('data-options')).split(',');
    expect(options.length > 0 && options[0] !== '', 'the aggregation list is empty for the catalog kind');
    await pick(alice, 'aggregation', options.includes('Max') ? 'Max' : options[0]);
    await pick(alice, 'subject', 'Host');
    await form(alice).locator('input[name=host]').waitFor({ timeout: 5000 });
    await form(alice).locator('input[name=service]').waitFor({ state: 'detached', timeout: 5000 });
    // The seeded or real host memory is in the gigabytes, so this fires at once.
    await fill(alice, 'threshold', 1000);
    await fill(alice, 'minimumSamples', 1);
  });
  await s('preview', async () => {
    await alice.getByTestId('rule-preview').click();
    await alice.getByTestId('rule-preview-result').waitFor({ timeout: 40000 });
    await alice.getByTestId('rule-preview-subject-0').waitFor();
    const text = (await alice.getByTestId('rule-preview-result').textContent()).replace(/\s+/g, ' ').trim();
    console.log(`  ${text.slice(0, 200)}`);
    expect(/would have fired \d+ times? on \d+ subjects?, sampled every/i.test(text), `the summary doesn't say what the rule would have done: ${text}`);
    expect(await alice.getByTestId('rule-preview-stale').count() === 0, 'a fresh preview is stale');
    await noOverflow(alice, 'preview at 1500 px');
  });
  await s('edit-marks-stale', async () => {
    await fill(alice, 'threshold', 2000);
    await alice.getByTestId('rule-preview-stale').waitFor({ timeout: 5000 });
    await fill(alice, 'threshold', 1000);
  });
  await s('field-error-under-its-field', async () => {
    await fill(alice, 'window', 1);
    await alice.getByTestId('rule-preview').click();
    const err = fieldError(alice, 'window');
    await err.first().waitFor({ timeout: 20000 });
    // It sits in the same field box as the Window input, not at the top or bottom of the form.
    const inField = await alice.evaluate(() => {
      const e = document.querySelector('[data-testid=rule-field-error][data-field=window]');
      return !!e?.closest('.jc-field')?.querySelector('input[name=window]');
    });
    expect(inField, 'the Window error is not in the Window field');
    expect(await fieldError(alice, 'threshold').count() === 0, 'a Window error also marked Threshold');
    // The same refusal for Save.
    await alice.getByTestId('rule-save').click();
    await err.first().waitFor();
    // A refused save saves nothing: the form stays open and no rule exists yet.
    await alice.waitForTimeout(1500);
    expect(await alice.getByTestId(`rule-edit-${key}`).count() === 0, 'the Window=1 save closed the form and listed the rule');
    expect(await getRule() === null, 'the Window=1 save created the rule');
  });
  await s('save', async () => {
    await fill(alice, 'window', 300);
    await alice.getByTestId('rule-save').click();
    await alice.getByTestId(`rule-edit-${key}`).waitFor({ timeout: 15000 });
    const r = await getRule();
    expect(r?.template === 'MetricThreshold' && r.enabled === true && r.definition?.threshold === 1000, `the saved rule: ${JSON.stringify(r)?.slice(0, 200)}`);
  });
  await s('change-threshold', async () => {
    await alice.getByTestId(`rule-edit-${key}`).click();
    await form(alice).waitFor();
    await fill(alice, 'threshold', 2000);
    await alice.getByTestId('rule-save').click();
    await alice.getByTestId(`rule-edit-${key}`).waitFor({ timeout: 15000 });
    await alice.waitForTimeout(500);
    const r = await getRule();
    expect(r?.definition?.threshold === 2000, `the threshold is not 2000: ${JSON.stringify(r)?.slice(0, 300)}`);
  });
  await s('disable', async () => {
    await alice.getByTestId(`rule-edit-${key}`).click();
    await form(alice).waitFor();
    await form(alice).locator('mat-checkbox[name=enabled] input').uncheck();
    await alice.getByTestId('rule-save').click();
    await alice.getByTestId(`rule-edit-${key}`).waitFor({ timeout: 15000 });
    await alice.waitForTimeout(500);
    const r = await getRule();
    expect(r?.enabled === false, `the rule is still enabled: ${JSON.stringify(r)?.slice(0, 200)}`);
    // The change feed says each of the three: create, change, disable.
    const changes = (await api('alice', 'GET', `/tenants/${tenant}/changes?limit=100`)).body?.items ?? [];
    const mine = changes.filter(c => c.attributes?.rule === key || c.summary?.includes(key));
    console.log(`  changes for ${key}: ${mine.map(c => c.summary).join(' | ')}`);
    for (const op of ['create', 'update', 'disable']) {
      const n = mine.filter(c => c.summary === `Rule ${key}: ${op}`).length;
      expect(n === 1, `expected one '${op}' change event for ${key}, saw ${n}`);
    }
    expect(mine.length === 3, `expected exactly three change events for ${key}, saw ${mine.length}`);
  });

  // ─── 4. A rule from the rules file ────────────────────────────────────────
  await s('config-rule-banner', async () => {
    await alice.goto(`${base}/rules`);
    await alice.getByTestId('rule-view-host-cpu').click();
    await form(alice).waitFor();
    const banner = (await alice.getByTestId('rule-readonly').textContent()).trim();
    expect(banner === 'From the rules file. Change it there.', `the banner reads: ${banner}`);
    expect(await alice.getByTestId('rule-save').count() === 0, 'a config rule has a Save');
    const live = await form(alice).locator('input:not([disabled]):not([type=hidden])').count();
    expect(live === 0, `${live} inputs are enabled on a config rule`);
    expect(await alice.getByTestId('rule-edit-host-cpu').count() === 0, 'a config rule has a Change button');
    const live2 = await form(alice).locator('mat-select:not(.mat-mdc-select-disabled), button:not([disabled])')
      .evaluateAll(els => els.map(e => (e.textContent ?? '').trim()).filter(t => !/^(Cancel|Close|Preview last 24 h)$/.test(t)));
    expect(live2.length === 0, `enabled controls on a config rule: ${live2.join(', ')}`);
  });
  await s('config-rule-put-409', async () => {
    const r = await api('alice', 'PUT', `${rules}/host-cpu`, { template: 'HostCpu', name: 'x', severity: 'Warning', threshold: 0.5 });
    expect(r.status === 409, `PUT of a config rule answered ${r.status}`);
  });

  // ─── 5. Another tenant, and a burst ───────────────────────────────────────
  await s('preview-other-tenant-403', async () => {
    const r = await api('alice', 'POST', `/tenants/${crypto.randomUUID()}/rules/preview`, { template: 'MetricThreshold' });
    expect(r.status === 403, `another tenant's route answered ${r.status}`);
  });
  // The preview is read at both widths before the burst below spends the actor's previews.
  for (const width of [1440, 390]) {
    await s(`width-${width}`, async () => {
      await alice.setViewportSize({ width, height: width > 600 ? 1000 : 900 });
      await alice.goto(`${base}/rules`);
      await alice.getByTestId(`rule-edit-${key}`).click();
      await form(alice).waitFor();
      await alice.getByTestId('rule-preview').click();
      await alice.getByTestId('rule-preview-subject-0').waitFor({ timeout: 40000 });
      await noOverflow(alice, `the form and preview at ${width} px`);
    });
  }
  await alice.setViewportSize({ width: 1500, height: 1100 });
  await s('preview-429-in-form', async () => {
    await alice.goto(`${base}/rules`);
    await alice.getByTestId(`rule-edit-${key}`).click();
    await form(alice).waitFor();
    // The limit is per actor and tenant: spend the bucket through the API, then ask once in the form.
    const draft = await getRule();
    const statuses = [];
    for (let i = 0; i < 14 && !statuses.includes(429); i++) statuses.push((await api('alice', 'POST', `${rules}/preview`, draft.definition)).status);
    expect(statuses.includes(429), `no 429 in 14 previews: ${statuses.join(',')}`);
    await alice.getByTestId('rule-preview').click();
    const sentence = alice.getByTestId('rule-error');
    await sentence.waitFor({ timeout: 15000 });
    const text = (await sentence.textContent()).trim();
    console.log(`  ${text}`);
    expect(/too many previews/i.test(text), `the sentence: ${text}`);
    expect(await alice.locator('[data-testid=rule-field-error]').count() === 0, 'a 429 marked a field');
  });

}

// ─── 2. erin, the operator ──────────────────────────────────────────────────

const erin = await signIn(browser, 'erin', problems, onError('erin'));
{
  const s = step(erin, 'erin');
  await s('view-and-preview', async () => {
    await erin.goto(`${base}/rules`);
    await erin.getByTestId('rules').waitFor({ timeout: 15000 });
    expect(await erin.getByTestId('rule-new').count() === 0, 'an operator has New rule');
    expect(await erin.getByTestId(`rule-edit-${key}`).count() === 0, 'an operator has Change');
    await erin.getByTestId(`rule-view-${key}`).click();
    await form(erin).waitFor();
    await erin.getByTestId('rule-role-note').waitFor();
    expect(await erin.getByTestId('rule-save').count() === 0, 'an operator has Save');
    await erin.getByTestId('rule-preview').click();
    await erin.getByTestId('rule-preview-result').waitFor({ timeout: 40000 });
  });
  await s('api-create-403', async () => {
    const r = await api('erin', 'POST', rules, { key: `${key}-x`, name: 'x', template: 'MetricThreshold' });
    expect(r.status === 403, `an operator's POST /rules answered ${r.status}`);
  });
}

// ─── 3. victor, the viewer ──────────────────────────────────────────────────

const victor = await signIn(browser, 'victor', problems, onError('victor'));
{
  const s = step(victor, 'victor');
  await s('view-no-preview', async () => {
    await victor.goto(`${base}/rules`);
    await victor.getByTestId('rules').waitFor({ timeout: 15000 });
    expect(await victor.getByTestId('rule-new').count() === 0, 'a viewer has New rule');
    await victor.getByTestId(`rule-view-${key}`).click();
    await form(victor).waitFor();
    await victor.getByTestId('rule-role-note').waitFor();
    expect(await victor.getByTestId('rule-preview').count() === 0, 'a viewer has Preview');
    expect(await victor.getByTestId('rule-save').count() === 0, 'a viewer has Save');
  });
  await s('api-preview-403', async () => {
    const r = await api('victor', 'POST', `${rules}/preview`, { template: 'MetricThreshold' });
    expect(r.status === 403, `a viewer's preview answered ${r.status}`);
  });
}

await browser.close();
console.log(`expected refusals seen: ${expected.length}`);
finish(problems);
