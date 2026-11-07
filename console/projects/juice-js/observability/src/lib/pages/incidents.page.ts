import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { OBSERVABILITY_OPTIONS } from '../core/options';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/** Operational work grouped across alerts: what's being handled, by state, and starting a new one, usually from an alert. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar],
  template: `
    <jc-context-bar>
      <h1>Incidents</h1>
      <span class="jc-spacer"></span>
      <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>State</mat-label>
        <mat-select [ngModel]="state()" (ngModelChange)="state.set($event)" name="state">
          <mat-option value="">Any</mat-option><mat-option value="open">Open</mat-option><mat-option value="investigating">Investigating</mat-option><mat-option value="mitigated">Mitigated</mat-option><mat-option value="resolved">Resolved</mat-option>
        </mat-select>
      </mat-form-field>
    </jc-context-bar>
    @if (incidents.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (incidents.error()) { <p class="jc-error">{{ error() }}</p> }
    <section class="jc-card">
      <h2>New incident</h2>
      <form class="jc-form" data-testid="incident-form" (ngSubmit)="create()">
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Title</mat-label><input matInput name="titleField" data-testid="incident-title" [(ngModel)]="titleField" maxlength="200"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity</mat-label>
          <mat-select name="severity" [(ngModel)]="severity"><mat-option value="">As its worst alert</mat-option><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Note</mat-label><input matInput name="note" [(ngModel)]="note" maxlength="2000"></mat-form-field>
        @if (alert()) { <span class="jc-muted jc-small" data-testid="incident-with-alert">With the alert you came from.</span> }
        <div class="jc-form-actions">
          <button mat-flat-button type="submit" data-testid="incident-save" [disabled]="saving()">Open incident</button>
          @if (saveError()) { <span class="jc-error" data-testid="incident-error">{{ saveError() }}</span> }
        </div>
      </form>
    </section>
    <table class="jc-table" data-testid="incidents">
      <thead><tr><th>Incident</th><th>Severity</th><th>State</th><th>Opened</th><th>By</th><th>Last change</th></tr></thead>
      <tbody>
        @for (i of incidents.value()?.items ?? []; track i.id) {
          <tr>
            <td><a [routerLink]="['incidents', i.id] | jcLink" [queryParams]="ctx.keep({ alert: null, title: null })">{{ i.title }}</a></td>
            <td>{{ i.severity }}</td>
            <td>{{ i.state }}</td>
            <td class="jc-nowrap">{{ i.createdAt | date: 'MMM d, HH:mm' }}</td>
            <td>{{ i.createdBy }}</td>
            <td class="jc-nowrap">{{ i.updatedAt | date: 'MMM d, HH:mm' }}</td>
          </tr>
        } @empty { <tr><td colspan="6" class="jc-muted">No incidents.</td></tr> }
      </tbody>
    </table>`,
})
export class IncidentsPage implements OnInit {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly base = inject(OBSERVABILITY_OPTIONS).basePath;
  /** From an alert's "Open an incident". */
  readonly alert = input<string>();
  readonly title = input<string>();
  protected readonly state = signal('');
  protected readonly saving = signal(false);
  protected readonly saveError = signal('');
  protected titleField = '';
  protected severity = '';
  protected note = '';

  protected readonly incidents = apiResource({ params: () => this.state(), stream: ({ params }) => this.api.incidents({ state: params }) });
  protected readonly error = computed(() => problemText(this.incidents.error()));

  ngOnInit() { this.titleField = this.title() ?? ''; }

  protected create() {
    this.saving.set(true);
    this.saveError.set('');
    const alert = this.alert();
    this.api.createIncident({ title: this.titleField, severity: this.severity || undefined, note: this.note, alertIds: alert ? [alert] : [] }).subscribe({
      next: incident => {
        this.saving.set(false);
        const base = (this.base ?? '/').replace(/\/$/, '');
        void this.router.navigate([`${base}/incidents/${incident.id}`], { queryParams: this.ctx.keep({ alert: null, title: null }) });
      },
      error: e => { this.saving.set(false); this.saveError.set((e as { status?: number }).status === 403 ? 'Opening an incident needs the operator or admin role.' : problemText(e)); },
    });
  }
}
