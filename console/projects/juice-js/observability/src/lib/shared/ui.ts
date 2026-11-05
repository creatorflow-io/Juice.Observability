import { ObservabilityLinks } from '../core/options';
import {
  ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import type { ECharts, EChartsOption } from 'echarts';
import { ApiService } from '../core/api.service';
import { ContextService, RANGES } from '../core/context.service';
import { HealthState } from '../core/models';
import { onThemeChange, seriesColors, themeColor } from './theme';

/** A health state, with "no data" for a missing one (Console requirements §10: missing data is not zero). */
@Component({
  selector: 'jc-health',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="jc-badge" [class]="'jc-health-' + (state() ?? 'none').toLowerCase()"
    [attr.title]="stale() ? 'Evaluation has stopped: this state may be out of date' : null">
    {{ state() ?? 'No data' }}@if (stale()) {<span class="jc-stale"> · stale</span>}</span>`,
})
export class HealthBadge {
  readonly state = input<HealthState | null | undefined>();
  readonly stale = input(false);
}

/** Observed or inferred dependency evidence (Console requirements §9): inferred is never shown as confirmed. */
@Component({
  selector: 'jc-confidence',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="jc-badge" [class.jc-observed]="observed()" [class.jc-inferred]="!observed()"
    [attr.title]="observed() ? 'Seen in traces: a call from one service to the other' : 'Inferred from span attributes, not a confirmed call'">
    {{ observed() ? 'observed' : 'inferred' }}</span>`,
})
export class ConfidenceBadge {
  readonly confidence = input.required<string>();
  protected readonly observed = computed(() => this.confidence().toLowerCase() === 'observed');
}

/** Attributes as a key/value table, sorted, values wrapped. */
@Component({
  selector: 'jc-kv',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (entries().length === 0) { <p class="jc-muted">{{ empty() }}</p> }
    @else {
      <table class="jc-kv"><tbody>
        @for (e of entries(); track e[0]) { <tr><th>{{ e[0] }}</th><td>{{ e[1] }}</td></tr> }
      </tbody></table>
    }`,
})
export class KeyValueTable {
  readonly values = input<Record<string, string | null | undefined>>({});
  readonly empty = input('None');
  protected readonly entries = computed(() =>
    Object.entries(this.values()).filter((e): e is [string, string] => e[1] !== null && e[1] !== undefined && e[1] !== '')
      .sort((a, b) => a[0].localeCompare(b[0])));
}

/**
 * The context bar on every page (Console requirements §13, §17): environment, time range, and a global lookup for a
 * trace id, a span id or a service name.
 */
@Component({
  selector: 'jc-context-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatSelectModule, MatIconModule, MatButtonModule],
  template: `
    <div class="jc-context">
      <ng-content />
      <span class="jc-spacer"></span>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm">
        <mat-label>Environment</mat-label>
        <mat-select [value]="ctx.environment()" (valueChange)="ctx.set({ env: $event || null })">
          <mat-option value="">All</mat-option>
          @for (e of environments(); track e.id) { <mat-option [value]="e.key">{{ e.key }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm">
        <mat-label>Time range</mat-label>
        <mat-select [value]="ctx.range()" (valueChange)="ctx.set({ range: $event })">
          @for (r of ranges; track r) { <mat-option [value]="r">Last {{ r }}</mat-option> }
          @if (ctx.range() === 'custom') { <mat-option value="custom" disabled>Custom window</mat-option> }
        </mat-select>
      </mat-form-field>
      <form class="jc-lookup" (ngSubmit)="lookup()">
        <mat-icon>search</mat-icon>
        <input name="q" [(ngModel)]="query" placeholder="Trace ID, span ID or service" aria-label="Go to trace, span or service">
      </form>
    </div>
    @if (message()) { <p class="jc-muted jc-lookup-message">{{ message() }}</p> }`,
})
export class ContextBar {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly links = inject(ObservabilityLinks);
  protected readonly ranges = Object.keys(RANGES);
  protected readonly environments = toSignal(this.api.environments().pipe(map(p => p.items), catchError(() => of([]))), { initialValue: [] });
  protected query = '';
  protected readonly message = signal('');

  lookup() {
    const q = this.query.trim();
    if (!q) return;
    this.message.set('Searching…');
    this.api.search(q).subscribe({
      next: page => {
        const hit = page.items[0];
        if (!hit) { this.message.set(`Nothing found for "${q}".`); return; }
        this.message.set(page.items.length > 1 ? `${page.items.length} matches; showing the first.` : '');
        const extra = hit.kind === 'span' ? { span: hit.id } : {};
        void (hit.kind === 'service'
          ? this.router.navigate(this.links.to('services', hit.id), { queryParams: this.ctx.keep() })
          : this.router.navigate(this.links.to('traces', hit.traceId), { queryParams: this.ctx.keep(extra) }));
      },
      error: () => this.message.set('Search failed.'),
    });
  }
}

export interface ChartSeries { name: string; points: { at: string; value: number | null }[]; unit?: string; }

/**
 * The API returns no point for a bucket without data; give each missing bucket a null so the chart shows a gap
 * instead of drawing a line across it (no data is not zero).
 */
export function withGaps(points: { at: string; value: number | null }[], from: string, to: string, stepSeconds: number) {
  const step = stepSeconds * 1000;
  const byTime = new Map(points.map(p => [new Date(p.at).getTime(), p.value]));
  const out: { at: string; value: number | null }[] = [];
  for (let t = Math.floor(new Date(from).getTime() / step) * step; t < new Date(to).getTime(); t += step) {
    out.push({ at: new Date(t).toISOString(), value: byTime.get(t) ?? null });
  }
  return out;
}

/**
 * A time series chart (Apache ECharts, loaded on first use). A missing bucket is a gap in the line, never zero.
 */
@Component({
  selector: 'jc-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="jc-chart-title">{{ title() }}</div>
    @if (empty()) { <div class="jc-chart jc-chart-empty">No data in this window</div> }
    <div #host class="jc-chart" [hidden]="empty()"></div>`,
})
export class Chart implements OnDestroy {
  readonly title = input('');
  readonly series = input<ChartSeries[]>([]);
  readonly format = input<(v: number) => string>(v => String(Math.round(v * 100) / 100));
  /** Moments to mark with a vertical line. */
  readonly marks = input<string[]>([]);
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  protected readonly empty = computed(() => this.series().every(s => s.points.every(p => p.value === null)));
  private chart?: ECharts;
  private observer?: ResizeObserver;
  private stopThemeWatch?: () => void;

  constructor() {
    afterNextRender(async () => {
      // Only what a line chart needs: the full library is three times the size.
      const [echarts, charts, components, renderers] = await Promise.all([
        import('echarts/core'), import('echarts/charts'), import('echarts/components'), import('echarts/renderers')]);
      echarts.use([charts.LineChart, components.GridComponent, components.TooltipComponent, components.LegendComponent, components.MarkLineComponent, renderers.SVGRenderer]);
      this.chart = echarts.init(this.host().nativeElement, undefined, { renderer: 'svg' });
      this.observer = new ResizeObserver(() => this.chart?.resize());
      this.observer.observe(this.host().nativeElement);
      this.stopThemeWatch = onThemeChange(() => this.render());
      this.render();
    });
    effect(() => { this.series(); this.marks(); this.render(); });
  }

  private render() {
    if (!this.chart) return;
    const format = this.format();
    // The app's theme colours (observability.colors), read at each render so a theme switch repaints.
    const host = this.host().nativeElement;
    const [text, muted, border, colors] = [themeColor('text', host), themeColor('muted', host), themeColor('border', host), seriesColors(host)];
    const option: EChartsOption = {
      animation: false,
      color: colors.length ? colors : undefined,
      grid: { left: 56, right: 16, top: 16, bottom: 28 },
      tooltip: { trigger: 'axis', valueFormatter: v => (typeof v === 'number' ? format(v) : '—') },
      legend: this.series().length > 1 ? { top: 0, textStyle: { color: text } } : undefined,
      xAxis: { type: 'time', axisLabel: { color: muted, hideOverlap: true }, axisLine: { lineStyle: { color: border } } },
      yAxis: { type: 'value', axisLabel: { color: muted, formatter: (v: number) => format(v) },
        splitLine: { lineStyle: { color: border } } },
      series: this.series().map((s, i) => ({
        name: s.name, type: 'line', showSymbol: false, connectNulls: false,
        data: s.points.map(p => [p.at, p.value]),
        ...(i === 0 && this.marks().length ? { markLine: {
          silent: true, symbol: 'none', label: { show: false }, lineStyle: { color: muted, type: 'dashed' as const },
          data: this.marks().map(at => ({ xAxis: at })),
        } } : {}),
      })),
    };
    this.chart.setOption(option, true);
  }

  ngOnDestroy() {
    this.observer?.disconnect();
    this.stopThemeWatch?.();
    this.chart?.dispose();
  }
}
