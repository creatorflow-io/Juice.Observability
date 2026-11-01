import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';

/** Alerts, newest first (Console requirements §14), filtered by state. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Alerts</h1>
      @for (s of states; track s) {
        <a class="jc-chip" [class.jc-selected]="s === (state() ?? 'firing')" routerLink="/alerts" [queryParams]="ctx.keep({ state: s })">{{ s }}</a>
      }
    </jc-context-bar>
    @if (alerts.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (alerts.error()) { <p class="jc-error">{{ error() }}</p> }
    <table class="jc-table">
      <thead><tr><th>Severity</th><th>Rule</th><th>Subject</th><th>State</th><th>Value</th><th>Fired</th><th>Acknowledged</th></tr></thead>
      <tbody>
        @for (a of alerts.value() ?? []; track a.id) {
          <tr>
            <td><span class="jc-badge" [class.jc-health-unhealthy]="a.severity === 'Critical'" [class.jc-health-degraded]="a.severity === 'Warning'">{{ a.severity }}</span></td>
            <td><a [routerLink]="['/alerts', a.id]" [queryParams]="ctx.keep()">{{ a.rule }}</a></td>
            <td>{{ a.subjectKey }}</td>
            <td>{{ a.state }}@if (a.noData) { <span class="jc-muted" title="The last evaluation had too few samples; the state is kept"> · no data</span> }</td>
            <td>{{ a.lastValue ?? '—' }}</td>
            <td>{{ a.firedAt | ago }}</td>
            <td>{{ a.acknowledgedBy ?? '—' }}</td>
          </tr>
        } @empty { <tr><td colspan="7" class="jc-muted">No {{ state() ?? 'firing' }} alerts.</td></tr> }
      </tbody>
    </table>`,
})
export class AlertsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly states = ['firing', 'pending', 'resolved'];
  readonly state = input<string>();
  protected readonly alerts = rxResource({
    params: () => this.state() ?? 'firing',
    stream: ({ params }) => this.api.alerts({ state: params }).pipe(map(p => p.items)),
  });
  protected readonly error = computed(() => problemText(this.alerts.error()));
}
