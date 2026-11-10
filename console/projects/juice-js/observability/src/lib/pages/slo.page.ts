import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { SloDay, SloDefinition, SloSummary } from '../core/models';
import { LinkPipe } from '../core/options';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';
import { burnText, objective, percent, sloState, times } from './slos.page';

const WINDOWS: Record<string, string> = { '5m': '5 minutes', '30m': '30 minutes', '1h': '1 hour', '6h': '6 hours', '3d': '3 days' };

/**
 * One SLO (roadmap Phase 9): its budget over the last 30 days, how fast it burns in each window, its days, what counts as a request, and its versions.
 * A window with too few requests, or too many minutes that weren't counted, says so instead of a figure.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LinkPipe, DatePipe, MatProgressBarModule, ContextBar],
  template: `
    <jc-context-bar>
      <a class="jc-crumb" [routerLink]="['slos'] | jcLink" [queryParams]="ctx.keep()">SLOs</a><span class="jc-crumb-sep">›</span>
      <h1>{{ detail.value()?.slo?.name ?? key() }}</h1>
    </jc-context-bar>
    @if (detail.isLoading() && !detail.value()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (detail.value(); as d) {
      <p class="jc-summary">
        <span class="jc-badge" [class]="state(d.slo.state).css" data-testid="slo-state">{{ state(d.slo.state).text }}</span>
        <strong>{{ objective(d.slo) }}</strong> · {{ d.slo.category }} · {{ d.slo.service }} in {{ d.slo.environment }} · version {{ d.slo.version }}
        @if (d.slo.pendingVersion) { <span class="jc-muted"> (version {{ d.slo.pendingVersion }} pending)</span> }
        @if (d.slo.retired) { <span class="jc-muted"> · retired</span> }
      </p>

      <section class="jc-card" data-testid="slo-budget">
        <h2>Error budget, last 30 days</h2>
        @switch (d.slo.state) {
          @case ('Unknown') {
            <p data-testid="slo-budget-unknown">Only {{ percent(d.slo.coverage, 1) }} of the last 30 days' minutes are counted, so there's no budget to show yet. Minutes that weren't
              read are never taken as good: either counting is still catching up, or telemetry didn't arrive.</p>
          }
          @case ('InsufficientData') {
            <p>{{ d.slo.total.toLocaleString() }} requests in 30 days: too few to say whether the objective is met.</p>
          }
          @default {
            <p><strong>{{ percent(d.slo.attainment, 3) }}</strong> of {{ d.slo.total.toLocaleString() }} requests were good, against an objective of {{ d.slo.target }}%.</p>
            @if ((d.slo.budgetUsed ?? 0) <= 1) {
              <p>{{ percent(d.slo.budgetUsed, 1) }} of the budget is used ({{ d.slo.bad.toLocaleString() }} bad of {{ allowed(d.slo).toLocaleString() }} allowed),
                {{ percent(d.slo.budgetRemaining, 1) }} left.</p>
            } @else {
              <p class="jc-bad">The budget is overspent: {{ d.slo.bad.toLocaleString() }} bad requests against {{ allowed(d.slo).toLocaleString() }} allowed, {{ times(d.slo.budgetUsed!) }} the budget.</p>
            }
          }
        }
      </section>

      <section class="jc-card" data-testid="slo-burn">
        <h2>Burn rate</h2>
        <p class="jc-muted jc-small">How fast the budget is being spent: 1× spends it exactly over 30 days. A window needs enough requests, and nearly all its minutes counted.</p>
        <table class="jc-table">
          <thead><tr><th>Window</th><th>Requests</th><th>Good</th><th>Burn rate</th></tr></thead>
          <tbody>
            @for (w of d.slo.burn; track w.name) {
              <tr [attr.data-testid]="'slo-burn-' + w.name">
                <td>{{ windowName(w.name) }}</td><td>{{ w.total.toLocaleString() }}</td><td>{{ percent(w.attainment, 2) }}</td><td>{{ burn(w) }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>

      <section class="jc-card" data-testid="slo-alerts">
        <h2>Burn alerts</h2>
        <p class="jc-muted jc-small">Fast burn (14.4× over 1 hour and 5 minutes) is critical; slow burn (6× over 6 hours and 30 minutes, 1× over 3 days and 6 hours) a warning.
          Each fires when both its windows reach the rate and resolves when its short window is under it, then waits at least 10 minutes before firing
          again. A window without a rate holds the alert as it is.</p>
        @if (d.alerts.length) {
          <table class="jc-table">
            <thead><tr><th>Alert</th><th>State</th><th>Fired</th><th>Resolved</th><th>Burn rate</th></tr></thead>
            <tbody>
              @for (a of d.alerts; track a.id) {
                <tr [attr.data-testid]="'slo-alert-' + a.rule">
                  <td><a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.rule }}</a> <span class="jc-muted jc-small">{{ a.severity }}</span></td>
                  <td>{{ a.state }}@if (a.noData && a.state !== 'Resolved') { <span class="jc-muted jc-small"> · held: a window has no rate</span> }</td>
                  <td class="jc-nowrap">{{ a.firedAt | date: 'MMM d, HH:mm' }}</td>
                  <td class="jc-nowrap">{{ a.resolvedAt ? (a.resolvedAt | date: 'MMM d, HH:mm') : '—' }}</td>
                  <td>{{ a.burnRate === null ? '—' : times(a.burnRate) }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else { <p class="jc-muted">No burn alerts yet.</p> }
      </section>

      <section class="jc-card" data-testid="slo-days">
        <h2>By day</h2>
        @if (d.days.length) {
          <table class="jc-table">
            <thead><tr><th>Day</th><th>Requests</th><th>Good</th><th>Left out</th><th>Minutes counted</th></tr></thead>
            <tbody>
              @for (day of daysNewestFirst(d.days); track day.day) {
                <tr>
                  <td class="jc-nowrap">{{ day.day | date: 'MMM d' : 'UTC' }}</td>
                  <td>{{ day.total.toLocaleString() }}</td>
                  <td [class.jc-bad]="day.attainment !== null && day.attainment * 100 < d.slo.target">{{ percent(day.attainment, 3) }}</td>
                  <td class="jc-small">{{ excluded(day) }}</td>
                  <td [class.jc-bad]="day.minutes < day.expectedMinutes && !isToday(day)">{{ day.minutes }} of {{ day.expectedMinutes }}</td>
                </tr>
              }
            </tbody>
          </table>
        } @else { <p class="jc-muted">Nothing counted yet.</p> }
      </section>

      <section class="jc-card" data-testid="slo-eligibility">
        <h2>What counts</h2>
        <ul class="jc-small">
          <li>Requests to <strong>{{ d.definition.service }}</strong> in {{ d.definition.environment }}: its server and consumer spans,
            {{ d.definition.operations.length ? 'only ' + d.definition.operations.join(', ') : 'every operation' }}.</li>
          <li>Never counted: {{ d.definition.excludeOperations.join(', ') || 'nothing' }} (health checks and the like).</li>
          <li>5xx, 429, a failed span and a failed message are bad.</li>
          <li>Other 4xx (the client's mistake): {{ clientErrors(d.definition) }}.</li>
          <li>408 and 499 (the client gave up): {{ d.definition.cancellations === 'Bad' ? 'counted as bad' : 'not counted' }}.</li>
          <li>Retries: each attempt the service received is its own request.</li>
          @if (d.definition.sli === 'Latency') { <li>Latency counts requests that succeeded: good at {{ d.definition.thresholdMs }} ms or less.</li> }
        </ul>
      </section>

      <section class="jc-card" data-testid="slo-versions">
        <h2>Versions</h2>
        <table class="jc-table">
          <thead><tr><th>Version</th><th>Objective</th><th>From</th><th>Status</th><th>Loaded</th></tr></thead>
          <tbody>
            @for (v of d.versions; track v.version) {
              <tr [attr.data-testid]="'slo-version-' + v.version">
                <td>{{ v.version }}</td><td>{{ objective(v.definition) }}</td><td class="jc-nowrap">{{ v.effectiveFrom | date: 'MMM d, HH:mm' }}</td>
                <td>{{ v.status }}</td><td class="jc-small">{{ v.createdAt | date: 'MMM d, HH:mm' }} by {{ v.createdBy }}</td>
              </tr>
            }
          </tbody>
        </table>
        <p class="jc-muted jc-small">Each minute is counted under the version in effect then, so a new objective doesn't rewrite the past.</p>
      </section>
    }
  `,
})
export class SloPage {
  readonly key = input.required<string>();
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly detail = apiResource({ params: () => this.key(), stream: ({ params }) => this.api.slo(params) });
  protected readonly error = computed(() => problemText(this.detail.error()));
  protected readonly objective = objective;
  protected readonly percent = percent;
  protected readonly burn = burnText;

  protected readonly state = sloState;
  protected readonly times = times;
  protected windowName(name: string) { return WINDOWS[name] ?? name; }
  /** The API's allowance: each version's requests at its own target, so it agrees with the budget used. */
  protected allowed(s: SloSummary) { return Math.round(s.budgetAllowed); }
  protected daysNewestFirst(days: SloDay[]) { return [...days].reverse(); }
  protected isToday(day: SloDay) { return day.day.slice(0, 10) === new Date().toISOString().slice(0, 10); }
  protected excluded(day: SloDay) {
    const parts = [[day.excludedHealth, 'health checks'], [day.excludedCancelled, 'given up'], [day.excludedClientErrors, 'client errors']]
      .filter(([n]) => (n as number) > 0).map(([n, what]) => `${(n as number).toLocaleString()} ${what}`);
    return parts.length ? parts.join(', ') : '—';
  }
  protected clientErrors(d: SloDefinition) { return d.clientErrors === 'Bad' ? 'bad' : d.clientErrors === 'Exclude' ? 'not counted' : 'counted as good'; }
}
