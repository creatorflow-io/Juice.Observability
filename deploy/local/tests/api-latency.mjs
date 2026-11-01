// Platform API latency (requirements §9: p95 ≤ 500 ms for inventory/health/alert reads at 20 requests/s).
// Needs the stack and the API (scripts/run-api.sh).   node tests/api-latency.mjs [seconds]   (default 60)
import { readFileSync } from 'node:fs';

const env = Object.fromEntries(readFileSync(new URL('../.env', import.meta.url), 'utf8').split('\n')
  .filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).trim()]));
const seconds = Number(process.argv[2] ?? 60);
const rate = 20;
const api = 'http://localhost:5180/api/v1';

const tokenResponse = await fetch('http://localhost:8080/realms/juice/protocol/openid-connect/token', {
  method: 'POST',
  body: new URLSearchParams({ grant_type: 'password', client_id: 'juice-platform-cli', username: 'victor', password: env.KEYCLOAK_DEMO_PASSWORD }),
});
const headers = { authorization: `Bearer ${(await tokenResponse.json()).access_token}` };
const tenant = (await (await fetch(`${api}/tenants`, { headers })).json()).items[0].id;
const paths = ['/applications', '/resources?limit=50', '/health?limit=50', '/alerts?limit=50', '/topology', '/rules', '/health?subjectType=service&state=unhealthy']
  .map(p => `${api}/tenants/${tenant}${p}`);

const latencies = [];
let errors = 0;
const started = Date.now();
const pending = [];
for (let i = 0; i < seconds * rate; i++) {
  const due = started + (i * 1000) / rate;
  await new Promise(r => setTimeout(r, Math.max(0, due - Date.now())));
  const t = performance.now();
  pending.push(fetch(paths[i % paths.length], { headers }).then(async r => {
    await r.arrayBuffer();
    if (!r.ok) errors++;
    latencies.push(performance.now() - t);
  }, () => errors++));
}
await Promise.all(pending);
latencies.sort((a, b) => a - b);
const q = p => latencies[Math.min(latencies.length - 1, Math.ceil(latencies.length * p) - 1)].toFixed(1);
console.log(`${latencies.length} requests at ${rate}/s over ${seconds} s: p50 ${q(0.5)} ms, p95 ${q(0.95)} ms, max ${q(1)} ms, errors ${errors} (target p95 ≤ 500 ms)`);
process.exit(Number(q(0.95)) <= 500 && errors === 0 ? 0 : 1);
