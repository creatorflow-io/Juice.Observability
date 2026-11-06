import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { LoadRun, LoadRunLimit, LoadRunResults, LoadRunService } from '../core/models';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/** Comparing more runs than this doesn't fit on a page. */
const MAX_COMPARED = 4;

function perSecond(value: number): string { return value >= 10 ? value.toFixed(0) : value.toFixed(2); }

/** Where a service started to struggle, in words. */
export function limitText(limit: LoadRunLimit): string {
  const at = `from ${perSecond(limit.perSecond)}/s`;
  switch (limit.reason) {
    case 'latency': return `Latency ${at}: p95 ${limit.value.toFixed(0)} ms, ${limit.baseline?.toFixed(0)} ms when quiet`;
    case 'consumer-busy': return `Message handling busy ${at}: ${(limit.value * 100).toFixed(0)}% of the time`;
    case 'queue': return `Thread pool queue ${at}: ${limit.value.toFixed(0)} waiting`;
    case 'errors': return `Errors ${at}: ${(limit.value * 100).toFixed(1)}%`;
    case 'backlog': return `Outbox backlog ${at}: ${limit.value.toFixed(0)} undelivered`;
    default: return `${limit.reason} ${at}`;
  }
}

/**
 * Load test runs: each is the window a test marked through the API (the platform generates no load). Selected runs are compared per
 * service, with where each started to struggle, which everyday traffic can't show. The selection is in the URL.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatProgressBarModule, ContextBar],
  template: `
    <jc-context-bar><h1>Load runs</h1></jc-context-bar>
    @if (runs.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (runs.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (runs.value(); as page) {
      @if (page.items.length === 0) {
        <p class="jc-muted">No load runs yet. A load test marks its start and end with <code>POST /load-runs</code> and <code>POST /load-runs/{{ '{' }}id{{ '}' }}/end</code>; see the README.</p>
      } @else {
        <table class="jc-table" data-testid="load-runs">
          <thead><tr><th>Compare</th><th>Run</th><th>Environment</th><th>Started</th><th>Length</th><th>By</th></tr></thead>
          <tbody>
            @for (r of page.items; track r.id) {
              <tr>
                <td><input type="checkbox" [attr.aria-label]="'Compare ' + r.name" [checked]="selected().includes(r.id)"
                  [disabled]="!selected().includes(r.id) && selected().length >= maxCompared" (change)="toggle(r.id)" /></td>
                <td><strong>{{ r.name }}</strong></td>
                <td>{{ r.environment ?? 'all' }}</td>
                <td>{{ time(r.startedAt) }}</td>
                <td>@if (r.endedAt) { {{ length(r) }} } @else { <span class="jc-muted">not ended</span> }</td>
                <td>{{ r.startedBy }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    }

    @if (results.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (results.error()) { <p class="jc-error">{{ resultsError() }}</p> }
    @if (results.value(); as compared) {
      @if (compared.length) {
        <section class="jc-card" aria-labelledby="jc-load-compare" data-testid="load-compare">
          <h3 id="jc-load-compare">{{ compared.length === 1 ? 'Results' : 'Comparison' }}</h3>
          @if (serviceNames().length === 0) { <p class="jc-muted">No telemetry in these windows yet. Only whole time steps count, so a short run may show nothing.</p> }
          @else {
            <table class="jc-table">
              <thead><tr><th>Service</th>@for (c of compared; track c.run.id) { <th>{{ c.run.name }}</th> }</tr></thead>
              <tbody>
                @for (name of serviceNames(); track name) {
                  <tr>
                    <th>{{ name }}</th>
                    @for (c of compared; track c.run.id) {
                      <td>
                        @if (service(c, name); as s) {
                          <div>Peak {{ rate(s.peakPerSecond) }}/s · p95 {{ s.p95AtPeakMs !== null ? s.p95AtPeakMs.toFixed(0) + ' ms' : '—' }} · errors {{ (s.errorRateAtPeak * 100).toFixed(1) }}%</div>
                          @if (s.limit; as l) { <div class="jc-bad" data-testid="load-limit">{{ limit(l) }}</div> }
                          @else { <div class="jc-muted">No limit reached</div> }
                          @if (s.serviceId) { <a class="jc-small" [routerLink]="['services', s.serviceId] | jcLink" [queryParams]="window(c.run)">Open the window</a> }
                        } @else { <span class="jc-muted">No traffic</span> }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
            @for (c of compared; track c.run.id) {
              @for (s of c.services; track s.name) {
                <details class="jc-load-steps">
                  <summary>{{ c.run.name }}: {{ s.name }}, each {{ c.stepSeconds }} s</summary>
                  <table class="jc-table">
                    <thead><tr><th>Time</th><th>Load/s</th><th>p95</th><th>Errors</th><th>Thread pool queue</th><th>Consumer busy</th><th>Outbox backlog</th></tr></thead>
                    <tbody>
                      @for (p of s.points; track p.at) {
                        <tr [class.jc-bad]="s.limit?.at === p.at">
                          <td>{{ time(p.at) }}</td>
                          <td>{{ rate(p.perSecond) }}</td>
                          <td>{{ p.p95Ms !== null ? p.p95Ms.toFixed(0) + ' ms' : '—' }}</td>
                          <td>{{ (p.errorRate * 100).toFixed(1) }}%</td>
                          <td>{{ p.queue ?? '—' }}</td>
                          <td>{{ (p.consumerBusy * 100).toFixed(0) }}%</td>
                          <td>{{ p.backlog }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </details>
              }
            }
            <ul class="jc-capacity-notes">
              <li>Counted from spans, which undercount if traces are sampled. A message's wait in its queue isn't in its span: look at how busy the consumer was.</li>
              <li>The outbox backlog includes messages of rolled-back transactions. Figures from other hardware carry over only roughly.</li>
            </ul>
          }
        </section>
      }
    }`,
})
export class LoadRunsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  /** The selected run ids, comma separated. */
  readonly compare = input<string>();

  protected readonly maxCompared = MAX_COMPARED;
  protected readonly selected = computed(() => (this.compare() ?? '').split(',').filter(Boolean).slice(0, MAX_COMPARED));
  protected readonly runs = apiResource({ params: () => this.ctx.environment(), stream: ({ params }) => this.api.loadRuns({ environment: params }) });
  protected readonly results = apiResource({
    params: () => ({ ids: this.selected() }),
    stream: ({ params }) => params.ids.length ? forkJoin(params.ids.map(id => this.api.loadRunResults(id))) : of<LoadRunResults[]>([]),
  });
  protected readonly error = computed(() => problemText(this.runs.error()));
  protected readonly resultsError = computed(() => problemText(this.results.error()));
  /** Services of all compared runs, those that reached a limit first. */
  protected readonly serviceNames = computed(() => {
    const services = (this.results.value() ?? []).flatMap(c => c.services);
    const limited = new Set(services.filter(s => s.limit).map(s => s.name));
    return [...new Set(services.map(s => s.name))].sort((a, b) => Number(limited.has(b)) - Number(limited.has(a)) || a.localeCompare(b));
  });

  protected readonly limit = limitText;
  protected readonly rate = perSecond;

  protected service(results: LoadRunResults, name: string): LoadRunService | null {
    return results.services.find(s => s.name === name) ?? null;
  }

  protected time(at: string): string { return new Date(at).toLocaleString(); }

  protected length(run: LoadRun): string {
    const seconds = Math.round((new Date(run.endedAt!).getTime() - new Date(run.startedAt).getTime()) / 1000);
    return seconds >= 120 ? `${Math.round(seconds / 60)} min` : `${seconds} s`;
  }

  /** A service's pages over exactly this run's window. */
  protected window(run: LoadRun) {
    return this.ctx.keep({ from: run.startedAt, to: run.endedAt ?? new Date().toISOString(), range: null, env: run.environment ?? (this.ctx.environment() || null) });
  }

  protected toggle(id: string) {
    const current = this.selected();
    const next = current.includes(id) ? current.filter(c => c !== id) : [...current, id];
    void this.router.navigate([], { queryParams: { compare: next.join(',') || null }, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
