import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { cursorList } from '../shared/paging';
import { problemText } from '../shared/telemetry';

/** Alerts, newest first (Console requirements §14), filtered by state; 100 at a time, with the next on request. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatButtonModule, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Alerts</h1>
      @for (s of states; track s) {
        <a class="jc-chip" [class.jc-selected]="s === (state() ?? 'firing')" [routerLink]="['alerts'] | jcLink" [queryParams]="ctx.keep({ state: s })">{{ s }}</a>
      }
    </jc-context-bar>
    @if (alerts.first.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (alerts.first.error()) { <p class="jc-error">{{ error() }}</p> }
    <table class="jc-table">
      <thead><tr><th>Severity</th><th>Rule</th><th>Subject</th><th>State</th><th>Value</th><th>Fired</th><th>Acknowledged</th></tr></thead>
      <tbody>
        @for (a of alerts.items(); track a.id) {
          <tr>
            <td><span class="jc-badge" [class.jc-health-unhealthy]="a.severity === 'Critical'" [class.jc-health-degraded]="a.severity === 'Warning'">{{ a.severity }}</span></td>
            <td><a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.rule }}</a></td>
            <td>{{ a.subjectKey }}</td>
            <td>{{ a.state }}@if (a.noData) { <span class="jc-muted" title="The last evaluation had too few samples; the state is kept"> · no data</span> }</td>
            <td>{{ a.lastValue ?? '—' }}</td>
            <td>{{ a.firedAt | ago }}</td>
            <td>{{ a.acknowledgedBy ?? '—' }}</td>
          </tr>
        } @empty { @if (alerts.first.hasValue()) { <tr><td colspan="7" class="jc-muted">No {{ state() ?? 'firing' }} alerts.</td></tr> } }
      </tbody>
    </table>
    @if (alerts.items().length > 0) {
      <div class="jc-more">
        <span class="jc-muted">{{ alerts.items().length }} {{ state() ?? 'firing' }} alert{{ alerts.items().length === 1 ? '' : 's' }}{{ alerts.hasMore() ? ', newest first' : '' }}</span>
        @if (alerts.hasMore()) {
          <button mat-button (click)="alerts.more()" [disabled]="alerts.loadingMore()">{{ alerts.loadingMore() ? 'Loading…' : 'Load more' }}</button>
        }
        @if (alerts.moreError()) { <span class="jc-error">{{ moreError() }}</span> }
      </div>
    }`,
})
export class AlertsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly states = ['firing', 'pending', 'resolved'];
  readonly state = input<string>();
  protected readonly alerts = cursorList(() => this.state() ?? 'firing', state => ({ state }), q => this.api.alerts(q));
  protected readonly error = computed(() => problemText(this.alerts.first.error()));
  protected readonly moreError = computed(() => problemText(this.alerts.moreError()));
}
