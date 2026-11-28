// M56: a checkbox in a form is top-anchored, so its box lines up with the text line of the field beside it (the fill-appearance field's input line), and
// stays put when that field shows an error below. For the forms that have a checkbox (silences, rules, escalation selections, and the agent
// form's request fields when an agent is enrolled), at 1440, 1024 and 390 px, light and dark: each checkbox that shares a row with a field
// has its box centre within 2 px of that field's input centre, pristine and with a tall error under every neighbour.
//
//   CONSOLE_DIST=dist/juice-console/browser node e2e/forms-layout.mjs      (the local stack up)
//
// Exits 0 with "skipped" when the stack (or deploy/local/.env) isn't there.
const skip = reason => { console.log(`skipped: ${reason}`); process.exit(0); };
let lib;
try { lib = await import('./lib.mjs'); } catch (e) { skip(`the local stack's deploy/local/.env can't be read (${e.code ?? e.message})`); }
const { launch, signIn, base, keycloak, finish } = lib;
const reachable = async url => { try { return (await fetch(url, { signal: AbortSignal.timeout(4000) })).status < 500; } catch { return false; } };
if (!await reachable(`${keycloak}/realms/juice`) || !await reachable(base)) skip(`the local stack or the Console at ${base} isn't up`);

const WIDTHS = [1440, 1024, 390];
const THEMES = ['light', 'dark'];
const TOLERANCE = 2;

/** Each form: where it is, how it is opened, and what to do before measuring (a checkbox that only shows in some state). */
const forms = {
  silences: { path: 'silences', form: 'silence-form', prepare: async page => {
    await page.locator('[data-testid=silence-form] mat-select[name=kind]').click();
    await page.locator('mat-option', { hasText: 'Maintenance window' }).click();
  } },
  // The agent's "Ask the agent" form, on the first enrolled agent's page.
  agent: { path: 'agents', form: 'command-form', prepare: async page => {
    await page.locator('a[href*="/agents/"]').first().click({ timeout: 15000 });
    await page.getByTestId('command-form').waitFor({ timeout: 15000 });
  } },
  rules: { path: 'rules', open: 'rule-new', form: 'rule-form' },
  escalation: { path: 'escalation', open: 'policy-new', form: null },
};

/** In the page: every checkbox in the form's page that shares a row with a field, and how far its box centre is from that field's input centre. */
const measure = ({ withErrors, formId }) => {
  const root = formId ? document.querySelector(`[data-testid="${formId}"]`) ?? document : document;
  const form = root.tagName === 'FORM' ? root : document.querySelector('form.jc-form, form') ?? document;
  const visible = el => el.getClientRects().length > 0;
  const boxes = [...form.querySelectorAll('mat-checkbox')].filter(visible);
  const fields = [...form.querySelectorAll('mat-form-field')].filter(visible);
  if (withErrors) {
    for (const f of fields) {
      const wrap = f.closest('.jc-field') ?? f.parentElement;
      if (wrap.querySelector('.probe-error')) continue;
      const e = document.createElement('div');
      e.className = 'jc-field-error probe-error';
      e.textContent = 'A long error that wraps onto a second and a third line so the field wrapper grows well past its input.';
      wrap.appendChild(e);
    }
  }
  const out = [];
  for (const cb of boxes) {
    const control = cb.querySelector('.mdc-checkbox__background') ?? cb.querySelector('input');
    const c = control.getBoundingClientRect();
    const cy = c.top + c.height / 2;
    const cell = cb.closest('.jc-field') ?? cb;
    for (const f of fields) {
      const fr = f.getBoundingClientRect();
      const fieldCell = f.closest('.jc-field') ?? f;
      if (fieldCell === cell) continue;
      const cellTop = cell.getBoundingClientRect().top, fieldTop = fieldCell.getBoundingClientRect().top;
      if (Math.abs(cellTop - fieldTop) > 1) continue; // not the same row
      const input = f.querySelector('.mat-mdc-form-field-infix input, .mat-mdc-form-field-infix mat-select, .mat-mdc-form-field-infix') ?? f;
      const r = input.getBoundingClientRect();
      out.push({ box: cb.textContent.trim().slice(0, 30) || cb.getAttribute('name'), delta: Math.round((cy - (r.top + r.height / 2)) * 10) / 10, field: fr.width });
    }
  }
  return out;
};

const problems = [];
const check = (ok, what) => { if (!ok) problems.push(what); console.log(ok ? 'ok  ' : 'FAIL', what); };
const browser = await launch();
try {
  // The served build has no hub behind it (the stack's host answers its page): those console errors say nothing about the layout.
  const page = await signIn(browser, 'alice', problems, e => { if (!/negotiation|start the connection|WebSocket/.test(e)) problems.push(e); });
  let compared = 0;
  for (const [name, f] of Object.entries(forms)) {
    for (const width of WIDTHS) {
      for (const theme of THEMES) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(`${base}/${f.path}`);
        await page.evaluate(t => document.body.classList.toggle('dark', t === 'dark'), theme);
        if (f.open) await page.getByTestId(f.open).click({ timeout: 15000 });
        await page.locator('form').first().waitFor({ timeout: 15000 });
        if (f.prepare) await f.prepare(page);
        await page.waitForTimeout(300);
        // No label loses its tail to its field's width (at 390 px above all).
        const clipped = await page.evaluate(id => [...(document.querySelector(`[data-testid="${id}"]`) ?? document).querySelectorAll('mat-form-field label')]
          .filter(l => l.getClientRects().length > 0 && l.scrollWidth > l.clientWidth + 1).map(l => l.textContent.trim()), f.form);
        check(clipped.length === 0, `${name} at ${width}px, ${theme}: no label is clipped${clipped.length ? ` (${clipped.join(', ')})` : ''}`);
        for (const withErrors of [false, true]) {
          const rows = await page.evaluate(measure, { withErrors, formId: f.form });
          const at = `${name} at ${width}px, ${theme}${withErrors ? ', neighbours with errors' : ''}`;
          const bad = rows.filter(r => Math.abs(r.delta) > TOLERANCE);
          compared += rows.length;
          check(bad.length === 0, `${at}: ${rows.length} checkbox/field pair(s) on one row${bad.length ? `, off by ${bad.map(r => `${r.box} ${r.delta}px`).join(', ')}` : ''}`);
        }
      }
    }
  }
  check(compared > 0, `at least one checkbox shared a row with a field (${compared} pairs)`);
} finally {
  await browser.close();
}
finish(problems);
