import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { EMPTY, Observable, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_OPTIONS, OBSERVABILITY_ROUTES, EscalationPolicy, AlertDetail } from '@juice-js/observability';
import { NotificationsPage } from '../../projects/juice-js/observability/src/lib/pages/notifications.page';
import { AlertPage } from '../../projects/juice-js/observability/src/lib/pages/alert.page';

const policy = (key: string, pipelinePolicyExists: boolean): EscalationPolicy => ({
  id: `id-${key}`, key, name: key.toUpperCase(), enabled: true, ruleKeys: [], environments: [], minimumSeverity: 'Critical', pipelinePolicyExists, updatedAt: '2026-11-28T01:00:00Z',
});
const problem = (status: number, title: string, detail: string) => throwError(() => ({ status, error: { title, detail } }));

/** An ApiService that answers only what a test names; anything else completes empty. Returns the calls it got. */
function setup(api: Record<string, (...args: never[]) => Observable<unknown>>, options: { admin?: boolean; notificationsLink?: string | null } = {}) {
  const calls: Record<string, unknown[][]> = {};
  const fake = new Proxy({}, {
    get: (_, name: string) => (...args: unknown[]) => {
      (calls[name] ??= []).push(args);
      return (api[name] as ((...a: unknown[]) => Observable<unknown>) | undefined)?.(...args) ?? EMPTY;
    },
  });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: fake },
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: () => options.admin ?? true },
      { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink: options.notificationsLink ?? '/alerts/notifications' } }],
  });
  return calls;
}
const flush = async (fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };
const text = (fixture: { nativeElement: HTMLElement }, id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`)?.textContent?.trim() ?? null;
const click = (fixture: { nativeElement: HTMLElement }, id: string) => (fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement).click();

describe('Escalation selection page (M50)', () => {
  const items = [policy('night', true), policy('orphan', false)];
  const list = () => of({ items, nextCursor: null });

  it('badges a selection whose pipeline policy is gone, and links the steps to the Notifications page', async () => {
    setup({ escalationPolicies: list });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    expect(text(fixture, 'no-pipeline-policy-orphan')).toContain('No policy with this key in Notifications');
    expect(text(fixture, 'no-pipeline-policy-night')).toBeNull();
    const steps = fixture.nativeElement.querySelector('[data-testid="steps-night"]') as HTMLAnchorElement;
    expect(steps.getAttribute('href')).toBe('/alerts/notifications/go/escalation-policies');
  });

  it('edits the selection fields only: the update body has no steps or stopOnAcknowledge', async () => {
    const calls = setup({ escalationPolicies: list, updateEscalationPolicy: () => of(items[0]) });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    click(fixture, 'policy-change-night');
    await flush(fixture);
    click(fixture, 'policy-save');
    await flush(fixture);
    const [key, body] = calls['updateEscalationPolicy'][0] as [string, Record<string, unknown>];
    expect(key).toBe('night');
    expect(Object.keys(body).sort()).toEqual(['enabled', 'environments', 'key', 'minimumSeverity', 'name', 'ruleKeys']);
  });

  it('shows a 422 no_pipeline_policy inline, with the API text, and keeps the form open', async () => {
    setup({ escalationPolicies: list,
      updateEscalationPolicy: () => problem(422, 'No such pipeline policy', 'The notification pipeline has no escalation policy night: create it on the Escalation Policies page.') });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    click(fixture, 'policy-change-night');
    await flush(fixture);
    click(fixture, 'policy-save');
    await flush(fixture);
    expect(text(fixture, 'policy-error')).toContain('No such pipeline policy: The notification pipeline has no escalation policy');
    expect(text(fixture, 'policy-form')).not.toBeNull();
  });

  it('shows a bare failure as its status', async () => {
    setup({ escalationPolicies: list, updateEscalationPolicy: () => throwError(() => ({ status: 500 })) });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    click(fixture, 'policy-change-night');
    await flush(fixture);
    click(fixture, 'policy-save');
    await flush(fixture);
    expect(text(fixture, 'policy-error')).toBe('Request failed (500).');
  });

  it('deletes an orphaned selection and reloads the list', async () => {
    const calls = setup({ escalationPolicies: list, deleteEscalationPolicy: () => of(undefined) });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    click(fixture, 'policy-delete-orphan');
    await flush(fixture);
    expect(calls['deleteEscalationPolicy']).toEqual([['orphan']]);
    expect(calls['escalationPolicies'].length).toBe(2);
  });

  it('shows a refused delete inline', async () => {
    setup({ escalationPolicies: list, deleteEscalationPolicy: () => problem(404, 'Policy not found', 'No policy orphan.') });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    click(fixture, 'policy-delete-orphan');
    await flush(fixture);
    expect(text(fixture, 'policy-delete-error')).toBe('Policy not found: No policy orphan.');
  });

  it('hides New selection, Change and Delete without the admin right', async () => {
    setup({ escalationPolicies: list }, { admin: false });
    const fixture = TestBed.createComponent(NotificationsPage);
    await flush(fixture);
    expect(text(fixture, 'policy-new')).toBeNull();
    expect(text(fixture, 'policy-change-night')).toBeNull();
    expect(text(fixture, 'policy-delete-night')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('NIGHT');
  });

  it('sends every deep link under the old Webhooks address to the escalation selection, under the base path', () => {
    const old = OBSERVABILITY_ROUTES.find(r => r.path === 'notifications')!;
    expect(old.children!.map(r => r.path)).toEqual(['**']);
    const redirect = old.children![0].redirectTo as () => string;
    for (const [basePath, expected] of [['/obs/', '/obs/escalation'], ['/', '/escalation']]) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [{ provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath, notificationsLink: null } }] });
      expect(TestBed.runInInjectionContext(redirect)).toBe(expected);
    }
  });
});

describe('Alert page links to the notifications admin (M50)', () => {
  const detail = {
    alert: { id: 'a1', rule: 'r', subjectKey: 's', subjectType: 'x', subjectId: null, environment: null, severity: 'Critical', state: 'Resolved', breachStartedAt: null,
      firedAt: null, resolvedAt: null, lastEvaluatedAt: '2026-11-28T01:00:00Z', lastValue: null, noData: false, acknowledgedAt: null, acknowledgedBy: null },
    evidence: {}, acknowledgeComment: null, history: [], subjectHealth: [], links: [],
  } as AlertDetail;
  const notifications = { correlationKey: 'alert:a1', silences: [], incidents: [] };

  it('links "Deliveries and escalation" to the correlation page of the key, under the mounted pages', async () => {
    setup({ alert: () => of(detail), alertNotifications: () => of(notifications) });
    const fixture = TestBed.createComponent(AlertPage);
    fixture.componentRef.setInput('id', 'a1');
    await flush(fixture);
    const a = fixture.nativeElement.querySelector('[data-testid="alert-deliveries-link"]') as HTMLAnchorElement;
    expect(a.textContent).toContain('Deliveries and escalation');
    expect(a.getAttribute('href')).toBe('/alerts/notifications/go/correlations/observability/alert:a1');
  });

  it('keeps a configured absolute console base as written, with its mixed-case host and port', async () => {
    setup({ alert: () => of(detail), alertNotifications: () => of(notifications) }, { notificationsLink: 'https://Console.Example.COM:8443/Admin/' });
    const fixture = TestBed.createComponent(AlertPage);
    fixture.componentRef.setInput('id', 'a1');
    await flush(fixture);
    const a = fixture.nativeElement.querySelector('[data-testid="alert-deliveries-link"]') as HTMLAnchorElement;
    expect(a.getAttribute('href')).toBe('https://Console.Example.COM:8443/Admin/go/correlations/observability/alert%3Aa1');
  });
});
