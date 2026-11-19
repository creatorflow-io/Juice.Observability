// The count of requests waiting for approval in the side menu, drawn by juice-layout's menu (MenuBadgeService): beside Approvals while
// Automation is open, on Automation's header while it's closed, never both on screen, not even for a frame while the group opens or
// closes; on the icon's corner with the menu folded; 99+ above 99; gone at zero. The count is stubbed (the API's answer to
// /action-requests/waiting), so this needs only the stack's sign-in and API, not an agent or real requests; e2e/approvals.mjs counts real ones.
//   node e2e/approvals-badge.mjs                     (CONSOLE_DIST=<ng build output>/browser to try a build before deploying it)
import { launch, signIn, steps, expect, finish, base } from './lib.mjs';

const problems = [];
const browser = await launch();
const page = await signIn(browser, 'alice', problems);
const step = steps('approvals-badge', problems)(page);

let waiting = 3;
await page.context().route('**/action-requests/waiting*', r => r.fulfill({ json: { total: waiting, forYou: waiting } }));

const approvals = 'juice-nav-menu juice-menu-badge[data-menu-id=approvals]';
const automation = 'juice-nav-menu juice-menu-badge[data-menu-id=observability-automation]';
const header = page.locator('juice-nav-menu mat-expansion-panel-header', { hasText: 'Automation' });
const entry = label => page.locator('juice-nav-menu mat-list-item', { hasText: label }).first();

/** The counts a person can see now (not hidden, not in a closed group), as `menu id=text`. */
const onScreen = () => page.evaluate(() => Array.from(document.querySelectorAll('juice-nav-menu juice-menu-badge:not([hidden])'))
  .filter(b => { const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !b.closest('[inert]'); })
  .map(b => `${b.getAttribute('data-menu-id')}=${b.textContent.trim()}`));

/** Runs `act`, then reports the frames (for 1.5 s) that had the count on screen twice. */
async function framesWithTwo(act) {
  await page.evaluate(() => {
    const w = window; w.__twice = 0; w.__frames = 0;
    const tick = () => {
      const shown = Array.from(document.querySelectorAll('juice-nav-menu juice-menu-badge:not([hidden])'))
        .filter(b => { const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !b.closest('[inert]'); });
      if (shown.length > 1) w.__twice++;
      if (++w.__frames < 600) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  await act();
  await page.waitForTimeout(1500);
  return page.evaluate(() => window.__twice);
}

await step('beside-approvals-while-open', async () => {
  await page.goto(`${base}/agents`);
  await page.locator(`${approvals}:not([hidden])`).waitFor({ timeout: 15000 });
  const shown = await onScreen();
  expect(JSON.stringify(shown) === '["approvals=3"]', `on screen: ${JSON.stringify(shown)}`);
  const hint = await page.locator(approvals).getAttribute('aria-label');
  expect(hint === '3 requests waiting for your approval', `the hint: ${hint}`);
});

await step('on-automation-while-closed', async () => {
  await page.goto(`${base}/services`);
  await page.locator(`${automation}:not([hidden])`).waitFor({ timeout: 15000 });
  const shown = await onScreen();
  expect(JSON.stringify(shown) === '["observability-automation=3"]', `on screen: ${JSON.stringify(shown)}`);
  const hint = await page.locator(automation).getAttribute('title');
  expect(hint === '3 requests waiting for your approval', `Automation's hint: ${hint}`);
});

await step('never-twice-while-the-group-moves', async () => {
  // Opening Automation by hand, closing it, and arriving on Agents (which opens it): the count moves in the same frame.
  for (const [what, act] of [
    ['open', () => header.click()],
    ['close', () => header.click()],
    ['arrive', async () => { await header.click(); await page.waitForTimeout(400); await entry('Agents').click(); }],
    ['leave', () => entry('Logs').click()],
  ]) {
    const twice = await framesWithTwo(act);
    expect(twice === 0, `${what}: the count twice on screen in ${twice} frames`);
    const shown = await onScreen();
    expect(shown.length === 1, `${what}: on screen after: ${JSON.stringify(shown)}`);
  }
});

await step('folded-on-the-icon', async () => {
  await page.goto(`${base}/agents`);
  await page.locator(`${approvals}:not([hidden])`).waitFor({ timeout: 15000 });
  await page.locator('juice-nav-menu .bottom-menu button').click();   // fold
  await page.mouse.move(900, 500);                                    // off the menu, which unfolds under the pointer
  await page.waitForTimeout(800);
  const where = await page.evaluate(sel => {
    const badge = document.querySelector(sel), row = badge?.closest('mat-list-item'), icon = row?.querySelector('mat-icon');
    if (!badge || !row || !icon) return null;
    const b = badge.getBoundingClientRect(), r = row.getBoundingClientRect(), i = icon.getBoundingClientRect();
    return { folded: badge.classList.contains('folded'), inRow: b.left >= r.left && b.right <= r.right && b.top >= r.top && b.bottom <= r.bottom,
      onCorner: b.left > i.left + i.width / 2 && b.left < i.right && b.top < i.top + i.height / 2, inLabel: !!badge.closest('[matListItemTitle]') };
  }, approvals);
  expect(where?.folded && where.inRow && where.onCorner && !where.inLabel, `folded: ${JSON.stringify(where)}`);
  const twice = await framesWithTwo(async () => { await page.hover('juice-nav-menu mat-nav-list'); await page.mouse.move(900, 500); });
  expect(twice === 0, `hovering the folded menu: the count twice in ${twice} frames`);
});

await step('99-plus', async () => {
  // Still folded: the wider count stays on the icon's corner, inside the row.
  waiting = 120;
  await page.goto(`${base}/agents`);
  await page.locator(`${approvals}:not([hidden])`, { hasText: '99+' }).waitFor({ timeout: 15000 });
  const hint = await page.locator(approvals).getAttribute('aria-label');
  expect(hint === '120 requests waiting for your approval', `the hint: ${hint}`);
  const inRow = await page.evaluate(sel => {
    const badge = document.querySelector(sel), row = badge?.closest('mat-list-item');
    const b = badge?.getBoundingClientRect(), r = row?.getBoundingClientRect();
    return !!b && !!r && b.left >= r.left && b.right <= r.right;
  }, approvals);
  expect(inRow, '99+ runs past the folded row');
  await page.locator('juice-nav-menu .bottom-menu button').click();   // unfold again, as the journey found it
});

await step('gone-at-zero', async () => {
  waiting = 0;
  await page.goto(`${base}/agents`);
  await page.locator('juice-nav-menu mat-icon >> text=approval').waitFor();
  await page.waitForTimeout(1500);
  expect((await onScreen()).length === 0, `on screen with nothing waiting: ${JSON.stringify(await onScreen())}`);
  await page.goto(`${base}/services`);
  await page.waitForTimeout(1500);
  expect((await onScreen()).length === 0, `on Automation with nothing waiting: ${JSON.stringify(await onScreen())}`);
});

await browser.close();
finish(problems);
