import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { ContextBar } from '../shared/ui';
import { MetricExplorer } from '../shared/telemetry';

/** Metrics of a service or a host (Console requirements §7): pick a scope, then a metric. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, ContextBar, MetricExplorer],
  template: `
    <jc-context-bar><h1>Metrics</h1></jc-context-bar>
    <div class="jc-filter-form">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Service</mat-label>
        <mat-select [ngModel]="service() ?? ''" (ngModelChange)="scope({ service: $event || null, resource: null })">
          <mat-option value="">Any</mat-option>
          @for (s of services.value() ?? []; track s.id) { <mat-option [value]="s.id">{{ s.key }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Host</mat-label>
        <mat-select [ngModel]="resource() ?? ''" (ngModelChange)="scope({ resource: $event || null, service: null })">
          <mat-option value="">Any</mat-option>
          @for (h of hosts.value() ?? []; track h.id) { <mat-option [value]="h.id">{{ h.displayName }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <jc-metric-explorer [scope]="query()" [initial]="metric() ?? null" />`,
})
export class MetricsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly service = input<string>();
  readonly resource = input<string>();
  readonly metric = input<string>();

  protected readonly services = rxResource({ stream: () => this.api.resources('service').pipe(map(p => p.items)) });
  protected readonly hosts = rxResource({ stream: () => this.api.resources('host').pipe(map(p => p.items)) });
  protected readonly query = computed(() => ({ serviceId: this.service(), resourceId: this.resource() }));

  scope(change: Record<string, string | null>) {
    void this.router.navigate([], { queryParams: change, queryParamsHandling: 'merge' });
  }
}
