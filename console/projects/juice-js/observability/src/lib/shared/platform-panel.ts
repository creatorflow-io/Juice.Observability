import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { EMPTY, map, switchMap, timer } from 'rxjs';
import { ApiService } from '../core/api.service';
import { PlatformLevel } from '../core/models';
import { AgoPipe } from './format';
import { apiResource } from './resource';
import { problemText } from './telemetry';

/** How often the panel reads again: the platform samples every 15 s, so faster shows nothing new. */
const REFRESH_MS = 15_000;

const LEVEL_CLASS: Record<PlatformLevel, string> = {
  ok: 'jc-health-healthy', warn: 'jc-health-degraded', bad: 'jc-health-unhealthy', unknown: 'jc-health-unknown',
};

const RESOURCE_NAMES: Record<string, string> = {
  'clickhouse.cpu': 'ClickHouse CPU', 'clickhouse.inserts_in_flight': 'Collector inserts in flight', 'collector.queue': 'Export queue',
};

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '–';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let v = bytes, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v >= 100 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
}

export function formatRate(perSecond: number | null | undefined): string {
  if (perSecond === null || perSecond === undefined) return '–';
  return perSecond >= 1000 ? `${(perSecond / 1000).toFixed(perSecond >= 100_000 ? 0 : 1)}k/s` : `${perSecond.toFixed(perSecond >= 10 ? 0 : 1)}/s`;
}

/** A resource's figure in its own unit: cores and inserts as numbers, shares as percentages. */
export function formatMeasure(value: number | null | undefined, unit: string): string {
  if (value === null || value === undefined) return '–';
  return unit === 'cores' || unit === 'inserts' ? value.toFixed(value >= 10 ? 0 : 1) : `${Math.round(value * 100)} %`;
}

/**
 * The Overview's "Platform" panel (Phase 11 R8) and the capacity view (R9), for operators: the workers, ingestion, ClickHouse and disk, each
 * green, amber or red against the scale steps' lines, and what the platform held at in the steps it is calibrated on. A viewer doesn't see it.
 */
@Component({
  selector: 'jc-platform-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DecimalPipe],
  template: `
    @if (!hidden()) {
      <section class="jc-card jc-platform" aria-labelledby="jc-platform-title">
        <div class="jc-row">
          <h2 id="jc-platform-title">Platform</h2>
          @if (o(); as o) {
            <span class="jc-badge" [class]="cls(o.status === 'ok' ? 'ok' : 'bad')">{{ o.status }}</span>
            <span class="jc-muted">sampled {{ o.at | ago }}</span>
          }
        </div>
        @if (error()) { <p class="jc-error">{{ error() }}</p> }
        @if (o(); as o) {
          <div class="jc-grid-2">
            <div>
              <h3>Workers</h3>
              @for (w of o.workers; track w.name) {
                <div class="jc-list-row">
                  <span class="jc-badge" [class]="cls(w.level)">{{ w.state }}</span>
                  <strong>{{ w.name }}</strong>
                  @if (w.utilisation !== null) { <span title="Its recent passes against its interval">{{ pct(w.utilisation) }} busy</span> }
                  @if (w.lastPassEndedAt) { <span class="jc-muted">last pass {{ w.lastPassEndedAt | ago }}</span> }
                </div>
              }
              @for (b of o.backlogs; track b.name) {
                <div class="jc-list-row">
                  <span class="jc-badge" [class]="cls(b.level)">{{ b.state }}</span>
                  <strong>{{ b.name }} backlog</strong>
                  <span class="jc-muted">{{ b.ageSeconds === null ? '–' : seconds(b.ageSeconds) }} of {{ seconds(b.lineSeconds) }}</span>
                </div>
              }
            </div>
            <div>
              <h3>Ingestion</h3>
              @if (o.ingestion; as i) {
                @for (s of i.signals; track s.name) {
                  <div class="jc-list-row">
                    <span class="jc-badge" [class]="cls(s.level)">{{ s.name }}</span>
                    <span>{{ rate(s.acceptedPerSecond) }} received</span>
                    <span [class.jc-muted]="!s.refusedPerSecond">{{ rate(s.refusedPerSecond) }} refused</span>
                    <span [class.jc-muted]="!s.droppedPerSecond">{{ rate(s.droppedPerSecond) }} dropped</span>
                  </div>
                }
                @for (q of i.queues; track q.exporter) {
                  <div class="jc-list-row">
                    <span class="jc-badge" [class]="cls(q.level)">{{ pct(q.fill) }}</span>
                    <span>{{ q.exporter }} queue</span>
                    <span class="jc-muted">{{ q.size | number }} of {{ q.capacity | number }}</span>
                  </div>
                }
              } @else { <p class="jc-muted">No figures: the Collector's metrics page isn't read (Telemetry:CollectorMetricsUrl), or the last sample is old.</p> }

              <h3>ClickHouse</h3>
              @if (o.clickHouse; as c) {
                <div class="jc-list-row">
                  <span class="jc-badge" [class]="cls(c.cpuLevel)">CPU</span>
                  <span>{{ c.cores === null ? '–' : c.cores.toFixed(1) }} of {{ c.coresAvailable ?? '–' }} cores</span>
                  <span class="jc-muted">{{ c.runningQueries }} queries running</span>
                </div>
                <div class="jc-list-row">
                  <span class="jc-badge" [class]="cls(c.insertLevel)">inserts</span>
                  <span>p95 {{ ms(c.insertP95Seconds) }}</span>
                  <span class="jc-muted">{{ c.insertsInFlight === null ? '–' : c.insertsInFlight.toFixed(1) }} in flight</span>
                </div>
                @for (r of c.reads.slice(0, 6); track r.name) {
                  <div class="jc-list-row">
                    <span class="jc-badge" [class]="cls(r.level)">p95 {{ ms(r.p95Seconds) }}</span>
                    <span>{{ r.name }}</span><span class="jc-muted">{{ r.count }} in 15 s</span>
                  </div>
                }
              } @else { <p class="jc-muted">No figures: ClickHouse isn't sampled (ClickHouse:MonitorConnectionString), or didn't answer.</p> }

              <h3>Disk</h3>
              @if (o.disk; as d) {
                <div class="jc-list-row">
                  <span class="jc-badge" [class]="cls(d.level)">{{ d.daysUntilFull === null ? 'not growing' : days(d.daysUntilFull) + ' until full' }}</span>
                  <span>{{ bytes(d.used) }} used, {{ bytes(d.free) }} free</span>
                </div>
                <div class="jc-list-row">
                  <span>{{ bytes(d.bytesPerDay) }} a day@if (d.estimated) { <span class="jc-muted"> (estimated from today)</span> }</span>
                  <span class="jc-muted">at retention: {{ bytes(d.needAtRetention) }} of {{ bytes(d.total) }}</span>
                </div>
              } @else { <p class="jc-muted">No figures.</p> }
            </div>
          </div>

          <h3>Capacity</h3>
          <p class="jc-muted">{{ o.capacity.label }}</p>
          <div class="jc-list-row">
            @for (l of o.capacity.load; track l.signal) {
              <span>{{ l.signal }} <strong>{{ l.multiple === null ? '–' : l.multiple.toFixed(1) + '×' }}</strong>
                <span class="jc-muted">({{ rate(l.perSecond) }} of {{ rate(l.reference) }} at 1×)</span></span>
            }
          </div>
          @if (o.capacity.calibrated) {
            <p>Held every line to <strong>{{ o.capacity.heldTo }}×</strong>; crossed one by {{ o.capacity.crossedAt }}×
              @if (o.capacity.crossed.length) { <span class="jc-muted">({{ o.capacity.crossed.join('; ') }})</span> }.
              @if (o.capacity.headroom !== null) { Today's load is {{ (1 / o.capacity.headroom * 100).toFixed(0) }} % of what held. }</p>
          }
          <div class="jc-list-row jc-muted">
            Now: {{ o.capacity.mix.rules }} rules, {{ o.capacity.mix.slos }} SLOs, {{ o.capacity.mix.services }} services
            @if (o.capacity.mix.calibratedRules !== null) {
              · calibrated with {{ o.capacity.mix.calibratedRules }} rules, {{ o.capacity.mix.calibratedSlos }} SLOs, {{ o.capacity.mix.calibratedServices }} services
              and {{ o.capacity.mix.calibratedSearchesPerSecond }} searches a second
            }
          </div>
          <table class="jc-table">
            <thead><tr><th>Resource</th><th>Now</th>@if (o.capacity.calibrated) { <th>Where every line held ({{ o.capacity.heldTo }}×)</th><th>Where one crossed ({{ o.capacity.crossedAt }}×)</th> }</tr></thead>
            <tbody>
              @for (r of o.capacity.resources; track r.name) {
                <tr>
                  <td>{{ resource(r.name) }}</td>
                  <td><span class="jc-badge" [class]="cls(r.level)">{{ measure(r.now, r.unit) }}</span></td>
                  @if (o.capacity.calibrated) { <td>{{ measure(r.atHeld, r.unit) }}</td><td>{{ measure(r.atCrossed, r.unit) }}</td> }
                </tr>
              }
            </tbody>
          </table>
        }
      </section>
    }`,
})
export class PlatformPanel {
  private readonly api = inject(ApiService);

  protected readonly overview = apiResource({
    // Only for operators and admins (the tenant says so): a viewer never asks, so never gets the 403.
    stream: () => this.api.tenant$.pipe(switchMap(t => t.canOperate ? timer(0, REFRESH_MS).pipe(switchMap(() => this.api.platformOverview())) : EMPTY)),
  });
  protected readonly o = computed(() => this.overview.value());
  /** Whether the user is an operator or admin, from the tenant (false until it is known). */
  private readonly canOperate = toSignal(this.api.tenant$.pipe(map(t => !!t.canOperate)), { initialValue: false });
  /** A viewer isn't an operator: no panel (and a 403 from a role changed since sign-in hides it too, without an error). */
  protected readonly hidden = computed(() => !this.canOperate() || (this.overview.error() as { status?: number } | undefined)?.status === 403);
  protected readonly error = computed(() => this.overview.error() && !this.hidden() ? problemText(this.overview.error()) : '');

  protected cls(level: PlatformLevel): string { return `jc-badge ${LEVEL_CLASS[level]}`; }
  protected pct(share: number): string { return `${Math.round(share * 100)} %`; }
  protected rate(perSecond: number | null): string { return formatRate(perSecond); }
  protected bytes(bytes: number | null): string { return formatBytes(bytes); }
  protected measure(value: number | null, unit: string): string { return formatMeasure(value, unit); }
  protected ms(seconds: number | null): string { return seconds === null ? '–' : `${Math.round(seconds * 1000)} ms`; }
  protected seconds(s: number): string { return s >= 120 ? `${Math.round(s / 60)} min` : `${Math.round(s)} s`; }
  protected days(d: number): string { return d >= 365 ? 'over a year' : `${Math.round(d)} days`; }
  protected resource(name: string): string { return RESOURCE_NAMES[name] ?? (name.startsWith('worker.') ? `Worker ${name.slice(7)}` : name); }
}
