import { HttpHeaders } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, EMPTY, Observable, Subject, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_CAN_OPERATE, OBSERVABILITY_OPTIONS } from '@juice-js/observability';
import { RulesPage } from '../../projects/juice-js/observability/src/lib/pages/rules.page';
import { ruleFieldErrors } from '../../projects/juice-js/observability/src/lib/shared/form-errors';

type Api = Record<string, (...args: never[]) => Observable<unknown>>;
const problem = (status: number, title: string, detail: string, extra: object = {}) => ({ status, error: { title, detail, ...extra } });

const catalog = of({
  truncated: false,
  items: [
    { name: 'system.memory.usage', type: 'gauge', unit: 'By', description: '' },
    { name: 'http.requests', type: 'sum', monotonic: true, unit: '1', description: '' },
    { name: 'queue.depth', type: 'sum', monotonic: false, unit: '1', description: '' },
    { name: 'http.duration', type: 'histogram', unit: 'ms', description: '' },
    { name: '<script>alert(1)</script>', type: 'gauge', unit: '', description: '' },
  ],
});
const page = <T>(items: T[]) => of({ items, nextCursor: null });
const base = { rules: () => page([]), allAlerts: () => page([]), signalPacks: () => of([]), metrics: () => catalog };

const metricRule = {
  key: 'm1', name: 'M1', template: 'MetricThreshold', severity: 'Warning', enabled: true, managedBy: 'api', etag: 'e1',
  definition: {
    threshold: 0.9, schedule: '00:01:00', window: '00:05:00', forDuration: '00:02:00', recoveryDuration: '00:02:00', cooldown: '00:10:00', minimumSamples: 10,
    metric: { name: 'system.memory.usage', kind: 'Gauge', aggregation: 'Avg', filters: [], subject: 'Host', comparison: 'Above' },
  },
};
const configRule = { ...metricRule, key: 'c1', name: 'C1', template: 'HostCpu', managedBy: 'config', definition: { ...metricRule.definition, metric: undefined } };
const platformRule = { ...metricRule, key: 'p1', name: 'P1', template: 'SloBurn', managedBy: 'platform', definition: { ...metricRule.definition, metric: undefined } };

const subject = (over: object = {}) => ({
  subjectKey: 'host-a', subjectType: 'Host', breaches: 5, noData: 1, wouldOpen: 1, wouldFire: 1, firstFiredAt: '2026-11-28T10:00:00Z', last: 9, min: 1, max: 9, lastDetail: null,
  points: [{ at: '2026-11-28T09:00:00Z', value: 1, condition: 'Recovered' }, { at: '2026-11-28T09:15:00Z', value: 9, condition: 'Breach' }, { at: '2026-11-28T09:30:00Z', value: null, condition: 'NoData' }],
  ...over,
});
const preview = (over: object = {}) => ({
  from: '2026-11-27T10:00:00Z', to: '2026-11-28T10:00:00Z', step: '00:15:00', points: 96, pointsEvaluated: 96, complete: true, cut: null, quiet: null, unit: 'By',
  definition: {}, subjects: [subject()], subjectsTruncated: false, ...over,
});

interface Options { admin?: boolean | (() => boolean); operate?: boolean | (() => boolean) }
const role = (v: boolean | (() => boolean) | undefined) => () => (typeof v === 'function' ? v() : (v ?? true));
function setup(api: Api, options: Options = {}) {
  const calls: Record<string, unknown[][]> = {};
  const tenant = new BehaviorSubject({ id: 't1' });
  const fake = new Proxy({}, {
    get: (_, name: string) => {
      if (name === 'tenant$') return tenant;
      return (...args: unknown[]) => {
        (calls[name] ??= []).push(args);
        return (api[name] as ((...a: unknown[]) => Observable<unknown>) | undefined)?.(...args) ?? EMPTY;
      };
    },
  });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: fake },
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: role(options.admin) },
      { provide: OBSERVABILITY_CAN_OPERATE, useValue: role(options.operate) },
      { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink: null } }],
  });
  return { calls, tenant };
}

type Fx = { nativeElement: HTMLElement; whenStable(): Promise<unknown>; detectChanges(): void };
const flush = async (f: Fx) => { f.detectChanges(); await f.whenStable(); f.detectChanges(); };
const id = (f: Fx, testid: string) => f.nativeElement.querySelector(`[data-testid="${testid}"]`) as HTMLElement | null;
const click = async (f: Fx, testid: string) => { id(f, testid)!.click(); await flush(f); };
const type = async (f: Fx, name: string, value: string) => {
  const input = f.nativeElement.querySelector(`input[name="${name}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flush(f);
};
const choose = async (f: Fx, name: string, text: string) => {
  const select = f.nativeElement.querySelector(`mat-select[name="${name}"]`) as HTMLElement;
  (select.querySelector('.mat-mdc-select-trigger') as HTMLElement).click();
  await flush(f);
  const option = Array.from(document.querySelectorAll('mat-option')).find(o => o.textContent!.trim() === text) as HTMLElement;
  option.click();
  await flush(f);
};
const has = (f: Fx, selector: string) => f.nativeElement.querySelector(selector) !== null;
const marked = (f: Fx) => (Array.from(f.nativeElement.querySelectorAll('[data-testid="rule-field-error"]')) as HTMLElement[]).map(e => e.dataset['field']).sort();
const options = (f: Fx, name: string) => (f.nativeElement.querySelector(`mat-select[name="${name}"]`)?.getAttribute('data-options') ?? '').split(',').filter(Boolean);

async function open(api: Api, roles: Options = {}, testid = 'rule-new') {
  const ctx = setup({ ...base, ...api }, roles);
  const f = TestBed.createComponent(RulesPage);
  await flush(f);
  await click(f, testid);
  return { f, ...ctx };
}
async function fillMetricRule(f: Fx, metric = 'system.memory.usage') {
  await choose(f, 'template', 'MetricThreshold');
  await type(f, 'key', 'new-rule');
  await type(f, 'name', 'New rule');
  await type(f, 'threshold', '0.9');
  await type(f, 'metricName', metric);
}

describe('ruleFieldErrors with errors[]', () => {
  it('places each message by its field token and keeps the rest in one sentence', () => {
    const r = ruleFieldErrors(problem(400, 'Invalid request', 'x', { errors: [
      "rule 'k': Window must be at least 1 minute",
      "rule 'k': Metric.Aggregation Last doesn't fit a Histogram (use P50)",
      "rule 'k': Metric.Filters env: Key must match ^[A-Za-z_]$",
      "rule 'k': Metric.Kind is required (Gauge, Counter, UpDownCounter or Histogram)",
      "rule 'k': something else entirely",
    ] }));
    expect(Object.keys(r.fields).sort()).toEqual(['aggregation', 'filters', 'metricKind', 'window']);
    expect(r.fields['window']).toEqual(['Window must be at least 1 minute']);
    expect(r.rest).toBe('something else entirely');
  });
  it('without errors[] the path reading is unchanged', () => {
    expect(Object.keys(ruleFieldErrors(problem(400, 'Invalid request', 'Unreadable definition at $.threshold.')).fields)).toEqual(['threshold']);
  });
});

describe('building rules', () => {
  it('metric picker fills kind from catalog; unseen metric requires kind', async () => {
    const { f, calls } = await open({ createRule: () => EMPTY });
    await choose(f, 'template', 'MetricThreshold');
    await type(f, 'metricName', 'system.memory.usage');
    expect(has(f, 'mat-select[name="metricKind"]')).toBe(false);
    expect(f.nativeElement.querySelector('[data-testid="rule-kind"]')?.textContent).toContain('Gauge');
    await type(f, 'metricName', 'my.own.metric');
    expect(has(f, 'mat-select[name="metricKind"]')).toBe(true);
    await type(f, 'key', 'k');
    await type(f, 'name', 'n');
    await type(f, 'threshold', '1');
    await click(f, 'rule-save');
    expect(marked(f)).toContain('metricKind');
    expect(calls['createRule']).toBeUndefined();
    await choose(f, 'metricKind', 'Counter');
    expect(options(f, 'aggregation')).toEqual(['Rate', 'Increase']);
  });

  it('aggregations follow kind (gauge, counter, updown, histogram)', async () => {
    const { f } = await open({});
    await choose(f, 'template', 'MetricThreshold');
    await type(f, 'metricName', 'system.memory.usage');
    expect(options(f, 'aggregation')).toEqual(['Last', 'Avg', 'Max', 'Min']);
    await type(f, 'metricName', 'http.requests');
    expect(options(f, 'aggregation')).toEqual(['Rate', 'Increase']);
    await type(f, 'metricName', 'queue.depth');
    expect(options(f, 'aggregation')).toEqual(['Last']);
    await type(f, 'metricName', 'http.duration');
    expect(options(f, 'aggregation')).toEqual(['P50', 'P90', 'P95', 'P99', 'Avg', 'Rate']);
    expect(f.nativeElement.querySelector('[data-testid="rule-unit"]')?.textContent).toContain('ms');
  });

  it('six filters refused locally; In with 21 values refused', async () => {
    const { f, calls } = await open({ createRule: () => EMPTY });
    await fillMetricRule(f);
    for (let i = 0; i < 6; i++) await click(f, 'rule-filter-add');
    expect(f.nativeElement.querySelectorAll('[data-testid^="rule-filter-"][data-row]')).toHaveLength(5);
    expect(marked(f)).toEqual(['filters']);
    for (let i = 4; i > 0; i--) await click(f, 'rule-filter-remove-' + i);
    await type(f, 'filterKey-0', 'env');
    await choose(f, 'filterOp-0', 'In');
    await type(f, 'filterValues-0', Array.from({ length: 21 }, (_, i) => 'v' + i).join(','));
    await click(f, 'rule-save');
    expect(marked(f)).toContain('filters');
    expect(calls['createRule']).toBeUndefined();
  });

  it('host templates show Host only; MetricThreshold subject Host hides Service/Environment', async () => {
    const { f } = await open({});
    expect(has(f, 'input[name="service"]')).toBe(true);
    for (const t of ['HostMemory', 'HostCpu', 'HostDisk']) {
      await choose(f, 'template', t);
      expect(has(f, 'input[name="host"]'), t).toBe(true);
      expect(has(f, 'input[name="service"]'), t).toBe(false);
      expect(has(f, 'input[name="environment"]'), t).toBe(false);
    }
    await choose(f, 'template', 'MetricThreshold');
    expect(has(f, 'input[name="service"]')).toBe(true);
    await choose(f, 'subject', 'Host');
    expect(has(f, 'input[name="host"]')).toBe(true);
    expect(has(f, 'input[name="service"]')).toBe(false);
    expect(has(f, 'input[name="environment"]')).toBe(false);
  });

  it('offers all templates except SloBurn, and saves a metric rule as a typed body', async () => {
    const { f, calls } = await open({ createRule: () => of({}) });
    await fillMetricRule(f);
    await choose(f, 'aggregation', 'Max');
    await choose(f, 'comparison', 'Below');
    await click(f, 'rule-filter-add');
    await type(f, 'filterKey-0', 'state');
    await type(f, 'filterValues-0', 'used');
    const select = f.nativeElement.querySelector('mat-select[name="template"]')!;
    expect(select.getAttribute('data-options')!.split(',')).toEqual(
      ['ErrorRate', 'LatencyP95', 'ThroughputAbsent', 'DeliveryFailureRate', 'HostMemory', 'HostCpu', 'HostDisk', 'MediatorErrors', 'ConsumerFailures', 'MetricThreshold']);
    await click(f, 'rule-save');
    const body = calls['createRule'][0][0] as Record<string, unknown>;
    expect(body['template']).toBe('MetricThreshold');
    expect(body['metric']).toEqual({ name: 'system.memory.usage', kind: 'Gauge', aggregation: 'Max', filters: [{ key: 'state', op: 'Equals', values: ['used'] }], subject: 'Service', comparison: 'Below' });
  });
});

describe('preview', () => {
  it('preview sends the draft, shows summary/subjects/step; edit marks stale', async () => {
    const { f, calls } = await open({ previewRule: () => of(preview()) });
    await fillMetricRule(f);
    await click(f, 'rule-preview');
    const sent = calls['previewRule'][0][0] as Record<string, unknown>;
    expect(sent['template']).toBe('MetricThreshold');
    expect(sent['threshold']).toBe(0.9);
    expect((sent['metric'] as Record<string, unknown>)['name']).toBe('system.memory.usage');
    const summary = id(f, 'rule-preview-result')!.textContent!;
    expect(summary).toMatch(/would have fired 1 time on 1 subject/i);
    expect(summary).toContain('every 15 min');
    expect(id(f, 'rule-preview-subject-0')!.textContent).toContain('host-a');
    expect(id(f, 'rule-preview-subject-0')!.querySelectorAll('.jc-strip > i')).toHaveLength(3);
    expect(id(f, 'rule-preview-stale')).toBeNull();
    await type(f, 'threshold', '0.8');
    expect(id(f, 'rule-preview-stale')).not.toBeNull();
    await click(f, 'rule-preview');
    expect(id(f, 'rule-preview-stale')).toBeNull();
  });

  it('shows the bucket bounds of a histogram, a cut, a truncation and a quiet reason', async () => {
    const { f } = await open({
      previewRule: () => of(preview({ complete: false, cut: 'stopped after 40 of 96 points', subjectsTruncated: true, quiet: 'the metric was not reported in 24 h',
        subjects: [subject({ lastDetail: { lowerBound: '100', upperBound: '250' } })] })),
    });
    await fillMetricRule(f, 'http.duration');
    await click(f, 'rule-preview');
    const text = id(f, 'rule-preview-result')!.textContent!;
    expect(text).toContain('stopped after 40 of 96 points');
    expect(text).toContain('50');
    expect(text).toContain('not reported in 24 h');
    expect(id(f, 'rule-preview-subject-0')!.textContent).toContain('250');
  });

  it('preview 400 errors[] placed per field (Window, Metric.Aggregation, Metric.Filters k)', async () => {
    const { f } = await open({ previewRule: () => throwError(() => problem(400, 'Invalid request', 'x', { errors: [
      "rule 'preview': Window must be at least 1 minute",
      "rule 'preview': Metric.Aggregation Last doesn't fit a Histogram (use P50)",
      "rule 'preview': Metric.Filters env: Key must match ^[A-Za-z_]$",
    ] })) });
    await fillMetricRule(f);
    await click(f, 'rule-preview');
    expect(marked(f)).toEqual(['aggregation', 'filters', 'window']);
    expect(id(f, 'rule-error')).toBeNull();
  });

  it('preview 429 and 503 show one sentence with retry time; no field mark', async () => {
    for (const status of [429, 503]) {
      const { f } = await open({ previewRule: () => throwError(() => ({ ...problem(status, 'Refused', 'detail'), headers: new HttpHeaders({ 'Retry-After': '30' }) })) });
      await fillMetricRule(f);
      await click(f, 'rule-preview');
      expect(marked(f)).toEqual([]);
      const all = f.nativeElement.querySelectorAll('[data-testid="rule-error"]');
      expect(all).toHaveLength(1);
      expect(all[0].textContent).toContain('30 seconds');
    }
  });

  it('double click on Preview sends once', async () => {
    const pending = new Subject<unknown>();
    const { f, calls } = await open({ previewRule: () => pending });
    await fillMetricRule(f);
    id(f, 'rule-preview')!.click();
    id(f, 'rule-preview')!.click();
    await flush(f);
    expect(calls['previewRule']).toHaveLength(1);
    pending.next(preview());
    pending.complete();
    await flush(f);
    expect(id(f, 'rule-preview-result')).not.toBeNull();
  });

  it('a preview needs only what the rule needs: no key or name', async () => {
    const { f, calls } = await open({ previewRule: () => of(preview()) });
    await choose(f, 'template', 'MetricThreshold');
    await type(f, 'threshold', '1');
    await type(f, 'metricName', 'system.memory.usage');
    await click(f, 'rule-preview');
    expect(calls['previewRule']).toHaveLength(1);
  });
});

describe('roles and ownership', () => {
  const rules = () => ({ rules: () => page([metricRule, configRule, platformRule]) });

  it('admin sees New/Change/Preview', async () => {
    const { f } = await open(rules(), { admin: true, operate: true }, 'rule-edit-m1');
    expect(id(f, 'rule-new')).not.toBeNull();
    expect(id(f, 'rule-preview')).not.toBeNull();
    expect(id(f, 'rule-save')).not.toBeNull();
    expect(id(f, 'rule-readonly')).toBeNull();
  });

  it('operator sees View+Preview, no Save', async () => {
    const { f, calls } = await open({ ...rules(), previewRule: () => of(preview()), updateRule: () => EMPTY }, { admin: false, operate: true }, 'rule-view-m1');
    expect(id(f, 'rule-new')).toBeNull();
    expect(id(f, 'rule-edit-m1')).toBeNull();
    expect(id(f, 'rule-save')).toBeNull();
    expect(id(f, 'rule-preview')).not.toBeNull();
    for (const input of Array.from(f.nativeElement.querySelectorAll('[data-testid="rule-form"] input')) as HTMLInputElement[]) expect(input.disabled, input.name).toBe(true);
    await click(f, 'rule-preview');
    expect((calls['previewRule'][0][0] as Record<string, unknown>)['key']).toBe('m1');
    f.nativeElement.querySelector('form')!.dispatchEvent(new Event('submit'));
    await flush(f);
    expect(calls['updateRule']).toBeUndefined();
  });

  it('viewer sees View only, no Preview', async () => {
    const { f } = await open(rules(), { admin: false, operate: false }, 'rule-view-m1');
    expect(id(f, 'rule-new')).toBeNull();
    expect(id(f, 'rule-preview')).toBeNull();
    expect(id(f, 'rule-save')).toBeNull();
  });

  it('config rule: banner, all fields disabled, no Save, even for admin', async () => {
    const { f, calls } = await open({ ...rules(), updateRule: () => EMPTY }, { admin: true, operate: true }, 'rule-view-c1');
    expect(id(f, 'rule-edit-c1')).toBeNull();
    expect(id(f, 'rule-readonly')!.textContent).toContain('From the rules file. Change it there.');
    expect(id(f, 'rule-save')).toBeNull();
    for (const input of Array.from(f.nativeElement.querySelectorAll('[data-testid="rule-form"] input')) as HTMLInputElement[]) expect(input.disabled, input.name).toBe(true);
    expect(f.nativeElement.querySelector('mat-select[name="template"]')!.getAttribute('aria-disabled')).toBe('true');
    f.nativeElement.querySelector('form')!.dispatchEvent(new Event('submit'));
    await flush(f);
    expect(calls['updateRule']).toBeUndefined();
  });

  it('platform rule: read-only', async () => {
    const { f } = await open(rules(), { admin: true, operate: true }, 'rule-view-p1');
    expect(id(f, 'rule-readonly')).not.toBeNull();
    expect(id(f, 'rule-save')).toBeNull();
    expect(id(f, 'rule-preview')).toBeNull();
  });

  it('a role lost after the form opened stops the save', async () => {
    let admin = true;
    const { f, calls } = await open({ ...rules(), updateRule: () => EMPTY }, { admin: () => admin }, 'rule-edit-m1');
    admin = false;
    f.nativeElement.querySelector('form')!.dispatchEvent(new Event('submit'));
    await flush(f);
    expect(calls['updateRule']).toBeUndefined();
  });
});

describe('listing, stale state and tenants', () => {
  it('list shows Never observes for quiet metric rule', async () => {
    setup({ ...base, rules: () => page([{ ...metricRule, quiet: 'the metric has not been reported in 24 h' }]) });
    const f = TestBed.createComponent(RulesPage);
    await flush(f);
    expect(id(f, 'rule-quiet-m1')!.textContent).toContain('Never observes');
  });

  it('412 on save shows reload sentence, draft kept', async () => {
    const { f } = await open({ rules: () => page([metricRule]), updateRule: () => throwError(() => problem(412, 'Precondition failed', 'etag')) }, {}, 'rule-edit-m1');
    await type(f, 'threshold', '0.75');
    await click(f, 'rule-save');
    expect(id(f, 'rule-error')!.textContent).toMatch(/changed/i);
    expect(marked(f)).toEqual([]);
    expect((f.nativeElement.querySelector('input[name="threshold"]') as HTMLInputElement).value).toBe('0.75');
  });

  it('tenant change clears form, preview and errors', async () => {
    const { f, tenant } = await open({ previewRule: () => of(preview()) });
    await fillMetricRule(f);
    await click(f, 'rule-preview');
    expect(id(f, 'rule-preview-result')).not.toBeNull();
    tenant.next({ id: 't2' });
    await flush(f);
    expect(id(f, 'rule-form')).toBeNull();
    expect(id(f, 'rule-preview-result')).toBeNull();
    expect(id(f, 'rule-error')).toBeNull();
  });
});

describe('review lows', () => {
  it('a rule with no managedBy is read-only', async () => {
    const { f } = await open({ rules: () => page([{ ...metricRule, managedBy: '' }]) }, {}, 'rule-view-m1');
    expect(id(f, 'rule-readonly')).not.toBeNull();
    expect(id(f, 'rule-save')).toBeNull();
  });

  it('Change and View are disabled while the list reloads after a tenant change', async () => {
    const pending = new Subject<unknown>();
    let first = true;
    const { f, tenant } = await open({ rules: () => { if (first) { first = false; return page([metricRule]); } return pending; } }, {}, 'rule-edit-m1');
    tenant.next({ id: 't2' });
    f.detectChanges();
    expect((id(f, 'rule-edit-m1') as HTMLButtonElement | null)?.disabled ?? true).toBe(true);
  });

  it('a catalog that arrives after the form opened sets the kind and fits the aggregation', async () => {
    const late = new Subject<unknown>();
    const { f } = await open({ metrics: () => late });
    await choose(f, 'template', 'MetricThreshold');
    await type(f, 'metricName', 'queue.depth');
    expect(has(f, 'mat-select[name="metricKind"]')).toBe(true);
    late.next({ truncated: false, items: [{ name: 'queue.depth', type: 'sum', monotonic: false, unit: '1', description: '' }] });
    await flush(f);
    expect(has(f, 'mat-select[name="metricKind"]')).toBe(false);
    expect(options(f, 'aggregation')).toEqual(['Last']);
  });

  it('filter Remove buttons name their row and the inputs point at the filters error', async () => {
    const { f } = await open({});
    await fillMetricRule(f);
    await click(f, 'rule-filter-add');
    await click(f, 'rule-filter-add');
    expect(id(f, 'rule-filter-remove-1')!.getAttribute('aria-label')).toBe('Remove filter 2');
    await click(f, 'rule-save');
    expect(f.nativeElement.querySelector('input[name="filterKey-0"]')!.getAttribute('aria-describedby')).toBe('err-filters');
  });

  it('the definition a preview echoes (enabled: true) never changes the form: a disabled rule stays disabled, and is saved so', async () => {
    const { f, calls } = await open({ rules: () => page([{ ...metricRule, enabled: false }]), previewRule: () => of(preview({ definition: { ...metricRule.definition, enabled: true, threshold: 99 } })), updateRule: () => of({}) }, {}, 'rule-edit-m1');
    const enabled = () => f.nativeElement.querySelector('mat-checkbox[name="enabled"] input') as HTMLInputElement;
    expect(enabled().checked).toBe(false);
    await click(f, 'rule-preview');
    expect(enabled().checked).toBe(false);
    expect((f.nativeElement.querySelector('input[name="threshold"]') as HTMLInputElement).value).toBe('0.9');
    await click(f, 'rule-save');
    expect((calls['updateRule'][0][1] as Record<string, unknown>)['enabled']).toBe(false);
  });

  it('a message led by an Object.prototype name is not a field', () => {
    const r = ruleFieldErrors(problem(400, 'Invalid request', 'x', { errors: ['constructor is odd', 'toString too'] }));
    expect(r.fields).toEqual({});
    expect(r.rest).toBe('constructor is odd toString too');
  });
});

describe('safety', () => {
  it('unknown error text placed in form sentence, escaped', async () => {
    const { f } = await open({ previewRule: () => throwError(() => problem(500, '<img src=x onerror=alert(1)>', '<b>boom</b>')) });
    await fillMetricRule(f);
    await click(f, 'rule-preview');
    expect(id(f, 'rule-error')!.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(f.nativeElement.querySelector('img')).toBeNull();
    expect(f.nativeElement.querySelector('[data-testid="rule-error"] b')).toBeNull();
  });

  it('<script> metric names and filter values render as text', async () => {
    const evil = '<script>alert(1)</script>';
    const { f } = await open({ previewRule: () => of(preview({ subjects: [subject({ subjectKey: evil })] })) });
    await fillMetricRule(f, evil);
    await click(f, 'rule-filter-add');
    await type(f, 'filterKey-0', 'k');
    await type(f, 'filterValues-0', evil);
    await click(f, 'rule-preview');
    expect(f.nativeElement.querySelector('script')).toBeNull();
    expect(id(f, 'rule-preview-subject-0')!.textContent).toContain(evil);
    expect((f.nativeElement.querySelector('input[name="filterValues-0"]') as HTMLInputElement).value).toBe(evil);
  });
});
