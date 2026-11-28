import { DatePipe, DecimalPipe, formatNumber } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { MetricInfo, PreviewSubject, Rule, RulePreview, SignalPack } from '../core/models';
import { OBSERVABILITY_CAN_ADMIN, OBSERVABILITY_CAN_OPERATE } from '../core/options';
import { FieldError, ruleFieldErrors } from '../shared/form-errors';
import { formatBytes, formatDuration } from '../shared/format';

/** What a rule can be made from, in the order the picker shows them, and what each does. SloBurn is the platform's own, made for SLOs. */
export const TEMPLATES: Record<string, string> = {
  ErrorRate: 'Ratio of failed requests, 0 to 1. Fires above the threshold.',
  LatencyP95: 'Milliseconds at the 95th percentile. Fires above the threshold.',
  ThroughputAbsent: 'Requests in the window, for a service that had traffic. Fires at or below the threshold.',
  DeliveryFailureRate: 'Ratio of failed deliveries, 0 to 1. Fires above the threshold.',
  HostMemory: 'Ratio of host memory used, 0 to 1. Fires above the threshold.',
  HostCpu: 'Ratio of host CPU used, 0 to 1. Fires above the threshold.',
  HostDisk: 'Ratio of host disk space used, 0 to 1. Fires above the threshold.',
  MediatorErrors: 'Mediator errors in the window. Fires above the threshold.',
  ConsumerFailures: 'Failed consumer messages in the window. Fires above the threshold.',
  MetricThreshold: 'One metric, aggregated over the window, against a threshold. Fires above it, or below it.',
  SloBurn: "An SLO's burn rate over a long and a short window. Fires when both reach the rate; one alert per SLO.",
};

const HOST_TEMPLATES = ['HostMemory', 'HostCpu', 'HostDisk'];
const AGGREGATIONS: Record<string, string[]> = {
  Gauge: ['Last', 'Avg', 'Max', 'Min'],
  Counter: ['Rate', 'Increase'],
  UpDownCounter: ['Last'],
  Histogram: ['P50', 'P90', 'P95', 'P99', 'Avg', 'Rate'],
};
const KINDS = Object.keys(AGGREGATIONS);
const MAX_FILTERS = 5, MAX_VALUES = 20, MAX_VALUE_LENGTH = 256;

/** A TimeSpan as the API writes it (`1.02:03:04`) in seconds. */
export function seconds(value: unknown): number {
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(String(value ?? ''));
  return match ? (+(match[1] ?? 0)) * 86400 + +match[2] * 3600 + +match[3] * 60 + +match[4] : 0;
}

export function span(total: number): string {
  const s = Math.max(0, Math.round(total || 0));
  const pad = (n: number) => String(n).padStart(2, '0');
  const days = Math.floor(s / 86400);
  return `${days ? `${days}.` : ''}${pad(Math.floor(s / 3600) % 24)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`;
}

const FIELD_ORDER = ['key', 'name', 'template', 'severity', 'host', 'service', 'environment', 'metricName', 'metricKind', 'aggregation', 'subject', 'comparison', 'filters', 'threshold', 'recoveryThreshold',
  'minimumSamples', 'schedule', 'window', 'forDuration', 'recoveryDuration', 'cooldown', 'enabled'];
/** The form's input name where it differs from the API's field name. */
const INPUT_NAME: Record<string, string> = { forDuration: 'for', recoveryDuration: 'recovery', filters: 'filterKey-0' };

interface FilterRow { key: string; op: 'Equals' | 'In'; values: string; }
interface RuleForm {
  key: string; name: string; template: string; severity: string; service: string; environment: string; host: string; threshold: number | null; recoveryThreshold: number | null;
  minimumSamples: number; scheduleSeconds: number; windowSeconds: number; forSeconds: number; recoverySeconds: number; cooldownSeconds: number; enabled: boolean;
  metricName: string; metricKind: string; aggregation: string; subject: string; comparison: string; filters: FilterRow[];
}

const NEW_RULE: RuleForm = {
  key: '', name: '', template: 'ErrorRate', severity: 'Warning', service: '', environment: '', host: '', threshold: null, recoveryThreshold: null,
  minimumSamples: 10, scheduleSeconds: 60, windowSeconds: 300, forSeconds: 120, recoverySeconds: 120, cooldownSeconds: 600, enabled: true,
  metricName: '', metricKind: '', aggregation: '', subject: 'Service', comparison: 'Above', filters: [],
};

/** The kind of a catalog metric: a gauge, a histogram, or a sum that says whether it only grows. A sum that doesn't say is left for the person to name. */
function kindOf(m: MetricInfo | undefined): string {
  if (!m) return '';
  const type = m.type.toLowerCase();
  if (type === 'gauge') return 'Gauge';
  if (type === 'histogram') return 'Histogram';
  if (type === 'sum') return m.monotonic === true ? 'Counter' : m.monotonic === false ? 'UpDownCounter' : '';
  return '';
}

function formFrom(rule: Rule | null): RuleForm {
  if (!rule) return { ...NEW_RULE, filters: [] };
  const d = rule.definition;
  const m = (d['metric'] ?? {}) as Record<string, unknown>;
  const filters = ((m['filters'] as { key?: string; op?: string; values?: string[] }[] | undefined) ?? []).map(x => {
    const op: 'Equals' | 'In' = x.op === 'In' ? 'In' : 'Equals';
    return { key: String(x.key ?? ''), op, values: op === 'In' ? (x.values ?? []).join(', ') : String(x.values?.[0] ?? '') };
  });
  return {
    key: rule.key, name: rule.name, template: rule.template, severity: rule.severity, service: String(d['service'] ?? ''), environment: String(d['environment'] ?? ''), host: String(d['host'] ?? ''),
    threshold: d['threshold'] as number, recoveryThreshold: (d['recoveryThreshold'] as number | null) ?? null, minimumSamples: d['minimumSamples'] as number, scheduleSeconds: seconds(d['schedule']), windowSeconds: seconds(d['window']),
    forSeconds: seconds(d['forDuration']), recoverySeconds: seconds(d['recoveryDuration']), cooldownSeconds: seconds(d['cooldown']), enabled: rule.enabled,
    metricName: String(m['name'] ?? ''), metricKind: String(m['kind'] ?? ''), aggregation: String(m['aggregation'] ?? ''), subject: String(m['subject'] ?? 'Service'), comparison: String(m['comparison'] ?? 'Above'), filters,
  };
}

/** The values of a filter row: one for Equals (as typed), several for In (comma separated). */
function valuesOf(row: FilterRow): string[] {
  return row.op === 'In' ? row.values.split(',').map(v => v.trim()).filter(Boolean) : row.values.trim() ? [row.values.trim()] : [];
}

function plural(n: number, word: string): string { return `${n} ${word}${n === 1 ? '' : 's'}`; }

/** A step as a person says it: 15 min, 1 h. */
function stepText(step: string): string {
  const s = seconds(step);
  if (s >= 3600 && s % 3600 === 0) return `${s / 3600} h`;
  if (s >= 60 && s % 60 === 0) return `${s / 60} min`;
  return `${s} s`;
}

/**
 * The form of one rule: new, changed, or only viewed (a viewer or operator, a rule from the deployment's file, or one the platform made for an SLO),
 * and its 24-hour preview. Rules stay typed templates (D5): nothing here takes a query. Whether it may save is asked again at the moment of saving, so a
 * form left open across a role change cannot send.
 */
@Component({
  selector: 'jc-rule-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, FieldError, DatePipe, DecimalPipe],
  template: `
    <section class="jc-card" data-testid="rule-form">
      <h2>{{ title() }}</h2>
      @if (lock(); as why) {
        <p class="jc-muted" role="note" data-testid="rule-readonly">{{ why === 'config' ? 'From the rules file. Change it there.' : 'Made by the platform for an SLO. Change the SLO instead.' }}</p>
      } @else if (ro()) {
        <p class="jc-muted jc-small" role="note" data-testid="rule-role-note">You can look at this rule. Changing it takes an admin.</p>
      }
      <datalist id="jc-rule-metrics">@for (m of catalog(); track m.name) { <option [value]="m.name"></option> }</datalist>
      <form class="jc-form jc-form-grid" (ngSubmit)="save()">
        @if (!rule()) {
          <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Key</mat-label><input matInput name="key" [(ngModel)]="f.key" (ngModelChange)="touch('key')" [disabled]="ro()" required [attr.aria-invalid]="bad('key')" [attr.aria-describedby]="bad('key') ? 'err-key' : null"></mat-form-field><jc-field-error field="key" [messages]="msgs('key')" /></div>
        }
        <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" (ngModelChange)="touch('name')" [disabled]="ro()" required [attr.aria-invalid]="bad('name')" [attr.aria-describedby]="bad('name') ? 'err-name' : null"></mat-form-field><jc-field-error field="name" [messages]="msgs('name')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Template</mat-label>
          <mat-select name="template" [attr.data-options]="templates().join(',')" [(ngModel)]="f.template" (ngModelChange)="touch('template')" [disabled]="ro()" [attr.aria-describedby]="bad('template') ? 'err-template' : null">@for (t of templates(); track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select>
        </mat-form-field><jc-field-error field="template" [messages]="msgs('template')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity</mat-label>
          <mat-select name="severity" [(ngModel)]="f.severity" (ngModelChange)="touch('severity')" [disabled]="ro()" [attr.aria-describedby]="bad('severity') ? 'err-severity' : null"><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
        </mat-form-field><jc-field-error field="severity" [messages]="msgs('severity')" /></div>
        @if (metric()) {
          <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Metric</mat-label><input matInput name="metricName" list="jc-rule-metrics" autocomplete="off" data-testid="rule-metric" [(ngModel)]="f.metricName" (ngModelChange)="metricChanged()" [disabled]="ro()" [attr.aria-invalid]="bad('metricName')" [attr.aria-describedby]="bad('metricName') ? 'err-metricName' : null"></mat-form-field><jc-field-error field="metricName" [messages]="msgs('metricName')" />
            @if (f.metricName.trim() && catalogKind()) { <div class="jc-muted jc-small" data-testid="rule-kind">Kind: {{ catalogKind() }}, from what this tenant reported in the last 24 hours.</div> }
            @else if (f.metricName.trim()) { <div class="jc-muted jc-small">Not reported in the last 24 hours: choose its kind.</div> }
          </div>
          @if (needsKind()) {
            <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Kind</mat-label>
              <mat-select name="metricKind" [(ngModel)]="f.metricKind" (ngModelChange)="kindChanged()" [disabled]="ro()" [attr.aria-describedby]="bad('metricKind') ? 'err-metricKind' : null">@for (k of kinds; track k) { <mat-option [value]="k">{{ k }}</mat-option> }</mat-select>
            </mat-form-field><jc-field-error field="metricKind" [messages]="msgs('metricKind')" /></div>
          }
          <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Aggregation</mat-label>
            <mat-select name="aggregation" data-testid="rule-aggregation" [attr.data-options]="aggregations().join(',')" [(ngModel)]="f.aggregation" (ngModelChange)="touch('aggregation')" [disabled]="ro()" [attr.aria-describedby]="bad('aggregation') ? 'err-aggregation' : null">@for (a of aggregations(); track a) { <mat-option [value]="a">{{ a }}</mat-option> }</mat-select>
          </mat-form-field><jc-field-error field="aggregation" [messages]="msgs('aggregation')" /></div>
          <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Subject</mat-label>
            <mat-select name="subject" [(ngModel)]="f.subject" (ngModelChange)="touch('subject')" [disabled]="ro()" [attr.aria-describedby]="bad('subject') ? 'err-subject' : null"><mat-option value="Service">Service</mat-option><mat-option value="Host">Host</mat-option></mat-select>
          </mat-form-field><jc-field-error field="subject" [messages]="msgs('subject')" /></div>
          <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Fires when</mat-label>
            <mat-select name="comparison" [(ngModel)]="f.comparison" (ngModelChange)="touch('comparison')" [disabled]="ro()" [attr.aria-describedby]="bad('comparison') ? 'err-comparison' : null"><mat-option value="Above">Above</mat-option><mat-option value="Below">Below</mat-option></mat-select>
          </mat-form-field><jc-field-error field="comparison" [messages]="msgs('comparison')" /></div>
        }
        @if (hostScoped()) {
          <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Host (all if empty)</mat-label><input matInput name="host" [(ngModel)]="f.host" (ngModelChange)="touch('host')" [disabled]="ro()" [attr.aria-invalid]="bad('host')" [attr.aria-describedby]="bad('host') ? 'err-host' : null"></mat-form-field><jc-field-error field="host" [messages]="msgs('host')" /></div>
        } @else {
          <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Service (all if empty)</mat-label><input matInput name="service" [(ngModel)]="f.service" (ngModelChange)="touch('service')" [disabled]="ro()" [attr.aria-invalid]="bad('service')" [attr.aria-describedby]="bad('service') ? 'err-service' : null"></mat-form-field><jc-field-error field="service" [messages]="msgs('service')" /></div>
          <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Environment (all if empty)</mat-label><input matInput name="environment" [(ngModel)]="f.environment" (ngModelChange)="touch('environment')" [disabled]="ro()" [attr.aria-invalid]="bad('environment')" [attr.aria-describedby]="bad('environment') ? 'err-environment' : null"></mat-form-field><jc-field-error field="environment" [messages]="msgs('environment')" /></div>
        }
        @if (metric()) {
          @for (row of f.filters; track row; let i = $index) {
            <div class="jc-field" [attr.data-testid]="'rule-filter-' + i" data-row><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Attribute</mat-label><input matInput [name]="'filterKey-' + i" [ngModelOptions]="{ standalone: true }" [(ngModel)]="row.key" (ngModelChange)="touch('filters')" [disabled]="ro()" [attr.aria-invalid]="bad('filters')" [attr.aria-describedby]="bad('filters') ? 'err-filters' : null"></mat-form-field></div>
            <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Match</mat-label>
              <mat-select [attr.name]="'filterOp-' + i" [ngModelOptions]="{ standalone: true }" [(ngModel)]="row.op" (ngModelChange)="touch('filters')" [disabled]="ro()"><mat-option value="Equals">Equals</mat-option><mat-option value="In">In</mat-option></mat-select>
            </mat-form-field></div>
            <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>{{ row.op === 'In' ? 'Values, comma separated' : 'Value' }}</mat-label><input matInput [name]="'filterValues-' + i" [ngModelOptions]="{ standalone: true }" [(ngModel)]="row.values" (ngModelChange)="touch('filters')" [disabled]="ro()" [attr.aria-invalid]="bad('filters')" [attr.aria-describedby]="bad('filters') ? 'err-filters' : null"></mat-form-field>
              @if (!ro()) { <button mat-button type="button" [attr.data-testid]="'rule-filter-remove-' + i" [attr.aria-label]="'Remove filter ' + (i + 1)" (click)="removeFilter(i)">Remove</button> }</div>
          }
          <div class="jc-form-actions jc-field-filters">
            @if (!ro()) { <button mat-button type="button" data-testid="rule-filter-add" (click)="addFilter()">Add attribute filter</button> }
            <jc-field-error field="filters" [messages]="msgs('filters')" />
          </div>
        }
        <p class="jc-muted jc-small jc-form-actions">{{ hint() }}</p>
        @if (offPack(); as pack) { <p class="jc-muted jc-small jc-form-actions" data-testid="rule-pack-off">{{ f.template }} reads the {{ pack }} signal pack, which this deployment has switched off: the rule can only be saved disabled.</p> }
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Threshold</mat-label><input matInput type="number" step="any" name="threshold" [(ngModel)]="f.threshold" (ngModelChange)="touch('threshold')" [disabled]="ro()" required [attr.aria-invalid]="bad('threshold')" [attr.aria-describedby]="bad('threshold') ? 'err-threshold' : null"></mat-form-field><jc-field-error field="threshold" [messages]="msgs('threshold')" />
          @if (unit()) { <div class="jc-muted jc-small" data-testid="rule-unit">Unit: {{ unit() }}</div> }</div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Recovery threshold</mat-label><input matInput type="number" step="any" name="recoveryThreshold" [(ngModel)]="f.recoveryThreshold" (ngModelChange)="touch('recoveryThreshold')" [disabled]="ro()" [attr.aria-invalid]="bad('recoveryThreshold')" [attr.aria-describedby]="bad('recoveryThreshold') ? 'err-recoveryThreshold' : null"></mat-form-field><jc-field-error field="recoveryThreshold" [messages]="msgs('recoveryThreshold')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Minimum samples</mat-label><input matInput type="number" name="minimumSamples" [(ngModel)]="f.minimumSamples" (ngModelChange)="touch('minimumSamples')" [disabled]="ro()" [attr.aria-invalid]="bad('minimumSamples')" [attr.aria-describedby]="bad('minimumSamples') ? 'err-minimumSamples' : null"></mat-form-field><jc-field-error field="minimumSamples" [messages]="msgs('minimumSamples')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Every (s)</mat-label><input matInput type="number" name="schedule" [(ngModel)]="f.scheduleSeconds" (ngModelChange)="touch('schedule')" [disabled]="ro()" [attr.aria-invalid]="bad('schedule')" [attr.aria-describedby]="bad('schedule') ? 'err-schedule' : null"></mat-form-field><jc-field-error field="schedule" [messages]="msgs('schedule')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Window (s)</mat-label><input matInput type="number" name="window" [(ngModel)]="f.windowSeconds" (ngModelChange)="touch('window')" [disabled]="ro()" [attr.aria-invalid]="bad('window')" [attr.aria-describedby]="bad('window') ? 'err-window' : null"></mat-form-field><jc-field-error field="window" [messages]="msgs('window')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Fire after (s)</mat-label><input matInput type="number" name="for" [(ngModel)]="f.forSeconds" (ngModelChange)="touch('forDuration')" [disabled]="ro()" [attr.aria-invalid]="bad('forDuration')" [attr.aria-describedby]="bad('forDuration') ? 'err-forDuration' : null"></mat-form-field><jc-field-error field="forDuration" [messages]="msgs('forDuration')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Recover after (s)</mat-label><input matInput type="number" name="recovery" [(ngModel)]="f.recoverySeconds" (ngModelChange)="touch('recoveryDuration')" [disabled]="ro()" [attr.aria-invalid]="bad('recoveryDuration')" [attr.aria-describedby]="bad('recoveryDuration') ? 'err-recoveryDuration' : null"></mat-form-field><jc-field-error field="recoveryDuration" [messages]="msgs('recoveryDuration')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Cooldown (s)</mat-label><input matInput type="number" name="cooldown" [(ngModel)]="f.cooldownSeconds" (ngModelChange)="touch('cooldown')" [disabled]="ro()" [attr.aria-invalid]="bad('cooldown')" [attr.aria-describedby]="bad('cooldown') ? 'err-cooldown' : null"></mat-form-field><jc-field-error field="cooldown" [messages]="msgs('cooldown')" /></div>
        <div class="jc-field"><mat-checkbox name="enabled" [(ngModel)]="f.enabled" (ngModelChange)="touch('enabled')" [disabled]="ro()" [attr.aria-describedby]="bad('enabled') ? 'err-enabled' : null">Enabled</mat-checkbox><jc-field-error field="enabled" [messages]="msgs('enabled')" /></div>
        @if (rule() && !ro()) { <p class="jc-muted jc-small jc-form-actions">Disabling this rule, or changing its template or scope, resolves its open alerts: nothing would be watching them any more.</p> }
        @if (summary()) { <p class="jc-error jc-small jc-form-actions" role="alert" data-testid="rule-error">{{ summary() }}</p> }
        <div class="jc-form-actions">
          @if (!ro()) { <button mat-flat-button type="submit" data-testid="rule-save" [disabled]="saving()">Save</button> }
          @if (canPreview()) { <button mat-stroked-button type="button" data-testid="rule-preview" [disabled]="previewing()" (click)="preview()">{{ previewing() ? 'Previewing...' : 'Preview last 24 h' }}</button> }
          <button mat-button type="button" (click)="closed.emit()">{{ ro() ? 'Close' : 'Cancel' }}</button>
        </div>
        @if (result(); as r) {
          <div class="jc-preview" [class.jc-stale]="stale()">
            @if (stale()) { <p class="jc-error jc-small" role="status" data-testid="rule-preview-stale">The rule changed since this preview. Preview again to see what it would do now.</p> }
            <p data-testid="rule-preview-result">{{ summaryText(r) }}
              <span class="jc-muted jc-small">The rule is evaluated at those steps, so this is approximate.
                @if (!r.complete) { {{ r.cut ?? 'The preview was cut short.' }}. }
                @if (r.quiet) { Never observes: {{ r.quiet }}. }
                @if (r.subjectsTruncated) { Only the first 50 subjects are shown, the ones that would fire most. }</span></p>
            @if (!r.subjects.length) { <p class="jc-muted jc-small">No subject had data in the last 24 hours.</p> }
            <ol>
              @for (s of r.subjects; track s.subjectKey; let i = $index) {
                <li class="jc-preview-subject" [attr.data-testid]="'rule-preview-subject-' + i">
                  <strong>{{ s.subjectKey }}</strong><span class="jc-muted"> · {{ s.subjectType }}</span>:
                  would fire {{ s.wouldFire }}, open {{ s.wouldOpen }}, {{ s.breaches }} over the threshold, {{ s.noData }} without data@if (s.firstFiredAt) {, first at {{ s.firstFiredAt | date: 'MMM d, HH:mm' }} }.
                  @if (s.last !== null) { <span class="jc-muted">Last {{ value(s.last, r.unit) }}@if (s.min !== null) {, lowest {{ value(s.min, r.unit) }}}@if (s.max !== null) {, highest {{ value(s.max, r.unit) }}}.</span> }
                  @for (d of detail(s); track d[0]) { <span class="jc-muted"> {{ d[0] }} {{ d[1] }}.</span> }
                  <div class="jc-strip" role="img" [attr.aria-label]="s.breaches + ' of ' + s.points.length + ' points over the threshold'">@for (p of s.points; track p.at) { <i [class]="'jc-pv-' + p.condition.toLowerCase()" [attr.title]="(p.at | date: 'MMM d, HH:mm') + ': ' + (p.value === null ? 'no data' : (p.value | number: '1.0-3')) + ', ' + p.condition"></i> }</div>
                </li>
              }
            </ol>
          </div>
        }
      </form>
    </section>`,
})
export class RuleEditor {
  private readonly api = inject(ApiService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly destroyRef = inject(DestroyRef);
  private readonly canAdmin = inject(OBSERVABILITY_CAN_ADMIN);
  private readonly canOperate = inject(OBSERVABILITY_CAN_OPERATE);

  /** The rule being changed or viewed; none for a new one. */
  readonly rule = input<Rule | null>(null);
  /** The templates the picker offers. */
  readonly templates = input<string[]>([]);
  readonly packs = input<SignalPack[]>([]);
  readonly saved = output<void>();
  readonly closed = output<void>();

  protected readonly kinds = KINDS;
  protected readonly f: RuleForm = { ...NEW_RULE, filters: [] };
  protected readonly saving = signal(false);
  protected readonly previewing = signal(false);
  /** One sentence about the whole save or preview (refused, stale, busy, unreachable, or a path no field here has), and the messages each field shows under itself. */
  protected readonly summary = signal('');
  protected readonly fieldMessages = signal<Record<string, string[]>>({});
  protected readonly result = signal<RulePreview | null>(null);
  private readonly edits = signal(0);
  private readonly resultEdits = signal(0);
  protected readonly stale = computed(() => this.result() !== null && this.resultEdits() !== this.edits());
  protected readonly catalog = signal<MetricInfo[]>([]);
  private readonly roles = signal({ admin: false, operate: false });

  /** Why the rule can't be changed by anyone: it comes from the deployment's file, or the platform made it. */
  protected readonly lock = computed(() => { const r = this.rule(); return r && r.managedBy !== 'api' ? (r.managedBy === 'platform' ? 'platform' : 'config') : null; });
  /** Whether this form only shows: a locked rule, or a person who is not an admin. */
  protected readonly ro = computed(() => this.lock() !== null || !this.roles().admin);
  protected readonly canPreview = computed(() => this.roles().operate && this.rule()?.template !== 'SloBurn');
  protected readonly title = computed(() => { const r = this.rule(); return !r ? 'New rule' : `${this.ro() ? 'View' : 'Change'} ${r.key}`; });
  protected readonly metric = computed(() => { this.edits(); return this.f.template === 'MetricThreshold'; });
  private readonly byName = computed(() => new Map(this.catalog().map(m => [m.name, m])));

  constructor() {
    this.refresh();
    this.api.metrics({ from: new Date(Date.now() - 86_400_000).toISOString(), to: new Date().toISOString() }).pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: page => { this.catalog.set(page.items ?? []); this.catalogArrived(); }, error: () => this.catalog.set([]) });
  }

  ngOnInit() { Object.assign(this.f, formFrom(this.rule())); this.edits.update(n => n + 1); }

  private refresh() { this.roles.set({ admin: !!this.canAdmin(), operate: !!this.canOperate() }); }

  protected hint(): string { this.edits(); return TEMPLATES[this.f.template] ?? ''; }
  protected hostScoped(): boolean { this.edits(); return HOST_TEMPLATES.includes(this.f.template) || (this.f.template === 'MetricThreshold' && this.f.subject === 'Host'); }
  protected offPack(): string | null { this.edits(); return this.packs().find(p => !p.enabled && p.ruleTemplates.includes(this.f.template))?.name ?? null; }

  /** What the tenant reported the metric as, when it did in the last 24 hours. */
  protected catalogKind(): string { this.edits(); return kindOf(this.byName().get(this.f.metricName.trim())); }
  protected needsKind(): boolean { this.edits(); return !!this.f.metricName.trim() && !this.catalogKind(); }
  private kind(): string { return this.catalogKind() || this.f.metricKind; }
  protected aggregations(): string[] { this.edits(); return AGGREGATIONS[this.kind()] ?? []; }
  protected unit(): string { this.edits(); return this.f.template === 'MetricThreshold' ? this.byName().get(this.f.metricName.trim())?.unit ?? '' : ''; }

  protected metricChanged() {
    // A kind the catalog gave belongs to that metric only: another name starts with none, unless the person chose one.
    if (this.catalogKind()) { this.f.metricKind = this.catalogKind(); this.kindFromCatalog = true; }
    else if (this.kindFromCatalog) { this.f.metricKind = ''; this.kindFromCatalog = false; }
    this.fitAggregation();
    this.touch('metricName');
    this.clear('metricKind');
  }
  private kindFromCatalog = false;
  /** The catalog came after the form opened: a kind it gives is taken, and the aggregation is made to fit it. */
  private catalogArrived() {
    const kind = this.catalogKind();
    if (!this.f.metricName.trim() || !kind || kind === this.f.metricKind) return;
    this.f.metricKind = kind;
    this.kindFromCatalog = true;
    this.fitAggregation();
    this.edits.update(n => n + 1);
  }
  protected kindChanged() { this.kindFromCatalog = false; this.fitAggregation(); this.touch('metricKind'); }
  private fitAggregation() {
    const fits = AGGREGATIONS[this.kind()] ?? [];
    if (!fits.includes(this.f.aggregation)) this.f.aggregation = fits[0] ?? '';
  }

  protected addFilter() {
    if (this.f.filters.length >= MAX_FILTERS) { this.setMessages({ ...this.fieldMessages(), filters: [`Filters: at most ${MAX_FILTERS}.`] }); return; }
    this.f.filters.push({ key: '', op: 'Equals', values: '' });
    this.touch('filters');
  }
  protected removeFilter(i: number) { this.f.filters.splice(i, 1); this.touch('filters'); }

  protected msgs(field: string): string[] { return this.fieldMessages()[field] ?? []; }
  protected bad(field: string): 'true' | null { return this.msgs(field).length ? 'true' : null; }
  private setMessages(m: Record<string, string[]>) { this.fieldMessages.set(m); }
  private clear(field: string) {
    if (!this.fieldMessages()[field]) return;
    const { [field]: _, ...rest } = this.fieldMessages();
    this.fieldMessages.set(rest);
  }
  /** An edit: takes its own message away, leaves the others, and makes a shown preview stale. */
  protected touch(field: string) { this.clear(field); this.edits.update(n => n + 1); }
  private reset() { this.summary.set(''); this.fieldMessages.set({}); }

  /** The fields the open form shows: a message for any other never reaches the person unless it goes to the sentence. */
  private shown(): Set<string> {
    const s = new Set(['name', 'template', 'severity', 'threshold', 'recoveryThreshold', 'minimumSamples', 'schedule', 'window', 'forDuration', 'recoveryDuration', 'cooldown', 'enabled']);
    if (!this.rule()) s.add('key');
    for (const k of this.hostScoped() ? ['host'] : ['service', 'environment']) s.add(k);
    if (this.f.template === 'MetricThreshold') for (const k of ['metricName', 'aggregation', 'subject', 'comparison', 'filters']) s.add(k);
    if (this.needsKind()) s.add('metricKind');
    return s;
  }
  private mark(fields: Record<string, string[]>, summary: string) {
    const shown = this.shown();
    const placed: Record<string, string[]> = {};
    const lost: string[] = [];
    for (const [k, v] of Object.entries(fields)) { if (shown.has(k)) placed[k] = v; else lost.push(...v); }
    this.fieldMessages.set(placed);
    this.summary.set([summary, ...lost].filter(Boolean).join(' '));
    // In the order the form shows them, so a select is found as well as an input.
    const first = FIELD_ORDER.find(k => placed[k]);
    if (first) setTimeout(() => {
      const el = (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>(`[name="${INPUT_NAME[first] ?? first}"]`);
      // A mat-checkbox carries the name on its host; the focusable control is the input inside it.
      (el?.matches('mat-checkbox') ? el.querySelector<HTMLElement>('input') : el)?.focus();
    });
  }

  /** Every field the rule needs, at once, so the person fixes them in one go. A preview needs no key or name: the API fills them. */
  private invalid(forPreview: boolean): Record<string, string[]> {
    const f = this.f;
    const out: Record<string, string[]> = {};
    if (!forPreview && !this.rule() && !f.key?.trim()) out['key'] = ['Key: enter a key.'];
    if (!forPreview && !f.name?.trim()) out['name'] = ['Name: enter a name.'];
    if (f.threshold === null || f.threshold === undefined || !Number.isFinite(Number(f.threshold))) out['threshold'] = ['Threshold: enter a number.'];
    if (f.template !== 'MetricThreshold') return out;
    if (!f.metricName.trim()) out['metricName'] = ['Metric: enter a metric name.'];
    else if (!this.kind()) out['metricKind'] = ['Kind: choose one, this metric was not reported in the last 24 hours.'];
    else if (!f.aggregation) out['aggregation'] = ['Aggregation: choose one.'];
    const filters: string[] = [];
    if (f.filters.length > MAX_FILTERS) filters.push(`Filters: at most ${MAX_FILTERS}.`);
    const seen = new Set<string>();
    for (const row of f.filters) {
      const key = row.key.trim();
      const named = `Filters ${key.slice(0, 40)}`.trim();
      const values = valuesOf(row);
      if (!key) filters.push('Filters: each filter needs an attribute.');
      else if (seen.has(key)) filters.push(`${named}: used more than once.`);
      seen.add(key);
      if (row.op === 'Equals' && values.length !== 1) filters.push(`${named}: Equals takes one value.`);
      if (row.op === 'In' && (values.length < 1 || values.length > MAX_VALUES)) filters.push(`${named}: In takes 1 to ${MAX_VALUES} values.`);
      if (values.some(v => v.length > MAX_VALUE_LENGTH)) filters.push(`${named}: each value is at most ${MAX_VALUE_LENGTH} characters.`);
    }
    if (filters.length) out['filters'] = filters;
    return out;
  }

  /** The typed body: the same for a save and a preview. */
  private body() {
    const f = this.f;
    const host = this.hostScoped();
    const body: Record<string, unknown> = {
      key: f.key, name: f.name, template: f.template, severity: f.severity, service: host ? '' : f.service.trim(), environment: host ? '' : f.environment.trim().toLowerCase(), host: host ? f.host.trim() : '',
      threshold: f.threshold, recoveryThreshold: f.recoveryThreshold, minimumSamples: f.minimumSamples, schedule: span(f.scheduleSeconds), window: span(f.windowSeconds), forDuration: span(f.forSeconds),
      recoveryDuration: span(f.recoverySeconds), cooldown: span(f.cooldownSeconds), enabled: f.enabled,
    };
    if (f.template === 'MetricThreshold') {
      body['metric'] = {
        name: f.metricName.trim(), kind: this.kind(), aggregation: f.aggregation, filters: f.filters.map(r => ({ key: r.key.trim(), op: r.op, values: valuesOf(r) })),
        subject: f.subject, comparison: f.comparison,
      };
    }
    return body;
  }

  private retry(e: { headers?: { get?: (name: string) => string | null } }): string {
    const n = Math.ceil(Number(e.headers?.get?.('Retry-After')));
    return n > 0 ? `Try again in ${plural(n, 'second')}.` : 'Try again in a moment.';
  }

  /** A failed save or preview: one sentence for a refusal of the whole thing, a message under each field the API names. */
  private fail(e: unknown, preview: boolean) {
    const err = e as { status?: number; headers?: { get?: (name: string) => string | null } };
    if (preview && (err?.status === 429 || err?.status === 503)) {
      this.mark({}, `${err.status === 429 ? 'Too many previews just now.' : 'The preview is busy or unavailable.'} ${this.retry(err)}`);
    } else if (!preview && err?.status === 412) {
      this.mark({}, 'This rule was changed by someone else after you opened it. Cancel and open it again to see the current version; your draft stays here until you do.');
    } else {
      const r = ruleFieldErrors(e);
      this.mark(r.fields, r.rest);
    }
  }

  protected save() {
    this.refresh();
    // Asked again here: a form left open across a role change, or a rule that is not this deployment's to change, never sends.
    if (this.saving() || this.ro()) return;
    const local = this.invalid(false);
    if (Object.keys(local).length) { this.mark(local, ''); return; }
    const editing = this.rule();
    const body = this.body();
    const request: Observable<Rule> = editing ? this.api.updateRule(editing.key, body, editing.etag) : this.api.createRule(body);
    this.saving.set(true);
    this.reset();
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.saving.set(false); this.saved.emit(); },
      error: e => { this.saving.set(false); this.fail(e, false); },
    });
  }

  protected preview() {
    this.refresh();
    if (this.previewing() || !this.canPreview()) return;
    const local = this.invalid(true);
    if (Object.keys(local).length) { this.mark(local, ''); return; }
    const sent = this.edits();
    this.previewing.set(true);
    this.reset();
    this.api.previewRule(this.body()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => { this.previewing.set(false); this.result.set(r); this.resultEdits.set(sent); },
      error: e => { this.previewing.set(false); this.fail(e, true); },
    });
  }

  protected summaryText(r: RulePreview): string {
    const fired = r.subjects.reduce((n, s) => n + s.wouldFire, 0);
    const on = r.subjects.filter(s => s.wouldFire > 0).length;
    const step = `sampled every ${stepText(r.step)}`;
    return (fired > 0 ? `Would have fired ${plural(fired, 'time')} on ${plural(on, 'subject')}, ${step}.` : `Would not have fired in the last 24 hours, ${step}.`);
  }
  protected detail(s: PreviewSubject): [string, string][] { return Object.entries(s.lastDetail ?? {}).filter(([k]) => k.toLowerCase() !== 'unit'); }
  /** A preview value with its unit once: bytes and seconds humanised, other units after the number. */
  protected value(v: number, unit: string | null | undefined): string {
    if (unit === 'By') return formatBytes(v);
    if (unit === 's') return formatDuration(v * 1000);
    const n = formatNumber(v, 'en-US', '1.0-3');
    return unit ? `${n} ${unit}` : n;
  }
}
