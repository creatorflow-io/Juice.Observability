// The scale tests' query mix (docs/phase-10-plan.md P4), the same at every step, as erin (reads only). Not victor: tests/breach-to-alert.sh
// signs in as victor alongside, and Keycloak's quick-login check locks a user out for a minute after two password sign-ins less than a
// second apart (seen in the second smoke run).
//   - API reads at 20/s, open loop: inventory, health, alerts, rules and SLOs (requirements §9: p95 ≤ 500 ms);
//   - a trace search and a log search every second, over the last hour (plan: "Measures and targets"; no §9 target);
//   - one Console journey a minute in a signed-in browser: Overview, Services, a service, Alerts, SLOs, Traces of a service.
//   node scale/query-mix.mjs --duration 1800 --out dir    (or --until <epoch seconds>: stop then, whenever it started)
// Writes queries.csv (time, class, path, status, ms), journey.csv (time, page, ms, ok) and queries.json (per class: count, errors, p50,
// p95, max). Needs the scale stack's API on localhost:5180.
import { writeFileSync, appendFileSync, mkdirSync } from 'node:fs';
import { base, launch, signIn, tokenFor } from '../../../console/e2e/lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, x, i, all) => (x.startsWith('--') ? [...a, [x.slice(2), all[i + 1]]] : a), []));
const duration = Number(args.duration ?? 60), out = args.out ?? '.', namespace = args.namespace ?? 'shop';
mkdirSync(out, { recursive: true });
writeFileSync(`${out}/queries.csv`, 'time,class,path,status,ms\n');
writeFileSync(`${out}/journey.csv`, 'time,page,ms,ok\n');
const api = `${base}/api/v1`;
const sleep = ms => new Promise(r => setTimeout(r, ms));

// A token renewed every 4 minutes (they live 5) on a timer, so no timed read waits for Keycloak and the reads don't all fetch one at once.
const USER = 'erin';
// Refused (a lockout, Keycloak still starting): retried every 5 s for up to 3 minutes.
async function token() {
  for (let i = 0; ; i++) {
    try { return await tokenFor(USER); } catch (e) { if (i >= 36) throw e; console.error(`token: ${e.message}, retrying`); await new Promise(r => setTimeout(r, 5000)); }
  }
}
let bearer = await token();
setInterval(async () => { try { bearer = await token(); } catch (e) { console.error(`token: ${e.message}`); } }, 240000).unref();
const auth = async () => ({ authorization: `Bearer ${bearer}` });
const get = async path => (await fetch(`${api}${path}`, { headers: await auth() })).json();

const tenant = (await get('/tenants')).items[0].id;
const t = p => `/tenants/${tenant}${p}`;
// The shop's services (generate.mjs) and the SLOs, once they exist: discovery may still be catching up when the run starts.
let services = [], slos = [];
for (let i = 0; i < 30 && (services.length < (namespace === 'shop' ? 10 : 1) || slos.length < (namespace === 'shop' ? 50 : 1)); i++) {
  services = ((await get(t('/resources?type=service&limit=200'))).items ?? []).filter(s => (s.key ?? '').startsWith(`${namespace}/`));
  slos = ((await get(t('/slos?limit=200'))).items ?? []).map(s => s.key);
  if (services.length < (namespace === 'shop' ? 10 : 1) || slos.length < (namespace === 'shop' ? 50 : 1)) await sleep(10000);
}
console.log(`query mix until ${new Date(args.until ? Number(args.until) * 1000 : Date.now() + duration * 1000).toISOString()}: ${services.length} ${namespace} services, ${slos.length} SLOs`);
if (services.length === 0) { console.error('no shop services: is the generator running?'); process.exit(1); }

const reads = i => {
  const svc = services[i % services.length].id, slo = slos[i % Math.max(1, slos.length)];
  return [
    t('/applications'), t('/resources?limit=50'), t('/health?limit=50'), t('/alerts?limit=50'), t('/alerts?state=firing&limit=50'),
    t('/topology'), t('/rules'), t('/slos?limit=50'), slo ? t(`/slos/${encodeURIComponent(slo)}`) : t('/slos'), t('/health?subjectType=service&state=unhealthy'),
    t(`/resources/${svc}`), t(`/health?subjectId=${svc}`),
  ][i % 12];
};
const since = () => new Date(Date.now() - 60 * 60000).toISOString();
const searches = i => (i % 2 === 0
  ? t(`/telemetry/traces?serviceId=${services[(i / 2) % services.length | 0].id}&from=${encodeURIComponent(since())}&limit=50`)
  : t(`/telemetry/logs?q=failed&from=${encodeURIComponent(since())}&limit=50`));

const results = { api: [], search: [], page: [] };
const errors = { api: 0, search: 0, page: 0 };
const lines = [];
const flush = () => { if (lines.length) appendFileSync(`${out}/queries.csv`, lines.splice(0).join('')); };
async function timed(cls, path) {
  const started = performance.now();
  try {
    const r = await fetch(`${api}${path}`, { headers: await auth() });
    await r.arrayBuffer();
    const ms = performance.now() - started;
    // An error answer counts as slower than any target, as no answer does: a fast 401 or 503 isn't a fast read.
    if (r.ok) results[cls].push(ms); else { results[cls].push(Infinity); errors[cls]++; }
    lines.push(`${new Date().toISOString()},${cls},${path.replace(/,/g, ';')},${r.status},${ms.toFixed(1)}\n`);
  } catch (e) {
    // No answer at all (status 0): why goes in the log, since the CSV can't say.
    // Counted as slower than any target, so a read that never answers can't drop out of the percentile.
    results[cls].push(Infinity); errors[cls]++; lines.push(`${new Date().toISOString()},${cls},${path.replace(/,/g, ';')},0,${(performance.now() - started).toFixed(1)}\n`);
    console.error(`${cls} ${path}: ${e.cause?.code ?? e.cause?.message ?? e.message}`);
  }
}

// The browser journey, once a minute.
async function journeys(until) {
  const problems = [];
  const browser = await launch();
  // Page errors and API answers of 400 or more seen by the browser count against the page they happened on.
  let pageProblems = 0;
  const page = await signIn(browser, USER, problems, () => { pageProblems++; });
  problems.length = 0;
  const svc = services[0];
  const pages = [['overview', '/'], ['services', '/services'], ['service', `/services/${svc.id}`], ['alerts', '/alerts'], ['slos', '/slos'], ['traces', `/traces?service=${svc.id}`]];
  while (Date.now() < until - 60000) {
    const minute = Date.now();
    for (const [name, url] of pages) {
      const started = performance.now();
      let ok = true; pageProblems = 0; const before = problems.length;
      try { await page.goto(`${base}${url}`, { timeout: 30000 }); await page.waitForLoadState('networkidle', { timeout: 30000 }); }
      catch { ok = false; }
      if (pageProblems || problems.length > before) ok = false;
      if (!ok) errors.page++;
      const ms = performance.now() - started;
      results.page.push(ms);
      appendFileSync(`${out}/journey.csv`, `${new Date().toISOString()},${name},${ms.toFixed(0)},${ok}\n`);
    }
    await sleep(Math.max(0, 60000 - (Date.now() - minute)));
  }
  await browser.close();
}

const started = Date.now(), until = args.until ? Number(args.until) * 1000 : started + duration * 1000;
// A browser that can't start or sign in is a failed journey, not a crashed run.
const browsing = journeys(until).catch(e => { console.error(`journey: ${e.message}`); errors.page++; results.page.push(Infinity); });
const pending = [];
for (let i = 0; Date.now() < until; i++) {
  // 20 API reads and 2 searches each second, on a fixed schedule (open loop: a slow answer doesn't slow the offered rate).
  const due = started + (i * 1000) / 20;
  await sleep(Math.max(0, due - Date.now()));
  pending.push(timed('api', reads(i)));
  if (i % 10 === 0) pending.push(timed('search', searches(i / 10)));
  if (i % 200 === 0) { flush(); pending.splice(0, pending.length - 500); }
}
await Promise.all(pending);
await browsing;
flush();

// A percentile that falls on a read with no answer says so (JSON has no Infinity).
const q = (list, p) => { const s = [...list].sort((a, b) => a - b); if (!s.length) return null; const v = s[Math.min(s.length - 1, Math.ceil(s.length * p) - 1)]; return Number.isFinite(v) ? Number(v.toFixed(1)) : 'no answer'; };
const summary = Object.fromEntries(Object.entries(results).map(([k, v]) => [k, { count: v.length, errors: errors[k], p50: q(v, 0.5), p95: q(v, 0.95), max: q(v, 1) }]));
writeFileSync(`${out}/queries.json`, JSON.stringify(summary, null, 2));
for (const [k, s] of Object.entries(summary)) console.log(`${k}: ${s.count} (${s.errors} errors), p50 ${s.p50} ms, p95 ${s.p95} ms, max ${s.max} ms`);
