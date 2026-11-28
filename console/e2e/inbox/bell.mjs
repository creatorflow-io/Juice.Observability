// M54 (slice C): the Console's header bell against a host with the embedded inbox on (Notifications:Inbox:Enabled=true, and the Console's
// config.json "inbox": true; deploy/local has both). erin (operator) signs in, so she is recorded with her roles; an alert then fires and
// her bell counts 1; opening the item lands on /alerts/<id>. victor (viewer) isn't a recipient: his bell stays at zero.
//   BASE=… node e2e/inbox/bell.mjs        (CONSOLE_DIST=<ng build output>/browser to try a build first; see e2e/lib.mjs)
// Run on the local stack after slice B (the host side) has merged.
import { launch, signIn, steps, expect, finish, base } from '../lib.mjs';
import { fireAlert, must, platformPath, until } from '../notifications-admin/lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const browser = await launch();
const bell = page => page.locator('juice-notification-bell button');
const badge = async page => Number((await bell(page).locator('.mat-badge-content').first().textContent().catch(() => '0'))?.trim() || 0);

try {
  const step = steps('inbox-bell', problems);

  // erin: signs in before the alert fires (nobody who never signed in is reached), then sees the bell with no items.
  const erin = await signIn(browser, 'erin', problems);
  await step(erin, 'operator')('bell-in-the-header', async () => {
    await bell(erin).waitFor({ timeout: 15000 });
    expect(await badge(erin) === 0, 'erin starts with an empty inbox');
  });
  // victor (a viewer) has the bell too, and holds nothing.
  const victor = await signIn(browser, 'victor', problems);
  await bell(victor).waitFor({ timeout: 15000 });

  const alertId = await fireAlert(run);

  await step(erin, 'operator')('count-goes-to-1', async () => {
    await until(async () => badge(erin), n => n === 1, 60);
    expect(await badge(erin) === 1, 'erin\'s bell counts the alert');
  });

  await step(erin, 'operator')('opening-the-item-lands-on-the-alert', async () => {
    await bell(erin).click();
    const open = erin.locator('.inbox-item-open').first();
    await open.waitFor({ timeout: 10000 });
    await open.click();
    await erin.waitForURL(u => new URL(u).pathname === `/alerts/${alertId}`, { timeout: 15000 });
    expect(new URL(erin.url()).origin === new URL(base).origin, 'the item link stays on the Console');
  });

  await step(victor, 'viewer')('no-items', async () => {
    expect(await badge(victor) === 0, 'victor (a viewer) gets no item');
  });

  // Cleanup: the rule the journey made stops (rules can't be deleted, only disabled).
  await must('alice', 'PUT', `${await platformPath('rules')}/e2e-${run}`, { name: `E2E ${run}`, template: 'ErrorRate', severity: 'Critical',
    service: `e2e-m48/svc-${run}`, threshold: 0.05, forDuration: '00:00:00', enabled: false }, { allow: [404] });
} catch (e) {
  problems.push(e.message.split('\n')[0]);
} finally {
  await browser.close();
}
finish(problems);
