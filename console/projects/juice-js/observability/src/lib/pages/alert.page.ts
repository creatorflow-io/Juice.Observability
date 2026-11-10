import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { apiResource } from '../shared/resource';
import { DatePipe, LowerCasePipe } from '@angular/common';
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
import { SuggestionsCard } from '../shared/suggestions';
import { CorrelationDecision } from '../core/models';

/**
 * One alert (Console requirements §14): evidence, lifecycle history, the subject's health and reasons, the way into
 * the investigation, and acknowledgement for operators.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, LowerCasePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    ContextBar, HealthBadge, KeyValueTable, AgoPipe, ReasonPipe, SuggestionsCard],
  template: `
    <jc-context-bar>
      <a [routerLink]="['alerts'] | jcLink" [queryParams]="ctx.keep()" class="jc-crumb">Alerts</a><span class="jc-crumb-sep">›</span>
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
          <a mat-flat-button [routerLink]="['services', d.alert.subjectId] | jcLink" [queryParams]="investigate(d.alert.environment)">Investigate</a>
          <a mat-stroked-button [routerLink]="['services', d.alert.subjectId] | jcLink" [queryParams]="investigate(d.alert.environment, { tab: 'traces', status: 'error' })">Error traces</a>
        }
        @for (l of d.links; track l.url) { <a mat-button [href]="l.url" target="_blank" rel="noopener">{{ l.title }} ↗</a> }
        @if (d.alert.state !== 'Resolved') {
          <a mat-stroked-button data-testid="alert-open-incident" [routerLink]="['incidents'] | jcLink" [queryParams]="incidentLink(d.alert.id, d.alert.rule, d.alert.subjectKey)">Open an incident</a>
          <a mat-stroked-button data-testid="alert-silence" [routerLink]="['silences'] | jcLink" [queryParams]="silenceLink(d.alert.subjectKey, d.alert.environment, d.alert.subjectType)">Silence this</a>
        }
      </div>
      @if (notifications.value(); as n) {
        @for (s of n.silences; track s.id) {
          <p class="jc-notice" data-testid="alert-silenced">Notifications are held until {{ s.endsAt | date: 'MMM d, HH:mm' }}: “{{ s.comment }}” ({{ s.createdBy }}). The alert itself still fires and resolves.</p>
        }
        @for (i of n.incidents; track i.id) {
          <p>In incident <a [routerLink]="['incidents', i.id] | jcLink" [queryParams]="ctx.keep()">{{ i.title }}</a> ({{ i.state }})</p>
        }
      }
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
      <section class="jc-card" data-testid="alert-related">
        <h2>Around this alert</h2>
        @if (related.value(); as r) {
          <p class="jc-notice" data-testid="alert-related-note">{{ r.note }}</p>
          <table class="jc-table"><tbody>
            @for (i of r.items; track i.type + i.id) {
              <tr>
                <td class="jc-nowrap">{{ i.at | date: 'MMM d, HH:mm:ss' }}</td>
                <td><strong>{{ i.title }}</strong>@if (i.detail) { <div class="jc-muted jc-small">{{ i.detail }}</div> }</td>
                <td class="jc-small"><ul class="jc-reasons">@for (x of i.reasons; track x.code) { <li>{{ x.text }} <span class="jc-muted">(+{{ x.points }})</span></li> }</ul></td>
              </tr>
            } @empty { <tr><td class="jc-muted">No changes or dependency health changes in the 2 hours before this alert.</td></tr> }
          </tbody></table>
          @if (d.alert.subjectId && d.alert.subjectType === 'service') {
            <a mat-stroked-button [routerLink]="['timeline'] | jcLink" [queryParams]="timelineLink(d.alert.subjectId, d.alert.environment, r.breachStartedAt)">Open the timeline</a>
          }
        }
        @if (related.error()) { <p class="jc-error">{{ relatedError() }}</p> }
      </section>
      @if (d.alert.state === 'Firing' || merged()) {
        <section class="jc-card" data-testid="alert-suggestions">
          <h2>Suggested with this alert</h2>
          @if (merged(); as m) { <p data-testid="alert-suggestion-merged">Merged into <a [routerLink]="['incidents', m] | jcLink" [queryParams]="ctx.keep()">the incident</a>.</p> }
          <jc-suggestions [suggestions]="suggestions.value()" (decided)="decided($event)" />
          @if (suggestions.error()) { <p class="jc-error">{{ suggestionsError() }}</p> }
        </section>
      }
      <section class="jc-card" data-testid="alert-notifications">
        <h2>Notifications</h2>
        @if (notifications.value(); as n) {
          @for (e of n.escalations; track e.policy) {
            <p>Escalation <strong>{{ e.name }}</strong>: {{ e.stepsSent }} of {{ e.steps }} steps sent, {{ e.state | lowercase }}@if (e.stopReason) { ({{ e.stopReason | reason }}) }@if (e.nextAt) {, next at {{ e.nextAt | date: 'HH:mm:ss' }} }</p>
          }
          <table class="jc-table">
            <thead><tr><th>To</th><th>Event</th><th>Status</th><th>Attempts</th><th>Detail</th><th>When</th></tr></thead>
            <tbody>
              @for (x of n.deliveries; track x.delivery.id) {
                <tr>
                  <td>{{ x.endpointKey }} <span class="jc-muted jc-small">{{ x.kind }}</span></td>
                  <td>{{ x.delivery.event }}@if (x.delivery.escalationStep) { step {{ x.delivery.escalationStep }} }</td>
                  <td [class.jc-bad]="x.delivery.status === 'Failed'">{{ x.delivery.status }}</td>
                  <td>{{ x.delivery.attempts }}</td>
                  <td class="jc-small">{{ x.delivery.lastError }}@if (x.delivery.nextAttemptAt && x.delivery.status === 'Retrying') { · next {{ x.delivery.nextAttemptAt | date: 'HH:mm:ss' }} }</td>
                  <td class="jc-nowrap">{{ x.delivery.createdAt | date: 'MMM d, HH:mm:ss' }}</td>
                </tr>
              } @empty { <tr><td colspan="6" class="jc-muted">Nothing sent about this alert: no channel takes it.</td></tr> }
            </tbody>
          </table>
        }
        @if (notifications.error()) { <p class="jc-error">{{ notificationsError() }}</p> }
      </section>
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

  protected readonly detail = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.alert(params) });
  protected readonly error = computed(() => problemText(this.detail.error()));
  protected readonly related = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.relatedToAlert(params) });
  protected readonly relatedError = computed(() => problemText(this.related.error()));
  protected readonly notifications = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.alertNotifications(params) });
  protected readonly notificationsError = computed(() => problemText(this.notifications.error()));
  /** Only a firing alert is suggested with others, so only its page asks. */
  protected readonly suggestions = apiResource({
    params: () => (this.detail.value()?.alert.state === 'Firing' ? this.id() : undefined),
    stream: ({ params }) => this.api.alertSuggestions(params),
  });
  protected readonly suggestionsError = computed(() => problemText(this.suggestions.error()));
  /** The incident a suggestion on this page was merged into. Keyed by alert: the page is reused when a suggestion's link opens another alert. */
  private readonly mergedInto = signal<{ alert: string; incident: string } | null>(null);
  protected readonly merged = computed(() => (this.mergedInto()?.alert === this.id() ? this.mergedInto()!.incident : null));

  /** A decision, or null when one was refused because the list was stale: either way, read it again. */
  protected decided(d: CorrelationDecision | null) {
    if (d?.incidentId) this.mergedInto.set({ alert: this.id(), incident: d.incidentId });
    this.suggestions.reload();
    this.notifications.reload();
  }

  incidentLink(alert: string, rule: string, subject: string) { return this.ctx.keep({ alert, title: `${rule} on ${subject}` }); }

  /** The new-silence form, filled in for this alert's subject. */
  silenceLink(subject: string, environment: string | null, type: string) {
    const name = type === 'service' ? subject.replace(/^service:/, '').replace(/@[^@]*$/, '') : null;
    const host = type === 'host' ? subject.replace(/^host:/, '') : null;
    return this.ctx.keep({ service: name, environment, host });
  }

  /** The service page over the alert's environment, from shortly before the breach. */
  /** The service's timeline from 2 hours before the breach to 1 hour after. */
  timelineLink(service: string, environment: string | null, breach: string) {
    const at = new Date(breach).getTime();
    return this.ctx.keep({ service, env: environment, range: null, from: new Date(at - 2 * 3_600_000).toISOString(), to: new Date(at + 3_600_000).toISOString() });
  }

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
      next: () => { this.sending.set(false); this.detail.reload(); this.notifications.reload(); },
      error: e => {
        this.sending.set(false);
        this.ackError.set((e as { status?: number }).status === 403 ? 'Acknowledging needs the operator or admin role.' : problemText(e));
      },
    });
  }
}
