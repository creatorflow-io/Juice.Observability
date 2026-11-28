import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
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
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/**
 * Silences and maintenance windows. Alerts go on firing and resolving under one: only what is sent changes, and what was held is sent when it ends if it still matters. Every
 * silence has an end and a reason, and shows how much it's holding, so a forgotten one can't quietly hide an outage.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar],
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
      <form class="jc-form" data-testid="silence-form" (ngSubmit)="create()">
        <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Kind</mat-label>
          <mat-select name="kind" [(ngModel)]="kind"><mat-option value="Silence">Silence, from now</mat-option><mat-option value="Maintenance">Maintenance window</mat-option></mat-select>
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Why</mat-label><input matInput name="comment" data-testid="silence-comment" [(ngModel)]="comment" maxlength="500"></mat-form-field>
        @if (kind === 'Maintenance') {
          <mat-form-field subscriptSizing="dynamic"><mat-label>Starts</mat-label><input matInput type="datetime-local" name="starts" [(ngModel)]="starts"></mat-form-field>
          <mat-checkbox name="weekly" [(ngModel)]="weekly">Every week</mat-checkbox>
        }
        <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>For (hours)</mat-label><input matInput type="number" min="0.1" step="0.5" name="hours" data-testid="silence-hours" [(ngModel)]="hours"></mat-form-field>
        <p class="jc-muted jc-small jc-form-actions">What to hold. Everything filled in must match.</p>
        <mat-form-field subscriptSizing="dynamic"><mat-label>Service</mat-label><input matInput name="service" data-testid="silence-service" [(ngModel)]="serviceField" [disabled]="all"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic"><mat-label>Environment</mat-label><input matInput name="environment" [(ngModel)]="environmentField" [disabled]="all"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic"><mat-label>Host</mat-label><input matInput name="host" [(ngModel)]="hostField" [disabled]="all"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic"><mat-label>Rules, comma separated</mat-label><input matInput name="rules" [(ngModel)]="rules" [disabled]="all"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity up to</mat-label>
          <mat-select name="maximum" [(ngModel)]="maximum" [disabled]="all"><mat-option value="">Any</mat-option><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option></mat-select>
        </mat-form-field>
        <mat-checkbox name="all" [(ngModel)]="all">Everything</mat-checkbox>
        <div class="jc-form-actions">
          <button mat-flat-button type="submit" data-testid="silence-save" [disabled]="saving()">{{ kind === 'Silence' ? 'Silence' : 'Schedule' }}</button>
          @if (saveError()) { <span class="jc-error" data-testid="silence-error">{{ saveError() }}</span> }
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
  protected readonly saveError = signal('');
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

  protected create() {
    const start = this.kind === 'Maintenance' && this.starts ? new Date(this.starts) : new Date();
    const end = new Date(start.getTime() + (this.hours || 0) * 3_600_000);
    this.saving.set(true);
    this.saveError.set('');
    this.api.createSilence({
      kind: this.kind, comment: this.comment, startsAt: this.kind === 'Maintenance' ? start.toISOString() : undefined, endsAt: end.toISOString(), repeatWeekly: this.kind === 'Maintenance' && this.weekly,
      allAlerts: this.all, service: this.all ? undefined : this.serviceField, environment: this.all ? undefined : this.environmentField, host: this.all ? undefined : this.hostField,
      ruleKeys: this.all ? [] : this.rules.split(',').map(r => r.trim()).filter(Boolean), maximumSeverity: this.all ? undefined : this.maximum || undefined,
    }).subscribe({
      next: () => { this.saving.set(false); this.comment = ''; this.silences.reload(); },
      error: e => { this.saving.set(false); this.saveError.set((e as { status?: number }).status === 403 ? 'Silencing needs the operator or admin role.' : problemText(e)); },
    });
  }

  protected cancel(s: Silence) {
    this.api.cancelSilence(s.id).subscribe({
      next: () => this.silences.reload(),
      error: e => this.saveError.set((e as { status?: number }).status === 403 ? 'Ending a silence needs the operator or admin role.' : problemText(e)),
    });
  }
}
