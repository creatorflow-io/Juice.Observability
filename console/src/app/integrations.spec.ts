import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { EMPTY, Observable, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_OPTIONS } from '@juice-js/observability';
import { IntegrationInstanceDto, IntegrationPluginDto, IntegrationsApi } from '../../projects/juice-js/observability/src/lib/core/integrations-api';
import { IntegrationPage } from '../../projects/juice-js/observability/src/lib/pages/integration.page';
import { IntegrationsPage } from '../../projects/juice-js/observability/src/lib/pages/integrations.page';
import { IntegrationForm, buildRequest, formProblems, initialValues } from '../../projects/juice-js/observability/src/lib/shared/integration-form';

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

function setup(api: Api, admin = true) {
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
      { provide: ApiService, useValue: new Proxy({}, { get: () => () => EMPTY }) },
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: () => admin },
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

    const v = { ...initialValues(PLUGIN, null), name: 'a', 'config.endpoint': 'http://x', 'config.batch': '500', 'config.tags': 'a\nb\nc', interval: '30', 'secret.token': 'env:JUICE_INTEGRATION_T' };
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
