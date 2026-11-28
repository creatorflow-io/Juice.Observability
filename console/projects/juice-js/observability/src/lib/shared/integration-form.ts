import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { FieldDto, IntegrationInstanceDto, IntegrationInstanceRequest, IntegrationPluginDto, IntegrationsApi } from '../core/integrations-api';
import { FieldError, FormMarks } from './form-errors';
import { problemText } from './telemetry';

export type FormValues = Record<string, string | boolean>;
type Section = 'config' | 'scope';

/** A form value's key: `config.region`, `scope.team`, `secret.token`, and `name` and `interval` for the instance's own. */
export const fieldKey = (section: Section | 'secret', name: string) => `${section}.${name}`;

const INTEGER = /^-?\d+$/;

function listOf(text: string): string[] { return text.split(/\r?\n/).map(s => s.trim()).filter(Boolean); }

/** The form's starting values: the instance's own when editing, else empty (the plugin's default interval). Secret values are refs, as text. */
export function initialValues(plugin: IntegrationPluginDto, instance: IntegrationInstanceDto | null): FormValues {
  const v: FormValues = { name: instance?.name ?? '', interval: String(instance?.intervalSeconds ?? plugin.defaultIntervalSeconds) };
  for (const [section, fields, source] of [['config', plugin.config.fields, instance?.config], ['scope', plugin.scope.fields, instance?.scope]] as const) {
    for (const f of fields) {
      const raw = source?.[f.name];
      v[fieldKey(section, f.name)] = f.kind === 'boolean' ? raw === true : Array.isArray(raw) ? raw.map(String).join('\n') : raw === undefined || raw === null ? '' : String(raw);
    }
  }
  for (const s of plugin.secrets) v[fieldKey('secret', s.name)] = instance?.secretRefs?.[s.name] ?? '';
  return v;
}

/** What is wrong with one field's value, in words, or null. The API checks the same rules again; this saves a round trip. */
export function fieldProblem(f: FieldDto, value: string | boolean | undefined): string | null {
  if (f.kind === 'boolean') return null;
  const text = String(value ?? '').trim();
  if (text === '') return f.required ? `${f.label}: required.` : null;
  if (f.kind === 'integer' || f.kind === 'number') {
    const n = Number(text);
    if (f.kind === 'integer' ? !INTEGER.test(text) : !Number.isFinite(n)) return `${f.label}: enter ${f.kind === 'integer' ? 'a whole number' : 'a number'}.`;
    if (f.min != null && n < f.min) return `${f.label}: at least ${f.min}.`;
    if (f.max != null && n > f.max) return `${f.label}: at most ${f.max}.`;
    return null;
  }
  if (f.kind === 'stringList') {
    const items = listOf(text);
    if (f.maxItems != null && items.length > f.maxItems) return `${f.label}: at most ${f.maxItems} entries.`;
    if (f.maxLength != null && items.some(i => i.length > f.maxLength!)) return `${f.label}: each entry at most ${f.maxLength} characters.`;
    return null;
  }
  if (f.kind === 'enum' && f.allowed && !f.allowed.includes(text)) return `${f.label}: choose one of the listed values.`;
  if (f.maxLength != null && text.length > f.maxLength) return `${f.label}: at most ${f.maxLength} characters.`;
  if (f.pattern) {
    try { if (!new RegExp(f.pattern).test(text)) return `${f.label}: doesn't match the expected format.`; } catch { /* the server decides on a pattern the browser can't read */ }
  }
  return null;
}

/** Every field's problem by form key, for the plugin's config and scope, the instance's name and interval, and the required secret refs. */
export function formProblems(plugin: IntegrationPluginDto, v: FormValues): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  const add = (key: string, message: string | null) => { if (message) out[key] = [message]; };
  const name = String(v['name'] ?? '').trim();
  add('name', name === '' ? 'Name: required.' : name.length > 64 ? 'Name: at most 64 characters.' : null);
  const interval = String(v['interval'] ?? '').trim();
  const secs = Number(interval);
  add('interval', !INTEGER.test(interval) ? 'Every (seconds): enter a whole number.'
    : secs < plugin.minIntervalSeconds ? `Every (seconds): at least ${plugin.minIntervalSeconds}.` : secs > 86_400 ? 'Every (seconds): at most 86400.' : null);
  for (const [section, fields] of [['config', plugin.config.fields], ['scope', plugin.scope.fields]] as const)
    for (const f of fields) add(fieldKey(section, f.name), fieldProblem(f, v[fieldKey(section, f.name)]));
  for (const s of plugin.secrets) {
    const ref = String(v[fieldKey('secret', s.name)] ?? '').trim();
    add(fieldKey('secret', s.name), ref === '' && s.required ? `${s.label}: required.` : null);
  }
  return out;
}

function sectionOf(fields: FieldDto[], section: Section, v: FormValues): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const f of fields) {
    const raw = v[fieldKey(section, f.name)];
    if (f.kind === 'boolean') { out[f.name] = raw === true; continue; }
    const text = String(raw ?? '').trim();
    if (text === '') continue;
    out[f.name] = f.kind === 'integer' || f.kind === 'number' ? Number(text) : f.kind === 'stringList' ? listOf(text) : text;
  }
  return out;
}

/** The request body for the API: empty optional fields are left out, and a secret is only ever its reference text. */
export function buildRequest(plugin: IntegrationPluginDto, v: FormValues, create: boolean): IntegrationInstanceRequest {
  const secretRefs: Record<string, string> = {};
  for (const s of plugin.secrets) {
    const ref = String(v[fieldKey('secret', s.name)] ?? '').trim();
    if (ref) secretRefs[s.name] = ref;
  }
  return {
    ...(create ? { pluginKey: plugin.key } : {}),
    name: String(v['name']).trim(), config: sectionOf(plugin.config.fields, 'config', v), scope: sectionOf(plugin.scope.fields, 'scope', v),
    secretRefs, intervalSeconds: Number(String(v['interval']).trim()),
  };
}

/** The form key a server error path (`name`, `config.region`, `secretRefs.token`, `$.intervalSeconds`) belongs to, or null. */
function placeKey(path: string, plugin: IntegrationPluginDto): string | null {
  const p = path.replace(/^\$\.?/, '').replace(/\[\s*['"]?([^\]'"]+)['"]?\s*\]/g, '.$1');
  const [head, ...rest] = p.split('.');
  const tail = rest.join('.');
  const h = head.toLowerCase();
  if (h === 'name') return 'name';
  if (h === 'intervalseconds' || h === 'interval') return 'interval';
  if (h === 'config' && plugin.config.fields.some(f => f.name === tail)) return fieldKey('config', tail);
  if (h === 'scope' && plugin.scope.fields.some(f => f.name === tail)) return fieldKey('scope', tail);
  if ((h === 'secretrefs' || h === 'secrets') && plugin.secrets.some(s => s.name === tail)) return fieldKey('secret', tail);
  return null;
}

/**
 * Create or edit an integration instance, the fields built from the plugin's schema (`FieldDto`). A secret is asked for as a reference
 * (`env:NAME`, `file:/path`) in a plain text input: the Console never takes, shows or keeps a secret's value. All text is interpolated.
 */
@Component({
  selector: 'jc-integration-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, FieldError],
  template: `
    <form class="jc-form jc-card" data-testid="integration-form" (ngSubmit)="save()" novalidate>
      <h2>{{ instance() ? 'Edit ' + instance()!.name : 'New ' + plugin().displayName + ' instance' }}</h2>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
        <mat-label>Name</mat-label>
        <input matInput name="name" data-field="name" data-testid="if-name" maxlength="64" [ngModel]="v()['name']" (ngModelChange)="set('name', $event)" [attr.aria-invalid]="marks.bad('name')">
      </mat-form-field>
      <jc-field-error field="name" [messages]="marks.msgs('name')" />
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm">
        <mat-label>Every (seconds)</mat-label>
        <input matInput type="number" name="interval" data-field="interval" data-testid="if-interval" [attr.min]="plugin().minIntervalSeconds" [ngModel]="v()['interval']" (ngModelChange)="set('interval', $event)" [attr.aria-invalid]="marks.bad('interval')">
      </mat-form-field>
      <jc-field-error field="interval" [messages]="marks.msgs('interval')" />

      @for (section of sections(); track section.name) {
        @if (section.fields.length) {
          <h3>{{ section.title }}</h3>
          @for (f of section.fields; track f.name) {
            <div class="jc-field">
              @switch (f.kind) {
                @case ('boolean') {
                  <mat-checkbox [name]="key(section.name, f)" [attr.data-field]="key(section.name, f)" [attr.data-testid]="'if-' + key(section.name, f)"
                    [ngModel]="v()[key(section.name, f)]" (ngModelChange)="set(key(section.name, f), $event)">{{ f.label }}</mat-checkbox>
                }
                @case ('enum') {
                  <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
                    <mat-label>{{ f.label }}{{ f.required ? ' *' : '' }}</mat-label>
                    <select matNativeControl [name]="key(section.name, f)" [attr.data-field]="key(section.name, f)" [attr.data-testid]="'if-' + key(section.name, f)"
                      [ngModel]="v()[key(section.name, f)]" (ngModelChange)="set(key(section.name, f), $event)">
                      <option value=""></option>
                      @for (o of f.allowed ?? []; track o) { <option [value]="o">{{ o }}</option> }
                    </select>
                  </mat-form-field>
                }
                @case ('stringList') {
                  <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
                    <mat-label>{{ f.label }}{{ f.required ? ' *' : '' }} (one per line)</mat-label>
                    <textarea matInput rows="3" [name]="key(section.name, f)" [attr.data-field]="key(section.name, f)" [attr.data-testid]="'if-' + key(section.name, f)"
                      [ngModel]="v()[key(section.name, f)]" (ngModelChange)="set(key(section.name, f), $event)"></textarea>
                  </mat-form-field>
                }
                @default {
                  <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
                    <mat-label>{{ f.label }}{{ f.required ? ' *' : '' }}{{ f.kind === 'duration' ? ' (hh:mm:ss or ISO 8601)' : '' }}</mat-label>
                    <input matInput [type]="f.kind === 'integer' || f.kind === 'number' ? 'number' : 'text'" [attr.step]="f.kind === 'number' ? 'any' : null"
                      [attr.min]="f.min ?? null" [attr.max]="f.max ?? null" [attr.maxlength]="f.maxLength ?? null"
                      [name]="key(section.name, f)" [attr.data-field]="key(section.name, f)" [attr.data-testid]="'if-' + key(section.name, f)"
                      [ngModel]="v()[key(section.name, f)]" (ngModelChange)="set(key(section.name, f), $event)" [attr.aria-invalid]="marks.bad(key(section.name, f))">
                  </mat-form-field>
                }
              }
              <jc-field-error [field]="key(section.name, f)" [messages]="marks.msgs(key(section.name, f))" />
            </div>
          }
        }
      }

      @if (plugin().secrets.length) {
        <h3>Secrets</h3>
        <p class="jc-muted jc-small">Give where each secret is kept, as <span class="jc-mono">env:NAME</span> or <span class="jc-mono">file:/path</span>. The value is read by the platform when it is needed and never passes through the Console.</p>
        @for (s of plugin().secrets; track s.name) {
          <div class="jc-field">
            <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
              <mat-label>{{ s.label }} (reference){{ s.required ? ' *' : '' }}</mat-label>
              <input matInput type="text" autocomplete="off" autocapitalize="off" spellcheck="false" placeholder="env:JUICE_INTEGRATION_..."
                [name]="'secret-' + s.name" [attr.data-field]="'secret.' + s.name" [attr.data-testid]="'if-secret.' + s.name"
                [ngModel]="v()['secret.' + s.name]" (ngModelChange)="set('secret.' + s.name, $event)" [attr.aria-invalid]="marks.bad('secret.' + s.name)">
            </mat-form-field>
            <jc-field-error [field]="'secret.' + s.name" [messages]="marks.msgs('secret.' + s.name)" />
          </div>
        }
      }

      @if (instance()) { <p class="jc-muted jc-small">Changing the configuration, scope, references or interval returns the instance to Configured: validate and enable it again.</p> }
      @if (stale()) {
        <div class="jc-notice" role="alert" data-testid="stale-reload">
          <p>This instance was changed by someone else since you opened it. Reload to see the current version; your edits here are not saved.</p>
          <button mat-flat-button type="button" data-testid="stale-reload-button" (click)="reload.emit()">Reload</button>
        </div>
      }
      @if (marks.summary()) { <p class="jc-error" role="alert" data-testid="integration-form-error">{{ marks.summary() }}</p> }
      <div class="jc-row">
        <button mat-flat-button type="submit" data-testid="integration-save" [disabled]="busy()">{{ instance() ? 'Save' : 'Create' }}</button>
        <button mat-button type="button" data-testid="integration-cancel" (click)="cancelled.emit()">Cancel</button>
      </div>
    </form>`,
})
export class IntegrationForm {
  private readonly api = inject(IntegrationsApi);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly plugin = input.required<IntegrationPluginDto>();
  readonly instance = input<IntegrationInstanceDto | null>(null);
  readonly saved = output<IntegrationInstanceDto>();
  readonly cancelled = output<void>();
  /** The instance changed under the form (412): the host reloads it. */
  readonly reload = output<void>();

  protected readonly marks = new FormMarks();
  protected readonly busy = signal(false);
  protected readonly stale = signal(false);
  private readonly edited = signal<FormValues | null>(null);
  protected readonly v = computed(() => this.edited() ?? initialValues(this.plugin(), this.instance()));
  protected readonly sections = computed(() => [
    { name: 'config' as Section, title: 'Configuration', fields: this.plugin().config.fields },
    { name: 'scope' as Section, title: 'Scope', fields: this.plugin().scope.fields },
  ]);

  protected key(section: Section, f: FieldDto) { return fieldKey(section, f.name); }

  protected set(key: string, value: string | boolean) {
    this.edited.set({ ...this.v(), [key]: value });
    this.marks.clear(key);
  }

  private order(): string[] {
    const p = this.plugin();
    return ['name', 'interval', ...p.config.fields.map(f => fieldKey('config', f.name)), ...p.scope.fields.map(f => fieldKey('scope', f.name)),
      ...p.secrets.map(s => fieldKey('secret', s.name))];
  }

  protected save() {
    const plugin = this.plugin();
    const problems = formProblems(plugin, this.v());
    this.stale.set(false);
    if (Object.keys(problems).length) { this.marks.mark(problems, '', this.order(), this.host.nativeElement); return; }
    this.marks.reset();
    const body = buildRequest(plugin, this.v(), !this.instance());
    const current = this.instance();
    this.busy.set(true);
    (current ? this.api.update(current.id, body, current.etag) : this.api.create(body)).subscribe({
      next: saved => { this.busy.set(false); this.saved.emit(saved); },
      error: e => {
        this.busy.set(false);
        const status = (e as { status?: number }).status;
        if (status === 412) { this.stale.set(true); return; }
        const fields: Record<string, string[]> = {};
        const rest: string[] = [];
        const errors = (e as { error?: { errors?: Record<string, string[]> } }).error?.errors;
        if (status === 400 && errors && typeof errors === 'object' && !Array.isArray(errors)) {
          for (const [path, messages] of Object.entries(errors)) {
            const key = placeKey(path, plugin);
            for (const m of Array.isArray(messages) ? messages : [String(messages)]) (key ? (fields[key] ??= []) : rest).push(m);
          }
        }
        const summary = status === 403 ? 'Changing integrations needs the admin role in this deployment’s tenant.'
          : status === 428 ? 'The instance has to be reloaded before it can be changed.'
          : Object.keys(fields).length || rest.length ? rest.join(' ') : problemText(e);
        this.marks.mark(fields, summary, this.order(), this.host.nativeElement);
      },
    });
  }
}
