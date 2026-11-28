// M48 (phase 5; the Notifications Console's 4c configure journey, phase-4c-plan proof point 5, in Juice Console): delivery set up entirely
// under Alerts › Notifications works end to end. alice (admin) allows the receiver's private address, creates a signed webhook integration
// with a target, and a route from her rule's firing alerts to it, with a digest window the page checks; an alert of the rule then arrives,
// signed, at the receiver. On the way the API's field errors show on their fields, and a mute is made and cancelled; the setup is taken down
// again in the browser (an integration that sent is disabled, not deleted). The Alerts › Notifications menu leads in, and the old Webhooks address opens Escalation selection.
//   BASE=… node e2e/notifications-admin/configure.mjs      (see lib.mjs for the host it needs)
import { createHmac } from 'node:crypto';
import { launch, signIn, steps, expect, finish, base, pages, tenantPage, adminPath, must, until, receiver, fireAlert, hookSecret,
  hookSecretRef } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const refused = [];
const keys = { hooks: `e2e-hooks-${run}`, hook: `e2e-hook-${run}`, route: `e2e-cfg-${run}` };
const hook = await receiver();

const browser = await launch();
try {
  const alice = await signIn(browser, 'alice', problems, refused, '/notifications');
  const step = steps('na-configure', problems)(alice);
  const go = async page => { await alice.goto(`${base}${tenantPage(page)}`); };
  const pick = async (testId, option) => {
    await alice.getByTestId(testId).click();
    await alice.getByRole('option', { name: option, exact: true }).click();
  };

  await step('reached-from-the-escalation-page-and-the-menu', async () => {
    // /notifications (the old webhooks address) redirects to Escalation selection; the menu's group has the embedded pages.
    await alice.getByTestId('policy-new').waitFor({ timeout: 15000 });
    expect(new URL(alice.url()).pathname.endsWith('/escalation'), 'the old webhooks address redirects to /escalation');
    await alice.goto(`${base}${tenantPage('')}`);
    await alice.waitForURL(u => u.pathname.startsWith(`${pages}/tenants/`), { timeout: 15000 });
    // Its items, open or closed (a closed group's are in the page, hidden).
    const menu = await alice.locator('juice-expandable-menu', { hasText: 'Alert notifications' }).first().evaluate(e => e.textContent);
    for (const label of ['Deliveries', 'Routes', 'Integrations', 'Types', 'Settings']) expect(menu.includes(label), `the menu group lists ${label}`);
    expect(!menu.includes('Subscribers') && !menu.includes('Groups'), 'the menu group has no people pages (embedded)');
  });

  const allowRow = alice.locator(`[data-testid=allow-row]:is([data-destination="${hook.address}"], [data-destination="${hook.address}/32"])`);
  await step('allowlist', async () => {
    await go('allowlist');
    await alice.getByTestId('allowlist').waitFor({ timeout: 10000 });
    await alice.waitForTimeout(1000);
    if (await allowRow.count()) {
      await allowRow.getByTestId('allow-edit').click();
    } else {
      await alice.getByTestId('allow-new').click();
      await alice.getByTestId('allow-destination').fill(hook.address);
    }
    await alice.getByTestId('allow-ports').fill(String(hook.port));
    await alice.getByTestId('allow-save').click();
    const allowed = await until(() => allowRow.innerText().catch(() => ''), t => t.includes(String(hook.port)), 10);
    expect(allowed.includes(String(hook.port)), `the allowlist lists ${hook.address} on port ${hook.port}`);
  });

  await step('webhook-integration', async () => {
    await go('integrations');
    await alice.getByTestId('integration-new').click();
    await alice.getByTestId('integration-key').fill(keys.hooks);
    await pick('integration-channel', 'webhook');
    await alice.getByTestId('integration-secret-ref').fill(hookSecretRef);
    await alice.getByTestId('integration-save').click();
    await alice.waitForURL(u => u.pathname.endsWith(`/integrations/${keys.hooks}`), { timeout: 10000 });
    await alice.getByTestId('target-new').click();
    await alice.getByTestId('target-key').fill(keys.hook);
    await alice.getByTestId('target-address').fill(hook.url);
    await alice.getByTestId('target-save').click();
    const row = alice.locator(`[data-testid=target-row][data-key="${keys.hook}"]`);
    await row.waitFor({ timeout: 10000 });
    expect((await row.innerText()).includes(hook.url), `the target ${keys.hook} is added for the receiver`);
  });

  await step('route-with-a-digest-window', async () => {
    await go('routes');
    await alice.getByTestId('route-new').click();
    await alice.getByTestId('route-key').fill(keys.route);
    await alice.getByTestId('match-types').fill('observability.alert.firing');
    await alice.getByTestId('match-add-label').click();
    await alice.getByTestId('match-label-name').last().fill('rule');
    await alice.getByTestId('match-label-matcher').last().fill(`e2e-${run}`);
    await alice.getByTestId('route-targets').click();
    await alice.getByRole('option', { name: new RegExp(`^${keys.hook}\\b`) }).click();
    await alice.keyboard.press('Escape');
    // A window past an hour is refused on its field, before anything is sent.
    await alice.getByTestId('route-digest').fill('4000');
    await alice.getByTestId('route-save').click();
    const tooLong = await alice.locator('[data-testid=field-error][data-pointer="/digestSeconds"]').innerText({ timeout: 5000 }).catch(() => '');
    expect(tooLong.includes('0 to 3600'), `a digest window of 4000 s is refused on its field (${tooLong})`);
    await alice.getByTestId('route-digest').fill('0');
    await alice.getByTestId('route-save').click();
    const row = alice.locator(`[data-testid=route-row][data-key="${keys.route}"]`);
    await row.waitFor({ timeout: 10000 });
    const text = await row.innerText();
    expect(text.includes('observability.alert.firing') && text.includes(keys.hook) && text.includes('at once'),
      `the route lists its type, its target and sends at once (${text.replace(/\s+/g, ' ')})`);
  });

  await step('an-alert-arrives-signed', async () => {
    const alertId = await fireAlert(run);
    const got = await until(async () => [...hook.received], rs => rs.length > 0, 60);
    const signed = got[0] && got[0].signature === 'v1,' + createHmac('sha256', hookSecret).update(`${got[0].id}.${got[0].timestamp}.${got[0].body}`).digest('base64');
    expect(got.length === 1 && signed, `the alert arrived once, signed with the integration's secret (${got.length}, ${signed ? 'valid' : 'invalid'} signature)`);
    // The alert page links its deliveries and escalation to the admin correlation page.
    await alice.goto(`${base}/alerts/${alertId}`);
    await alice.getByTestId('alert-deliveries-link').click();
    await alice.waitForURL(u => u.pathname.includes(`/tenants/`) && u.pathname.includes('/correlations/observability/alert'), { timeout: 15000 });
    expect(JSON.parse(got[0].body).type === 'alert.firing', 'it is the firing alert (the 1.x body: type alert.firing)');
  });

  await step('mute-made-and-cancelled', async () => {
    await go('mutes');
    await alice.getByTestId('mute-new').click();
    await alice.getByTestId('match-types').fill('observability.alert.firing');
    await alice.getByTestId('mute-reason').fill(`e2e ${run}`);
    await alice.getByTestId('mute-save').click();
    const row = alice.locator('[data-testid=mute-row]', { hasText: `e2e ${run}` });
    await row.first().waitFor({ timeout: 10000 });
    expect((await row.first().innerText()).includes('active'), 'the mute is listed as active');
    await row.first().getByTestId('mute-cancel').click();
    await row.first().getByTestId('mute-cancel-confirm').click();
    await until(() => row.count(), n => n === 0, 10);
    expect(await row.count() === 0, 'once cancelled it leaves the current mutes');
  });

  await step('taken-down', async () => {
    await go('routes');
    const routeRow = alice.locator(`[data-testid=route-row][data-key="${keys.route}"]`);
    await routeRow.getByTestId('route-delete').click();
    await routeRow.getByTestId('route-delete-confirm').click();
    await until(() => routeRow.count(), n => n === 0, 10);
    expect(await routeRow.count() === 0, 'the route is deleted');
    await go(`integrations/${keys.hooks}`);
    await alice.getByTestId('integration-delete').click();
    await alice.getByTestId('integration-delete-confirm').click();
    await alice.getByTestId('integration-action-error').waitFor({ timeout: 10000 });
    expect((await alice.getByTestId('integration-action-error').innerText()).includes('disable it instead'), 'an integration that sent isn\'t deleted: the page says why');
    await alice.locator('[data-testid=integration-enabled] input').uncheck();
    await alice.getByTestId('integration-save').click();
    const state = await until(() => alice.getByTestId('integration-state').innerText().catch(() => ''), s => s.includes('disabled'), 10);
    expect(state.includes('disabled'), 'the integration is disabled');
  });

  await step('the-api-agrees', async () => {
    const mine = (await must('alice', 'GET', adminPath('integrations'))).find(i => i.key === keys.hooks);
    expect(mine && !mine.enabled && mine.secretRef === hookSecretRef, `the API has the integration, disabled, with its secret reference (${JSON.stringify(mine)?.slice(0, 120)})`);
  });
} finally {
  await browser.close();
  await hook.close();
}
// Expected refusals: the digest window is checked in the page (nothing sent), and the delete of an integration that sent (409).
const unexpected = refused.filter(r => !/ http 409 .*\/integrations\/e2e-hooks-[^/]+$/.test(r)
  && !/console: Failed to load resource: the server responded with a status of 409/.test(r));
if (unexpected.length) problems.push(`API calls refused: ${unexpected.join('; ')}`);
finish(problems);
