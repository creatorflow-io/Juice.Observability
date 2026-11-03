import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe, ReasonPipe } from '../shared/format';
import { ConfidenceBadge, ContextBar, HealthBadge, KeyValueTable } from '../shared/ui';
import { LogTable, MetricExplorer, TraceTable, problemText } from '../shared/telemetry';

/**
 * A host, container, pod, database or destination (Console requirements §11): what runs on it or who uses it, its
 * health, and its telemetry. A database or destination has traces (the calls to it) but no logs or metrics of its own.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatProgressBarModule, ContextBar, HealthBadge, ConfidenceBadge, KeyValueTable, TraceTable, LogTable,
    MetricExplorer, AgoPipe],
  template: `
    <jc-context-bar>
      <a [routerLink]="['resources'] | jcLink" [queryParams]="ctx.keep({ type: resource.value()?.type })" class="jc-crumb">Resources</a>
      <span class="jc-crumb-sep">›</span>
      <h1>{{ resource.value()?.displayName ?? '…' }}</h1>
      <span class="jc-muted">{{ resource.value()?.type }}</span>
    </jc-context-bar>
    @if (resource.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (resource.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (resource.value(); as r) {
      <div class="jc-grid-2">
        <section class="jc-card">
          <h2>Identity</h2>
          <jc-kv [values]="{ key: r.key, 'first seen': r.firstSeen, 'last seen': r.lastSeen }" />
          <jc-kv [values]="r.attributes" empty="" />
          @for (h of r.health; track h.subjectId) {
            <p><jc-health [state]="h.state" [stale]="h.stale" /> {{ h.reasons.map(reason).join(' · ') }}</p>
          }
        </section>
        <section class="jc-card">
          @if (isInfrastructure()) {
            <h2>Instances running here</h2>
            @for (i of r.instances; track i.id) {
              <div class="jc-list-row"><jc-health [state]="i.health" /><span>{{ i.instanceKey }}</span>
                <span class="jc-muted">{{ i.environment }}</span><span class="jc-muted">{{ i.lastSeen | ago }}</span></div>
            } @empty { <p class="jc-muted">No service instances seen on it.</p> }
          } @else {
            <h2>Used by</h2>
            @for (e of users(); track e.id) {
              <div class="jc-list-row"><a [routerLink]="['services', e.source] | jcLink" [queryParams]="ctx.keep()">{{ name(e.source) }}</a>
                <span>{{ e.relation }}</span><jc-confidence [confidence]="e.confidence" />
                <a [routerLink]="['topology'] | jcLink" [queryParams]="ctx.keep({ edge: e.id })">evidence</a></div>
            } @empty { <p class="jc-muted">No users seen{{ ctx.environment() ? ' in ' + ctx.environment() : '' }}.</p> }
          }
        </section>
      </div>
      @if (isInfrastructure()) {
        <h2>Metrics</h2>
        <jc-metric-explorer [scope]="{ resourceId: id() }" />
        <h2>Logs</h2>
        <jc-log-table [query]="{ resourceId: id() }" />
      } @else {
        <h2>Traces that reach it</h2>
        <jc-trace-table [query]="{ resourceId: id() }" />
      }
    }`,
})
export class ResourcePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly id = input.required<string>();

  protected readonly resource = rxResource({ params: () => this.id(), stream: ({ params }) => this.api.resource(params) });
  protected readonly topology = rxResource({ params: () => this.ctx.environment(), stream: ({ params }) => this.api.topology(params || null) });
  protected readonly error = computed(() => problemText(this.resource.error()));
  protected readonly isInfrastructure = computed(() => ['host', 'container', 'k8s-pod'].includes(this.resource.value()?.type ?? ''));
  protected readonly users = computed(() => (this.topology.value()?.edges ?? []).filter(e => e.target === this.id()));
  private readonly reasonPipe = new ReasonPipe();
  protected readonly reason = (r: string) => this.reasonPipe.transform(r);

  name(id: string) { return this.topology.value()?.nodes.find(n => n.id === id)?.displayName ?? id; }
}
