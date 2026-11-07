// Roadmap Phase 6 acceptance through the browser: an admin configures a rule, channels and escalation; a real alert from demo traffic reaches a signed webhook, Slack, Teams and email
// formats; an operator silences it, works an incident and acknowledges; the escalation stops as the policy says.
//
// Needs deploy/local up with the platform profile, demo traffic from e2e/demo-traffic.mjs (a failing payments-2), and the API able to reach this machine over HTTP, with:
//   Webhooks:AllowHttpToAllowlisted=true, Webhooks:AdditionalChatHosts:0=host.docker.internal, JUICE_WEBHOOK_E2E=<SECRET>, JUICE_WEBHOOK_E2E_SLACK=http://host.docker.internal:<PORT>/slack,
//   JUICE_WEBHOOK_E2E_TEAMS=http://host.docker.internal:<PORT>/teams, Webhooks:Smtp:Host=host.docker.internal, Webhooks:Smtp:Port=<SMTP>, Webhooks:Smtp:From=juice@example.com, Webhooks:Smtp:UseTls=false
//   BASE=http://localhost:5180 PORT=9091 SMTP=9092 SECRET=... node e2e/notifications.mjs
// Screenshots land in e2e/shots/. Exits 1 on any failed step, browser error or failed API call. What it creates stays, disabled, under keys named e2e-<time>.
import { chromium } from 'playwright-core';
import { createHmac, timingSafeEqual } from 'node:crypto';
import { createServer as createHttp } from 'node:http';
import { createServer as createTcp } from 'node:net';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const env = Object.fromEntries(readFileSync(fileURLToPath(new URL('../../deploy/local/.env', import.meta.url)), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const [port, smtpPort, secret] = [Number(process.env.PORT ?? 9091), Number(process.env.SMTP ?? 9092), process.env.SECRET];
if (!secret) throw new Error('SECRET is required');
const out = fileURLToPath(new URL('./shots/', import.meta.url));
mkdirSync(out, { recursive: true });
const run = `e2e-${Date.now().toString(36)}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ─── What the platform talks to ───────────────────────────────────────────
const received = { signed: [], slack: [], teams: [], mail: [] };
const http = createHttp((req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', () => {
    const path = req.url.split('?')[0].slice(1);
    if (path === 'signed') {
      const expected = Buffer.from(`v1,${createHmac('sha256', secret).update(`${req.headers['webhook-id']}.${req.headers['webhook-timestamp']}.${body}`).digest('base64')}`);
      const actual = Buffer.from(String(req.headers['webhook-signature'] ?? ''));
      const valid = expected.length === actual.length && timingSafeEqual(expected, actual);
      received.signed.push({ valid, json: JSON.parse(body) });
    } else if (received[path]) received[path].push(JSON.parse(body));
    res.writeHead(path === 'failing' ? 503 : 200).end();
  });
}).listen(port, '0.0.0.0');
const smtp = createTcp(socket => {
  let data = false, message = '';
  socket.write('220 test\r\n');
  socket.on('data', chunk => {
    for (const line of chunk.toString().split('\r\n')) {
      if (data) {
        if (line === '.') { data = false; received.mail.push(message); message = ''; socket.write('250 done\r\n'); } else message += `${line}\n`;
      } else if (/^DATA/i.test(line)) { data = true; socket.write('354 go\r\n'); } else if (/^QUIT/i.test(line)) { socket.end('221 bye\r\n'); } else if (line) socket.write('250 ok\r\n');
    }
  });
  socket.on('error', () => {});
}).listen(smtpPort, '0.0.0.0');

// ─── Browser and API, as each user ────────────────────────────────────────
const token = async user => (await (await fetch('http://localhost:8080/realms/juice/protocol/openid-connect/token', { method: 'POST',
  body: new URLSearchParams({ grant_type: 'password', client_id: 'juice-platform-cli', username: user, password: env.KEYCLOAK_DEMO_PASSWORD }) })).json()).access_token;
const adminToken = await token('alice');
const headers = { authorization: `Bearer ${adminToken}`, 'content-type': 'application/json' };
const tenant = (await (await fetch(`${base}/api/v1/tenants`, { headers })).json()).items[0].id;
const api = async (method, path, body) => {
  const response = await fetch(`${base}/api/v1/tenants/${tenant}${path}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
};

const browser = await chromium.launch({ executablePath: process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
const problems = [];
let n = 0;
async function signIn(user) {
  const context = await browser.newContext({ viewport: { width: 1500, height: 1100 } });
  const page = await context.newPage();
  page.on('console', m => { if (m.type() === 'error') problems.push(`console (${user}): ${m.text()}`); });
  page.on('pageerror', e => problems.push(`pageerror (${user}): ${e.message}`));
  page.on('response', r => { if (r.url().includes('/api/') && r.status() >= 400) problems.push(`http ${r.status()} ${r.url()}`); });
  await page.goto(`${base}/`);
  await page.waitForURL(/realms\/juice/, { timeout: 30000 });
  await page.fill('#username', user);
  await page.fill('#password', env.KEYCLOAK_DEMO_PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(u => u.toString().startsWith(base) && !u.toString().includes('login-completed'), { timeout: 30000 });
  return page;
}
const step = async (page, name, fn) => {
  try { await fn(); } catch (e) { problems.push(`${name}: ${e.message.split('\n')[0]}`); name += '-FAILED'; }
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${out}p6-${String(++n).padStart(2, '0')}-${name}.png`, fullPage: true });
  console.log(new Date().toISOString().slice(11, 19), 'step', name);
};
const until = async (what, predicate, seconds = 120) => {
  for (let i = 0; i < seconds; i++) { if (await predicate()) return; await sleep(1000); }
  throw new Error(`timed out waiting for ${what}`);
};
const select = async (page, name, option) => { await page.click(`mat-select[name=${name}]`); await page.click(`mat-option:has-text("${option}")`); };
const escalated = () => received.signed.filter(r => r.json.type === 'alert.escalated');

const admin = await signIn('alice');
const operator = await signIn('erin');
const rule = `${run}-rule`, signed = `${run}-signed`, slack = `${run}-slack`, teams = `${run}-teams`, mail = `${run}-mail`, failing = `${run}-failing`, policy = `${run}-policy`;
let alert;

await step(admin, 'allowlist', async () => {
  await admin.goto(`${base}/notifications`);
  await admin.fill('[data-testid=allow-destination]', 'host.docker.internal');
  await admin.fill('[data-testid=allow-ports]', String(port));
  await admin.click('[data-testid=allow-add]');
  await admin.waitForSelector('[data-testid=allowlist] >> text=host.docker.internal');
});
await step(admin, 'channel-signed', async () => {
  await admin.click('[data-testid=channel-new]');
  await admin.fill('[data-testid=channel-form] input[name=key]', signed);
  await select(admin, 'kind', 'Signed webhook');
  await admin.fill('[data-testid=channel-form] input[name=url]', `http://host.docker.internal:${port}/signed?token=do-not-show`);
  await admin.fill('[data-testid=channel-form] input[name=secretRef]', 'env:JUICE_WEBHOOK_E2E');
  await admin.fill('[data-testid=channel-form] input[name=ruleKeys]', rule);
  await admin.click('[data-testid=channel-save]');
  await admin.waitForSelector(`[data-testid=channels] >> text=${signed}`);
  if ((await admin.textContent('[data-testid=channels]')).includes('do-not-show')) throw new Error('a channel showed its URL path');
});
await step(admin, 'channel-slack', async () => {
  await admin.click('[data-testid=channel-new]');
  await admin.fill('[data-testid=channel-form] input[name=key]', slack);
  await admin.fill('[data-testid=channel-form] input[name=secretRef]', 'env:JUICE_WEBHOOK_E2E_SLACK');
  await admin.fill('[data-testid=channel-form] input[name=ruleKeys]', rule);
  await admin.click('[data-testid=channel-save]');
  await admin.waitForSelector(`[data-testid=channels] >> text=${slack}`);
  await api('POST', '/webhooks', { key: teams, kind: 'teams', secretRef: 'env:JUICE_WEBHOOK_E2E_TEAMS', ruleKeys: [rule] });
  await api('POST', '/webhooks', { key: mail, kind: 'email', recipients: ['oncall@example.com'], ruleKeys: [rule] });
  await api('POST', '/webhooks', { key: failing, url: `http://host.docker.internal:${port}/failing`, secretRef: 'env:JUICE_WEBHOOK_E2E', ruleKeys: [rule] });
});
await step(admin, 'policy', async () => {
  await admin.click('[data-testid=policy-new]');
  await admin.fill('[data-testid=policy-form] input[name=key]', policy);
  await admin.fill('[data-testid=policy-form] input[name=name]', `Escalation ${run}`);
  await admin.fill('[data-testid=policy-form] textarea[name=steps]', `1: ${signed}\n3: ${signed}`);
  await admin.fill('[data-testid=policy-form] input[name=ruleKeys]', rule);
  await select(admin, 'minimumSeverity', 'Info');
  await admin.click('[data-testid=policy-save]');
  await admin.waitForSelector(`[data-testid=policies] >> text=Escalation ${run}`);
});
await step(admin, 'rule', async () => {
  await admin.goto(`${base}/rules`);
  await admin.click('[data-testid=rule-new]');
  await admin.fill('[data-testid=rule-form] input[name=key]', rule);
  await admin.fill('[data-testid=rule-form] input[name=name]', `Payments errors ${run}`);
  await admin.fill('[data-testid=rule-form] input[name=service]', 'demo-shop/payments');
  await admin.fill('[data-testid=rule-form] input[name=environment]', 'production');
  await admin.fill('[data-testid=rule-form] input[name=threshold]', '0.01');
  await admin.fill('[data-testid=rule-form] input[name=schedule]', '5');
  await admin.fill('[data-testid=rule-form] input[name=window]', '60');
  await admin.fill('[data-testid=rule-form] input[name=for]', '0');
  await admin.fill('[data-testid=rule-form] input[name=minimumSamples]', '1');
  await admin.click('[data-testid=rule-save]');
  await admin.waitForSelector(`[data-testid=rules] >> text=Payments errors ${run}`);
});
await step(operator, 'firing-reaches-every-channel', async () => {
  await until('every channel to be told', () => received.signed.some(r => r.json.type === 'alert.firing') && received.slack.length && received.teams.length && received.mail.length);
  const first = received.signed.find(r => r.json.type === 'alert.firing');
  if (!first.valid) throw new Error('the signature did not verify');
  alert = first.json.alert.id;
  if (!received.slack[0].blocks) throw new Error('Slack got no blocks');
  if (received.teams[0].attachments?.[0]?.content?.type !== 'AdaptiveCard') throw new Error('Teams got no card');
  if (!/Subject: \[Juice\] Firing/.test(received.mail[0]) || !/oncall@example.com/.test(received.mail[0])) throw new Error(`mail reads wrong: ${received.mail[0].slice(0, 200)}`);
  await operator.goto(`${base}/alerts/${alert}?env=production`);
  await operator.waitForSelector(`[data-testid=alert-notifications] >> text=${signed}`);
  await operator.waitForSelector('[data-testid=alert-notifications] >> text=Delivered');
  await operator.waitForSelector(`[data-testid=alert-notifications] >> text=Escalation ${run}`);
});
await step(operator, 'silence-holds-escalation', async () => {
  await operator.click('[data-testid=alert-silence]');
  await operator.waitForSelector('[data-testid=silence-form]');
  if ((await operator.inputValue('[data-testid=silence-service]')) !== 'demo-shop/payments') throw new Error('the form did not carry the service');
  await operator.fill('[data-testid=silence-comment]', `Working on it ${run} --password hunter2`);
  await operator.click('[data-testid=silence-save]');
  await operator.waitForSelector(`[data-testid=silences] >> text=Working on it ${run}`);
  if ((await operator.textContent('[data-testid=silences]')).includes('hunter2')) throw new Error('a secret reached the page');
  // Past the first step's minute: nothing may arrive.
  const before = escalated().length;
  await sleep(75_000);
  if (escalated().length !== before) throw new Error('an escalation got through the silence');
  await operator.goto(`${base}/alerts/${alert}?env=production`);
  await operator.waitForSelector('[data-testid=alert-silenced]');
  await operator.waitForSelector('text=Firing');
});
await step(operator, 'silence-ends-escalation-goes-on', async () => {
  await operator.goto(`${base}/silences`);
  await operator.click(`tr:has-text("Working on it ${run}") >> button:has-text("End now")`);
  await operator.waitForSelector(`tr:has-text("Working on it ${run}") >> button:has-text("End now")`, { state: 'detached', timeout: 10000 });
  await until('the held escalation', () => escalated().length >= 1, 30);
  if (escalated()[0].json.escalation.step !== 1 || !escalated()[0].valid) throw new Error('wrong escalation');
});
await step(operator, 'incident', async () => {
  await operator.goto(`${base}/alerts/${alert}?env=production`);
  await operator.click('[data-testid=alert-open-incident]');
  await operator.waitForSelector('[data-testid=incident-with-alert]');
  await operator.fill('[data-testid=incident-title]', `Payments failing ${run}`);
  await operator.click('[data-testid=incident-save]');
  await operator.waitForSelector('[data-testid=incident-state] >> text=Open');
  await operator.waitForSelector('[data-testid=incident-open-alerts]');
  await operator.click('[data-testid=incident-move-Investigating]');
  await operator.waitForSelector('[data-testid=incident-state] >> text=Investigating');
  await operator.fill('[data-testid=incident-text]', 'ran psql --password hunter2 to look');
  await operator.click('[data-testid=incident-note]');
  await operator.waitForSelector('[data-testid=incident-events] >> text=Note');
  await operator.fill('[data-testid=incident-text]', 'Pool enlarged');
  await operator.click('[data-testid=incident-move-Resolved]');
  await operator.waitForSelector('[data-testid=incident-state] >> text=Resolved');
  const events = await operator.textContent('[data-testid=incident-events]');
  if (events.includes('hunter2')) throw new Error('a secret reached the page');
  for (const text of ['Opened', 'Alert added', 'Open → Investigating', 'Investigating → Resolved', 'Pool enlarged']) if (!events.includes(text)) throw new Error(`no "${text}" in what happened`);
  const state = (await api('GET', `/alerts/${alert}`)).alert.state;
  if (state !== 'Firing') throw new Error(`resolving the incident changed the alert to ${state}`);
});
await step(operator, 'acknowledge-stops-escalation', async () => {
  await operator.goto(`${base}/alerts/${alert}?env=production`);
  await operator.fill('input[name=comment]', 'Mine');
  await operator.click('button:has-text("Acknowledge")');
  await until('the escalation to stop', async () => (await api('GET', `/alerts/${alert}/notifications`)).escalations[0].state === 'Stopped', 20);
  const stopped = (await api('GET', `/alerts/${alert}/notifications`)).escalations[0];
  if (stopped.stopReason !== 'acknowledged') throw new Error(`stopped because ${stopped.stopReason}`);
  // Past the second step's 3 minutes from firing, if not there already.
  const count = escalated().length;
  await sleep(Math.max(0, new Date(received.signed[0].json.alert.firedAt).getTime() + 195_000 - Date.now()));
  if (escalated().length !== count) throw new Error('an escalation came after the acknowledgement');
  await operator.reload();
  await operator.waitForSelector('[data-testid=alert-notifications] >> text=Stopped', { timeout: 5000 }).catch(() => operator.waitForSelector('[data-testid=alert-notifications] >> text=stopped'));
});
await step(admin, 'disable-rule-closes-alert', async () => {
  await admin.goto(`${base}/rules`);
  await admin.click(`[data-testid=rule-edit-${rule}]`);
  await admin.click('[data-testid=rule-form] mat-checkbox[name=enabled] input');
  await admin.click('[data-testid=rule-save]');
  await admin.waitForSelector('[data-testid=rule-form]', { state: 'detached' });
  await until('the alert to resolve', async () => (await api('GET', `/alerts/${alert}`)).alert.state === 'Resolved', 15);
  const history = (await api('GET', `/alerts/${alert}`)).history.at(-1);
  if (history.reason !== 'rule_disabled') throw new Error(`resolved as ${history.reason}`);
  await until('the resolve to reach the channel', () => received.signed.some(r => r.json.type === 'alert.resolved'), 30);
});
await step(admin, 'failures-visible', async () => {
  // A receiver answering 503: the retries and the reason are on the page, and every other channel went on regardless.
  await admin.goto(`${base}/notifications`);
  await admin.click(`[data-testid=channel-history-${signed}]`);
  await admin.waitForSelector('[data-testid=channel-history] >> text=Delivered');
  await admin.waitForSelector('[data-testid=channel-history] >> text=alert.escalated');
  await admin.click(`[data-testid=channel-history-${failing}]`);
  await admin.waitForSelector('[data-testid=channel-history] >> text=status_503');
  await admin.waitForSelector('[data-testid=channel-history] >> text=Retrying');
  const row = await admin.textContent(`[data-testid=channels] tr:has-text("${failing}")`);
  if (!/Webhook/.test(row)) throw new Error(`no row for the failing channel: ${row}`);
  const seen = (await api('GET', `/alerts/${alert}/notifications`)).deliveries.find(d => d.endpointKey === failing && d.delivery.event === 'alert.firing').delivery;
  if (!(seen.attempts >= 2 && seen.history.every(a => a.statusCode === 503))) throw new Error(`attempts not visible: ${JSON.stringify(seen)}`);
});

// ─── Leave nothing active ─────────────────────────────────────────────────
const channels = (await api('GET', '/webhooks')).items.filter(c => c.key.startsWith(run));
for (const c of channels) await api('PUT', `/webhooks/${c.id}`, { kind: c.kind, secretRef: c.secretRef ?? undefined, recipients: c.recipients ?? undefined, enabled: false });
const policies = (await api('GET', '/escalation-policies')).items.filter(p => p.key.startsWith(run));
for (const p of policies) await api('PUT', `/escalation-policies/${p.key}`, { name: p.name, steps: p.steps, enabled: false });
await api('DELETE', '/webhooks/allowlist?destination=host.docker.internal');

console.log(`\nreceived: ${received.signed.length} signed, ${received.slack.length} slack, ${received.teams.length} teams, ${received.mail.length} mail`);
console.log('\nPROBLEMS:\n' + (problems.length ? [...new Set(problems)].join('\n') : 'none'));
await browser.close();
http.close();
smtp.close();
process.exit(problems.length ? 1 : 0);
