import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { apiResource } from '../shared/resource';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { ContextBar } from '../shared/ui';
import { TraceTable } from '../shared/telemetry';

/**
 * Trace search (Console requirements §5): narrow from a service and time window to one trace by operation, status,
 * duration, instance, resource, dependency, trace or span id, and span attributes such as `http.route=/orders` or
 * `messaging.destination.name=orders`. Every filter is a URL parameter, so a search is a shareable link.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ContextBar, TraceTable],
  template: `
    <jc-context-bar><h1>Traces</h1></jc-context-bar>
    <form class="jc-filter-form" (ngSubmit)="apply(f.value)" #f="ngForm">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Service</mat-label>
        <mat-select name="service" [ngModel]="service() ?? ''">
          <mat-option value="">Any</mat-option>
          @for (s of services.value() ?? []; track s.id) { <mat-option [value]="s.id">{{ s.key }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>Operation</mat-label><input matInput name="op" [ngModel]="op() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm">
        <mat-label>Status</mat-label>
        <mat-select name="status" [ngModel]="status() ?? ''">
          <mat-option value="">Any</mat-option><mat-option value="error">Error</mat-option><mat-option value="ok">No error</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-md"><mat-label>Min duration (ms)</mat-label><input matInput type="number" min="0" name="min" [ngModel]="min() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-md"><mat-label>Max duration (ms)</mat-label><input matInput type="number" min="0" name="max" [ngModel]="max() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>Trace ID</mat-label><input matInput name="trace" [ngModel]="trace() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>Span ID</mat-label><input matInput name="spanId" [ngModel]="spanId() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-wide">
        <mat-label>Attributes</mat-label>
        <input matInput name="attr" [ngModel]="attributes().join(', ')" placeholder="http.route=/orders, messaging.destination.name=orders">
      </mat-form-field>
      <button mat-flat-button type="submit">Search</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>
    @if (instance() || resource() || dependency()) {
      <p class="jc-muted">Also filtered by {{ scopeText() }}.
        <a href="" (click)="$event.preventDefault(); apply({ instance: null, resource: null, dependency: null })">Remove</a></p>
    }
    <jc-trace-table [query]="query()" [limit]="100" />`,
})
export class TracesPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly service = input<string>();
  readonly op = input<string>();
  readonly status = input<string>();
  readonly min = input<string>();
  readonly max = input<string>();
  readonly trace = input<string>();
  readonly spanId = input<string>();
  readonly attr = input<string | string[]>();
  readonly instance = input<string>();
  readonly resource = input<string>();
  readonly dependency = input<string>();

  protected readonly services = apiResource({ stream: () => this.api.resources('service').pipe(map(p => p.items)) });
  protected readonly scopeText = computed(() =>
    [this.instance() && 'an instance', this.resource() && 'a resource', this.dependency() && 'a dependency'].filter(x => !!x).join(', '));
  protected readonly attributes = computed(() => {
    const a = this.attr();
    return a === undefined ? [] : Array.isArray(a) ? a : [a];
  });
  protected readonly query = computed(() => ({
    serviceId: this.service(), operation: this.op(), status: this.status(), minDurationMs: this.min(), maxDurationMs: this.max(),
    traceId: this.trace(), spanId: this.spanId(), attribute: this.attributes(), instanceId: this.instance(),
    resourceId: this.resource(), dependencyId: this.dependency(),
  }));

  apply(value: Record<string, unknown>) {
    const queryParams: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(value)) queryParams[key] = v === '' || v === undefined ? null : v;
    if (typeof value['attr'] === 'string') {
      queryParams['attr'] = (value['attr'] as string).split(',').map(a => a.trim()).filter(Boolean);
    }
    void this.router.navigate([], { queryParams, queryParamsHandling: 'merge' });
  }

  clear() {
    void this.router.navigate([], { queryParams: this.ctx.keep() });
  }
}
