import { LinkPipe, ObservabilityLinks } from '../core/options';
import {
  ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, computed, effect, inject, input, untracked, viewChild,
} from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import type { Core, StylesheetJson } from 'cytoscape';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Topology } from '../core/models';
import { AgoPipe, DurationPipe, PercentPipe } from '../shared/format';
import { ConfidenceBadge, ContextBar, HealthBadge } from '../shared/ui';
import { problemText } from '../shared/telemetry';
import { onThemeChange, themeColor } from '../shared/theme';

/** A service's health → its --jc-* colour from the app's theme (observability.colors). */
const HEALTH_TOKENS: Record<string, string> = {
  Healthy: 'ok', Degraded: 'warn', Unhealthy: 'bad', Critical: 'crit', Offline: 'off', Unknown: 'none',
};

/** The error rates of the health policy's defaults (phase 0 decision D4). */
const EDGE_WARN_ERROR_RATE = 0.01;
const EDGE_BAD_ERROR_RATE = 0.05;

/**
 * Dependency topology (Console requirements §9): services, databases and destinations; zoom, pan and selection.
 * Observed edges are solid, inferred ones dashed and labelled, so an inference never reads as a confirmed call. A
 * selected edge shows its evidence and representative traces; the selection is in the URL. Edges are coloured by their error
 * rate in the selected window, and fade when they had no calls in it.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, ContextBar, HealthBadge, ConfidenceBadge,
    AgoPipe, DurationPipe, PercentPipe],
  template: `
    <jc-context-bar><h1>Dependencies</h1></jc-context-bar>
    @if (topology.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (topology.error()) { <p class="jc-error">{{ error() }}</p> }
    <div class="jc-topology">
      <div class="jc-graph-wrap">
        <div #graph class="jc-graph" aria-label="Dependency graph"></div>
        <div class="jc-graph-legend">
          <span><i class="jc-line"></i> observed</span><span><i class="jc-line jc-dashed"></i> inferred</span>
          <span><i class="jc-line jc-line-warn"></i> errors ≥ 1%</span><span><i class="jc-line jc-line-bad"></i> errors ≥ 5%</span>
          <span><i class="jc-line jc-line-idle"></i> no calls in window</span>
          <span>■ service ◆ database ⬣ destination</span>
          <button mat-button (click)="fit()">Fit</button>
        </div>
        @if (topology.value()?.truncated) { <p class="jc-muted">Showing the most recent edges only.</p> }
        @if (edgeStats.value()?.truncated) { <p class="jc-muted">Only the most recent edges are coloured.</p> }
        @if (topology.hasValue() && topology.value()!.nodes.length === 0) { <p class="jc-muted">No dependencies seen{{ ctx.environment() ? ' in ' + ctx.environment() : '' }}.</p> }
      </div>

      <aside class="jc-card jc-side">
        @if (selectedEdge(); as e) {
          <h2>{{ name(e.source) }} → {{ name(e.target) }}</h2>
          <p>{{ e.relation }} <jc-confidence [confidence]="e.confidence" /></p>
          @if (e.confidence.toLowerCase() !== 'observed') {
            <p class="jc-muted jc-small">Inferred from span attributes (for example db.system.name or messaging.destination.name), not a call
              seen between two instrumented services.</p>
          }
          @if (evidence.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
          @if (evidence.value(); as ev) {
            <dl class="jc-facts">
              <dt>Calls in window</dt><dd>{{ ev.count }}</dd>
              <dt>Error rate</dt><dd [class.jc-bad]="ev.errorRate >= 0.05">{{ ev.count ? (ev.errorRate | percent2) : 'no data' }}</dd>
              <dt>p95</dt><dd>{{ ev.p95Ms | duration }}</dd>
              <dt>Last seen</dt><dd>{{ ev.lastSeen ?? e.lastSeen | ago }}</dd>
            </dl>
            <h3>Representative traces</h3>
            @for (t of ev.errorTraces; track t) { <div><a [routerLink]="['traces', t] | jcLink" [queryParams]="ctx.keep()" class="jc-mono">{{ t.slice(0, 16) }}…</a> <span class="jc-badge jc-health-unhealthy">error</span></div> }
            @for (t of ev.slowestTraces; track t) { <div><a [routerLink]="['traces', t] | jcLink" [queryParams]="ctx.keep()" class="jc-mono">{{ t.slice(0, 16) }}…</a> <span class="jc-muted jc-small">slow</span></div> }
            @if (!ev.errorTraces.length && !ev.slowestTraces.length) { <p class="jc-muted">No calls in this window.</p> }
            <a mat-stroked-button [routerLink]="['traces'] | jcLink" [queryParams]="ctx.keep({ dependency: e.id })">All traces through this edge</a>
          }
          <p><a [routerLink]="link(e.source)" [queryParams]="ctx.keep()">Open {{ name(e.source) }}</a> ·
             <a [routerLink]="link(e.target)" [queryParams]="ctx.keep()">Open {{ name(e.target) }}</a></p>
        } @else if (selectedNode(); as n) {
          <h2>{{ n.displayName }}</h2>
          <p>{{ n.type }} <jc-health [state]="n.health" /></p>
          <a mat-stroked-button [routerLink]="link(n.id)" [queryParams]="ctx.keep()">Open</a>
        } @else {
          <p class="jc-muted">Select a node or an edge. Scroll to zoom, drag to pan.</p>
        }
      </aside>
    </div>`,
})
export class TopologyPage implements OnDestroy {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly links = inject(ObservabilityLinks);

  readonly edge = input<string>();
  readonly node = input<string>();

  private readonly graph = viewChild.required<ElementRef<HTMLDivElement>>('graph');
  private cy?: Core;
  private stopThemeWatch?: () => void;
  private destroyed = false;

  protected readonly topology = apiResource({ params: () => this.ctx.environment(), stream: ({ params }) => this.api.topology(params || null) });
  protected readonly error = computed(() => problemText(this.topology.error()));
  private readonly nodes = computed(() => new Map((this.topology.value()?.nodes ?? []).map(n => [n.id, n])));
  protected readonly selectedEdge = computed(() => this.topology.value()?.edges.find(e => e.id === this.edge()) ?? null);
  protected readonly selectedNode = computed(() => (this.node() ? this.nodes().get(this.node()!) : null) ?? null);
  protected readonly edgeStats = apiResource({
    params: () => ({ env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => this.api.dependencyStats({ environment: params.env, ...this.ctx.window() }),
  });
  protected readonly evidence = apiResource({
    // No edge selected: undefined params leave the resource idle.
    params: () => this.edge() ? { edge: this.edge()!, env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() } : undefined,
    stream: ({ params }) => this.api.dependency(params.edge, { environment: params.env, ...this.ctx.window() }),
  });

  constructor() {
    afterNextRender(async () => {
      const cytoscape = (await import('cytoscape')).default;
      // Left the page while the graph library loaded: its container is detached (no size, so cytoscape's first layout
      // throws), and a graph made now would outlive ngOnDestroy.
      if (this.destroyed) return;
      this.cy = cytoscape({
        container: this.graph().nativeElement,
        wheelSensitivity: 0.3,
        style: this.graphStyle(),
      });
      // The app switched theme (light/dark): restyle and recolour the nodes.
      this.stopThemeWatch = onThemeChange(() => {
        this.cy?.style(this.graphStyle());
        this.draw(this.topology.value());
      });
      this.cy.on('tap', 'edge', e => this.choose({ edge: e.target.id(), node: null }));
      this.cy.on('tap', 'node', e => this.choose({ node: e.target.id(), edge: null }));
      this.cy.on('tap', e => { if (e.target === this.cy) this.choose({ node: null, edge: null }); });
      this.draw(this.topology.value());
    });
    // Redraw (and so re-layout and re-fit) on new topology only: draw() also reads the selection and the edge figures,
    // which would otherwise redraw the graph on every click and time-range change. The effects below handle those.
    effect(() => { const topology = this.topology.value(); untracked(() => this.draw(topology)); });
    effect(() => { this.edgeStats.value(); this.colourEdges(); });
    effect(() => {
      const id = this.edge() ?? this.node();
      this.cy?.elements().unselect();
      if (id) this.cy?.getElementById(id).select();
    });
  }

  private draw(topology: Topology | undefined) {
    if (!this.cy || !topology) return;
    this.cy.elements().remove();
    this.cy.add([
      ...topology.nodes.map(n => ({ data: {
        id: n.id, label: n.displayName, type: n.type,
        color: this.color(n.type === 'service' ? HEALTH_TOKENS[n.health ?? 'Unknown'] ?? 'none' : 'muted') } })),
      ...topology.edges.filter(e => this.nodes().has(e.source) && this.nodes().has(e.target)).map(e => ({ data: {
        id: e.id, source: e.source, target: e.target, confidence: e.confidence.toLowerCase(),
        label: e.confidence.toLowerCase() === 'inferred' ? `${e.relation} (inferred)` : e.relation } })),
    ]);
    this.cy.layout({ name: 'breadthfirst', directed: true, padding: 30, spacingFactor: 1.2 }).run();
    const id = this.edge() ?? this.node();
    if (id) this.cy.getElementById(id).select();
    this.colourEdges();
  }

  /** Separate from drawing, so a new time range doesn't move the graph. An edge with no figures keeps the plain look. */
  private colourEdges() {
    if (!this.cy) return;
    this.cy.edges().data('traffic', null);
    for (const s of this.edgeStats.value()?.items ?? []) {
      this.cy.getElementById(s.dependencyId).data('traffic', s.count === 0 ? 'idle'
        : s.errorRate >= EDGE_BAD_ERROR_RATE ? 'bad' : s.errorRate >= EDGE_WARN_ERROR_RATE ? 'warn' : 'ok');
    }
  }

  private color(token: string) { return themeColor(token, this.graph().nativeElement); }

  /** The graph's look, in the app's theme colours. */
  private graphStyle() {
    const [text, muted, line, surface, selected] = [this.color('text'), this.color('muted'), this.color('border'),
      this.color('surface'), this.color('accent')];
    return [
      { selector: 'node', style: {
        label: 'data(label)', 'font-size': 11, color: text, 'text-valign': 'bottom', 'text-margin-y': 4,
        'background-color': 'data(color)', width: 26, height: 26, 'border-width': 2, 'border-color': surface } },
      { selector: 'node[type = "database"]', style: { shape: 'diamond' } },
      { selector: 'node[type = "messaging-destination"]', style: { shape: 'hexagon' } },
      { selector: 'node[type = "service"]', style: { shape: 'round-rectangle' } },
      { selector: 'edge', style: {
        width: 2, 'curve-style': 'bezier', 'target-arrow-shape': 'triangle', 'line-color': muted,
        'target-arrow-color': muted, label: 'data(label)', 'font-size': 9, color: muted, 'text-rotation': 'autorotate',
        'text-background-color': surface, 'text-background-opacity': 0.8, 'text-background-padding': '1px' } },
      { selector: 'edge[confidence = "inferred"]', style: { 'line-style': 'dashed', 'line-color': line, 'target-arrow-color': line } },
      { selector: 'edge[traffic = "warn"]', style: { 'line-color': this.color('warn'), 'target-arrow-color': this.color('warn'), width: 3 } },
      { selector: 'edge[traffic = "bad"]', style: { 'line-color': this.color('bad'), 'target-arrow-color': this.color('bad'), width: 3 } },
      { selector: 'edge[traffic = "idle"]', style: { opacity: 0.35 } },
      { selector: ':selected', style: { 'line-color': selected, 'target-arrow-color': selected, 'border-color': selected, 'border-width': 3 } },
    ] as StylesheetJson;
  }

  fit() { this.cy?.fit(undefined, 30); }

  private choose(selection: { edge?: string | null; node?: string | null }) {
    void this.router.navigate([], { queryParams: selection, queryParamsHandling: 'merge', replaceUrl: true });
  }

  name(id: string) { return this.nodes().get(id)?.displayName ?? id; }

  link(id: string) { return this.links.to(this.nodes().get(id)?.type === 'service' ? 'services' : 'resources', id); }

  ngOnDestroy() {
    this.destroyed = true;
    this.stopThemeWatch?.();
    this.cy?.destroy();
  }
}
