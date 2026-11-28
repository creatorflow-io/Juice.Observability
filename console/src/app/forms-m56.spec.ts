import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { EMPTY, Observable, of, throwError } from 'rxjs';
import { ApiService, OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_OPTIONS } from '@juice-js/observability';
import { AgentPage } from '../../projects/juice-js/observability/src/lib/pages/agent.page';
import { SilencesPage } from '../../projects/juice-js/observability/src/lib/pages/silences.page';
import { AGENT_REQUEST_FIELDS, SILENCE_FIELDS, ruleFieldErrors } from '../../projects/juice-js/observability/src/lib/shared/form-errors';

const problem = (status: number, title: string, detail: string) => ({ status, error: { title, detail } });

function setup(api: Record<string, (...args: never[]) => Observable<unknown>>) {
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
      { provide: OBSERVABILITY_CAN_ADMIN, useValue: () => true },
      { provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink: null } }],
  });
  return calls;
}

type Fx = { nativeElement: HTMLElement; whenStable(): Promise<unknown>; detectChanges(): void };
const flush = async (f: Fx) => { f.detectChanges(); await f.whenStable(); f.detectChanges(); };
const q = (f: Fx, testid: string) => f.nativeElement.querySelector(`[data-testid="${testid}"]`) as HTMLElement;
const click = (f: Fx, id: string) => q(f, id).click();
const type = async (f: Fx, name: string, value: string) => {
  const input = f.nativeElement.querySelector(`input[name="${name}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flush(f);
};
const fieldErrors = (f: Fx) => Array.from(f.nativeElement.querySelectorAll('[data-testid="rule-field-error"]')) as HTMLElement[];
const marked = (f: Fx) => fieldErrors(f).map(e => e.dataset['field']).sort();
const under = (f: Fx, field: string) => fieldErrors(f).filter(e => e.dataset['field'] === field && e.closest('.jc-field'));

const list = (items: unknown[]) => () => of({ items, nextCursor: null });
const param = (name: string, type: string, extra: Record<string, unknown> = {}) => ({
  name, type, required: false, description: null, minimum: null, maximum: null, maxLength: null, pattern: null, allowed: null, default: null, ...extra,
});
const action = (name: string, extra: Record<string, unknown> = {}) => ({
  name, description: `${name} does a thing.`, changing: false, custom: false, parameters: [], permitted: true, limits: [], risk: 'Medium', approval: 'SecondPerson', ...extra,
});
const AGENT = { id: 'a1', hostname: 'h1', operatingSystem: 'linux', version: '1', status: 'Active', resourceId: null, online: true, lastSeenAt: null,
  certificateNotAfter: '2027-01-01T00:00:00Z', enrolledBy: 'x', enrolledAt: '2026-01-01T00:00:00Z', revokedAt: null, revokedBy: null, revokeReason: null };

async function openAgent(actions: unknown[], extra: Record<string, (...args: never[]) => Observable<unknown>> = {}) {
  const calls = setup({
    agent: () => of(AGENT), agentActions: () => of({ reportedAt: '2026-11-28T00:00:00Z', items: actions }), actionRequests: list([]), agentCommands: list([]),
    agents: list([]), actionRequestsWaiting: () => of({ total: 0, forYou: 0 }), ...extra,
  });
  const fixture = TestBed.createComponent(AgentPage);
  fixture.componentRef.setInput('id', 'a1');
  await flush(fixture);
  await flush(fixture);
  return { fixture, calls };
}

const hasHint = (f: Fx) => !!q(f, 'command-dryrun-hint');

describe('field maps (M56)', () => {
  it('a path at $.reason is placed under the Why field of the agent form; endsAt under hours', () => {
    expect(ruleFieldErrors(problem(400, 'Invalid request', 'Bad value at $.reason.'), AGENT_REQUEST_FIELDS).fields).toEqual({ requestReason: ['Why: check this value.'] });
    expect(Object.keys(ruleFieldErrors(problem(422, 'x', 'Bad at $.endsAt.'), SILENCE_FIELDS).fields)).toEqual(['hours']);
    // The rule form's map is still the default.
    expect(Object.keys(ruleFieldErrors(problem(400, 'x', 'Bad at $.threshold.')).fields)).toEqual(['threshold']);
  });
});

describe('agent form (M56)', () => {
  it('required fields are marked at once; a 400 "at $.reason" shows under Why; labels use the grid', async () => {
    const { fixture, calls } = await openAgent([action('files.cleanup', { parameters: [param('path', 'string', { required: true })] })], {
      requestAction: () => throwError(() => problem(400, 'Invalid request', 'Unreadable request at $.reason.')),
    });
    expect(fixture.nativeElement.querySelector('form.jc-form.jc-form-grid')).not.toBeNull();
    click(fixture, 'command-issue');
    await flush(fixture);
    expect(marked(fixture)).toEqual(['path', 'requestReason']);
    expect(calls['requestAction']).toBeUndefined();
    const why = fixture.nativeElement.querySelector('input[name="requestReason"]') as HTMLInputElement;
    expect(why.getAttribute('aria-invalid')).toBe('true');
    expect(why.getAttribute('aria-describedby')).toBe('err-requestReason');

    await type(fixture, 'path', '/tmp');
    await type(fixture, 'requestReason', 'disk is full');
    expect(marked(fixture)).toEqual([]);
    click(fixture, 'command-issue');
    await flush(fixture);
    expect(calls['requestAction']).toHaveLength(1);
    expect(under(fixture, 'requestReason').map(e => e.textContent!.trim())).toEqual(['Why: check this value.']);
    expect(q(fixture, 'command-error')).toBeNull();
  });

  it('a 403 is one sentence for the form, no field', async () => {
    const { fixture } = await openAgent([action('files.cleanup')], { requestAction: () => throwError(() => problem(403, 'Forbidden', 'no')) });
    await type(fixture, 'requestReason', 'because');
    click(fixture, 'command-issue');
    await flush(fixture);
    expect(marked(fixture)).toEqual([]);
    expect(q(fixture, 'command-error').textContent).toContain('needs the operator or admin role');
  });

  it('dry-run hint: present only with dryRunApproval; checking dryRun on files.cleanup turns Request into Run', async () => {
    const cleanup = (extra: Record<string, unknown>) => action('files.cleanup', { parameters: [param('dryRun', 'boolean')], ...extra });
    const withHint = await openAgent([cleanup({ dryRunApproval: 'None' })]);
    expect(q(withHint.fixture, 'command-dryrun-hint').textContent).toContain('A dry run only reports and needs no approval; the real run needs a second person.');
    expect(q(withHint.fixture, 'command-issue').textContent!.trim()).toBe('Request');
    ((withHint.fixture.nativeElement as HTMLElement).querySelector('[data-testid="command-param-dryRun"] input') as HTMLInputElement).click();
    await flush(withHint.fixture);
    expect(q(withHint.fixture, 'command-issue').textContent!.trim()).toBe('Run');
    expect(q(withHint.fixture, 'command-approval').textContent).toContain('runs when asked');
    expect(withHint.fixture.nativeElement.querySelector('input[name="requestReason"]')).toBeNull();

    // No field from the server: no claim, and a dry run is treated as the action itself.
    const without = await openAgent([cleanup({})]);
    expect(hasHint(without.fixture)).toBe(false);
    ((without.fixture.nativeElement as HTMLElement).querySelector('[data-testid="command-param-dryRun"] input') as HTMLInputElement).click();
    await flush(without.fixture);
    expect(q(without.fixture, 'command-issue').textContent!.trim()).toBe('Request');

    // An action without a dryRun parameter: never a hint, even when the field is present.
    const plain = await openAgent([action('host.restart', { dryRunApproval: 'None' })]);
    expect(hasHint(plain.fixture)).toBe(false);
  });
});

describe('silences form (M56)', () => {
  async function open(extra: Record<string, (...args: never[]) => Observable<unknown>> = {}) {
    const calls = setup({ silences: list([]), ...extra });
    const fixture = TestBed.createComponent(SilencesPage);
    await flush(fixture);
    return { fixture, calls };
  }

  it('an empty Why and hours are marked in one submit and nothing is sent', async () => {
    const { fixture, calls } = await open({ createSilence: () => EMPTY });
    expect(fixture.nativeElement.querySelector('form.jc-form.jc-form-grid')).not.toBeNull();
    await type(fixture, 'hours', '');
    click(fixture, 'silence-save');
    await flush(fixture);
    expect(marked(fixture)).toEqual(['comment', 'hours']);
    expect(calls['createSilence']).toBeUndefined();
    const why = q(fixture, 'silence-comment');
    expect(why.getAttribute('aria-invalid')).toBe('true');
    expect(why.getAttribute('aria-describedby')).toBe('err-comment');
    await type(fixture, 'comment', 'deploy');
    expect(marked(fixture)).toEqual(['hours']);
  });

  it('a 400 naming $.comment and $.endsAt shows under Why and For (hours); a 403, 412 and 0 are one sentence', async () => {
    let error: unknown = problem(400, 'Invalid request', 'Bad at $.comment and $.endsAt.');
    const { fixture } = await open({ createSilence: () => throwError(() => error) });
    await type(fixture, 'comment', 'deploy');
    click(fixture, 'silence-save');
    await flush(fixture);
    expect(marked(fixture)).toEqual(['comment', 'hours']);
    expect(under(fixture, 'hours')).toHaveLength(1);
    expect(q(fixture, 'silence-error')).toBeNull();

    for (const status of [403, 412, 0]) {
      error = problem(status, 'Nope', 'Bad at $.comment.');
      click(fixture, 'silence-save');
      await flush(fixture);
      expect(marked(fixture)).toEqual([]);
      expect(q(fixture, 'silence-error').textContent!.trim()).not.toBe('');
    }
  });
});
