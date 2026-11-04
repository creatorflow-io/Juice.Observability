import { LinkPipe, ObservabilityLinks } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatButtonModule } from '@angular/material/button';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe, DurationPipe, PercentPipe, ReasonPipe } from '../shared/format';
import { ConfidenceBadge, ContextBar, HealthBadge } from '../shared/ui';
import { LogTable, MetricExplorer, ServiceStatsCharts, TraceTable, problemText } from '../shared/telemetry';

const TABS = ['overview', 'operations', 'dependencies', 'traces', 'logs', 'metrics'] as const;

/**
 * One service across its environments (Console requirements §8): health with reasons, instances, operations,
 * dependencies, traces, logs and metrics, without leaving the service. The tab and the selected operation are in the
 * URL, so the investigation can be shared and the back button returns to it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatTabsModule, MatProgressBarModule, MatButtonModule, ContextBar, HealthBadge, ConfidenceBadge,
    TraceTable, LogTable, MetricExplorer, ServiceStatsCharts, AgoPipe, DurationPipe, PercentPipe, ReasonPipe],
  template: `
    <jc-context-bar>
      <a [routerLink]="['services'] | jcLink" [queryParams]="ctx.keep()" class="jc-crumb">Services</a>
      <span class="jc-crumb-sep">›</span>
      <h1>{{ service.value()?.key ?? '…' }}</h1>
      @for (l of service.value()?.links ?? []; track l.url) { <a mat-button [href]="l.url" target="_blank" rel="noopener">{{ l.title }} ↗</a> }
    </jc-context-bar>
    @if (service.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (service.error()) { <p class="jc-error">{{ error() }}</p> }

    @if (service.value(); as s) {
      <div class="jc-health-strip">
        @for (h of health(); track h.environment) {
          <div class="jc-card jc-health-card" [class.jc-selected]="h.environment === ctx.environment()">
            <div class="jc-row"><strong>{{ h.environment ?? 'no environment' }}</strong><jc-health [state]="h.state" [stale]="h.stale" /></div>
            <ul class="jc-reasons">
              @for (r of h.reasons; track r) {
                <li>
                  @if (dependencyOf(r); as dep) {
                    {{ r | reason }} — <a [routerLink]="['services', dep.id] | jcLink" [queryParams]="ctx.keep()">open</a>
                  } @else { {{ r | reason }} }
                </li>
              }
            </ul>
            <div class="jc-muted jc-small">since {{ h.since | ago }} · evaluated {{ h.evaluatedAt | ago }}</div>
            @if (h.environment && h.environment !== ctx.environment()) {
              <button mat-button (click)="ctx.set({ env: h.environment })">Investigate {{ h.environment }}</button>
            }
          </div>
        } @empty { <p class="jc-muted">No health evaluated yet: no data.</p> }
      </div>

      <mat-tab-group [selectedIndex]="tabIndex()" (selectedIndexChange)="openTab($event)" animationDuration="0ms" mat-stretch-tabs="false">
        <mat-tab label="Overview">
          @if (tabIndex() === 0) {
            <jc-service-stats [query]="{ serviceId: id() }" />
            <h3>Instances</h3>
            <table class="jc-table">
              <thead><tr><th>Instance</th><th>Environment</th><th>Health</th><th>Version</th><th>State</th><th>Last seen</th><th></th></tr></thead>
              <tbody>
                @for (i of instances(); track i.id) {
                  <tr>
                    <td>{{ i.instanceKey }}@if (i.keySource !== 'Observed') { <span class="jc-muted jc-small" title="No service.instance.id: derived from host and process"> (derived)</span> }</td>
                    <td>{{ i.environment }}</td>
                    <td><jc-health [state]="i.health" /></td>
                    <td>{{ i.serviceVersion ?? '—' }}</td>
                    <td>{{ i.state }}</td>
                    <td>{{ i.lastSeen | ago }}</td>
                    <td class="jc-nowrap">
                      <a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'traces', instance: i.id })">traces</a> ·
                      <a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'logs', instance: i.id })">logs</a>
                      @if (i.hostResourceId) { · <a [routerLink]="['resources', i.hostResourceId] | jcLink" [queryParams]="ctx.keep()">host</a> }
                    </td>
                  </tr>
                } @empty { <tr><td colspan="7" class="jc-muted">No instances.</td></tr> }
              </tbody>
            </table>
          }
        </mat-tab>

        <mat-tab label="Operations">
          @if (tabIndex() === 1) {
            @if (operations.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
            <table class="jc-table">
              <thead><tr><th>Operation</th><th>Kind</th><th>Requests</th><th>Per second</th><th>Errors</th><th>p50</th><th>p95</th><th>p99</th><th>Last seen</th><th></th></tr></thead>
              <tbody>
                @for (o of operations.value()?.items ?? []; track o.name + o.kind) {
                  <tr [class.jc-selected]="o.name === operation()">
                    <td><a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'operations', op: o.name })">{{ o.name }}</a></td>
                    <td>{{ o.kind }}</td>
                    <td>{{ o.count }}</td>
                    <td>{{ o.requestsPerSecond.toFixed(2) }}</td>
                    <td [class.jc-bad]="o.errorRate >= 0.05">{{ o.errorRate | percent2 }}</td>
                    <td>{{ o.p50Ms | duration }}</td><td>{{ o.p95Ms | duration }}</td><td>{{ o.p99Ms | duration }}</td>
                    <td>{{ o.lastSeen | ago }}</td>
                    <td class="jc-nowrap">
                      <a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'traces', op: o.name, status: 'error' })">error traces</a> ·
                      <a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'traces', op: o.name, slow: o.p95Ms })">slow traces</a>
                    </td>
                  </tr>
                } @empty { <tr><td colspan="10" class="jc-muted">No operations in this window.</td></tr> }
              </tbody>
            </table>
            @if (operation(); as op) {
              <h3>{{ op }}</h3>
              <jc-service-stats [query]="{ serviceId: id(), operation: op }" />
            }
          }
        </mat-tab>

        <mat-tab label="Dependencies">
          @if (tabIndex() === 2) {
            <h3>Calls and uses</h3>
            <table class="jc-table">
              <thead><tr><th>Target</th><th>Relation</th><th>Evidence</th><th>Spans seen</th><th>Last seen</th><th></th></tr></thead>
              <tbody>
                @for (d of downstream(); track d.edge.id) {
                  <tr>
                    <td><a [routerLink]="d.link" [queryParams]="ctx.keep()">{{ d.name }}</a>&nbsp;<span class="jc-muted jc-small">{{ d.type }}</span></td>
                    <td>{{ d.edge.relation }}</td>
                    <td><jc-confidence [confidence]="d.edge.confidence" /></td>
                    <td>{{ d.edge.spanCount }}</td>
                    <td>{{ d.edge.lastSeen | ago }}</td>
                    <td><a [routerLink]="['topology'] | jcLink" [queryParams]="ctx.keep({ edge: d.edge.id })">evidence</a> ·
                        <a [routerLink]="[]" [queryParams]="ctx.keep({ tab: 'traces', dependency: d.edge.id })">traces</a></td>
                  </tr>
                } @empty { <tr><td colspan="6" class="jc-muted">No downstream dependencies seen.</td></tr> }
              </tbody>
            </table>
            <h3>Called by</h3>
            <table class="jc-table">
              <tbody>
                @for (d of upstream(); track d.edge.id) {
                  <tr><td><a [routerLink]="d.link" [queryParams]="ctx.keep()">{{ d.name }}</a></td><td>{{ d.edge.relation }}</td>
                      <td><jc-confidence [confidence]="d.edge.confidence" /></td><td>{{ d.edge.lastSeen | ago }}</td>
                      <td><a [routerLink]="['topology'] | jcLink" [queryParams]="ctx.keep({ edge: d.edge.id })">evidence</a></td></tr>
                } @empty { <tr><td class="jc-muted">No callers seen.</td></tr> }
              </tbody>
            </table>
          }
        </mat-tab>

        <mat-tab label="Traces">
          @if (tabIndex() === 3) {
            <div class="jc-filters">
              @for (f of traceFilters(); track f.label) { <span class="jc-chip">{{ f.label }} <a [routerLink]="[]" [queryParams]="f.clear" queryParamsHandling="merge" [attr.aria-label]="'Remove ' + f.label">×</a></span> }
            </div>
            <jc-trace-table [query]="traceQuery()" />
          }
        </mat-tab>

        <mat-tab label="Logs">
          @if (tabIndex() === 4) { <jc-log-table [query]="{ serviceId: id(), instanceId: instanceFilter() }" /> }
        </mat-tab>

        <mat-tab label="Metrics">
          @if (tabIndex() === 5) { <jc-metric-explorer [scope]="{ serviceId: id(), instanceId: instanceFilter() }" /> }
        </mat-tab>
      </mat-tab-group>
    }`,
})
export class ServicePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly links = inject(ObservabilityLinks);

  readonly id = input.required<string>();
  readonly tab = input<string>();
  readonly op = input<string>();
  readonly status = input<string>();
  readonly slow = input<string>();
  readonly instance = input<string>();
  readonly dependency = input<string>();

  protected readonly tabIndex = computed(() => Math.max(0, TABS.indexOf((this.tab() ?? 'overview') as typeof TABS[number])));
  protected readonly operation = computed(() => this.op() ?? null);
  protected readonly instanceFilter = computed(() => this.instance() ?? null);

  protected readonly service = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.resource(params) });
  protected readonly topology = apiResource({ params: () => this.ctx.environment(), stream: ({ params }) => this.api.topology(params || null) });
  protected readonly operations = apiResource({
    params: () => ({ id: this.id(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute(), on: this.tabIndex() === 1 }),
    stream: ({ params }) => this.api.operations({ serviceId: params.id, environment: params.env, ...this.ctx.window(), limit: 200 }),
  });

  protected readonly error = computed(() => problemText(this.service.error()));
  protected readonly health = computed(() => (this.service.value()?.health ?? []).filter(h => h.subjectType === 'service'));
  protected readonly instances = computed(() => {
    const env = this.ctx.environment();
    return (this.service.value()?.instances ?? []).filter(i => !env || i.environment === env);
  });

  private readonly nodes = computed(() => new Map((this.topology.value()?.nodes ?? []).map(n => [n.id, n])));
  private edgesWhere(match: 'source' | 'target') {
    const other = match === 'source' ? 'target' : 'source';
    return (this.topology.value()?.edges ?? []).filter(e => e[match] === this.id()).map(edge => {
      const node = this.nodes().get(edge[other]);
      return {
        edge, name: node?.displayName ?? edge[other], type: node?.type ?? '',
        link: this.links.to(node?.type === 'service' ? 'services' : 'resources', edge[other]),
      };
    });
  }
  protected readonly downstream = computed(() => this.edgesWhere('source'));
  protected readonly upstream = computed(() => this.edgesWhere('target'));

  /** `dependency_failed:shop/payments` links to that service when it is in the topology. */
  dependencyOf(reason: string) {
    const name = reason.match(/^(?:critical_)?dependency_(?:failed|unknown):(.+)$/)?.[1];
    if (!name) return null;
    const node = [...this.nodes().values()].find(n => n.type === 'service' && (n.displayName === name || name.endsWith(`/${n.displayName}`)));
    return node ? { id: node.id } : null;
  }

  protected readonly traceQuery = computed(() => ({
    serviceId: this.id(), operation: this.op(), status: this.status(), minDurationMs: this.slow(),
    instanceId: this.instance(), dependencyId: this.dependency(),
  }));
  /** The trace filters in effect, each removable on its own. */
  protected readonly traceFilters = computed(() => {
    const filters: { label: string; clear: Record<string, null> }[] = [];
    if (this.op()) filters.push({ label: `operation: ${this.op()}`, clear: { op: null } });
    if (this.status()) filters.push({ label: `status: ${this.status()}`, clear: { status: null } });
    if (this.slow()) filters.push({ label: `slower than ${Math.round(Number(this.slow()))} ms`, clear: { slow: null } });
    if (this.instance()) filters.push({ label: 'one instance', clear: { instance: null } });
    if (this.dependency()) filters.push({ label: 'through one dependency', clear: { dependency: null } });
    return filters;
  });

  openTab(index: number) {
    void this.router.navigate([], { queryParams: { tab: TABS[index] }, queryParamsHandling: 'merge' });
  }
}
