import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { SearchablePage } from '@juice-js/layout';
import { forkJoin, map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Health, HealthState } from '../core/models';
import { AgoPipe, ReasonPipe } from '../shared/format';
import { ContextBar, HealthBadge } from '../shared/ui';
import { problemText } from '../shared/telemetry';
import { screenPages } from '../shared/paging';

const SEVERITY: HealthState[] = ['Critical', 'Unhealthy', 'Degraded', 'Offline', 'Unknown', 'Healthy'];

/**
 * The service catalog (Console requirements §8): one row per service, environment-independent, with its health in
 * each environment. The toolbar search box filters it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatPaginatorModule, MatProgressBarModule, ContextBar, HealthBadge, AgoPipe],
  template: `
    <jc-context-bar><h1>Services</h1>
      @if (state()) { <span class="jc-chip">{{ state() }} <a [routerLink]="['services'] | jcLink" [queryParams]="ctx.keep({ retired: showRetired() || null })">×</a></span> }
      <a class="jc-small" data-testid="services-retired" [routerLink]="['services'] | jcLink"
         [queryParams]="ctx.keep({ state: state() || null, retired: showRetired() ? null : 'true' })"
         title="Discovery retires a service after Discovery:ServiceRetireAfter without telemetry (14 days by default)">{{ showRetired() ? 'Hide retired' : 'Show retired' }}</a>
    </jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (capped()) { <p class="jc-notice">Showing the first 5,000 only. Narrow the list with the search box or a filter.</p> }
    <table class="jc-table">
      <thead><tr><th>Service</th><th>Health by environment</th><th>Why</th><th>Last seen</th></tr></thead>
      <tbody>
        @for (s of pages.visible(); track s.id) {
          <tr>
            <td><a [routerLink]="['services', s.id] | jcLink" [queryParams]="ctx.keep()">{{ s.key }}</a></td>
            <td class="jc-envs">
              @if (s.retiredAt) { <span class="jc-badge jc-health-none" data-testid="service-retired" [title]="'Retired ' + (s.retiredAt | ago)">Retired</span> }
              @for (h of s.health; track h.environment) {
                <span><span class="jc-muted jc-small">{{ h.environment ?? '—' }}</span> <jc-health [state]="h.state" [stale]="h.stale" /></span>
              } @empty { @if (!s.retiredAt) { <jc-health [state]="null" /> } }
            </td>
            <td class="jc-small">{{ s.worst?.reasons?.slice(0, 2)?.map(reason)?.join(' · ') }}</td>
            <td>{{ s.lastSeen | ago }}</td>
          </tr>
        } @empty { <tr><td colspan="4" class="jc-muted">No services{{ filter() ? ' match "' + filter() + '"' : '' }}.</td></tr> }
      </tbody>
    </table>
    @if (pages.needed()) {
      <mat-paginator [length]="rows().length" [pageSize]="pages.size()" [pageIndex]="pages.index()" [pageSizeOptions]="pages.sizes"
                     (page)="pages.change($event)" aria-label="Pages" />
    }`,
})
export class ServicesPage implements SearchablePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly state = input<string>();
  /** `?retired=true`: retired services too, listed after the others. */
  readonly retired = input<string>();
  protected readonly showRetired = computed(() => this.retired() === 'true');
  protected readonly filter = signal('');
  private readonly reasonPipe = new ReasonPipe();
  protected readonly reason = (r: string) => this.reasonPipe.transform(r);

  protected readonly data = apiResource({
    params: () => ({ retired: this.showRetired() }),
    stream: ({ params }) => forkJoin({
      services: this.api.resources('service', params.retired),
      health: this.api.health({ subjectType: 'service' }),
    }).pipe(map(({ services, health }) => ({
      services: services.items, health: health.items, capped: services.nextCursor !== null || health.nextCursor !== null,
    }))),
  });
  protected readonly capped = computed(() => this.data.value()?.capped ?? false);
  protected readonly error = computed(() => problemText(this.data.error()));

  protected readonly rows = computed(() => {
    const data = this.data.value();
    if (!data) return [];
    const env = this.ctx.environment();
    const byService = new Map<string, Health[]>();
    for (const h of data.health) byService.set(h.subjectId, [...(byService.get(h.subjectId) ?? []), h]);
    const q = this.filter().toLowerCase();
    return data.services
      .map(s => {
        const health = (byService.get(s.id) ?? []).filter(h => !env || h.environment === env);
        const worst = [...health].sort((a, b) => SEVERITY.indexOf(a.state) - SEVERITY.indexOf(b.state))[0];
        return { ...s, health, worst };
      })
      // With an environment chosen, only the services seen in it.
      .filter(s => !env || s.health.length > 0)
      .filter(s => !q || s.key.toLowerCase().includes(q))
      .filter(s => !this.state() || s.health.some(h => h.state === this.state()))
      // Retired services last (they have no health), then the worst state first, then by key.
      .sort((a, b) => Number(!!a.retiredAt) - Number(!!b.retiredAt)
        || SEVERITY.indexOf(a.worst?.state ?? 'Unknown') - SEVERITY.indexOf(b.worst?.state ?? 'Unknown') || a.key.localeCompare(b.key));
  });

  protected readonly pages = screenPages(this.rows);

  onSearch(query: string) { this.filter.set(query); }
}
