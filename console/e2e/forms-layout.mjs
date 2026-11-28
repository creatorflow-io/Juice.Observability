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
    // An action with a boolean parameter (a checkbox with a long label), when the agent offers one.
    await page.getByTestId('command-action').click();
    const cleanup = page.getByTestId('action-files.cleanup');
    if (await cleanup.count()) await cleanup.click(); else await page.keyboard.press('Escape');
  } },
  rules: { path: 'rules', open: 'rule-new', form: 'rule-form' },
  escalation: { path: 'escalation', open: 'policy-new', form: null },
};

/**
 * In the page: every field box in a row starts at the same top. The fill containers (not the hints or errors under them) of the fields that
 * share a row (the same form, overlapping vertically) are compared; the result lists each row's spread.
 */
const rowSpread = ({ formId }) => {
  const form = document.querySelector(`[data-testid="${formId}"]`) ?? document.querySelector('form.jc-form, form') ?? document;
  const fills = [...form.querySelectorAll('mat-form-field')].filter(f => f.getClientRects().length > 0)
    .map(f => ({ r: (f.querySelector('.mdc-text-field') ?? f).getBoundingClientRect(), label: f.querySelector('mat-label')?.textContent.trim() }))
    .sort((a, b) => a.r.top - b.r.top);
  const rows = [];
  for (const x of fills) {
    const row = rows.find(r => x.r.top < r[0].r.bottom && x.r.bottom > r[0].r.top);
    if (row) row.push(x); else rows.push([x]);
  }
  return rows.filter(r => r.length > 1).map(r => ({
    spread: Math.round((Math.max(...r.map(x => x.r.top)) - Math.min(...r.map(x => x.r.top))) * 10) / 10,
    tops: r.map(x => `${x.label} ${Math.round(x.r.top)}`).join(', '),
  }));
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
  let rowsChecked = 0;
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
        // A checkbox's label (its first line, when it wraps) sits level with the box.
        const labels = await page.evaluate(id => [...(document.querySelector(`[data-testid="${id}"]`) ?? document).querySelectorAll('mat-checkbox')]
          .filter(cb => cb.getClientRects().length > 0).map(cb => {
            const box = cb.querySelector('.mdc-checkbox__background').getBoundingClientRect();
            // The first line of the label's own text (the first text node with something in it), not its box.
            const walker = document.createTreeWalker(cb.querySelector('label'), NodeFilter.SHOW_TEXT);
            let text;
            while ((text = walker.nextNode()) && !text.textContent.trim()) { /* skip whitespace */ }
            const range = document.createRange();
            range.selectNodeContents(text);
            const line = [...range.getClientRects()].find(r => r.width > 0);
            return { text: cb.textContent.trim().slice(0, 30), delta: Math.round((line.top + line.height / 2 - (box.top + box.height / 2)) * 10) / 10 };
          }), f.form);
        const off = labels.filter(l => Math.abs(l.delta) > TOLERANCE);
        check(off.length === 0, `${name} at ${width}px, ${theme}: ${labels.length} checkbox label(s) level with their box${off.length ? ` (off: ${off.map(l => `${l.text} ${l.delta}px`).join(', ')})` : ''}`);
        for (const withErrors of [false, true]) {
          const rows = await page.evaluate(measure, { withErrors, formId: f.form });
          const at = `${name} at ${width}px, ${theme}${withErrors ? ', neighbours with errors' : ''}`;
          const spreads = await page.evaluate(rowSpread, { formId: f.form });
          const ragged = spreads.filter(s => s.spread > TOLERANCE);
          rowsChecked += spreads.length;
          check(ragged.length === 0, `${at}: ${spreads.length} row(s) of fields start at one top${ragged.length ? ` (ragged: ${ragged.map(s => s.tops).join(' | ')})` : ''}`);
          const bad = rows.filter(r => Math.abs(r.delta) > TOLERANCE);
          compared += rows.length;
          check(bad.length === 0, `${at}: ${rows.length} checkbox/field pair(s) on one row${bad.length ? `, off by ${bad.map(r => `${r.box} ${r.delta}px`).join(', ')}` : ''}`);
        }
      }
    }
  }
  check(rowsChecked > 0, `fields shared a row (${rowsChecked} rows)`);
  check(compared > 0,`at least one checkbox shared a row with a field (${compared} pairs)`);
} finally {
  await browser.close();
}
finish(problems);
