import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe, ReasonPipe } from '../shared/format';
import { ContextBar, HealthBadge, KeyValueTable } from '../shared/ui';
import { problemText } from '../shared/telemetry';

/**
 * One alert (Console requirements §14): evidence, lifecycle history, the subject's health and reasons, the way into
 * the investigation, and acknowledgement for operators.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    ContextBar, HealthBadge, KeyValueTable, AgoPipe, ReasonPipe],
  template: `
    <jc-context-bar>
      <a routerLink="/alerts" [queryParams]="ctx.keep()" class="jc-crumb">Alerts</a><span class="jc-crumb-sep">›</span>
      <h1>{{ detail.value()?.alert?.rule ?? '…' }}</h1>
    </jc-context-bar>
    @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (detail.value(); as d) {
      <div class="jc-summary">
        <span class="jc-badge" [class.jc-health-unhealthy]="d.alert.severity === 'Critical'" [class.jc-health-degraded]="d.alert.severity === 'Warning'">{{ d.alert.severity }}</span>
        <strong>{{ d.alert.state }}</strong>
        <span>{{ d.alert.subjectKey }}</span>
        @if (d.alert.noData) { <span class="jc-muted">no data at the last evaluation: state kept</span> }
        @if (d.alert.subjectType === 'service' && d.alert.subjectId) {
          <a mat-flat-button [routerLink]="['/services', d.alert.subjectId]" [queryParams]="investigate(d.alert.environment)">Investigate</a>
          <a mat-stroked-button [routerLink]="['/services', d.alert.subjectId]" [queryParams]="investigate(d.alert.environment, { tab: 'traces', status: 'error' })">Error traces</a>
        }
        @for (l of d.links; track l.url) { <a mat-button [href]="l.url" target="_blank" rel="noopener">{{ l.title }} ↗</a> }
      </div>
      <div class="jc-grid-2">
        <section class="jc-card"><h2>Evidence</h2><jc-kv [values]="d.evidence" /></section>
        <section class="jc-card">
          <h2>Subject health</h2>
          @for (h of d.subjectHealth; track h.subjectId + h.environment) {
            <p><jc-health [state]="h.state" [stale]="h.stale" /> {{ h.environment }}</p>
            <ul class="jc-reasons">@for (r of h.reasons; track r) { <li>{{ r | reason }}</li> }</ul>
          } @empty { <p class="jc-muted">No health for this subject.</p> }
        </section>
      </div>
      <section class="jc-card">
        <h2>History</h2>
        <table class="jc-table"><tbody>
          @for (t of d.history; track t.at + t.to) {
            <tr><td>{{ t.at | date: 'MMM d, HH:mm:ss' }}</td><td>{{ t.from ?? '—' }} → <strong>{{ t.to }}</strong></td><td>{{ t.reason | reason }}</td><td>{{ t.value ?? '—' }}</td></tr>
          }
        </tbody></table>
      </section>
      <section class="jc-card">
        <h2>Acknowledgement</h2>
        @if (d.alert.acknowledgedAt) {
          <p>By <strong>{{ d.alert.acknowledgedBy }}</strong> {{ d.alert.acknowledgedAt | ago }}@if (d.acknowledgeComment) {: “{{ d.acknowledgeComment }}”}</p>
        } @else if (d.alert.state !== 'Resolved') {
          <form (ngSubmit)="acknowledge()" class="jc-filter-form">
            <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Comment</mat-label><input matInput name="comment" [(ngModel)]="comment" maxlength="1000"></mat-form-field>
            <button mat-flat-button type="submit" [disabled]="sending()">Acknowledge</button>
          </form>
          @if (ackError()) { <p class="jc-error">{{ ackError() }}</p> }
        } @else { <p class="jc-muted">Resolved without acknowledgement.</p> }
      </section>
    }`,
})
export class AlertPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly id = input.required<string>();
  protected comment = '';
  protected readonly sending = signal(false);
  protected readonly ackError = signal('');

  protected readonly detail = rxResource({ params: () => this.id(), stream: ({ params }) => this.api.alert(params) });
  protected readonly error = computed(() => problemText(this.detail.error()));

  /** The service page over the alert's environment, from shortly before the breach. */
  investigate(environment: string | null, extra: Record<string, string> = {}) {
    const alert = this.detail.value()?.alert;
    const start = alert?.breachStartedAt ? new Date(alert.breachStartedAt).getTime() - 15 * 60_000 : null;
    const end = alert?.resolvedAt ? new Date(alert.resolvedAt).getTime() + 5 * 60_000 : Date.now();
    const window = start && end - start <= 24 * 3_600_000
      ? { from: new Date(start).toISOString(), to: new Date(end).toISOString(), range: null } : {};
    return this.ctx.keep({ env: environment, ...window, ...extra });
  }

  acknowledge() {
    this.sending.set(true);
    this.ackError.set('');
    this.api.acknowledge(this.id(), this.comment).subscribe({
      next: () => { this.sending.set(false); this.detail.reload(); },
      error: e => {
        this.sending.set(false);
        this.ackError.set((e as { status?: number }).status === 403 ? 'Acknowledging needs the operator or admin role.' : problemText(e));
      },
    });
  }
}
