import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { ApiService, Query } from '../core/api.service';
import { ContextService, MAX_SEARCH_MS } from '../core/context.service';
import { DurationPipe } from './format';
import { Chart, withGaps } from './ui';

/** Shows a request's failure as a short line instead of a broken page. */
export function problemText(error: unknown): string {
  const e = error as { status?: number; error?: { title?: string; detail?: string } } | undefined;
  if (!e) return '';
  if (e.status === 0) return 'The Juice API cannot be reached.';
  return [e.error?.title, e.error?.detail].filter(Boolean).join(': ') || `Request failed (${e.status}).`;
}

/**
 * Traces matching a query in the current context, newest first (Console requirements §5): enough per row to pick a
 * useful one, and one click to its waterfall.
 */
@Component({
  selector: 'jc-trace-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, DurationPipe, MatProgressBarModule, MatButtonModule],
  template: `
    @if (traces.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (traces.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (traces.hasValue()) {
      @if (rows().length === 0) { <p class="jc-muted">No traces match in this window.</p> }
      @else {
        <table class="jc-table">
          <thead><tr><th>Start</th><th>Root operation</th><th>Duration</th><th>Spans</th><th>Status</th><th>Instance</th></tr></thead>
          <tbody>
            @for (t of rows(); track t.traceId) {
              <tr>
                <td class="jc-nowrap">{{ t.startedAt | date: 'MMM d, HH:mm:ss.SSS' }}</td>
                <td><a [routerLink]="['/traces', t.traceId]" [queryParams]="ctx.keep()">{{ t.rootService }} · {{ t.rootOperation }}</a>
                  @if (t.services.length > 1) { <div class="jc-muted jc-small">{{ t.services.join(' → ') }}</div> }</td>
                <td class="jc-nowrap"><span class="jc-bar" [style.width.%]="100 * t.durationMs / longest()"></span>{{ t.durationMs | duration }}</td>
                <td>{{ t.spanCount }}</td>
                <td>@if (t.errorCount > 0) { <span class="jc-badge jc-health-unhealthy">{{ t.errorCount }} error{{ t.errorCount > 1 ? 's' : '' }}</span> }
                    @else { <span class="jc-badge jc-health-healthy">ok</span> }</td>
                <td class="jc-muted jc-small">{{ t.rootInstance ?? t.rootHost ?? '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
        @if (traces.value().truncated) {
          <button mat-button (click)="older()">Older traces</button>
        }
      }
    }`,
})
export class TraceTable {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly query = input<Query>({});
  readonly limit = input(50);
  /** "Older traces" moves the end of the window back to the oldest row shown. */
  private readonly before = linkedSignal<Query, string | null>({ source: this.query, computation: () => null });

  protected readonly traces = rxResource({
    params: () => ({ query: this.query(), env: this.ctx.environment(), range: this.ctx.range(), before: this.before(), limit: this.limit() }),
    stream: ({ params }) => {
      const window = this.ctx.window(MAX_SEARCH_MS);
      return this.api.traces({ environment: params.env, ...window, ...(params.before ? { to: params.before } : {}), limit: params.limit, ...params.query });
    },
  });
  protected readonly rows = computed(() => this.traces.value()?.items ?? []);
  protected readonly longest = computed(() => Math.max(1, ...this.rows().map(t => t.durationMs)));
  protected readonly error = computed(() => problemText(this.traces.error()));

  older() {
    const rows = this.rows();
    if (rows.length) this.before.set(rows[rows.length - 1].startedAt);
  }
}

/**
 * Log records for a query in the current context (Console requirements §7): each with its trace and span, when the
 * record carries them.
 */
@Component({
  selector: 'jc-log-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, MatProgressBarModule],
  template: `
    @if (logs.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (logs.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (logs.hasValue()) {
      @if (rows().length === 0) { <p class="jc-muted">No log records match in this window.</p> }
      @else {
        <table class="jc-table jc-logs">
          <thead><tr><th>Time</th><th>Severity</th><th>Service</th><th>Message</th><th>Trace</th></tr></thead>
          <tbody>
            @for (l of rows(); track $index) {
              <tr>
                <td class="jc-nowrap">{{ l.timestamp | date: 'MMM d, HH:mm:ss.SSS' }}</td>
                <td><span class="jc-badge" [class]="severityClass(l.severityNumber)">{{ l.severity || l.severityNumber }}</span></td>
                <td class="jc-nowrap">{{ l.service }}@if (l.instance) {<div class="jc-muted jc-small">{{ l.instance }}</div>}</td>
                <td><pre class="jc-body">{{ l.body }}@if (l.bodyTruncated) {…}</pre></td>
                <td class="jc-nowrap">
                  @if (l.traceId) {
                    <a [routerLink]="['/traces', l.traceId]" [queryParams]="ctx.keep(l.spanId ? { span: l.spanId } : {})">{{ l.spanId ? 'span' : 'trace' }} ↗</a>
                  } @else { <span class="jc-muted">—</span> }
                </td>
              </tr>
            }
          </tbody>
        </table>
        @if (logs.value().truncated) { <p class="jc-muted">Showing the newest {{ rows().length }}; narrow the window or filters to see more.</p> }
      }
    }`,
})
export class LogTable {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly query = input<Query>({});

  protected readonly logs = rxResource({
    params: () => ({ query: this.query(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => this.api.logs({ environment: params.env, ...this.ctx.window(MAX_SEARCH_MS), limit: 200, ...params.query }),
  });
  protected readonly rows = computed(() => this.logs.value()?.items ?? []);
  protected readonly error = computed(() => problemText(this.logs.error()));

  severityClass(n: number) {
    return n >= 17 ? 'jc-health-unhealthy' : n >= 13 ? 'jc-health-degraded' : n >= 9 ? 'jc-health-healthy' : 'jc-health-none';
  }
}

/**
 * The metrics reported for a scope (service, instance or host) and a chart of the one selected (Console requirements
 * §7): a counter as a rate, a gauge or histogram as an average.
 */
@Component({
  selector: 'jc-metric-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatSelectModule, MatProgressBarModule, Chart],
  template: `
    @if (metrics.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (metrics.error()) { <p class="jc-error">{{ metricsError() }}</p> }
    @if (metrics.hasValue()) {
      @if (names().length === 0) { <p class="jc-muted">No metrics reported in this window.</p> }
      @else {
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
          <mat-label>Metric</mat-label>
          <mat-select [value]="selected()" (valueChange)="selected.set($event)">
            @for (m of names(); track m.name) { <mat-option [value]="m.name">{{ m.name }} <span class="jc-muted">({{ m.type }}{{ m.unit ? ', ' + m.unit : '' }})</span></mat-option> }
          </mat-select>
        </mat-form-field>
        @if (series.hasValue()) {
          <jc-chart [title]="title()" [series]="[{ name: series.value().name, points: points() }]" />
        }
      }
    }`,
})
export class MetricExplorer {
  private readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly scope = input<Query>({});
  readonly initial = input<string | null>(null);

  protected readonly metrics = rxResource({
    params: () => ({ scope: this.scope(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => this.api.metrics({ environment: params.env, ...this.ctx.window(), ...params.scope }),
  });
  protected readonly names = computed(() => this.metrics.value()?.items ?? []);
  protected readonly selected = linkedSignal(() => {
    const names = this.names();
    return names.find(m => m.name === this.initial())?.name ?? names[0]?.name ?? null;
  });
  protected readonly series = rxResource({
    // Idle until a metric is chosen.
    params: () => this.selected() ? { name: this.selected()!, scope: this.scope(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() } : undefined,
    stream: ({ params }) => {
      this.window = this.ctx.window();
      return this.api.series({ name: params.name, environment: params.env, ...this.window, ...params.scope });
    },
  });
  private window = { from: '', to: '' };
  protected readonly points = computed(() => {
    const s = this.series.value();
    return s ? withGaps(s.points, this.window.from, this.window.to, s.stepSeconds) : [];
  });
  protected readonly title = computed(() => {
    const s = this.series.value();
    if (!s) return '';
    const what = s.aggregation === 'rate' ? 'rate per second' : 'average';
    return `${s.name} — ${what}${s.unit ? ` (${s.unit}${s.aggregation === 'rate' ? '/s' : ''})` : ''}`;
  });
  protected readonly metricsError = computed(() => problemText(this.metrics.error()));
}

/** Requests, error rate and p95 of a service (or one operation) over the window. */
@Component({
  selector: 'jc-service-stats',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Chart, MatProgressBarModule],
  template: `
    @if (stats.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (stats.hasValue()) {
      <div class="jc-grid-3">
        <jc-chart title="Requests per second" [series]="[{ name: 'requests/s', points: pick('requestsPerSecond') }]" />
        <jc-chart title="Error rate" [series]="[{ name: 'errors', points: pick('errorRate') }]" [format]="percent" />
        <jc-chart title="p95 latency" [series]="[{ name: 'p95', points: pick('p95Ms') }]" [format]="ms" />
      </div>
    }`,
})
export class ServiceStatsCharts {
  private readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly query = input.required<Query>();
  private window = { from: '', to: '' };
  protected readonly percent = (v: number) => `${Math.round(v * 1000) / 10} %`;
  protected readonly ms = (v: number) => `${Math.round(v)} ms`;

  protected readonly stats = rxResource({
    params: () => ({ query: this.query(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => {
      this.window = this.ctx.window();
      return this.api.serviceStats({ environment: params.env, ...this.window, ...params.query });
    },
  });

  pick(field: 'requestsPerSecond' | 'errorRate' | 'p95Ms') {
    const s = this.stats.value();
    return s ? withGaps(s.points.map(p => ({ at: p.at, value: p[field] })), this.window.from, this.window.to, s.stepSeconds) : [];
  }
}

