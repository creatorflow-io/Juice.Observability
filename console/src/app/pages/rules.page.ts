import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { forkJoin, map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';

/**
 * Rules and their current state (Console requirements §14). Rules are typed templates in deployment configuration
 * (D5), so this page reads them; it never edits them and never takes a query.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Rules</h1><span class="jc-muted">From deployment configuration (rules.json); read-only.</span></jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }
    <table class="jc-table">
      <thead><tr><th>Rule</th><th>Template</th><th>Severity</th><th>Threshold</th><th>Recovery</th><th>Wait</th><th>Open alerts</th><th>Last evaluation</th></tr></thead>
      <tbody>
        @for (r of rows(); track r.key) {
          <tr [class.jc-muted]="!r.enabled">
            <td><strong>{{ r.name }}</strong><div class="jc-muted jc-small">{{ r.key }}@if (!r.enabled) { · disabled }</div></td>
            <td>{{ r.template }}</td>
            <td>{{ r.severity }}</td>
            <td>{{ r.definition['threshold'] ?? '—' }}</td>
            <td>{{ r.definition['recoveryThreshold'] ?? '—' }}</td>
            <td>{{ r.definition['forDuration'] ?? '—' }}</td>
            <td>
              @for (a of r.open; track a.id) {
                <div><a [routerLink]="['/alerts', a.id]" [queryParams]="ctx.keep()">{{ a.state }}</a> {{ a.subjectKey }} = {{ a.lastValue ?? 'no data' }}</div>
              } @empty { <span class="jc-muted">none</span> }
            </td>
            <td>{{ r.lastEvaluatedAt | ago }}</td>
          </tr>
        }
      </tbody>
    </table>`,
})
export class RulesPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly data = rxResource({
    stream: () => forkJoin({
      rules: this.api.rules().pipe(map(p => p.items)),
      firing: this.api.alerts({ state: 'firing' }).pipe(map(p => p.items)),
      pending: this.api.alerts({ state: 'pending' }).pipe(map(p => p.items)),
    }),
  });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly rows = computed(() => {
    const d = this.data.value();
    if (!d) return [];
    const open = [...d.firing, ...d.pending];
    return d.rules.map(r => {
      const mine = open.filter(a => a.rule === r.key);
      const last = mine.map(a => a.lastEvaluatedAt).sort().at(-1) ?? null;
      return { ...r, open: mine, lastEvaluatedAt: last };
    });
  });
}
