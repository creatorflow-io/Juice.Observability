import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { problemText } from './telemetry';

/** The rule form's field names, as the API writes them in a path (`$.recoveryThreshold`), and what the form calls each one. */
export const RULE_FIELDS: Record<string, { label: string; numeric: boolean }> = {
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

const BY_LOWER = new Map(Object.keys(RULE_FIELDS).map(k => [k.toLowerCase(), k]));
const PATH = /\$(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[\s*'([^']*)'\s*\]|\[\s*"([^"]*)"\s*\])/g;

/**
 * Splits a failed save into messages for the fields it names and one sentence for everything else. The API names a field the way
 * System.Text.Json does, "Unreadable definition at $.threshold.": the path is matched case-insensitively, one or several to a message.
 * Only a 400 or 422 is read for paths; a 403, a 412 or a failed connection says one thing about the whole form.
 */
export function ruleFieldErrors(error: unknown): { fields: Record<string, string[]>; rest: string } {
  const e = error as { status?: number; error?: { title?: string; detail?: string } } | undefined;
  const text = problemText(error);
  if (!e || (e.status !== 400 && e.status !== 422)) return { fields: {}, rest: text };
  const fields: Record<string, string[]> = {};
  let unplaced = false;
  const haystack = [e.error?.detail, e.error?.title].filter(Boolean).join(' ');
  let any = false;
  for (const m of haystack.matchAll(PATH)) {
    any = true;
    const key = BY_LOWER.get((m[1] ?? m[2] ?? m[3] ?? '').toLowerCase());
    if (!key) { unplaced = true; continue; }
    const f = RULE_FIELDS[key];
    const message = `${f.label}: ${f.numeric ? 'enter a number.' : 'check this value.'}`;
    const list = (fields[key] ??= []);
    if (!list.includes(message)) list.push(message);
  }
  return { fields, rest: !any || unplaced ? text : '' };
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
