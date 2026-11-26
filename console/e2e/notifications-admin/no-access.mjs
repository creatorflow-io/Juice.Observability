// M48 (phase 5; the Notifications Console's 4c no-access journey, phase-4c-plan proof point 2, in Juice Console): victor, a viewer, sees
// nothing of notifications (p5-d6). Alerts › Notifications and each of its pages, by link or deep link, is the no-access page saying why, with
// no tenant shown; the API refuses every read those pages start from, which alice (admin) is answered. The walked pages are the ones an
// embedded host has (NOTIFICATIONS_ADMIN_PAGES.embedded, read from the Console's own access answer), detail pages included.
//   BASE=… node e2e/notifications-admin/no-access.mjs      (see lib.mjs for the host it needs)
import { launch, signIn, steps, expect, finish, base, pages, tenantPage, adminPath, as, must, receiver, webhookSetup, webhookTeardown } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const refused = [];
// Rows for the detail pages: an integration (and, with alerts from other journeys, any delivery, notification and correlation there are).
const hook = await receiver();
const keys = await webhookSetup(hook, run);
const first = async path => ((await must('alice', 'GET', adminPath(path))).items ?? [])[0];
const [delivery, notification, correlation, type] = [await first('deliveries'), await first('notifications'), await first('correlations'),
  (await must('alice', 'GET', adminPath('types')))[0]];
const embedded = (await must('alice', 'GET', '/notifications/api/v1/access')).pages;
// [page id, route under the tenant, the read it starts from]
const walk = [
  ['overview', '', 'overview'], ['deliveries', 'deliveries', 'deliveries'], ['notifications', 'notifications', 'notifications'],
  ['correlations', 'correlations', 'correlations'], ['storms', 'storms', 'storms'], ['integrations', 'integrations', 'integrations'],
  ['integrations', `integrations/${keys.integration}`, 'integrations'], ['routes', 'routes', 'routes'], ['mutes', 'mutes', 'mutes'],
  ['escalation-policies', 'escalation-policies', 'escalation-policies'], ['allowlist', 'allowlist', 'allowlist'], ['types', 'types', 'types'],
  ['settings', 'settings', 'settings'],
  ...(delivery ? [['deliveries', `deliveries/${delivery.id}`, `deliveries/${delivery.id}`]] : []),
  ...(notification ? [['notifications', `notifications/${notification.id}`, `notifications/${notification.id}`]] : []),
  ...(type ? [['types', `types/${type.key}`, `types/${type.key}`]] : []),
  ...(correlation ? [['correlations', `correlations/${correlation.application}/${encodeURIComponent(correlation.key)}`,
    `correlations/${encodeURIComponent(correlation.key)}?application=${correlation.application}`]] : []),
];

const browser = await launch();
try {
  const step = steps('na-no-access', problems);
  await step(await browser.newPage(), 'pages')('every-page-of-the-mode-walked', async () => {
    const walked = new Set(walk.map(w => w[0]));
    const missing = embedded.filter(p => !walked.has(p));
    expect(missing.length === 0, `the walk covers every page the embedded mode has (missing ${missing.join(', ') || 'none'})`);
  });

  for (const [what, path, reason] of [['the menu link', pages, 'none'], ['a deep link', tenantPage('deliveries'), 'tenant'],
    ['a go link', `${pages}/go/routes`, 'none']]) {
    const victor = await signIn(browser, 'victor', problems, refused, path);
    await step(victor, what.replace(/ /g, '-'))('no-access', async () => {
      await victor.waitForURL(/\/no-access\?reason=/, { timeout: 15000 });
      const reached = new URL(victor.url());
      expect(reached.pathname === `${pages}/no-access` && reached.searchParams.get('reason') === reason,
        `victor at ${what} gets the no-access page, reason ${reason} (${victor.url()})`);
      await victor.getByTestId(reason === 'tenant' ? 'no-access-tenant' : 'no-access').waitFor({ timeout: 10000 });
      expect(await victor.getByTestId('tenant-bar').count() === 0, `victor at ${what} sees no tenant`);
    });
    await victor.context().close();
  }

  const victor = await signIn(browser, 'victor', problems, refused, pages);
  await victor.waitForURL(/\/no-access\?reason=/, { timeout: 15000 }).catch(() => {});
  await step(victor, 'every-page')('no-access', async () => {
    const reached = [];
    for (const [, route] of walk) {
      await victor.goto(`${base}${tenantPage(route)}`);
      await victor.waitForURL(/\/no-access\?reason=/, { timeout: 10000 }).catch(() => {});
      const shown = await victor.getByTestId('tenant-bar').count();
      if (!victor.url().includes('/no-access?') || shown) reached.push(`${route || '(overview)'}: ${victor.url()}`);
    }
    expect(reached.length === 0, `every page of ${walk.length} is the no-access page for victor (${reached.join('; ') || 'all'})`);
  });
  await victor.context().close();

  const api = await browser.newPage();
  await step(api, 'api')('reads-refused', async () => {
    const answers = [];
    for (const [, , read] of walk) {
      const [mine, his] = [await as('alice', 'GET', adminPath(read)), await as('victor', 'GET', adminPath(read))];
      if (mine.status !== 200 || his.status !== 403) answers.push(`${read}: alice ${mine.status}, victor ${his.status}`);
    }
    expect(answers.length === 0, `alice is answered and victor refused every read the pages start from (${answers.join('; ') || 'all'})`);
    const access = await as('victor', 'GET', '/notifications/api/v1/access');
    expect(access.status === 200 && access.json.tenants.length === 0, `victor's access read is answered, with no tenant (${access.text.slice(0, 80)})`);
  });
} finally {
  await browser.close();
  await hook.close();
  await webhookTeardown(keys).catch(e => problems.push(`teardown: ${e.message}`));
}
// Nothing refused in the browser: the pages start no read for him.
if (refused.length) problems.push(`API calls refused in the browser: ${refused.join('; ')}`);
finish(problems);
