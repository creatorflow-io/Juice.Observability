import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { EMPTY, Observable, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_OPTIONS } from '@juice-js/observability';
import { RulesPage } from '../../projects/juice-js/observability/src/lib/pages/rules.page';
import { ruleFieldErrors } from '../../projects/juice-js/observability/src/lib/shared/form-errors';

const problem = (status: number, title: string, detail: string) => ({ status, error: { title, detail } });

function setup(api: Record<string, (...args: never[]) => Observable<unknown>>) {
  const calls: Record<string, unknown[][]> = {};
  const fake = new Proxy({}, {
    get: (_, name: string) => name === 'tenant$' ? of({ id: 't1' }) : (...args: unknown[]) => {
      (calls[name] ??= []).push(args);
      return (api[name] as ((...a: unknown[]) => Observable<unknown>) | undefined)?.(...args) ?? EMPTY;
    },
  });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: fake },
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: () => true },
      { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink: null } }],
  });
  return calls;
}
const list = () => of({ items: [], nextCursor: null });
const base = { rules: () => of({ items: [], nextCursor: null }), allAlerts: list, signalPacks: () => of([]) };

type Fx = { nativeElement: HTMLElement; whenStable(): Promise<unknown>; detectChanges(): void };
const flush = async (f: Fx) => { f.detectChanges(); await f.whenStable(); f.detectChanges(); };
const click = (f: Fx, id: string) => (f.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement).click();
const type = async (f: Fx, name: string, value: string) => {
  const input = f.nativeElement.querySelector(`input[name="${name}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flush(f);
};
const fieldErrors = (f: Fx) => Array.from(f.nativeElement.querySelectorAll('[data-testid="rule-field-error"]')) as HTMLElement[];
const marked = (f: Fx) => fieldErrors(f).map(e => e.dataset['field']).sort();
const under = (f: Fx, field: string) => fieldErrors(f).filter(e => e.dataset['field'] === field && e.closest('.jc-field'));

async function openNew() {
  const calls = setup({ ...base, createRule: () => EMPTY });
  const fixture = TestBed.createComponent(RulesPage);
  await flush(fixture);
  click(fixture, 'rule-new');
  await flush(fixture);
  return { fixture, calls };
}
async function fillValid(f: Fx) {
  await type(f, 'key', 'my-rule');
  await type(f, 'name', 'My rule');
  await type(f, 'threshold', '0.5');
}

describe('ruleFieldErrors', () => {
  it('places $.Threshold, $[\'recoveryThreshold\'] and a trailing dot; an unknown path stays in one sentence', () => {
    const a = ruleFieldErrors(problem(400, 'Invalid request', 'Unreadable definition at $.Threshold.'));
    expect(Object.keys(a.fields)).toEqual(['threshold']);
    expect(a.rest).toBe('');
    const b = ruleFieldErrors(problem(400, 'Invalid request', "Unreadable definition at $['recoveryThreshold']."));
    expect(Object.keys(b.fields)).toEqual(['recoveryThreshold']);
    const c = ruleFieldErrors(problem(400, 'Invalid request', 'Unreadable definition at $.nothingLikeIt.'));
    expect(c.fields).toEqual({});
    expect(c.rest).toBe('Invalid request: Unreadable definition at $.nothingLikeIt.');
  });
  it('reads several paths in one message', () => {
    const r = ruleFieldErrors(problem(400, 'Invalid request', 'Unreadable definition at $.threshold and $.window.'));
    expect(Object.keys(r.fields).sort()).toEqual(['threshold', 'window']);
    expect(r.fields['threshold']).toEqual(['Threshold: enter a number.']);
  });
  it('403, 412 and status 0 are one sentence, no field', () => {
    for (const s of [403, 412, 0]) {
      const r = ruleFieldErrors(problem(s, 'Nope', 'Unreadable definition at $.threshold.'));
      expect(r.fields).toEqual({});
      expect(r.rest).not.toBe('');
    }
  });
});

describe('rules form', () => {
  it('400 "Unreadable definition at $.threshold." shows under Threshold and not beside Save', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.threshold.')) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    expect(under(f, 'threshold').map(e => e.textContent!.trim())).toEqual(['Threshold: enter a number.']);
    expect(f.nativeElement.querySelector('[data-testid="rule-error"]')).toBeNull();
    expect(f.nativeElement.querySelector('.jc-form-actions [data-testid="rule-error"]')).toBeNull();
  });

  it('two paths in one message mark both fields', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.threshold and $.window.')) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    expect(marked(f)).toEqual(['threshold', 'window']);
    expect(under(f, 'window')).toHaveLength(1);
  });

  it('an unknown path stays in one form sentence', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.mystery.')) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    expect(marked(f)).toEqual([]);
    expect(f.nativeElement.querySelector('[data-testid="rule-error"]')?.textContent).toContain('$.mystery');
  });

  it('a 400 naming $.template and $.service shows both, each under its field', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.template and $.service.')) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    expect(marked(f)).toEqual(['service', 'template']);
    expect(under(f, 'template')).toHaveLength(1);
  });

  it('a field this form is not showing (host on ErrorRate, key while editing) is never lost: it goes to the form sentence', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.host.')) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    expect(marked(f)).toEqual([]);
    expect(f.nativeElement.querySelector('[data-testid="rule-error"]')?.textContent).toContain('Host');

    const rule = { key: 'r1', name: 'R1', template: 'ErrorRate', severity: 'Warning', enabled: true, managedBy: 'api', etag: 'e', definition: { threshold: 1 } };
    setup({ ...base, rules: () => of({ items: [rule], nextCursor: null }), updateRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.key.')) });
    const g = TestBed.createComponent(RulesPage);
    await flush(g);
    click(g, 'rule-edit-r1');
    await flush(g);
    click(g, 'rule-save');
    await flush(g);
    expect(marked(g)).toEqual([]);
    expect(g.nativeElement.querySelector('[data-testid="rule-error"]')?.textContent).toContain('Key');
  });

  it('the first invalid control is focused, a select included', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.template and $.threshold.')) });
    const f = TestBed.createComponent(RulesPage);
    document.body.appendChild(f.nativeElement);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    await new Promise(r => setTimeout(r, 10));
    expect(document.activeElement?.closest('mat-select')?.getAttribute('name')).toBe('template');
    f.nativeElement.remove();
  });

  it('an error on Enabled focuses the real checkbox input', async () => {
    setup({ ...base, createRule: () => throwError(() => problem(400, 'Invalid request', 'Unreadable definition at $.enabled.')) });
    const f = TestBed.createComponent(RulesPage);
    document.body.appendChild(f.nativeElement);
    await flush(f);
    click(f, 'rule-new');
    await flush(f);
    await fillValid(f);
    click(f, 'rule-save');
    await flush(f);
    await new Promise(r => setTimeout(r, 10));
    const active = document.activeElement as HTMLInputElement;
    expect(active.tagName).toBe('INPUT');
    expect(active.type).toBe('checkbox');
    f.nativeElement.remove();
  });

  it('empty key, name and threshold are all marked in one submit, nothing is sent', async () => {
    const { fixture, calls } = await openNew();
    click(fixture, 'rule-save');
    await flush(fixture);
    expect(marked(fixture)).toEqual(['key', 'name', 'threshold']);
    expect(under(fixture, 'key')[0].textContent).toContain('Key');
    expect(calls['createRule']).toBeUndefined();
    expect(fixture.nativeElement.querySelector('input[name="key"]')?.getAttribute('aria-invalid')).toBe('true');
    expect(fixture.nativeElement.querySelector('input[name="key"]')?.getAttribute('aria-describedby')).toBe('err-key');
  });

  it('errors clear as the field is edited', async () => {
    const { fixture } = await openNew();
    click(fixture, 'rule-save');
    await flush(fixture);
    await type(fixture, 'key', 'abc');
    expect(marked(fixture)).toEqual(['name', 'threshold']);
  });

  it('a 403, a 412 (etag) and a 0 status show one form sentence, no field mark', async () => {
    for (const status of [403, 412, 0]) {
      setup({ ...base, createRule: () => throwError(() => problem(status, 'Refused', 'Something about the whole save.')) });
      const f = TestBed.createComponent(RulesPage);
      await flush(f);
      click(f, 'rule-new');
      await flush(f);
      await fillValid(f);
      click(f, 'rule-save');
      await flush(f);
      expect(marked(f)).toEqual([]);
      expect(f.nativeElement.querySelectorAll('[data-testid="rule-error"]')).toHaveLength(1);
    }
  });

  it('labels are not clipped: a label that must fit one grid column stays short, longer ones sit in a wide field', async () => {
    // jsdom has no layout, so this guards the cause of the clipping: a label longer than a column (200px at 16px type is about 22 characters).
    const { fixture } = await openNew();
    const fields = Array.from(fixture.nativeElement.querySelectorAll('.jc-form mat-form-field')) as HTMLElement[];
    expect(fields.length).toBeGreaterThan(10);
    for (const field of fields) {
      const label = field.querySelector('mat-label')!.textContent!.trim();
      const wide = field.classList.contains('jc-field-wide');
      expect(label.length, label).toBeLessThanOrEqual(wide ? 48 : 22);
    }
  });
});
