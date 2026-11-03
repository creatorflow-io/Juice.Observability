import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { HealthState } from '../core/models';
import { AgoPipe, ReasonPipe } from '../shared/format';
import { ContextBar, HealthBadge } from '../shared/ui';
import { problemText } from '../shared/telemetry';

const ORDER: HealthState[] = ['Critical', 'Unhealthy', 'Degraded', 'Offline', 'Unknown', 'Healthy'];

/**
 * Where an investigation starts (Console requirements §4, §21 step 1): what is firing and which services are not
 * healthy, each one click from its service page. Orientation, not a dashboard.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatProgressBarModule, ContextBar, HealthBadge, AgoPipe],
  template: `
    <jc-context-bar><h1>Overview</h1></jc-context-bar>
    @if (health.isLoading() || alerts.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (health.error()) { <p class="jc-error">{{ error() }}</p> }

    <div class="jc-tiles">
      @for (s of order; track s) {
        <a class="jc-card jc-tile" [routerLink]="['services'] | jcLink" [queryParams]="ctx.keep({ state: s })">
          <jc-health [state]="s" /><strong>{{ counts()[s] ?? 0 }}</strong><span class="jc-muted">services</span>
        </a>
      }
    </div>

    <section class="jc-card">
      <h2>Firing alerts</h2>
      @for (a of alerts.value() ?? []; track a.id) {
        <div class="jc-list-row">
          <span class="jc-badge" [class.jc-health-unhealthy]="a.severity === 'Critical'" [class.jc-health-degraded]="a.severity !== 'Critical'">{{ a.severity }}</span>
          <a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.rule }}</a>
          <span>{{ a.subjectKey }}</span>
          <span class="jc-muted">fired {{ a.firedAt | ago }}@if (a.acknowledgedBy) { · acknowledged by {{ a.acknowledgedBy }} }</span>
        </div>
      } @empty { <p class="jc-muted">Nothing firing.</p> }
    </section>

    <section class="jc-card">
      <h2>Services needing attention</h2>
      @for (h of attention(); track h.subjectId + h.environment) {
        <div class="jc-list-row">
          <jc-health [state]="h.state" [stale]="h.stale" />
          <a [routerLink]="['services', h.subjectId] | jcLink" [queryParams]="ctx.keep({ env: h.environment })">{{ h.subjectName }}</a>
          <span class="jc-muted">{{ h.environment }}</span>
          <span>{{ h.reasons.slice(0, 3).map(reason).join(' · ') }}</span>
          <span class="jc-muted">since {{ h.since | ago }}</span>
        </div>
      } @empty { <p class="jc-muted">Every evaluated service is healthy.</p> }
    </section>`,
})
export class OverviewPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly order = ORDER;
  private readonly reasonPipe = new ReasonPipe();
  protected readonly reason = (r: string) => this.reasonPipe.transform(r);

  protected readonly health = rxResource({
    params: () => this.ctx.environment(),
    stream: ({ params }) => this.api.health({ subjectType: 'service', environment: params }).pipe(map(p => p.items)),
  });
  protected readonly alerts = rxResource({ stream: () => this.api.allAlerts({ state: 'firing' }).pipe(map(p => p.items)) });
  protected readonly error = computed(() => problemText(this.health.error()));
  protected readonly counts = computed(() => {
    const counts: Partial<Record<HealthState, number>> = {};
    for (const h of this.health.value() ?? []) counts[h.state] = (counts[h.state] ?? 0) + 1;
    return counts;
  });
  protected readonly attention = computed(() => (this.health.value() ?? [])
    .filter(h => h.state !== 'Healthy')
    .sort((a, b) => ORDER.indexOf(a.state) - ORDER.indexOf(b.state)));
}
