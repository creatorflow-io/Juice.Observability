// Core 2.0 M3 A through the browser: the menu is built from /api/v1/modules and the libraries compiled into the Console.
//   1. every menu link opens a page of its own, never the not-found page;
//   2. with /modules stubbed to [core], no SLOs, Agents or Approvals link is left on the Overview, a service page or an alert page, and
//      their routes are not found.
//   node e2e/modules.mjs          (CONSOLE_DIST=dist/juice-console/browser node e2e/modules.mjs to try a build before deploying it)
// Needs the local stack and the demo traffic (e2e/demo-traffic.mjs) for a service page; an alert page is visited when an alert exists.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { base, expect, finish, launch, signIn, steps } from './lib.mjs';

const problems = [];
const browser = await launch();
const page = await signIn(browser, 'victor', problems);
const step = steps('modules', problems)(page);

const manifests = JSON.parse(readFileSync(fileURLToPath(new URL('../projects/juice-js/observability/src/lib/modules/builtin-manifests.json', import.meta.url)), 'utf8'));
const NOT_FOUND = /page (was )?not found|404/i;

/** The paths of the side menu's links, groups opened. */
async function menuLinks() {
  const nav = page.locator('nav a[href], jc-nav-menu a[href], mat-sidenav a[href]');
  await nav.first().waitFor({ timeout: 15000 });
  for (const group of await page.locator('[aria-expanded="false"]').all()) await group.click({ timeout: 2000 }).catch(() => undefined);
  const hrefs = await nav.evaluateAll(links => links.map(a => a.getAttribute('href')).filter(h => h && h.startsWith('/')));
  return [...new Set(hrefs)];
}

const gone = ['/slos', '/agents', '/approvals'];
const linksTo = async paths => (await page.locator('a[href]').evaluateAll(links => links.map(a => a.getAttribute('href')))).filter(h => paths.some(p => h === p || h?.startsWith(`${p}/`)));

await step('menu-links', async () => {
  await page.goto(`${base}/`);
  const links = await menuLinks();
  expect(links.length >= 10, `the menu has ${links.length} links`);
  console.log(`  ${links.length} menu links`);
  for (const link of links) {
    await page.goto(`${base}${link}`);
    await page.waitForTimeout(400);
    const text = await page.locator('body').innerText();
    expect(!NOT_FOUND.test(text.slice(0, 600)) && (await page.locator('h1').count()) > 0, `${link} shows no page of its own`);
  }
});

await step('core-only', async () => {
  const core = { coreVersion: '2.0.0', items: manifests.items.filter(m => m.name === 'core').map(m => ({ ...m, version: '2.0.0', supportedCore: '[2.0.0]' })) };
  await page.route('**/api/v1/modules', route => route.fulfill({ json: core }));
  await page.goto(`${base}/`);
  await menuLinks();
  expect((await linksTo(gone)).length === 0, `the Overview still links to ${(await linksTo(gone)).join(', ')}`);
  expect((await page.locator('a[href]', { hasText: /^\s*Automation\s*$/ }).count()) === 0, 'the Automation group should be hidden');

  await page.goto(`${base}/services`);
  const service = page.locator('a[href^="/services/"]').first();
  await service.waitFor({ timeout: 15000 });
  await service.click();
  await page.locator('h1').first().waitFor();
  await page.waitForTimeout(500);
  expect((await linksTo(gone)).length === 0, `a service page still links to ${(await linksTo(gone)).join(', ')}`);

  await page.goto(`${base}/alerts`);
  await page.waitForTimeout(800);
  const alert = page.locator('a[href^="/alerts/"]:not([href^="/alerts/notifications"])').first();
  if (await alert.count()) {
    await alert.click();
    await page.locator('h1').first().waitFor();
    await page.waitForTimeout(500);
    expect((await linksTo(gone)).length === 0, `an alert page still links to ${(await linksTo(gone)).join(', ')}`);
  } else console.log('  no alert to open: the alert page is covered by the Console specs');

  for (const path of gone) {
    await page.goto(`${base}${path}`);
    await page.waitForTimeout(500);
    expect(NOT_FOUND.test((await page.locator('body').innerText()).slice(0, 600)), `${path} should not be found when SLOs, Agents and Approvals aren't installed`);
  }
});

await browser.close();
finish(problems);
