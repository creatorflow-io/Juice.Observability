import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { SloSummary, SloWindow } from '../core/models';
import { LinkPipe } from '../core/options';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/** How a state reads, and how its badge looks. */
export const SLO_STATES: Record<string, { text: string; css: string }> = {
  Met: { text: 'Met', css: 'jc-health-healthy' },
  AtRisk: { text: 'At risk', css: 'jc-health-degraded' },
  Exhausted: { text: 'Budget exhausted', css: 'jc-health-unhealthy' },
  InsufficientData: { text: 'Not enough data', css: 'jc-health-none' },
  Unknown: { text: 'Unknown', css: 'jc-health-none' },
};

/** `99.5` → `99.5%`; an attainment ratio → percent with enough decimals to see it move. */
export function percent(ratio: number | null | undefined, digits = 2): string {
  return ratio === null || ratio === undefined ? '—' : `${(ratio * 100).toFixed(digits)}%`;
}

export function objective(s: { sli: string; target: number; thresholdMs: number | null }): string {
  return s.sli === 'Latency' ? `${s.target}% within ${s.thresholdMs} ms` : `${s.target}% available`;
}

/** A state's badge text and class; an unknown name shows as itself. */
export function sloState(name: string): { text: string; css: string } {
  return SLO_STATES[name] ?? { text: name, css: 'jc-health-none' };
}

/** How many times over the budget is: `3.5×`, `91×`. */
export function times(used: number): string {
  return `${used >= 10 ? Math.round(used).toLocaleString() : used.toFixed(1)}×`;
}

export function burnText(w: SloWindow | undefined): string {
  if (!w) return '—';
  if (w.state === 'Unknown') return 'unknown';
  if (w.state === 'InsufficientData') return 'too few requests';
  return `${(w.burnRate ?? 0).toFixed(1)}×`;
}

/**
 * Service level objectives (roadmap Phase 9), as the deployment's file defines them: technical ones (availability, latency) and business ones apart.
 * Missing data never reads as good: an SLO without enough counted minutes or requests says so instead of showing a budget.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, RouterLink, LinkPipe, MatProgressBarModule, ContextBar],
  template: `
    <jc-context-bar><h1>SLOs</h1></jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card" data-testid="slos-technical">
      <h2>Technical</h2>
      <p class="jc-muted jc-small">Availability and latency of services, from their requests over the last 30 days. Defined in the deployment's SLO file.</p>
      <ng-container *ngTemplateOutlet="table; context: { $implicit: technical() }" />
      @if (!technical().length && !data.isLoading()) { <p class="jc-muted" data-testid="slos-none">No technical SLOs are defined: add them to the file named by <code>Slos:File</code>.</p> }
    </section>

    <section class="jc-card" data-testid="slos-business">
      <h2>Business</h2>
      @if (business().length) {
        <ng-container *ngTemplateOutlet="table; context: { $implicit: business() }" />
      } @else {
        <p class="jc-muted">No business SLOs yet. Business measures (a business success rate, message processing, queue latency, data freshness) are kept apart from
          technical ones, and come in a later version.</p>
      }
    </section>

    <ng-template #table let-items>
      @if (items.length) {
        <table class="jc-table">
          <thead><tr><th>SLO</th><th>Objective</th><th>State</th><th>Last 30 days</th><th>Budget left</th><th>Burn, 1 h</th><th>Burn, 6 h</th></tr></thead>
          <tbody>
            @for (s of items; track s.key) {
              <tr [attr.data-testid]="'slo-' + s.key">
                <td><a [routerLink]="['slos', s.key] | jcLink" [queryParams]="ctx.keep()">{{ s.name }}</a>
                  <div class="jc-muted jc-small">{{ s.service }} · {{ s.environment }}@if (s.pendingVersion) { · version {{ s.pendingVersion }} pending }</div></td>
                <td>{{ objective(s) }}</td>
                <td><span class="jc-badge" [class]="state(s.state).css" data-testid="slo-state">{{ state(s.state).text }}</span></td>
                <td>{{ percent(s.attainment, 3) }}<div class="jc-muted jc-small">{{ s.total.toLocaleString() }} requests</div></td>
                <td data-testid="slo-budget-left">@if (s.budgetRemaining !== null && s.budgetUsed !== null) {
                    @if (s.budgetRemaining >= 0) { <div class="jc-meter"><span [style.width.%]="meter(s.budgetRemaining)"></span></div>{{ percent(s.budgetRemaining, 0) }} }
                    @else { <span class="jc-bad">none: overspent {{ times(s.budgetUsed) }}</span> }
                  } @else { — }</td>
                <td>{{ burn(s, '1h') }}</td>
                <td>{{ burn(s, '6h') }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    </ng-template>
  `,
  styles: [`
    .jc-meter { display: inline-block; width: 64px; height: 6px; margin-right: 8px; border-radius: 3px; background: var(--jc-border, rgba(127,127,127,.25)); vertical-align: middle; overflow: hidden; }
    .jc-meter span { display: block; height: 100%; background: currentColor; }
  `],
})
export class SlosPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly data = apiResource({ params: () => true, stream: () => this.api.slos() });
  private readonly all = computed(() => this.data.value()?.items ?? []);
  protected readonly technical = computed(() => this.all().filter(s => s.category === 'Technical'));
  protected readonly business = computed(() => this.all().filter(s => s.category === 'Business'));
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly objective = objective;
  protected readonly percent = percent;

  protected readonly state = sloState;
  protected readonly times = times;
  protected burn(s: SloSummary, name: string) { return burnText(s.burn.find(w => w.name === name)); }
  protected meter(remaining: number) { return Math.max(0, Math.min(100, remaining * 100)); }
}
