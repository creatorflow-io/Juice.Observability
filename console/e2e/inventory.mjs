// Phase 10, milestone 1: the inventory pages no other journey walks (docs/phase-10-console.md). As victor (viewer):
//   resources   every resource type opens without an error; the host list has at least one host (hostmetrics);
//   resource    a host's page shows its identity, health and the instances running on it, and links back to its list;
//   timeline    the Timeline opens over its default week, lists entries or says there are none, and an alert entry links to its alert;
//               narrowed to one service, it shows that service's stats above.
//   node e2e/inventory.mjs
// Needs deploy/local up with the platform profile, the hostmetrics pipeline running, and demo traffic (e2e/demo-traffic.mjs) for a service.
import { base, expect, finish, launch, signIn, steps } from './lib.mjs';

const problems = [];
const browser = await launch();
const page = await signIn(browser, 'victor', problems);
const step = steps('inventory', problems)(page);
const loaded = () => page.locator('mat-progress-bar').waitFor({ state: 'detached', timeout: 20000 }).catch(() => {});
const rows = () => page.locator('table.jc-table tbody tr');

for (const type of ['host', 'container', 'k8s-pod', 'database', 'messaging-destination']) {
  await step(`resources-${type}`, async () => {
    await page.goto(`${base}/resources?type=${type}`);
    await rows().first().waitFor({ timeout: 20000 });
    await loaded();
    expect(await page.locator('.jc-error').count() === 0, `the ${type} list shows an error`);
    const count = await page.locator('table.jc-table tbody tr a').count();
    console.log(`  ${type}: ${count || 'none seen'}`);
    if (type === 'host') expect(count > 0, 'no host listed: hostmetrics should report this machine');
  });
}

await step('resource-host', async () => {
  await page.goto(`${base}/resources?type=host`);
  const first = page.locator('table.jc-table tbody tr a').first();
  await first.waitFor({ timeout: 20000 });
  const name = (await first.textContent()).trim();
  await first.click();
  await page.locator('h1', { hasText: name }).waitFor({ timeout: 20000 });
  for (const section of ['Identity', 'Instances running here']) await page.locator('h2', { hasText: section }).waitFor();
  expect(await page.locator('jc-health').count() > 0, `${name} shows no health`);
  console.log(`  ${name}: ${(await page.locator('jc-health').first().textContent()).trim()}`);
  await page.locator('a.jc-crumb', { hasText: 'Resources' }).click();
  await page.waitForURL(/\/resources\?.*type=host/, { timeout: 10000 });
});

await step('timeline', async () => {
  await page.goto(`${base}/timeline`);
  await page.locator('[data-testid=timeline]').waitFor({ timeout: 20000 });
  await loaded();
  const entries = await page.locator('[data-testid=timeline] tbody tr').count();
  const empty = await page.locator('[data-testid=timeline] tbody', { hasText: 'Nothing in this window.' }).count();
  expect(entries > 0, 'the timeline has no rows, not even its empty message');
  console.log(`  ${empty ? 'nothing in the window' : `${entries} entries`}`);
  // An alert's entry, not the first link: changes and health entries link elsewhere.
  const alert = page.locator('[data-testid=timeline] tbody a[href*="/alerts/"]').first();
  if (await alert.count()) {
    const title = (await alert.textContent()).trim();
    await alert.click();
    await page.waitForURL(/\/alerts\/[^/?]+/, { timeout: 10000 });
    console.log(`  alert entry "${title}" opens its alert`);
  }
});

await step('timeline-service', async () => {
  await page.goto(`${base}/services`);
  const service = page.locator('table.jc-table tbody tr a').first();
  await service.waitFor({ timeout: 20000 });
  const href = await service.getAttribute('href');
  const id = href.split('?')[0].split('/').pop();
  await page.goto(`${base}/timeline?service=${id}`);
  await page.locator('[data-testid=timeline]').waitFor({ timeout: 20000 });
  await page.locator('jc-service-stats').waitFor({ timeout: 10000 });
  await page.locator('a.jc-crumb', { hasText: 'Service' }).waitFor();
});

await browser.close();
finish(problems);
