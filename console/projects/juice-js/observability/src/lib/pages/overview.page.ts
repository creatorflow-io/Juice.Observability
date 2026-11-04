import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { apiResource } from '../shared/resource';
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

/** How many rows each list shows: the overview orients, the alerts and services pages list everything. */
const SHOWN = 10;

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
      <div class="jc-row">
        <h2>Firing alerts @if (firing().length) { <span class="jc-muted">({{ firing().length }}@if (criticalCount()) { · {{ criticalCount() }} critical})</span> }</h2>
        @if (firing().length) { <a [routerLink]="['alerts'] | jcLink" [queryParams]="ctx.keep({ state: 'firing' })">All firing alerts</a> }
      </div>
      @for (a of firingShown(); track a.id) {
        <div class="jc-list-row">
          <span class="jc-badge" [class.jc-health-unhealthy]="a.severity === 'Critical'" [class.jc-health-degraded]="a.severity !== 'Critical'">{{ a.severity }}</span>
          <a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.rule }}</a>
          <span>{{ a.subjectKey }}</span>
          <span class="jc-muted">fired {{ a.firedAt | ago }}@if (a.acknowledgedBy) { · acknowledged by {{ a.acknowledgedBy }} }</span>
        </div>
      } @empty { <p class="jc-muted">Nothing firing.</p> }
      @if (firing().length > shown) {
        <p class="jc-more"><span class="jc-muted">Critical first, then the newest; {{ firing().length - shown }} more.</span>
          <a [routerLink]="['alerts'] | jcLink" [queryParams]="ctx.keep({ state: 'firing' })">See all {{ firing().length }}</a></p>
      }
    </section>

    <section class="jc-card">
      <h2>Services needing attention @if (attention().length) { <span class="jc-muted">({{ attention().length }})</span> }</h2>
      @for (h of attention().slice(0, shown); track h.subjectId + h.environment) {
        <div class="jc-list-row">
          <jc-health [state]="h.state" [stale]="h.stale" />
          <a [routerLink]="['services', h.subjectId] | jcLink" [queryParams]="ctx.keep({ env: h.environment })">{{ h.subjectName }}</a>
          <span class="jc-muted">{{ h.environment }}</span>
          <span>{{ h.reasons.slice(0, 3).map(reason).join(' · ') }}</span>
          <span class="jc-muted">since {{ h.since | ago }}</span>
        </div>
      } @empty { <p class="jc-muted">Every evaluated service is healthy.</p> }
      @if (attention().length > shown) {
        <p class="jc-more"><span class="jc-muted">The worst first; {{ attention().length - shown }} more. The counts above open each state's list:</span>
          @for (s of order; track s) { @if (s !== 'Healthy' && counts()[s]) {
            <a [routerLink]="['services'] | jcLink" [queryParams]="ctx.keep({ state: s })">{{ counts()[s] }} {{ s.toLowerCase() }}</a>
          } }</p>
      }
    </section>`,
})
export class OverviewPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly order = ORDER;
  protected readonly shown = SHOWN;
  private readonly reasonPipe = new ReasonPipe();
  protected readonly reason = (r: string) => this.reasonPipe.transform(r);

  protected readonly health = apiResource({
    params: () => this.ctx.environment(),
    stream: ({ params }) => this.api.health({ subjectType: 'service', environment: params }).pipe(map(p => p.items)),
  });
  protected readonly alerts = apiResource({
    params: () => this.ctx.environment(),
    stream: ({ params }) => this.api.allAlerts({ state: 'firing', environment: params }).pipe(map(p => p.items)),
  });
  protected readonly error = computed(() => problemText(this.health.error()));
  /** Critical first, then the newest. */
  protected readonly firing = computed(() => [...(this.alerts.value() ?? [])].sort((a, b) =>
    Number(b.severity === 'Critical') - Number(a.severity === 'Critical')
    || (b.firedAt ?? '').localeCompare(a.firedAt ?? '')));
  protected readonly firingShown = computed(() => this.firing().slice(0, SHOWN));
  protected readonly criticalCount = computed(() => this.firing().filter(a => a.severity === 'Critical').length);
  protected readonly counts = computed(() => {
    const counts: Partial<Record<HealthState, number>> = {};
    for (const h of this.health.value() ?? []) counts[h.state] = (counts[h.state] ?? 0) + 1;
    return counts;
  });
  protected readonly attention = computed(() => (this.health.value() ?? [])
    .filter(h => h.state !== 'Healthy')
    .sort((a, b) => ORDER.indexOf(a.state) - ORDER.indexOf(b.state)));
}
