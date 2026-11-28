import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { problemText } from './telemetry';

/** The rule form's field names, as the API writes them in a path (`$.recoveryThreshold`), and what the form calls each one. */
/** What a form calls an API field, whether it wants a number, and the form field that shows it when that has another name. */
export type FieldMap = Record<string, { label: string; numeric: boolean; field?: string }>;

export const RULE_FIELDS: FieldMap = {
  key: { label: 'Key', numeric: false },
  name: { label: 'Name', numeric: false },
  template: { label: 'Template', numeric: false },
  severity: { label: 'Severity', numeric: false },
  service: { label: 'Service', numeric: false },
  environment: { label: 'Environment', numeric: false },
  host: { label: 'Host', numeric: false },
  threshold: { label: 'Threshold', numeric: true },
  recoveryThreshold: { label: 'Recovery threshold', numeric: true },
  minimumSamples: { label: 'Minimum samples', numeric: true },
  schedule: { label: 'Every', numeric: true },
  window: { label: 'Window', numeric: true },
  forDuration: { label: 'Fire after', numeric: true },
  recoveryDuration: { label: 'Recover after', numeric: true },
  cooldown: { label: 'Cooldown', numeric: true },
  enabled: { label: 'Enabled', numeric: false },
};

/** The silence form: the API's names, and the form's own field name where it differs. */
export const SILENCE_FIELDS: FieldMap = {
  kind: { label: 'Kind', numeric: false },
  comment: { label: 'Why', numeric: false },
  startsAt: { label: 'Starts', numeric: false },
  endsAt: { label: 'For (hours)', numeric: true, field: 'hours' },
  repeatWeekly: { label: 'Every week', numeric: false, field: 'weekly' },
  service: { label: 'Service', numeric: false },
  environment: { label: 'Environment', numeric: false },
  host: { label: 'Host', numeric: false },
  ruleKeys: { label: 'Rules', numeric: false, field: 'rules' },
  maximumSeverity: { label: 'Severity up to', numeric: false, field: 'maximum' },
};

/** The "Ask the agent" form: the API's `reason` is the Why box. */
export const AGENT_REQUEST_FIELDS: FieldMap = {
  reason: { label: 'Why', numeric: false, field: 'requestReason' },
  action: { label: 'Action', numeric: false },
  version: { label: 'Version', numeric: false },
};

const PATH = /\$(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[\s*'([^']*)'\s*\]|\[\s*"([^"]*)"\s*\])/g;

/**
 * Splits a failed save into messages for the fields it names and one sentence for everything else. The API names a field the way
 * System.Text.Json does, "Unreadable definition at $.threshold.": the path is matched case-insensitively, one or several to a message.
 * Only a 400 or 422 is read for paths; a 403, a 412 or a failed connection says one thing about the whole form.
 */
export function ruleFieldErrors(error: unknown, map: FieldMap = RULE_FIELDS): { fields: Record<string, string[]>; rest: string } {
  const byLower = new Map(Object.keys(map).map(k => [k.toLowerCase(), k]));
  const e = error as { status?: number; error?: { title?: string; detail?: string } } | undefined;
  const text = problemText(error);
  if (!e || (e.status !== 400 && e.status !== 422)) return { fields: {}, rest: text };
  const fields: Record<string, string[]> = {};
  let unplaced = false;
  const haystack = [e.error?.detail, e.error?.title].filter(Boolean).join(' ');
  let any = false;
  for (const m of haystack.matchAll(PATH)) {
    any = true;
    const key = byLower.get((m[1] ?? m[2] ?? m[3] ?? '').toLowerCase());
    if (!key) { unplaced = true; continue; }
    const f = map[key];
    const message = `${f.label}: ${f.numeric ? 'enter a number.' : 'check this value.'}`;
    const list = (fields[f.field ?? key] ??= []);
    if (!list.includes(message)) list.push(message);
  }
  return { fields, rest: !any || unplaced ? text : '' };
}

/**
 * The field messages and the one form sentence of a form, for a page to bind: `msgs(field)` under each field, `bad(field)` for aria-invalid.
 * `mark` takes the fields the open form shows (a message for any other goes to the sentence, so nothing is lost) and focuses the first one,
 * in the order given.
 */
export class FormMarks {
  readonly fields = signal<Record<string, string[]>>({});
  readonly summary = signal('');

  msgs(field: string): string[] { return this.fields()[field] ?? []; }
  bad(field: string): 'true' | null { return this.msgs(field).length ? 'true' : null; }
  describedBy(field: string): string | null { return this.msgs(field).length ? 'err-' + field : null; }
  /** Editing a field takes its own message away and leaves the others. */
  clear(field: string) {
    if (!this.fields()[field]) return;
    const { [field]: _, ...rest } = this.fields();
    this.fields.set(rest);
  }
  reset() { this.summary.set(''); this.fields.set({}); }

  mark(fields: Record<string, string[]>, summary: string, order: string[], host: HTMLElement) {
    const shown = new Set(order);
    const placed: Record<string, string[]> = {};
    const lost: string[] = [];
    for (const [k, v] of Object.entries(fields)) { if (shown.has(k)) placed[k] = v; else lost.push(...v); }
    this.fields.set(placed);
    this.summary.set([summary, ...lost].filter(Boolean).join(' '));
    const first = order.find(k => placed[k]);
    if (first) setTimeout(() => {
      const el = host.querySelector<HTMLElement>(`[data-field="${first}"]`);
      // A mat-checkbox carries the name on its host; the focusable control is the input inside it.
      (el?.matches('mat-checkbox') ? el.querySelector<HTMLElement>('input') : el)?.focus();
    });
  }
}

/** One field's messages, under the field, and tied to it for a screen reader through the id the input names in aria-describedby. */
@Component({
  selector: 'jc-field-error',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@for (m of messages(); track m) { <div class="jc-field-error" role="alert" data-testid="rule-field-error" [attr.data-field]="field()" [id]="'err-' + field()">{{ m }}</div> }`,
})
export class FieldError {
  readonly field = input.required<string>();
  readonly messages = input<string[]>([]);
}
