import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, EMPTY, Observable, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_OPTIONS } from '@juice-js/observability';
import { IntegrationInstanceDto, IntegrationPluginDto, IntegrationsApi } from '../../projects/juice-js/observability/src/lib/core/integrations-api';
import { IntegrationPage } from '../../projects/juice-js/observability/src/lib/pages/integration.page';
import { IntegrationsPage } from '../../projects/juice-js/observability/src/lib/pages/integrations.page';
import { IntegrationForm, buildRequest, cap, fieldProblem, formProblems, initialValues } from '../../projects/juice-js/observability/src/lib/shared/integration-form';

type Api = Record<string, (...args: never[]) => Observable<unknown>>;
type Fx = { nativeElement: HTMLElement; whenStable(): Promise<unknown>; detectChanges(): void };

const PLUGIN: IntegrationPluginDto = {
  key: 'sample.jobs', displayName: 'Sample jobs', version: '1.0.0', capabilities: ['Health'], requiredPermissions: [],
  defaultIntervalSeconds: 300, minIntervalSeconds: 60,
  config: { version: 1, fields: [
    { name: 'endpoint', kind: 'string', label: 'Endpoint', required: true, maxLength: 20, pattern: '^https://' },
    { name: 'batch', kind: 'integer', label: 'Batch size', required: true, min: 1, max: 100 },
    { name: 'mode', kind: 'enum', label: 'Mode', required: false, allowed: ['fast', 'slow'] },
    { name: 'verbose', kind: 'boolean', label: 'Verbose', required: false },
    { name: 'tags', kind: 'stringList', label: 'Tags', required: false, maxItems: 2 },
  ] },
  scope: { version: 1, fields: [] },
  secrets: [{ name: 'token', label: 'API token', required: true }],
};

const INSTANCE = (extra: Partial<IntegrationInstanceDto> = {}): IntegrationInstanceDto => ({
  id: 'i1', pluginKey: 'sample.jobs', pluginName: 'Sample jobs', name: 'Jobs', state: 'Enabled', stateReason: null, intervalSeconds: 300, configVersion: 1,
  capabilities: ['Health'],
  health: { runtime: 'ok', connectivity: 'ok', authentication: 'ok', collection: 'ok', freshness: 'ok', freshnessSeconds: 10, lastFailure: null,
    lastFailureDetail: null, consecutiveFailures: 0 },
  lastSuccessAt: '2026-11-29T10:00:00Z', lastRunAt: '2026-11-29T10:00:00Z', nextRunAt: '2026-11-29T10:05:00Z',
  config: { endpoint: 'https://x', batch: 5 }, scope: {}, secretRefs: { token: 'env:JUICE_INTEGRATION_TOKEN' }, lastValidation: null,
  createdBy: 'victor', createdAt: '2026-11-28T00:00:00Z', updatedBy: 'victor', updatedAt: '2026-11-28T00:00:00Z', etag: 'e1', ...extra,
});

const role = { admin: true };
let tenant = new BehaviorSubject({ id: 't1' });

function setup(api: Api, admin = true) {
  role.admin = admin;
  tenant = new BehaviorSubject({ id: 't1' });
  const calls: Record<string, unknown[][]> = {};
  const fake = new Proxy({}, {
    get: (_, name: string) => (...args: unknown[]) => {
      (calls[name] ??= []).push(args);
      return (api[name] as ((...a: unknown[]) => Observable<unknown>) | undefined)?.(...args) ?? EMPTY;
    },
  });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideNoopAnimations(), { provide: IntegrationsApi, useValue: fake },
      { provide: ApiService, useValue: new Proxy({}, { get: (_, n) => (n === 'tenant$' ? tenant : () => EMPTY) }) },
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: () => role.admin },
      { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink: null } }],
  });
  return calls;
}

const flush = async (f: Fx) => { f.detectChanges(); await f.whenStable(); f.detectChanges(); await f.whenStable(); f.detectChanges(); };
const q = (f: Fx, testid: string) => f.nativeElement.querySelector(`[data-testid="${testid}"]`) as HTMLElement | null;
const text = (f: Fx) => (f.nativeElement.textContent ?? '').replace(/\s+/g, ' ');

async function openList(plugins: IntegrationPluginDto[], items: IntegrationInstanceDto[], admin = true) {
  const calls = setup({ plugins: () => of(plugins), instances: () => of({ items, nextCursor: null }) }, admin);
  const fixture = TestBed.createComponent(IntegrationsPage);
  await flush(fixture);
  return { fixture, calls };
}

async function openDetail(instance: IntegrationInstanceDto, extra: Api = {}, admin = true) {
  const calls = setup({ instance: () => of(instance), plugins: () => of([PLUGIN]), ...extra }, admin);
  const fixture = TestBed.createComponent(IntegrationPage);
  fixture.componentRef.setInput('id', instance.id);
  await flush(fixture);
  return { fixture, calls };
}

describe('integrations page (I1)', () => {
  it('renders_markup_in_name_and_detail_as_text', async () => {
    const name = '<img src=x onerror="window.pwned=1">Jobs';
    const detail = '<b>boom</b><script>window.pwned=2</script>';
    const failing = INSTANCE({
      name, state: 'Failed', stateReason: '<i>reason</i>',
      health: { ...INSTANCE().health, collection: 'failing', lastFailure: 'Remote', lastFailureDetail: detail, consecutiveFailures: 3 },
    });
    const list = await openList([PLUGIN], [failing]);
    expect(text(list.fixture)).toContain(name);
    expect(list.fixture.nativeElement.querySelector('img, script, i')).toBeNull();

    const { fixture } = await openDetail(failing);
    expect(q(fixture, 'integration-failure-detail')!.textContent).toBe(detail);
    expect(text(fixture)).toContain(name);
    expect(fixture.nativeElement.querySelector('img, script, b, i')).toBeNull();
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
  });

  it('form_built_from_schema_validates_required_and_ranges', async () => {
    const empty = formProblems(PLUGIN, initialValues(PLUGIN, null));
    expect(Object.keys(empty).sort()).toEqual(['config.batch', 'config.endpoint', 'name', 'secret.token']);

    const v = { ...initialValues(PLUGIN, null), name: 'a', 'config.endpoint': 'https://' + 'x'.repeat(20), 'config.batch': '500', 'config.tags': 'a\nb\nc', interval: '30', 'secret.token': 'env:JUICE_INTEGRATION_T' };
    const bad = formProblems(PLUGIN, v);
    expect(bad['config.endpoint']).toBeDefined();
    expect(bad['config.batch'][0]).toContain('at most 100');
    expect(bad['config.tags'][0]).toContain('at most 2');
    expect(bad['interval'][0]).toContain('at least 60');

    const good = { ...v, 'config.endpoint': 'https://x', 'config.batch': '5', 'config.tags': 'a\nb', interval: '120', 'config.verbose': true };
    expect(formProblems(PLUGIN, good)).toEqual({});
    expect(buildRequest(PLUGIN, good, true)).toEqual({
      pluginKey: 'sample.jobs', name: 'a', config: { endpoint: 'https://x', batch: 5, tags: ['a', 'b'], verbose: true }, scope: {},
      secretRefs: { token: 'env:JUICE_INTEGRATION_T' }, intervalSeconds: 120,
    });

    // The form on screen refuses an empty save without calling the API and marks the fields.
    const calls = setup({});
    const fixture = TestBed.createComponent(IntegrationForm);
    fixture.componentRef.setInput('plugin', PLUGIN);
    await flush(fixture);
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(calls['create']).toBeUndefined();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="rule-field-error"]').length).toBe(4);
  });

  it('secret_refs_are_reference_inputs_never_password_values', async () => {
    setup({});
    const fixture = TestBed.createComponent(IntegrationForm);
    fixture.componentRef.setInput('plugin', PLUGIN);
    fixture.componentRef.setInput('instance', INSTANCE());
    await flush(fixture);
    const secret = q(fixture, 'if-secret.token') as HTMLInputElement;
    expect(secret.type).toBe('text');
    expect(secret.value).toBe('env:JUICE_INTEGRATION_TOKEN');
    expect(secret.placeholder).toContain('env:');
    expect(fixture.nativeElement.querySelector('input[type=password]')).toBeNull();
    expect(text(fixture)).toContain('never passes through the Console');
  });

  it('non_admin_sees_no_mutation_controls', async () => {
    const list = await openList([PLUGIN], [INSTANCE()], false);
    expect(q(list.fixture, 'integration-new')).toBeNull();
    expect(q(list.fixture, 'integrations-readonly')).not.toBeNull();

    const { fixture } = await openDetail(INSTANCE({ config: null, scope: null, secretRefs: null }), {}, false);
    for (const id of ['integration-actions', 'integration-edit', 'integration-validate', 'integration-enable', 'integration-disable', 'integration-delete', 'integration-config'])
      expect(q(fixture, id)).toBeNull();
    expect(q(fixture, 'integration-readonly')).not.toBeNull();
    expect(q(fixture, 'integration-health')).not.toBeNull();

    // An admin gets them.
    const admin = await openList([PLUGIN], [INSTANCE()], true);
    expect(q(admin.fixture, 'integration-new')).not.toBeNull();
  });

  it('stale_etag_412_shows_reload', async () => {
    const { fixture, calls } = await openDetail(INSTANCE({ state: 'Disabled' }), {
      delete: () => throwError(() => ({ status: 412, error: { title: 'Precondition failed' } })),
    });
    q(fixture, 'integration-delete')!.click();
    await flush(fixture);
    q(fixture, 'integration-delete-confirm')!.click();
    await flush(fixture);
    expect(calls['delete'][0]).toEqual(['i1', 'e1']);
    expect(q(fixture, 'stale-reload')).not.toBeNull();
    const before = calls['instance'].length;
    q(fixture, 'stale-reload-button')!.click();
    await flush(fixture);
    expect(calls['instance'].length).toBeGreaterThan(before);
    expect(q(fixture, 'stale-reload')).toBeNull();
  });

  it('stale_etag_412_on_save_shows_reload_in_the_form', async () => {
    const { fixture, calls } = await openDetail(INSTANCE(), { update: () => throwError(() => ({ status: 412, error: {} })) });
    q(fixture, 'integration-edit')!.click();
    await flush(fixture);
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(calls['update'][0][2]).toBe('e1');
    expect(q(fixture, 'stale-reload')).not.toBeNull();
  });

  it('a_400_errors_object_is_placed_under_its_fields', async () => {
    const errors = { name: ['Name is taken.'], 'config.endpoint': ['Endpoint must use https.'], 'secretRefs.token': ['Outside the allowed prefix.'], other: ['Something else.'] };
    const calls = setup({ create: () => throwError(() => ({ status: 400, error: { title: 'Invalid request', errors } })) });
    const fixture = TestBed.createComponent(IntegrationForm);
    fixture.componentRef.setInput('plugin', PLUGIN);
    fixture.componentInstance['edited'].set({ ...initialValues(PLUGIN, null), name: 'a', 'config.endpoint': 'https://x', 'config.batch': '5', 'secret.token': 'env:JUICE_INTEGRATION_T' });
    await flush(fixture);
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(calls['create'].length).toBe(1);
    const marked = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="rule-field-error"]')).map(e => (e as HTMLElement).dataset['field']).sort();
    expect(marked).toEqual(['config.endpoint', 'name', 'secret.token']);
    expect(q(fixture, 'integration-form-error')!.textContent).toContain('Something else.');
  });

  it('save_rechecks_the_admin_role_and_sends_nothing_once_it_is_gone', async () => {
    const calls = setup({ create: () => of(INSTANCE()) });
    const fixture = TestBed.createComponent(IntegrationForm);
    fixture.componentRef.setInput('plugin', PLUGIN);
    fixture.componentInstance['edited'].set({ ...initialValues(PLUGIN, null), name: 'Jobs', 'config.endpoint': 'https://x', 'config.batch': '5', 'secret.token': 'env:JUICE_INTEGRATION_T' });
    await flush(fixture);
    role.admin = false;
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(calls['create']).toBeUndefined();
    expect(q(fixture, 'integration-form-error')!.textContent).toContain('admin role');
    // The same valid form sends once the role is back, so the role was what stopped it.
    role.admin = true;
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(calls['create'].length).toBe(1);
  });

  it('a_tenant_change_resets_the_list_form_and_reloads', async () => {
    const { fixture, calls } = await openList([PLUGIN], [], true);
    q(fixture, 'integration-new')!.click();
    await flush(fixture);
    expect(q(fixture, 'integration-form')).not.toBeNull();
    const before = calls['instances'].length;
    tenant.next({ id: 't2' });
    await flush(fixture);
    expect(q(fixture, 'integration-form')).toBeNull();
    expect(calls['instances'].length).toBeGreaterThan(before);
  });

  it('a_tenant_change_resets_the_detail_form_stale_notice_and_errors', async () => {
    const { fixture, calls } = await openDetail(INSTANCE(), { update: () => throwError(() => ({ status: 412, error: {} })) });
    q(fixture, 'integration-edit')!.click();
    await flush(fixture);
    q(fixture, 'integration-save')!.click();
    await flush(fixture);
    expect(q(fixture, 'stale-reload')).not.toBeNull();
    const before = calls['instance'].length;
    tenant.next({ id: 't2' });
    await flush(fixture);
    expect(q(fixture, 'integration-form')).toBeNull();
    expect(q(fixture, 'stale-reload')).toBeNull();
    expect(calls['instance'].length).toBeGreaterThan(before);
  });

  it('durations_accept_hhmmss_and_iso8601_and_plugin_text_and_patterns_are_bounded', () => {
    const f = { name: 'every', kind: 'duration' as const, label: 'Every', required: false };
    for (const ok of ['00:05:00', '1.02:03:04', 'PT5M', 'P1DT2H', 'PT0.5S']) expect(fieldProblem(f, ok)).toBeNull();
    for (const bad of ['5m', '00:75:00', 'P', 'PT', 'soon']) expect(fieldProblem(f, bad)).toContain('Every');
    const long = 'x'.repeat(500);
    expect(cap(long).length).toBe(200);
    expect(cap(long).endsWith('…')).toBe(true);
    // A pattern is left to the server: a catastrophic one costs the tab nothing.
    const evil = { name: 'p', kind: 'string' as const, label: 'P', required: false, pattern: '^(a+)+$' };
    expect(fieldProblem(evil, 'a'.repeat(40) + 'b')).toBeNull();
  });

  it('no_plugins_empty_state', async () => {
    const { fixture } = await openList([], []);
    expect(q(fixture, 'integrations-none')!.textContent).toContain('No integration plugins are installed');
    expect(q(fixture, 'integration-new')).toBeNull();
    expect(q(fixture, 'integrations')).toBeNull();
  });

  it('plugin_not_installed_shown_on_instance', async () => {
    const gone = INSTANCE({ pluginName: null, pluginKey: 'old.plugin', state: 'Failed', stateReason: 'plugin not installed' });
    const list = await openList([PLUGIN], [gone]);
    expect(q(list.fixture, 'plugin-missing')!.textContent).toContain('not installed');
    const { fixture } = await openDetail(gone);
    expect(q(fixture, 'plugin-missing')).not.toBeNull();
    expect((q(fixture, 'integration-edit') as HTMLButtonElement).disabled).toBe(true);
  });
});
