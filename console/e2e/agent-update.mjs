// Phase 7, milestone 2 on the local stack: an admin updates the local agent to a signed package, the launcher keeps it once it reaches
// the platform; then to a broken package, which the launcher rolls back. Operators can't update agents.
//   node e2e/agent-update.mjs
// Needs: deploy/local/scripts/init.sh run since the release key was added (secrets/release), the stack with the platform profile up,
// and the local agent enrolled (node e2e/agent.mjs). It rebuilds the platform and agent images unless SKIP_BUILD=1.
import { readFileSync, writeFileSync, existsSync, mkdtempSync, cpSync, rmSync } from 'node:fs';
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

if (!existsSync(releaseKey)) throw new Error('no release key: run deploy/local/scripts/init.sh again (it adds secrets/release without touching the rest)');

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

if (process.env.SKIP_BUILD !== '1') {
  step('build-images');
  run('docker', ['compose', '--profile', 'platform', '--profile', 'agent', 'up', '-d', '--build', '--wait', 'platform-api'], local);
  run('docker', ['compose', '--profile', 'platform', '--profile', 'agent', 'up', '-d', '--build', 'agent'], local);
}

const tenant = (await api('viewer', 'GET', '/tenants')).body.items[0].id;
const agentsPath = `/tenants/${tenant}/agents`;
const findAgent = async () => (await api('viewer', 'GET', agentsPath)).body.items.find(a => a.hostname === hostname);
let agent;
for (let i = 0; i < 60 && !(agent = await findAgent())?.online; i++) await sleep(1000);
if (!agent?.online) throw new Error('the local agent is not enrolled or not online: node e2e/agent.mjs, then docker compose logs agent');
console.log(`agent ${agent.id} on ${agent.hostname}, version ${agent.version}`);

step('packages');
const stamp = new Date().toISOString().replace(/\D/g, '').slice(0, 14);
const good = `1.1.0-demo.${stamp}`;
const broken = `1.1.0-broken.${stamp}`;
const work = mkdtempSync(join(tmpdir(), 'juice-agent-update-'));
try {
  run('dotnet', ['publish', 'src/Juice.Observability.Agent', '-c', 'Release', '-o', join(work, 'good'), '--no-self-contained', `-p:Version=${good}`, '-nodeReuse:false']);
  // The same program with its assembly replaced by junk: signed and well formed, but it can't start. The launcher must go back.
  cpSync(join(work, 'good'), join(work, 'broken'), { recursive: true });
  writeFileSync(join(work, 'broken', 'juice-agent.dll'), 'this is not an assembly');
  writeFileSync(join(work, 'broken', 'manifest.json'), JSON.stringify({ version: broken, entryPoint: 'juice-agent.dll' }));
  for (const name of ['good', 'broken']) {
    console.log(run('dotnet', ['run', 'deploy/agent/pack.cs', '--', '--publish', join(work, name), '--key', releaseKey, '--out', join(local, 'packages')]).trim());
  }
} finally {
  rmSync(work, { recursive: true, force: true });
}
const offered = (await api('viewer', 'GET', `${agentsPath}/packages`)).body.items.map(p => p.version);
for (const version of [good, broken]) if (!offered.includes(version)) problems.push(`the platform doesn't offer ${version}: ${offered.join(', ')}`);

step('operators-cant');
const byOperator = await api('operator', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'agent.update', parameters: { version: good } });
if (byOperator.status !== 403) problems.push(`an operator issued an update: ${byOperator.status}`);

async function update(version, expected) {
  const issued = await api('admin', 'POST', `${agentsPath}/${agent.id}/commands`, { action: 'agent.update', parameters: { version } });
  if (issued.status !== 201) { problems.push(`update to ${version}: ${issued.status} ${JSON.stringify(issued.body)}`); return null; }
  console.log(`  issued ${issued.body.id}: ${version}, sha256 ${issued.body.parameters.sha256.slice(0, 16)}...`);
  for (let i = 0; i < 300; i++) {
    const command = (await api('viewer', 'GET', `${agentsPath}/${agent.id}/commands/${issued.body.id}`)).body;
    if (!['Pending', 'Delivered'].includes(command.state)) {
      console.log(`  -> ${command.state}${command.reason ? ` (${command.reason})` : ''}${command.result ? ` ${JSON.stringify(command.result)}` : ''}`);
      if (command.state !== expected) problems.push(`update to ${version}: ${command.state} ${command.reason ?? ''}, expected ${expected}`);
      return command;
    }
    await sleep(1000);
  }
  problems.push(`update to ${version}: never answered`);
  return null;
}
async function version() {
  for (let i = 0; i < 60; i++) {
    const now = await findAgent();
    if (now?.online) return now.version;
    await sleep(1000);
  }
  return null;
}

step('update');
await update(good, 'Succeeded');
const afterGood = await version();
console.log(`  the agent reports version ${afterGood}`);
if (afterGood !== good) problems.push(`after the update the agent reports ${afterGood}, expected ${good}`);

step('rollback');
const rolledBack = await update(broken, 'Failed');
if (rolledBack && !rolledBack.reason?.startsWith('rolled_back')) problems.push(`the broken update failed for another reason: ${rolledBack.reason}`);
const afterBroken = await version();
console.log(`  the agent reports version ${afterBroken}`);
if (afterBroken !== good) problems.push(`after the rollback the agent reports ${afterBroken}, expected ${good}`);

console.log('\nlauncher log:\n' + run('docker', ['compose', '--profile', 'agent', 'logs', '--no-log-prefix', 'agent'], local).split('\n').filter(l => l.includes('launcher:')).slice(-8).join('\n'));
console.log('\nPROBLEMS:\n' + (problems.length ? problems.join('\n') : 'none'));
process.exit(problems.length ? 1 : 0);
