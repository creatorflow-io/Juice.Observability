import { ChangeDetectionStrategy, Component, ElementRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { Silence } from '../core/models';
import { FieldError, FormMarks, SILENCE_FIELDS, ruleFieldErrors } from '../shared/form-errors';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/**
 * Silences and maintenance windows. Alerts go on firing and resolving under one: only what is sent changes, and what was held is sent when it ends if it still matters. Every
 * silence has an end and a reason, and shows how much it's holding, so a forgotten one can't quietly hide an outage.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar, FieldError],
  template: `
    <jc-context-bar>
      <h1>Silences</h1>
      <span class="jc-spacer"></span>
      <mat-checkbox [ngModel]="showAll()" (ngModelChange)="showAll.set($event)" data-testid="silences-all">Include ended</mat-checkbox>
    </jc-context-bar>
    @if (silences.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (silences.error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card">
      <h2>New</h2>
      <form class="jc-form jc-form-grid" data-testid="silence-form" (ngSubmit)="create()" novalidate>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Kind</mat-label>
          <mat-select name="kind" data-field="kind"[(ngModel)]="kind" [attr.aria-describedby]="marks.describedBy('kind')"><mat-option value="Silence">Silence, from now</mat-option><mat-option value="Maintenance">Maintenance window</mat-option></mat-select>
        </mat-form-field><jc-field-error field="kind" [messages]="marks.msgs('kind')" /></div>
        <div class="jc-field jc-field-wide"><mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Why</mat-label><input matInput name="comment" data-field="comment"data-testid="silence-comment" [(ngModel)]="comment" (ngModelChange)="marks.clear('comment')" maxlength="500" required [attr.aria-invalid]="marks.bad('comment')" [attr.aria-describedby]="marks.describedBy('comment')"></mat-form-field><jc-field-error field="comment" [messages]="marks.msgs('comment')" /></div>
        @if (kind === 'Maintenance') {
          <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Starts</mat-label><input matInput type="datetime-local" name="starts" data-field="starts"[(ngModel)]="starts" (ngModelChange)="marks.clear('starts')" [attr.aria-invalid]="marks.bad('starts')" [attr.aria-describedby]="marks.describedBy('starts')"></mat-form-field><jc-field-error field="starts" [messages]="marks.msgs('starts')" /></div>
          <div class="jc-field"><mat-checkbox name="weekly" data-field="weekly"[(ngModel)]="weekly" (ngModelChange)="marks.clear('weekly')" [attr.aria-describedby]="marks.describedBy('weekly')">Every week</mat-checkbox><jc-field-error field="weekly" [messages]="marks.msgs('weekly')" /></div>
        }
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>For (hours)</mat-label><input matInput type="number" min="0.1" step="0.5" name="hours" data-field="hours"data-testid="silence-hours" [(ngModel)]="hours" (ngModelChange)="marks.clear('hours')" required [attr.aria-invalid]="marks.bad('hours')" [attr.aria-describedby]="marks.describedBy('hours')"></mat-form-field><jc-field-error field="hours" [messages]="marks.msgs('hours')" /></div>
        <p class="jc-muted jc-small jc-form-actions">What to hold. Everything filled in must match.</p>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Service</mat-label><input matInput name="service" data-field="service"data-testid="silence-service" [(ngModel)]="serviceField" (ngModelChange)="marks.clear('service')" [disabled]="all" [attr.aria-invalid]="marks.bad('service')" [attr.aria-describedby]="marks.describedBy('service')"></mat-form-field><jc-field-error field="service" [messages]="marks.msgs('service')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Environment</mat-label><input matInput name="environment" data-field="environment"[(ngModel)]="environmentField" (ngModelChange)="marks.clear('environment')" [disabled]="all" [attr.aria-invalid]="marks.bad('environment')" [attr.aria-describedby]="marks.describedBy('environment')"></mat-form-field><jc-field-error field="environment" [messages]="marks.msgs('environment')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Host</mat-label><input matInput name="host" data-field="host"[(ngModel)]="hostField" (ngModelChange)="marks.clear('host')" [disabled]="all" [attr.aria-invalid]="marks.bad('host')" [attr.aria-describedby]="marks.describedBy('host')"></mat-form-field><jc-field-error field="host" [messages]="marks.msgs('host')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Rules, comma separated</mat-label><input matInput name="rules" data-field="rules"[(ngModel)]="rules" (ngModelChange)="marks.clear('rules')" [disabled]="all" [attr.aria-invalid]="marks.bad('rules')" [attr.aria-describedby]="marks.describedBy('rules')"></mat-form-field><jc-field-error field="rules" [messages]="marks.msgs('rules')" /></div>
        <div class="jc-field"><mat-form-field subscriptSizing="dynamic"><mat-label>Severity up to</mat-label>
          <mat-select name="maximum" data-field="maximum"[(ngModel)]="maximum" (ngModelChange)="marks.clear('maximum')" [disabled]="all" [attr.aria-describedby]="marks.describedBy('maximum')"><mat-option value="">Any</mat-option><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option></mat-select>
        </mat-form-field><jc-field-error field="maximum" [messages]="marks.msgs('maximum')" /></div>
        <div class="jc-field"><mat-checkbox name="all" [(ngModel)]="all">Everything</mat-checkbox></div>
        @if (marks.summary()) { <p class="jc-error jc-small jc-form-actions" role="alert" data-testid="silence-error">{{ marks.summary() }}</p> }
        <div class="jc-form-actions">
          <button mat-flat-button type="submit" data-testid="silence-save" [disabled]="saving()">{{ kind === 'Silence' ? 'Silence' : 'Schedule' }}</button>
          @if (cancelError()) { <span class="jc-error" data-testid="silence-cancel-error">{{ cancelError() }}</span> }
        </div>
      </form>
    </section>

    <table class="jc-table" data-testid="silences">
      <thead><tr><th>Why</th><th>Holds</th><th>When</th><th>Holding</th><th>By</th><th></th></tr></thead>
      <tbody>
        @for (s of silences.value()?.items ?? []; track s.id) {
          <tr [class.jc-muted]="s.cancelledAt">
            <td><strong>{{ s.comment }}</strong><div class="jc-small">@if (s.active) { <span class="jc-badge jc-health-degraded">Active</span> } @else if (s.cancelledAt) { Cancelled by {{ s.cancelledBy }} } @else { {{ s.kind === 'Maintenance' ? 'Maintenance' : 'Silence' }} }</div></td>
            <td class="jc-small">{{ scope(s) }}</td>
            <td class="jc-nowrap jc-small">{{ s.startsAt | date: 'MMM d, HH:mm' }} to {{ s.endsAt | date: 'MMM d, HH:mm' }}@if (s.repeatWeekly) { <div>every week</div> }</td>
            <td>{{ s.held }}</td>
            <td>{{ s.createdBy }}</td>
            <td class="jc-row-actions">@if (!s.cancelledAt && !over(s)) { <button mat-button type="button" [attr.data-testid]="'silence-cancel-' + s.id" (click)="cancel(s)">End now</button> }</td>
          </tr>
        } @empty { <tr><td colspan="6" class="jc-muted">No silences: every matching alert notifies.</td></tr> }
      </tbody>
    </table>`,
})
export class SilencesPage implements OnInit {
  private readonly api = inject(ApiService);
  /** Filled in from an alert's "Silence this". */
  readonly service = input<string>();
  readonly environment = input<string>();
  readonly host = input<string>();

  protected readonly showAll = signal(false);
  protected readonly saving = signal(false);
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly marks = new FormMarks();
  protected readonly cancelError = signal('');
  protected kind = 'Silence';
  protected comment = '';
  protected hours = 1;
  protected starts = '';
  protected weekly = false;
  protected serviceField = '';
  protected environmentField = '';
  protected hostField = '';
  protected rules = '';
  protected maximum = '';
  protected all = false;

  protected readonly silences = apiResource({ params: () => ({ all: this.showAll() }), stream: ({ params }) => this.api.silences({ all: params.all || null }) });
  protected readonly error = computed(() => problemText(this.silences.error()));

  ngOnInit() {
    this.serviceField = this.service() ?? '';
    this.environmentField = this.environment() ?? '';
    this.hostField = this.host() ?? '';
  }

  protected scope(s: Silence): string {
    if (s.allAlerts) return 'Everything';
    return [s.service, s.environment, s.host, s.ruleKeys.join(', '), s.maximumSeverity ? `up to ${s.maximumSeverity}` : ''].filter(Boolean).join(' · ');
  }

  protected over(s: Silence): boolean { return !s.repeatWeekly && new Date(s.endsAt).getTime() <= Date.now(); }

  /** The fields the open form shows, in the order it shows them. */
  private order(): string[] {
    return ['kind', 'comment', ...(this.kind === 'Maintenance' ? ['starts', 'weekly'] : []), 'hours', ...(this.all ? [] : ['service', 'environment', 'host', 'rules', 'maximum'])];
  }

  /** Every required field at once, so the person fixes them in one go. */
  private invalid(): Record<string, string[]> {
    const out: Record<string, string[]> = {};
    if (!this.comment.trim()) out['comment'] = ['Why: say why, for whoever wonders where the alerts went.'];
    if (!(Number(this.hours) > 0)) out['hours'] = ['For (hours): enter a number above 0.'];
    return out;
  }

  protected create() {
    if (this.saving()) return;
    const local = this.invalid();
    if (Object.keys(local).length) { this.marks.mark(local, '', this.order(), this.el.nativeElement); return; }
    const start = this.kind === 'Maintenance' && this.starts ? new Date(this.starts) : new Date();
    const end = new Date(start.getTime() + (this.hours || 0) * 3_600_000);
    this.saving.set(true);
    this.marks.reset();
    this.cancelError.set('');
    this.api.createSilence({
      kind: this.kind, comment: this.comment, startsAt: this.kind === 'Maintenance' ? start.toISOString() : undefined, endsAt: end.toISOString(), repeatWeekly: this.kind === 'Maintenance' && this.weekly,
      allAlerts: this.all, service: this.all ? undefined : this.serviceField, environment: this.all ? undefined : this.environmentField, host: this.all ? undefined : this.hostField,
      ruleKeys: this.all ? [] : this.rules.split(',').map(r => r.trim()).filter(Boolean), maximumSeverity: this.all ? undefined : this.maximum || undefined,
    }).subscribe({
      next: () => { this.saving.set(false); this.comment = ''; this.silences.reload(); },
      error: e => {
        this.saving.set(false);
        // A 403, a 412 or a failed connection says one thing about the whole form; a 400 or 422 is placed by its paths.
        const r = ruleFieldErrors(e, SILENCE_FIELDS);
        const forbidden = (e as { status?: number }).status === 403;
        this.marks.mark(r.fields, forbidden ? 'Silencing needs the operator or admin role.' : r.rest, this.order(), this.el.nativeElement);
      },
    });
  }

  protected cancel(s: Silence) {
    this.cancelError.set('');
    this.api.cancelSilence(s.id).subscribe({
      next: () => this.silences.reload(),
      error: e => this.cancelError.set((e as { status?: number }).status === 403 ? 'Ending a silence needs the operator or admin role.' : problemText(e)),
    });
  }
}
