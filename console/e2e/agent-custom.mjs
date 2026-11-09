// Custom actions on the local stack, as a customer ships them: the sample agent build (samples/Juice.Observability.Agent.Sample) adds
// sample.file_info; it's published, signed with the release key and installed on the local agent with agent.update. Then the agent reports it,
// the platform offers it with the host's bounds (deploy/local/agent.json), an operator runs it, and what the host doesn't allow is refused.
//   node e2e/agent-custom.mjs
// Needs: the stack with the platform and agent profiles up and the local agent enrolled (node e2e/agent.mjs), deploy/local/scripts/init.sh's
// release key, and a platform image with action reports (docker compose --profile platform up -d --build platform-api).
import { readFileSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = fileURLToPath(new URL('../../', import.meta.url));
const local = join(repo, 'deploy', 'local');
const env = Object.fromEntries(readFileSync(join(local, '.env'), 'utf8').split('\n').filter(l => l.includes('=') && !l.startsWith('#'))
  .map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const base = process.env.BASE ?? 'http://localhost:5180';
const hostname = process.env.JUICE_HOST_NAME ?? 'local-docker-host';
const releaseKey = join(local, 'secrets', 'release', 'release.key');
const problems = [];
const step = (name) => console.log(new Date().toISOString().slice(11, 19), 'step', name);
const sleep = ms => new Promise(r => setTimeout(r, ms));
function run(command, args, cwd = repo) {
  const result = spawnSync(command, args, { cwd, encoding: 'utf8' });
  if (result.status !== 0) throw new Error(`${command} ${args.join(' ')}\n${result.stdout}\n${result.stderr}`);
  return result.stdout;
}
if (!existsSync(releaseKey)) throw new Error('no release key: run deploy/local/scripts/init.sh again');

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
const findAgent = async () => (await api('viewer', 'GET', agentsPath)).body.items.find(a => a.hostname === hostname);
let agent;
for (let i = 0; i < 60 && !(agent = await findAgent())?.online; i++) await sleep(1000);
if (!agent?.online) throw new Error('the local agent is not enrolled or not online: node e2e/agent.mjs');
console.log(`agent ${agent.id} on ${agent.hostname}, version ${agent.version}`);

async function command(who, action, parameters, expected) {
  const issued = await api(who, 'POST', `${agentsPath}/${agent.id}/commands`, { action, parameters });
  if (issued.status !== 201) { problems.push(`${action}: issue ${issued.status} ${JSON.stringify(issued.body)}`); return null; }
  for (let i = 0; i < 300; i++) {
    const c = (await api('viewer', 'GET', `${agentsPath}/${agent.id}/commands/${issued.body.id}`)).body;
    if (!['Pending', 'Delivered'].includes(c.state)) {
      console.log(`  ${action} ${JSON.stringify(parameters)} -> ${c.state}${c.reason ? ` (${c.reason})` : ''}${c.result ? ` ${JSON.stringify(c.result)}` : ''}`);
      if (c.state !== expected) problems.push(`${action}: ${c.state} ${c.reason ?? ''}, expected ${expected}`);
      return c;
    }
    await sleep(1000);
  }
  problems.push(`${action}: never answered`);
  return null;
}

step('package');
const version = `1.1.0-sample.${new Date().toISOString().replace(/\D/g, '').slice(0, 14)}`;
const work = mkdtempSync(join(tmpdir(), 'juice-agent-sample-'));
try {
  run('dotnet', ['publish', 'samples/Juice.Observability.Agent.Sample', '-c', 'Release', '-o', join(work, 'out'), '--no-self-contained', `-p:Version=${version}`, '-nodeReuse:false']);
  console.log(run('dotnet', ['run', 'deploy/agent/pack.cs', '--', '--publish', join(work, 'out'), '--key', releaseKey, '--out', join(local, 'packages')]).trim());
} finally {
  rmSync(work, { recursive: true, force: true });
}

step('install');
await command('admin', 'agent.update', { version }, 'Succeeded');
for (let i = 0; i < 60 && (agent = await findAgent())?.version !== version; i++) await sleep(1000);
if (agent?.version !== version) problems.push(`the agent reports ${agent?.version}, expected ${version}`);

step('reported');
let actions;
for (let i = 0; i < 30; i++) {
  actions = (await api('viewer', 'GET', `${agentsPath}/${agent.id}/actions`)).body;
  if (actions.items.some(a => a.name === 'sample.file_info')) break;
  await sleep(1000);
}
const custom = actions.items.find(a => a.name === 'sample.file_info');
console.log(`  reported ${actions.reportedAt}: ${actions.items.map(a => `${a.name}${a.custom ? ' (custom)' : ''}${a.permitted ? '' : ' (not permitted)'}`).join(', ')}`);
if (!custom) problems.push('sample.file_info was not reported');
else {
  if (!custom.custom || !custom.permitted) problems.push(`sample.file_info: custom ${custom.custom}, permitted ${custom.permitted}`);
  const allowed = custom.parameters.find(p => p.name === 'path')?.allowed ?? [];
  if (!allowed.includes('/hostfs/etc/os-release')) problems.push(`path isn't narrowed to the host's files: ${JSON.stringify(allowed)}`);
  console.log(`  limits: ${custom.limits.join('; ')}`);
}

step('run');
const os = await command('operator', 'sample.file_info', { path: '/hostfs/etc/os-release' }, 'Succeeded');
if (os?.result && os.result.exists !== true) problems.push(`/hostfs/etc/os-release: ${JSON.stringify(os.result)}`);
await command('operator', 'sample.file_info', { path: '/hostfs/var/log/dpkg.log' }, 'Succeeded');

step('refused');
const outside = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'sample.file_info', parameters: { path: '/hostfs/etc/shadow' } });
if (outside.status !== 400) problems.push(`a file the host doesn't list: ${outside.status}, expected 400`);
else console.log(`  /hostfs/etc/shadow -> 400 ${outside.body.errors?.[0] ?? outside.body.detail ?? ''}`);
const extra = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'sample.file_info', parameters: { path: '/hostfs/etc/hostname', command: 'cat' } });
if (extra.status !== 400) problems.push(`an extra parameter: ${extra.status}, expected 400`);
const byViewer = await api('viewer', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'sample.file_info', parameters: { path: '/hostfs/etc/hostname' } });
if (byViewer.status !== 403) problems.push(`a viewer: ${byViewer.status}, expected 403`);

console.log('\nPROBLEMS:\n' + (problems.length ? problems.join('\n') : 'none'));
process.exit(problems.length ? 1 : 0);
