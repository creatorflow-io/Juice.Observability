// Shared by the browser journeys: the local stack's .env, the Console's address, sign-in through Keycloak, and numbered steps with a
// screenshot each. New journeys use it; the older ones still carry their own copies.
import { chromium } from 'playwright-core';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/** deploy/local, with a trailing slash. */
export const local = fileURLToPath(new URL('../../deploy/local/', import.meta.url));

/** deploy/local/.env as an object (KEY=value lines; comments and CRLF ignored). */
export const env = Object.fromEntries(readFileSync(`${local}.env`, 'utf8').split(/\r?\n/)
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));

export const base = process.env.BASE ?? 'http://localhost:5180';
export const keycloak = process.env.KEYCLOAK ?? 'http://localhost:8080';

const shots = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(shots, { recursive: true });

export function launch() {
  return chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
}

/**
 * A new page signed in as `user`. Page errors go to `problems`; console errors and API answers of 400 or more go to `onError`
 * (by default `problems` too), so a journey that expects refusals can collect them apart.
 */
export async function signIn(browser, user, problems, onError = e => problems.push(e)) {
  const page = await (await browser.newContext({ viewport: { width: 1500, height: 1100 } })).newPage();
  page.on('pageerror', e => problems.push(`${user} pageerror: ${e.message}`));
  page.on('console', m => { if (m.type() === 'error') onError(`${user} console: ${m.text()}`); });
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) onError(`${user} http ${r.status()} ${r.url()}`); });
  await page.goto(`${base}/`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', user);
  await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });
  return page;
}

/** A token for `user` from the CLI client (password grant), for API calls beside the browser. */
export async function tokenFor(user) {
  const r = await fetch(`${keycloak}/realms/juice/protocol/openid-connect/token`, { method: 'POST',
    body: new URLSearchParams({ grant_type: 'password', client_id: 'juice-platform-cli', username: user, password: env.KEYCLOAK_DEMO_PASSWORD }) });
  if (!r.ok) throw new Error(`token for ${user}: ${r.status}`);
  return (await r.json()).access_token;
}

/**
 * Numbered steps for one journey: each runs, records its failure in `problems` instead of stopping the journey, and leaves a
 * screenshot `<journey>-NN-<name>.png` in e2e/shots.
 */
export function steps(journey, problems) {
  let n = 0;
  return (page, prefix = '') => async (name, fn) => {
    const label = prefix ? `${prefix} ${name}` : name;
    try { await fn(); } catch (e) { problems.push(`${label}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${shots}${journey}-${String(++n).padStart(2, '0')}${prefix ? `-${prefix}` : ''}-${name}.png`, fullPage: true });
    console.log(new Date().toISOString().slice(11, 19), 'step', label);
  };
}

export const expect = (cond, what) => { if (!cond) throw new Error(what); };

/** Prints the problems and exits 1 if there are any. */
export function finish(problems) {
  console.log(problems.length ? `PROBLEMS:\n  ${problems.join('\n  ')}` : 'PROBLEMS: none');
  process.exit(problems.length ? 1 : 0);
}
