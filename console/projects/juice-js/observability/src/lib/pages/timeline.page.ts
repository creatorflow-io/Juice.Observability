import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { apiResource } from '../shared/resource';
import { ServiceStatsCharts, problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/**
 * Changes, health and alerts in one order, with a service's telemetry above them over the same window. Things sharing a timeline
 * aren't said to be related; an alert's page ranks what may be, with its reasons.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, MatProgressBarModule, ContextBar, ServiceStatsCharts],
  template: `
    <jc-context-bar>
      @if (service()) { <a [routerLink]="['services', service()] | jcLink" [queryParams]="ctx.keep()" class="jc-crumb">Service</a><span class="jc-crumb-sep">›</span> }
      <h1>Timeline</h1>
    </jc-context-bar>
    @if (timeline.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (timeline.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (service(); as id) { <jc-service-stats [query]="{ serviceId: id }" /> }
    @if (timeline.value(); as t) {
      @if (t.truncated) { <p class="jc-muted">Showing the newest entries only. Choose a shorter window.</p> }
      <table class="jc-table" data-testid="timeline">
        <thead><tr><th>Time</th><th>What</th><th>Event</th><th>Details</th><th>Environment</th></tr></thead>
        <tbody>
          @for (e of t.entries; track e.at + e.type + e.title) {
            <tr [class.jc-timeline-change]="e.type === 'change'">
              <td class="jc-nowrap">{{ e.at | date: 'MMM d, HH:mm:ss' }}</td>
              <td>{{ label(e.type) }}</td>
              <td>
                @if (e.type === 'alert' && e.id) { <a [routerLink]="['alerts', e.id] | jcLink" [queryParams]="ctx.keep()">{{ e.title }}</a> }
                @else { <span [class.jc-bad]="bad(e.state)">{{ e.title }}</span> }
              </td>
              <td class="jc-muted">{{ e.detail }}</td>
              <td>{{ e.environment ?? 'all' }}</td>
            </tr>
          } @empty { <tr><td colspan="5" class="jc-muted">Nothing in this window.</td></tr> }
        </tbody>
      </table>
    }`,
})
export class TimelinePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  /** Narrows the timeline to one service. */
  readonly service = input<string>();

  protected readonly timeline = apiResource({
    params: () => ({ service: this.service(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => this.api.timeline({ serviceId: params.service ?? null, environment: params.env, ...this.ctx.window(7 * 24 * 3_600_000) }),
  });
  protected readonly error = computed(() => problemText(this.timeline.error()));

  protected label(type: string): string { return ({ change: 'Change', health: 'Health', alert: 'Alert' } as Record<string, string>)[type] ?? type; }
  protected bad(state: string | null): boolean { return !!state && ['Unhealthy', 'Critical', 'Offline', 'Firing'].includes(state); }
}
