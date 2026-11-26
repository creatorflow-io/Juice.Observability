// M48 (phase 5; the Notifications Console's 4c secrets journey, phase-4c-plan proof point 6, in Juice Console): no secret appears under
// Alerts › Notifications, on a page or in an answer it reads. The secrets are the stack's own values (deploy/local/.env: passwords, client
// secrets, keys) and the webhook's signing secret, which a webhook integration names by reference and has signed an alert's delivery with.
// alice (admin) and erin (operator) walk every page the embedded host has, detail pages by real ids; each page's text, its fields' values and
// every answer either saw are searched for each secret (as written, JSON-escaped or URL-encoded). The integration's page shows the
// reference, as given, to alice.
//   BASE=… node e2e/notifications-admin/secrets.mjs      (see lib.mjs for the host it needs)
import { launch, signIn, steps, expect, finish, base, env, tenantPage, adminPath, must, until, receiver, webhookSetup, webhookTeardown, fireAlert,
  hookSecret, hookSecretRef, holds, collectAnswers } from './lib.mjs';

const run = Date.now().toString(36);
const problems = [];
const refused = [];
const secrets = [...new Set([...Object.entries(env).filter(([k]) => /SECRET|PASSWORD|KEY|TOKEN/.test(k)).map(([, v]) => v), hookSecret])]
  .filter(v => v && v.length >= 8);
const found = text => secrets.filter(s => holds(text, s));

// An alert delivered, signed with the secret: it was read and used, so it was there to leak.
const hook = await receiver();
const keys = await webhookSetup(hook, run);
await fireAlert(run);
const signed = await until(async () => hook.received.filter(r => r.signature), rs => rs.length > 0, 60);
const delivery = ((await must('alice', 'GET', adminPath(`deliveries?integration=${keys.integration}`))).items ?? [])[0];
const notification = ((await must('alice', 'GET', adminPath('notifications?type=observability.alert.firing'))).items ?? [])[0];
const correlation = ((await must('alice', 'GET', adminPath('correlations'))).items ?? [])[0];
const routes = ['', 'deliveries', 'notifications', 'correlations', 'storms', 'integrations', `integrations/${keys.integration}`, 'routes', 'mutes',
  'escalation-policies', 'allowlist', 'types', 'types/observability.alert.firing', 'settings',
  ...(delivery ? [`deliveries/${delivery.id}`] : []), ...(notification ? [`notifications/${notification.id}`] : []),
  ...(correlation ? [`correlations/${correlation.application}/${encodeURIComponent(correlation.key)}`] : [])];

const browser = await launch();
try {
  const setup = await browser.newPage();
  await steps('na-secrets', problems)(setup)('seeded', async () => {
    expect(secrets.length >= 8, `secrets to look for: ${secrets.length}`);
    expect(signed.length > 0, `the alert arrived signed (${hook.received.length} received)`);
    expect(delivery && notification && correlation, 'a delivery, a notification and a correlation to open by id');
  });
  for (const user of ['alice', 'erin']) {
    const page = await signIn(browser, user, problems, refused, tenantPage(''));
    await page.getByTestId('overview-tiles').waitFor({ timeout: 15000 }).catch(() => {});
    await page.waitForLoadState('networkidle').catch(() => {});
    const step = steps('na-secrets', problems)(page, user);
    const collected = collectAnswers(page);
    await step('every-page', async () => {
      let leaked = [];
      for (const route of routes) {
        await page.goto(`${base}${tenantPage(route)}`);
        await page.waitForLoadState('networkidle').catch(() => {});
        const isIntegration = route === `integrations/${keys.integration}`;
        if (isIntegration) await page.getByTestId('integration-secret-ref').waitFor({ timeout: 10000 }).catch(() => {});
        const fields = await page.locator('input, textarea').evaluateAll(es => es.map(e => e.value).join('\n'));
        const text = `${await page.locator('body').innerText()}\n${fields}`;
        if (isIntegration && user === 'alice') {
          expect(await page.getByTestId('integration-secret-ref').inputValue() === hookSecretRef, 'alice sees the integration\'s secret reference, as given');
        }
        leaked = leaked.concat(found(text).map(s => `${route || '(overview)'}: ${s.slice(0, 3)}…`));
        await collected.read();
      }
      expect(leaked.length === 0, `${user}: no secret on any of ${routes.length} pages (${leaked.join('; ') || 'none'})`);
    });
    await step('every-answer', async () => {
      const { answers, unread } = await collected.read();
      const api = answers.filter(a => a.url.includes('/notifications/api/'));
      const inAnswers = answers.flatMap(a => found(a.text ?? '').map(s => `${a.url.replace(base, '')}: ${s.slice(0, 3)}…`));
      expect(api.length >= routes.length && unread.length === 0 && inAnswers.length === 0,
        `${user}: no secret in any of ${answers.length} answers (${api.length} from the notifications API) (${inAnswers.join('; ') || 'none'}; unread ${unread.map(a => a.url).join(', ') || 'none'})`);
    });
    await page.context().close();
  }
} finally {
  await browser.close();
  await hook.close();
  await webhookTeardown(keys).catch(e => problems.push(`teardown: ${e.message}`));
}
if (refused.length) problems.push(`API calls refused: ${refused.join('; ')}`);
finish(problems);
