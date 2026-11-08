// Phase 7 on the local stack: an admin creates an enrollment token, a containerized Juice Agent enrolls with it, and an operator
// runs each read-only diagnostic. What the agent's local allowlist (deploy/local/agent.json) doesn't permit is refused by the agent,
// though the platform signed it.
//   node e2e/agent.mjs              enroll if needed, then run the checks
//   REVOKE=1 node e2e/agent.mjs     ...and revoke the agent at the end
// Needs the stack with the platform profile up (deploy/local).
import { readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const local = new URL('../../deploy/local/', import.meta.url);
const env = Object.fromEntries(readFileSync(new URL('.env', local), 'utf8').split('\n').filter(l => l.includes('=') && !l.startsWith('#'))
  .map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const problems = [];
const step = (name) => console.log(new Date().toISOString().slice(11, 19), 'step', name);
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function token(user) {
  const res = await fetch('http://localhost:8080/realms/juice/protocol/openid-connect/token', {
    method: 'POST', body: new URLSearchParams({ grant_type: 'password', client_id: 'juice-platform-cli', username: user, password: env.KEYCLOAK_DEMO_PASSWORD }),
  });
  return (await res.json()).access_token;
}
const tokens = { admin: await token('alice'), operator: await token('erin'), viewer: await token('victor') };
async function api(who, method, path, body) {
  const res = await fetch(`${base}/api/v1${path}`, {
    method, headers: { Authorization: `Bearer ${tokens[who]}`, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}
const tenant = (await api('viewer', 'GET', '/tenants')).body.items[0].id;
const agentsPath = `/tenants/${tenant}/agents`;

step('enroll');
let agent = (await api('viewer', 'GET', agentsPath)).body.items.find(a => a.hostname === (process.env.JUICE_HOST_NAME ?? 'local-docker-host'));
if (!agent) {
  const created = await api('admin', 'POST', `${agentsPath}/enrollment-tokens`, { purpose: 'local agent', expiresInMinutes: 15 });
  if (created.status !== 201) throw new Error(`enrollment token: ${created.status} ${JSON.stringify(created.body)}`);
  writeFileSync(new URL('secrets/agent-enrollment-token', local), created.body.token);
  const up = spawnSync('docker', ['compose', '--profile', 'platform', '--profile', 'agent', 'up', '-d', '--build', 'agent'], { cwd: local, encoding: 'utf8' });
  if (up.status !== 0) throw new Error(up.stderr);
  for (let i = 0; i < 60 && !agent; i++) {
    await sleep(1000);
    agent = (await api('viewer', 'GET', agentsPath)).body.items.find(a => a.hostname === (process.env.JUICE_HOST_NAME ?? 'local-docker-host'));
  }
  if (!agent) throw new Error('the agent never enrolled: docker compose logs agent');
}
console.log(`agent ${agent.id} on ${agent.hostname}, ${agent.operatingSystem}, version ${agent.version}`);

async function run(action, parameters, expected, reason) {
  const issued = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, { action, parameters });
  if (issued.status !== 201) { problems.push(`${action}: issue ${issued.status} ${JSON.stringify(issued.body)}`); return null; }
  for (let i = 0; i < 60; i++) {
    const command = (await api('viewer', 'GET', `${agentsPath}/${agent.id}/commands/${issued.body.id}`)).body;
    if (!['Pending', 'Delivered'].includes(command.state)) {
      if (command.state !== expected || (reason && command.reason !== reason)) problems.push(`${action} ${JSON.stringify(parameters)}: ${command.state} ${command.reason ?? ''}, expected ${expected} ${reason ?? ''}`);
      console.log(`  ${action} ${JSON.stringify(parameters)} -> ${command.state}${command.reason ? ` (${command.reason})` : ''} in ${command.durationMs ?? '-'} ms`);
      return command;
    }
    await sleep(500);
  }
  problems.push(`${action}: never answered`);
  return null;
}

step('allowed');
const summary = await run('host.summary', {}, 'Succeeded');
if (summary?.result) console.log(`  ${summary.result.hostname}: ${summary.result.processorCount} CPUs, load ${summary.result.loadAverage}, memory ${Math.round(summary.result.memoryAvailableBytes / 1e9)} of ${Math.round(summary.result.memoryTotalBytes / 1e9)} GB available`);
const disk = await run('disk.usage', { pathPrefix: '/hostfs' }, 'Succeeded');
if (disk?.result) console.log(`  ${disk.result.filesystems.length} filesystems under /hostfs`);
const tcp = await run('tcp.reachability', { host: 'postgres', port: 5432, timeoutMs: 2000 }, 'Succeeded');
if (tcp?.result && !tcp.result.reachable) problems.push(`postgres:5432 not reachable: ${tcp.result.error}`);

step('refused-by-the-host');
await run('tcp.reachability', { host: 'example.com', port: 443, timeoutMs: 2000 }, 'Rejected', 'outside_local_bounds');
await run('tcp.reachability', { host: 'postgres', port: 5432, timeoutMs: 8000 }, 'Rejected', 'outside_local_bounds');
await run('disk.usage', { pathPrefix: '/etc' }, 'Rejected', 'outside_local_bounds');

step('refused-by-the-platform');
for (const [body, status] of [[{ action: 'shell.exec', parameters: { command: 'id' } }, 400], [{ action: 'tcp.reachability', parameters: { host: 'postgres', port: 70000, timeoutMs: 1000 } }, 400]]) {
  const res = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, body);
  if (res.status !== status) problems.push(`${JSON.stringify(body)}: ${res.status}, expected ${status}`);
}
const viewerIssue = await api('viewer', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'host.summary' });
if (viewerIssue.status !== 403) problems.push(`a viewer issued a command: ${viewerIssue.status}`);

if (process.env.REVOKE === '1') {
  step('revoke');
  await api('admin', 'POST', `${agentsPath}/${agent.id}/revocation`, { reason: 'local check finished' });
  const after = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'host.summary' });
  if (after.status !== 409) problems.push(`a revoked agent took a command: ${after.status}`);
}

console.log('\nPROBLEMS:\n' + (problems.length ? problems.join('\n') : 'none'));
process.exit(problems.length ? 1 : 0);
