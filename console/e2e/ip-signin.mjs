// Sign-in check over the machine address: the Console (Keycloak PKCE) and Grafana (Keycloak OIDC).
import { chromium } from 'playwright-core';
import { readFileSync } from 'node:fs';
const env = Object.fromEntries(readFileSync('../deploy/local/.env', 'utf8').split('\n').filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const host = process.env.HOST ?? env.PUBLIC_HOST ?? 'localhost';
const b = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const problems = [];
const page = await b.newPage({ viewport: { width: 1500, height: 800 } });
page.on('console', m => { if (m.type() === 'error') problems.push(`console: ${m.text()}`); });
page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
const login = async () => {
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', 'erin'); await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD); await page.click('#kc-login');
};
await page.goto(`http://${host}:5180/services?env=local&range=1h`);
await login();
await page.waitForSelector('table.jc-table tbody tr td a', { timeout: 30000 });
console.log('console url:', page.url(), '| secure context:', await page.evaluate(() => isSecureContext));
await page.click('table.jc-table tbody tr td a >> nth=0');
const link = page.locator('a:has-text("Grafana")').first();
await link.waitFor({ timeout: 15000 });
console.log('grafana link:', await link.getAttribute('href'));
await page.screenshot({ path: 'e2e/shots/sample/13-console-by-ip.png' });
await page.goto(`http://${host}:3000/login`);
await page.click('a:has-text("Sign in with Keycloak")');
// Keycloak may still have the session from the Console sign-in (single sign-on).
await Promise.race([page.waitForURL(new RegExp(`^http://${host.replaceAll('.', '\.')}:3000/(?!login)`), { timeout: 30000 }), login().catch(() => {})]);
await page.waitForURL(new RegExp(`^http://${host.replaceAll('.', '\.')}:3000/(?!login)`), { timeout: 30000 });
const user = await (await page.request.get(`http://${host}:3000/api/user`)).json();
console.log('grafana user:', user.login, '| role:', (await (await page.request.get(`http://${host}:3000/api/user/orgs`)).json()).map(o => o.role).join(','));
console.log('PROBLEMS:', problems.length ? problems.join('\n') : 'none');
await b.close();
