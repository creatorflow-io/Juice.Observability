import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { apiResource } from '../shared/resource';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin, map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Rule } from '../core/models';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';

const TEMPLATES: Record<string, string> = {
  ErrorRate: 'Ratio of failed requests, 0 to 1. Fires above the threshold.',
  LatencyP95: 'Milliseconds at the 95th percentile. Fires above the threshold.',
  ThroughputAbsent: 'Requests in the window, for a service that had traffic. Fires at or below the threshold.',
  DeliveryFailureRate: 'Ratio of failed deliveries, 0 to 1. Fires above the threshold.',
  HostMemory: 'Ratio of host memory used, 0 to 1. Fires above the threshold.',
  MediatorErrors: 'Mediator errors in the window. Fires above the threshold.',
  ConsumerFailures: 'Failed consumer messages in the window. Fires above the threshold.',
  SloBurn: "An SLO's burn rate over a long and a short window. Fires when both reach the rate; one alert per SLO.",
};

/** A TimeSpan as the API writes it (`1.02:03:04`) in seconds. */
function seconds(value: unknown): number {
  const match = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(String(value ?? ''));
  return match ? (+(match[1] ?? 0)) * 86400 + +match[2] * 3600 + +match[3] * 60 + +match[4] : 0;
}

function span(total: number): string {
  const s = Math.max(0, Math.round(total || 0));
  const pad = (n: number) => String(n).padStart(2, '0');
  const days = Math.floor(s / 86400);
  return `${days ? `${days}.` : ''}${pad(Math.floor(s / 3600) % 24)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`;
}

interface RuleForm {
  key: string; name: string; template: string; severity: string; service: string; environment: string; host: string; threshold: number | null; recoveryThreshold: number | null;
  minimumSamples: number; scheduleSeconds: number; windowSeconds: number; forSeconds: number; recoverySeconds: number; cooldownSeconds: number; enabled: boolean;
}

const NEW_RULE: RuleForm = {
  key: '', name: '', template: 'ErrorRate', severity: 'Warning', service: '', environment: '', host: '', threshold: null, recoveryThreshold: null,
  minimumSamples: 10, scheduleSeconds: 60, windowSeconds: 300, forSeconds: 120, recoverySeconds: 120, cooldownSeconds: 600, enabled: true,
};

/**
 * Rules and their current state (Console requirements §14), and changing them for admins. They stay typed templates (D5): this page never takes a query.
 * A rule in the deployment's file is shown but changed there. Disabling one, or changing what it watches, closes its open alerts, which the form says first.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar>
      <h1>Rules</h1>
      <span class="jc-spacer"></span>
      <button mat-flat-button type="button" data-testid="rule-new" (click)="startNew()">New rule</button>
    </jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (form(); as f) {
      <section class="jc-card" data-testid="rule-form">
        <h2>{{ editing() ? 'Change ' + editing()!.key : 'New rule' }}</h2>
        <form class="jc-form" (ngSubmit)="save(f)">
          @if (!editing()) { <mat-form-field subscriptSizing="dynamic"><mat-label>Key</mat-label><input matInput name="key" [(ngModel)]="f.key" required></mat-form-field> }
          <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" required></mat-form-field>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Template</mat-label>
            <mat-select name="template" [(ngModel)]="f.template">@for (t of templates; track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity</mat-label>
            <mat-select name="severity" [(ngModel)]="f.severity"><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
          </mat-form-field>
          @if (f.template === 'HostMemory') {
            <mat-form-field subscriptSizing="dynamic"><mat-label>Host (all if empty)</mat-label><input matInput name="host" [(ngModel)]="f.host"></mat-form-field>
          } @else {
            <mat-form-field subscriptSizing="dynamic"><mat-label>Service (all if empty)</mat-label><input matInput name="service" [(ngModel)]="f.service"></mat-form-field>
            <mat-form-field subscriptSizing="dynamic"><mat-label>Environment (all if empty)</mat-label><input matInput name="environment" [(ngModel)]="f.environment"></mat-form-field>
          }
          <p class="jc-muted jc-small jc-form-actions">{{ hint(f.template) }}</p>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Threshold</mat-label><input matInput type="number" step="any" name="threshold" [(ngModel)]="f.threshold" required></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Recovery threshold</mat-label><input matInput type="number" step="any" name="recoveryThreshold" [(ngModel)]="f.recoveryThreshold"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Minimum samples</mat-label><input matInput type="number" name="minimumSamples" [(ngModel)]="f.minimumSamples"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Every (s)</mat-label><input matInput type="number" name="schedule" [(ngModel)]="f.scheduleSeconds"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Window (s)</mat-label><input matInput type="number" name="window" [(ngModel)]="f.windowSeconds"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Fire after (s)</mat-label><input matInput type="number" name="for" [(ngModel)]="f.forSeconds"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Recover after (s)</mat-label><input matInput type="number" name="recovery" [(ngModel)]="f.recoverySeconds"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Cooldown (s)</mat-label><input matInput type="number" name="cooldown" [(ngModel)]="f.cooldownSeconds"></mat-form-field>
          <mat-checkbox name="enabled" [(ngModel)]="f.enabled">Enabled</mat-checkbox>
          @if (editing()) { <p class="jc-muted jc-small jc-form-actions">Disabling this rule, or changing its template or scope, resolves its open alerts: nothing would be watching them any more.</p> }
          <div class="jc-form-actions">
            <button mat-flat-button type="submit" data-testid="rule-save" [disabled]="saving()">Save</button>
            <button mat-button type="button" (click)="form.set(null)">Cancel</button>
            @if (saveError()) { <span class="jc-error" data-testid="rule-error">{{ saveError() }}</span> }
          </div>
        </form>
      </section>
    }
    <table class="jc-table" data-testid="rules">
      <thead><tr><th>Rule</th><th>Template</th><th>Severity</th><th>Threshold</th><th>Recovery</th><th>Wait</th><th>Open alerts</th><th>Last evaluation</th><th></th></tr></thead>
      <tbody>
        @for (r of rows(); track r.key) {
          <tr [class.jc-muted]="!r.enabled">
            <td><strong>{{ r.name }}</strong><div class="jc-muted jc-small">{{ r.key }}@if (!r.enabled) { · disabled }@if (r.managedBy === 'config') { · from the rules file }@if (r.managedBy === 'platform') { · the platform's, for SLOs }</div></td>
            <td>{{ r.template }}</td>
            <td>{{ r.severity }}</td>
            <td>{{ r.definition['threshold'] ?? '—' }}</td>
            <td>{{ r.definition['recoveryThreshold'] ?? '—' }}</td>
            <td>{{ r.definition['forDuration'] ?? '—' }}</td>
            <td>
              @for (a of r.open; track a.id) {
                <div><a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.state }}</a> {{ a.subjectKey }} = {{ a.lastValue ?? 'no data' }}</div>
              } @empty { <span class="jc-muted">none</span> }
            </td>
            <td>{{ r.lastEvaluatedAt | ago }}</td>
            <td class="jc-row-actions">@if (r.managedBy === 'api') { <button mat-button type="button" [attr.data-testid]="'rule-edit-' + r.key" (click)="edit(r)">Change</button> }</td>
          </tr>
        }
      </tbody>
    </table>`,
})
export class RulesPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  /** What a rule can be made from: SloBurn is described, but its rules are the platform's own, made for SLOs. */
  protected readonly templates = Object.keys(TEMPLATES).filter(t => t !== 'SloBurn');
  protected readonly form = signal<RuleForm | null>(null);
  protected readonly editing = signal<Rule | null>(null);
  protected readonly saving = signal(false);
  protected readonly saveError = signal('');
  protected readonly data = apiResource({
    stream: () => forkJoin({
      rules: this.api.rules().pipe(map(p => p.items)),
      firing: this.api.allAlerts({ state: 'firing' }).pipe(map(p => p.items)),
      pending: this.api.allAlerts({ state: 'pending' }).pipe(map(p => p.items)),
    }),
  });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly rows = computed(() => {
    const d = this.data.value();
    if (!d) return [];
    const open = [...d.firing, ...d.pending];
    return d.rules.map(r => {
      const mine = open.filter(a => a.rule === r.key);
      const last = mine.map(a => a.lastEvaluatedAt).sort().at(-1) ?? null;
      return { ...r, open: mine, lastEvaluatedAt: last };
    });
  });

  protected hint(template: string): string { return TEMPLATES[template] ?? ''; }

  protected startNew() { this.editing.set(null); this.saveError.set(''); this.form.set({ ...NEW_RULE }); }

  protected edit(rule: Rule) {
    const d = rule.definition;
    this.editing.set(rule);
    this.saveError.set('');
    this.form.set({
      key: rule.key, name: rule.name, template: rule.template, severity: rule.severity, service: String(d['service'] ?? ''), environment: String(d['environment'] ?? ''), host: String(d['host'] ?? ''),
      threshold: d['threshold'] as number, recoveryThreshold: (d['recoveryThreshold'] as number | null) ?? null, minimumSamples: d['minimumSamples'] as number, scheduleSeconds: seconds(d['schedule']), windowSeconds: seconds(d['window']),
      forSeconds: seconds(d['forDuration']), recoverySeconds: seconds(d['recoveryDuration']), cooldownSeconds: seconds(d['cooldown']), enabled: rule.enabled,
    });
  }

  protected save(f: RuleForm) {
    const host = f.template === 'HostMemory';
    const body = {
      key: f.key, name: f.name, template: f.template, severity: f.severity, service: host ? '' : f.service.trim(), environment: host ? '' : f.environment.trim().toLowerCase(), host: host ? f.host.trim() : '',
      threshold: f.threshold, recoveryThreshold: f.recoveryThreshold, minimumSamples: f.minimumSamples, schedule: span(f.scheduleSeconds), window: span(f.windowSeconds), forDuration: span(f.forSeconds),
      recoveryDuration: span(f.recoverySeconds), cooldown: span(f.cooldownSeconds), enabled: f.enabled,
    };
    const editing = this.editing();
    const request: Observable<Rule> = editing ? this.api.updateRule(editing.key, body, editing.etag) : this.api.createRule(body);
    this.saving.set(true);
    this.saveError.set('');
    request.subscribe({
      next: () => { this.saving.set(false); this.form.set(null); this.data.reload(); },
      error: e => { this.saving.set(false); this.saveError.set(problemText(e)); },
    });
  }
}
