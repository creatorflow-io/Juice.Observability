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
import { LogTable } from '../shared/telemetry';

/** Log search (Console requirements §7): by service, severity, text, trace or span; each record links to its trace. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ContextBar, LogTable],
  template: `
    <jc-context-bar><h1>Logs</h1></jc-context-bar>
    <form class="jc-filter-form" (ngSubmit)="apply(f.value)" #f="ngForm">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Service</mat-label>
        <mat-select name="service" [ngModel]="service() ?? ''">
          <mat-option value="">Any</mat-option>
          @for (s of services.value() ?? []; track s.id) { <mat-option [value]="s.id">{{ s.key }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm">
        <mat-label>Severity at least</mat-label>
        <mat-select name="severity" [ngModel]="severity() ?? ''">
          <mat-option value="">Any</mat-option><mat-option value="5">Debug</mat-option><mat-option value="9">Info</mat-option>
          <mat-option value="13">Warn</mat-option><mat-option value="17">Error</mat-option><mat-option value="21">Fatal</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Message contains</mat-label><input matInput name="q" [ngModel]="q() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>Trace ID</mat-label><input matInput name="trace" [ngModel]="trace() ?? ''"></mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>Span ID</mat-label><input matInput name="spanId" [ngModel]="spanId() ?? ''"></mat-form-field>
      <button mat-flat-button type="submit">Search</button>
      <button mat-button type="button" (click)="clear()">Clear</button>
    </form>
    <jc-log-table [query]="query()" />`,
})
export class LogsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly service = input<string>();
  readonly severity = input<string>();
  readonly q = input<string>();
  readonly trace = input<string>();
  readonly spanId = input<string>();
  readonly instance = input<string>();
  readonly resource = input<string>();

  protected readonly services = apiResource({ stream: () => this.api.resources('service').pipe(map(p => p.items)) });
  protected readonly query = computed(() => ({
    serviceId: this.service(), minSeverity: this.severity(), q: this.q(), traceId: this.trace(), spanId: this.spanId(),
    instanceId: this.instance(), resourceId: this.resource(),
  }));

  apply(value: Record<string, unknown>) {
    const queryParams: Record<string, unknown> = {};
    for (const [key, v] of Object.entries(value)) queryParams[key] = v === '' || v === undefined ? null : v;
    void this.router.navigate([], { queryParams, queryParamsHandling: 'merge' });
  }

  clear() { void this.router.navigate([], { queryParams: this.ctx.keep() }); }
}
