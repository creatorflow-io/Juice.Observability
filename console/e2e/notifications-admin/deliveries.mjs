// M48 (phase 5; the Notifications Console's 4c deliveries journey, phase-4c-plan proof point 4, in Juice Console): erin, an operator, finds
// a failed alert notification under Alerts › Notifications, inspects it and retries it with the same id; a bulk replay from a filter
// replays exactly what the filter shows. She reads and operates, but configures nothing: the setup pages offer her no change, and the API
// refuses one. alice (admin) makes the webhook setup through the API; alerts come from failing telemetry.
//   BASE=… node e2e/notifications-admin/deliveries.mjs      (see lib.mjs for the host it needs)
import { launch, signIn, steps, expect, finish, base, tenantPage, adminPath, as, must, until, receiver, webhookSetup, webhookTeardown, fireAlert,
  rowIds, routeAlerts } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const refused = [];
const hook = await receiver();
hook.failing = true;
const keys = await webhookSetup(hook, run);
const failedOf = async () => ((await must('erin', 'GET', adminPath(`deliveries?status=failed&integration=${keys.integration}`))).items ?? []);
const firstAlert = await fireAlert(run);
const [failed] = await until(failedOf, ds => ds.length === 1, 60);
expect(failed, 'the first alert\'s delivery failed at the receiver');

const browser = await launch();
try {
  const erin = await signIn(browser, 'erin', problems, refused, tenantPage(`deliveries?status=failed&integration=${keys.integration}`));
  const step = steps('na-deliveries', problems)(erin);

  await step('found', async () => {
    await erin.getByTestId('delivery-row').first().waitFor({ timeout: 15000 });
    const rows = await rowIds(erin);
    expect(rows.length === 1 && rows[0] === failed.id, `the failed list for the integration shows the one delivery (${rows.join(', ')})`);
    await erin.getByTestId('delivery-row').first().getByRole('link').first().click();
    await erin.getByTestId('attempt').first().waitFor({ timeout: 10000 });
    expect(erin.url().endsWith(tenantPage(`deliveries/${failed.id}`)), `the delivery opens by its id under Alerts › Notifications (${erin.url()})`);
    expect((await erin.getByTestId('attempt').first().innerText()).includes('404'), 'its attempt shows the receiver\'s 404');
  });

  await step('retried', async () => {
    hook.failing = false;
    await erin.getByTestId('retry').click();
    await erin.getByTestId('retry-result').waitFor({ timeout: 10000 });
    const status = await until(async () => {
      await erin.getByRole('button', { name: 'Refresh' }).click();
      await erin.waitForTimeout(300);
      return erin.getByTestId('delivery-status').innerText().catch(() => '');
    }, s => s.includes('delivered'), 30);
    expect(status.includes('delivered'), `the retried delivery is delivered (${status})`);
    expect(await erin.getByTestId('attempt').count() === 2, 'it shows both attempts');
    const twice = hook.received.filter(r => r.id === failed.id);
    expect(twice.length === 2 && twice[1].body === twice[0].body, `the receiver got the same delivery twice, same id and body (${twice.length})`);
  });

  await step('replayed', async () => {
    // Two more alerts fail; the filter shows them; the replay takes exactly those.
    hook.failing = true;
    for (const more of [`${run}b`, `${run}c`]) {
      await routeAlerts(keys, more);
      await fireAlert(more);
    }
    const replayIds = (await until(failedOf, ds => ds.length === 2, 60)).map(d => d.id).sort();
    expect(replayIds.length === 2, `two more deliveries failed (${replayIds.length})`);
    await erin.goto(`${base}${tenantPage('deliveries')}`);
    await erin.getByTestId('filter-status').click();
    await erin.getByRole('option', { name: 'failed', exact: true }).click();
    await erin.getByTestId('filter-integration').fill(keys.integration);
    await erin.getByTestId('filter-apply').click();
    await until(async () => (await rowIds(erin)).length, n => n === 2, 15);
    const shown = (await rowIds(erin)).sort();
    expect(JSON.stringify(shown) === JSON.stringify(replayIds), `the filter shows the two failed deliveries (${shown.length})`);
    hook.failing = false;
    await erin.getByTestId('replay').click();
    await erin.getByTestId('replay-confirm').click();
    await erin.getByTestId('replay-result').waitFor({ timeout: 10000 });
    const result = await erin.getByTestId('replay-result').innerText();
    expect(/Replayed 2\b/.test(result), `the replay put back two (${result.trim()})`);
    const again = await until(async () => [...new Set(hook.received.filter(r => replayIds.includes(r.id) && r.status === 200).map(r => r.id))].sort(),
      ids => ids.length === 2, 30);
    expect(JSON.stringify(again) === JSON.stringify(replayIds), `the receiver got exactly those two again (${again.length})`);
  });

  await step('configures-nothing', async () => {
    // The setup pages show her what is there, and offer no change.
    for (const [page, row, controls] of [['routes', 'route-row', ['route-new', 'route-edit', 'route-delete']],
      [`integrations/${keys.integration}`, 'target-row', ['integration-delete', 'target-new', 'target-edit']],
      ['allowlist', 'allow-row', ['allow-new', 'allow-edit', 'allow-delete']]]) {
      await erin.goto(`${base}${tenantPage(page)}`);
      await erin.getByTestId(row).first().waitFor({ timeout: 10000 });
      for (const control of controls) expect(await erin.getByTestId(control).count() === 0, `${page}: no ${control} for an operator`);
    }
    // And the API refuses one: not_allowed.
    const change = await as('erin', 'PUT', adminPath(`routes/${keys.route}`), { match: {}, targets: [keys.target], enabled: false });
    expect(change.status === 403 && change.json?.code === 'not_allowed', `the API refuses her a change (${change.status} ${change.json?.code})`);
  });
} finally {
  await browser.close();
  await hook.close();
  await webhookTeardown(keys).catch(e => problems.push(`teardown: ${e.message}`));
}
expect(firstAlert, 'an alert fired');
if (refused.length) problems.push(`API calls refused: ${refused.join('; ')}`);
finish(problems);
