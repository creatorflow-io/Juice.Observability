import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Span } from '../core/models';
import { DurationPipe, formatDuration, plainTraceId, preciseTime } from '../shared/format';
import { ContextBar, KeyValueTable } from '../shared/ui';
import { LogTable, problemText } from '../shared/telemetry';

interface Row { span: Span; depth: number; children: number; offset: number; selfError: boolean; slowest: boolean; }

/** A stable colour per service, from a small palette that reads in both themes. */
const PALETTE = ['#3b82f6', '#8b5cf6', '#14b8a6', '#f59e0b', '#ec4899', '#22c55e', '#06b6d4', '#a855f7', '#84cc16', '#f97316'];

/**
 * A trace as a waterfall (Console requirements §6): the span hierarchy with timing relative to the trace start, so
 * parallel calls show side by side; errors and the slowest spans marked; subtrees collapsible. The selected span is
 * in the URL (`span`), with its details, its logs, and links to its service, its dependency, and the metrics and logs
 * around it (§7).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Fills the window; the waterfall and the span scroll on their own (styles.scss).
  host: { class: 'jc-fill-page' },
  imports: [RouterLink, DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, ContextBar, KeyValueTable, LogTable, DurationPipe],
  template: `
    <jc-context-bar>
      <a routerLink="/traces" [queryParams]="ctx.keep()" class="jc-crumb">Traces</a>
      <span class="jc-crumb-sep">›</span>
      <h1 class="jc-mono">{{ id() }}</h1>
    </jc-context-bar>
    @if (trace.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (trace.error()) { <p class="jc-error">{{ error() }}</p> }

    @if (trace.value(); as t) {
      <div class="jc-summary">
        <span><strong>{{ root()?.service }}</strong> · {{ root()?.name }}</span>
        <span>{{ start() | date: 'MMM d, HH:mm:ss.SSS' }}</span>
        <span>{{ total() | duration }}</span>
        <span>{{ t.spans.length }} spans · {{ services().length }} services</span>
        @if (errors() > 0) { <span class="jc-badge jc-health-unhealthy">{{ errors() }} failed</span> }
        <button mat-button (click)="showErrorsOnly.set(!showErrorsOnly())">{{ showErrorsOnly() ? 'Show all spans' : 'Error paths only' }}</button>
        <a mat-button [routerLink]="['/logs']" [queryParams]="ctx.keep({ trace: id() })">Logs of this trace</a>
        @if (t.truncated) { <span class="jc-muted">Showing the first {{ t.spans.length }} spans.</span> }
      </div>
      <div class="jc-legend">@for (s of services(); track s) { <span><i [style.background]="color(s)"></i>{{ s }}</span> }</div>

      <div class="jc-trace" [class.jc-with-detail]="selected()">
        <div class="jc-waterfall" role="tree">
          <div class="jc-wf-head"><span>Span</span><span class="jc-wf-scale">
            @for (tick of ticks(); track tick.at) { <span [style.left.%]="tick.at">{{ tick.label }}</span> }</span></div>
          @for (r of visible(); track r.span.spanId) {
            <div class="jc-wf-row" role="treeitem" [class.jc-selected]="r.span.spanId === selectedId()"
                 [class.jc-wf-error]="r.selfError" (click)="select(r.span.spanId)" tabindex="0" (keydown.enter)="select(r.span.spanId)">
              <span class="jc-wf-name" [style.padding-left.px]="r.depth * 14">
                @if (r.children > 0) {
                  <button class="jc-wf-toggle" (click)="toggle(r.span.spanId, $event)" [attr.aria-label]="collapsed().has(r.span.spanId) ? 'Expand' : 'Collapse'">
                    {{ collapsed().has(r.span.spanId) ? '▸' : '▾' }}</button>
                } @else { <span class="jc-wf-toggle"></span> }
                <i class="jc-dot" [style.background]="color(r.span.service)"></i>
                <span class="jc-wf-service">{{ r.span.service }}</span>&nbsp;<span>{{ r.span.name }}</span>
                @if (r.selfError) { <mat-icon class="jc-wf-icon jc-bad" title="Failed">error</mat-icon> }
                @if (r.slowest) { <mat-icon class="jc-wf-icon" title="Among the slowest spans">schedule</mat-icon> }
                @if (collapsed().has(r.span.spanId)) { <span class="jc-muted jc-small">+{{ r.children }}</span> }
              </span>
              <span class="jc-wf-track">
                <span class="jc-wf-bar" [style.left.%]="r.offset" [style.width.%]="width(r.span)" [style.background]="color(r.span.service)"></span>
                <span class="jc-wf-time" [style.left.%]="r.offset">{{ r.span.durationMs | duration }}</span>
              </span>
            </div>
          }
        </div>

        @if (selected(); as s) {
          <aside class="jc-card jc-span-detail">
            <div class="jc-row"><h2>{{ s.name }}</h2><button mat-icon-button (click)="select(null)" aria-label="Close"><mat-icon>close</mat-icon></button></div>
            <div class="jc-span-facts">
              <span class="jc-badge" [class.jc-health-unhealthy]="s.status === 'Error'" [class.jc-health-healthy]="s.status === 'Ok'">{{ s.status }}</span>
              <span>{{ s.kind }}</span><span>{{ s.durationMs | duration }}</span>
              <span>at +{{ offsetMs(s) | duration }}</span>
            </div>
            @if (s.statusMessage) { <p class="jc-error">{{ s.statusMessage }}</p> }

            <nav class="jc-actions">
              @if (s.serviceId) { <a mat-stroked-button [routerLink]="['/services', s.serviceId]" [queryParams]="ctx.keep()">Service {{ s.service }}</a> }
              @if (s.target; as target) {
                <a mat-stroked-button [routerLink]="target.kind === 'service' ? ['/services', target.id] : ['/resources', target.id]" [queryParams]="ctx.keep()">
                  {{ target.kind === 'service' ? 'Calls' : 'Uses' }} {{ target.name }}</a>
              }
              @if (s.serviceId) {
                <a mat-stroked-button [routerLink]="['/services', s.serviceId]" [queryParams]="aroundSpan(s, { tab: 'metrics' })">Metrics around</a>
                <a mat-stroked-button [routerLink]="['/services', s.serviceId]" [queryParams]="aroundSpan(s, { tab: 'logs' })">Service logs around</a>
                <a mat-stroked-button [routerLink]="['/services', s.serviceId]" [queryParams]="ctx.keep({ tab: 'traces', op: s.name })">Other traces of {{ s.name }}</a>
              }
            </nav>

            <h3>Logs of this span</h3>
            <jc-log-table [query]="spanLogQuery()" [compact]="true" />

            <h3>Span</h3>
            <jc-kv [values]="{ 'service': s.service, 'environment': s.environment, 'instance': s.instance, 'host': s.host,
              'trace id': id(), 'span id': s.spanId, 'parent span id': s.parentSpanId, 'started': s.startedAt,
              'instrumentation scope': s.scope ? s.scope + (s.scopeVersion ? ' ' + s.scopeVersion : '') : null }" />
            <h3>Attributes</h3>
            <jc-kv [values]="s.attributes" empty="No attributes." />
            @if (s.events.length) {
              <h3>Events</h3>
              @for (e of s.events; track $index) {
                <div class="jc-event" [class.jc-wf-error]="e.name === 'exception'">
                  <strong>{{ e.name }}</strong> <span class="jc-muted">+{{ eventOffset(s, e.at) | duration }}</span>
                  <jc-kv [values]="e.attributes" empty="" />
                </div>
              }
            }
            @if (s.links.length) {
              <h3>Links</h3>
              @for (l of s.links; track l.spanId) {
                <a [routerLink]="['/traces', l.traceId]" [queryParams]="ctx.keep({ span: l.spanId })" class="jc-mono">{{ l.traceId }} / {{ l.spanId }}</a>
              }
            }
          </aside>
        }
      </div>
    }`,
})
export class TracePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  /** A trace id, or a W3C traceparent such as a Juice.Audit record before 10.4 holds; links from either open the trace. */
  readonly traceId = input.required<string>();
  readonly span = input<string>();

  protected readonly id = computed(() => plainTraceId(this.traceId()));
  protected readonly trace = rxResource({ params: () => this.id(), stream: ({ params }) => this.api.trace(params) });
  protected readonly error = computed(() => problemText(this.trace.error()));
  protected readonly collapsed = signal(new Set<string>());
  protected readonly showErrorsOnly = signal(false);
  protected readonly selectedId = computed(() => this.span() ?? null);

  private readonly spans = computed(() => this.trace.value()?.spans ?? []);
  // Sub-millisecond precision: spans of a fast in-process flow start microseconds apart.
  protected readonly start = computed(() => Math.min(...this.spans().map(s => preciseTime(s.startedAt))));
  protected readonly total = computed(() =>
    Math.max(0.001, Math.max(...this.spans().map(s => preciseTime(s.startedAt) + s.durationMs)) - this.start()));
  protected readonly root = computed(() => this.spans().find(s => !s.parentSpanId) ?? this.spans()[0]);
  protected readonly services = computed(() => [...new Set(this.spans().map(s => s.service))]);
  protected readonly errors = computed(() => this.spans().filter(s => s.status === 'Error').length);
  protected readonly selected = computed(() => this.spans().find(s => s.spanId === this.selectedId()) ?? null);
  protected readonly spanLogQuery = computed(() => {
    const s = this.selected();
    return s ? { spanId: s.spanId, ...this.windowAround(s) } : {};
  });

  /** Depth-first order with depth; spans whose parent is missing are roots. Children start-ordered. */
  private readonly rows = computed<Row[]>(() => {
    const spans = this.spans();
    const ids = new Set(spans.map(s => s.spanId));
    const byParent = new Map<string | null, Span[]>();
    for (const s of spans) {
      const parent = s.parentSpanId && ids.has(s.parentSpanId) ? s.parentSpanId : null;
      byParent.set(parent, [...(byParent.get(parent) ?? []), s]);
    }
    const slowest = new Set([...spans].sort((a, b) => b.durationMs - a.durationMs).slice(0, Math.max(1, Math.ceil(spans.length * 0.05))).map(s => s.spanId));
    const rows: Row[] = [];
    const walk = (parent: string | null, depth: number) => {
      for (const s of (byParent.get(parent) ?? []).sort((a, b) => a.startedAt.localeCompare(b.startedAt))) {
        rows.push({
          span: s, depth, children: byParent.get(s.spanId)?.length ?? 0,
          offset: 100 * (preciseTime(s.startedAt) - this.start()) / this.total(),
          selfError: s.status === 'Error', slowest: slowest.has(s.spanId) && spans.length > 3,
        });
        walk(s.spanId, depth + 1);
      }
    };
    walk(null, 0);
    return rows;
  });

  /** Hides collapsed subtrees; "error paths only" keeps failed spans and their ancestors. */
  protected readonly visible = computed(() => {
    const rows = this.rows();
    const parentOf = new Map(this.spans().map(s => [s.spanId, s.parentSpanId]));
    let keep: Set<string> | null = null;
    if (this.showErrorsOnly()) {
      keep = new Set<string>();
      for (const r of rows.filter(r => r.selfError)) {
        for (let id: string | null | undefined = r.span.spanId; id; id = parentOf.get(id)) keep.add(id);
      }
    }
    const out: Row[] = [];
    let hiddenBelow: number | null = null;
    for (const r of rows) {
      if (hiddenBelow !== null && r.depth > hiddenBelow) continue;
      hiddenBelow = null;
      if (keep && !keep.has(r.span.spanId)) continue;
      out.push(r);
      if (this.collapsed().has(r.span.spanId)) hiddenBelow = r.depth;
    }
    return out;
  });

  protected readonly ticks = computed(() => [0, 25, 50, 75].map(at => ({ at, label: formatDuration(this.total() * at / 100) })));

  color(service: string) {
    return PALETTE[this.services().indexOf(service) % PALETTE.length];
  }

  width(span: Span) {
    return Math.max(0.3, 100 * span.durationMs / this.total());
  }

  offsetMs(span: Span) {
    return preciseTime(span.startedAt) - this.start();
  }

  eventOffset(span: Span, at: string) {
    return preciseTime(at) - preciseTime(span.startedAt);
  }

  toggle(spanId: string, event: Event) {
    event.stopPropagation();
    const next = new Set(this.collapsed());
    if (!next.delete(spanId)) next.add(spanId);
    this.collapsed.set(next);
  }

  select(spanId: string | null) {
    void this.router.navigate([], { queryParams: { span: spanId }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private windowAround(span: Span) {
    const start = new Date(span.startedAt).getTime();
    return { from: new Date(start - 60_000).toISOString(), to: new Date(start + span.durationMs + 60_000).toISOString() };
  }

  /** The service page over a window around this span, so its metrics and logs line up with it. */
  aroundSpan(span: Span, extra: Record<string, string>) {
    return { ...this.ctx.around(span.startedAt, span.durationMs), ...extra };
  }
}
