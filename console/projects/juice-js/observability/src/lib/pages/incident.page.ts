import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { IncidentEvent } from '../core/models';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';
import { SuggestionsCard } from '../shared/suggestions';

const MOVES: Record<string, string[]> = {
  Open: ['Investigating', 'Mitigated', 'Resolved'],
  Investigating: ['Mitigated', 'Resolved'],
  Mitigated: ['Investigating', 'Resolved'],
  Resolved: ['Investigating'],
};

/** One incident: its state and the moves from it, its alerts, and who did what. Resolving it changes no alert, and says so if some are still open. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, ContextBar, SuggestionsCard],
  template: `
    <jc-context-bar>
      <a [routerLink]="['incidents'] | jcLink" [queryParams]="ctx.keep()" class="jc-crumb">Incidents</a><span class="jc-crumb-sep">›</span>
      <h1>{{ detail.value()?.incident?.title ?? '…' }}</h1>
    </jc-context-bar>
    @if (detail.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (detail.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (detail.value(); as d) {
      <div class="jc-summary">
        <span class="jc-badge" [class.jc-health-unhealthy]="d.incident.severity === 'Critical'" [class.jc-health-degraded]="d.incident.severity === 'Warning'">{{ d.incident.severity }}</span>
        <strong data-testid="incident-state">{{ d.incident.state }}</strong>
        <span class="jc-muted">Opened by {{ d.incident.createdBy }} {{ d.incident.createdAt | date: 'MMM d, HH:mm' }}</span>
      </div>
      @if (d.incident.state !== 'Resolved' && d.openAlerts > 0) {
        <p class="jc-notice" data-testid="incident-open-alerts">{{ d.openAlerts }} of its alerts {{ d.openAlerts === 1 ? 'is' : 'are' }} still open. Resolving the incident won't change them.</p>
      }
      <section class="jc-card">
        <h2>Move or note</h2>
        <form class="jc-form" (ngSubmit)="note()">
          <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Comment or note</mat-label><input matInput name="text" data-testid="incident-text" [(ngModel)]="text" maxlength="2000"></mat-form-field>
          <button mat-stroked-button type="submit" data-testid="incident-note" [disabled]="sending()">Add note</button>
          @for (m of moves(d.incident.state); track m) {
            <button mat-flat-button type="button" [attr.data-testid]="'incident-move-' + m" [disabled]="sending()" (click)="move(m)">{{ d.incident.state === 'Resolved' ? 'Reopen' : m }}</button>
          }
          @if (actionError()) { <span class="jc-error" data-testid="incident-action-error">{{ actionError() }}</span> }
        </form>
      </section>
      <section class="jc-card" data-testid="incident-alerts">
        <h2>Alerts</h2>
        <table class="jc-table"><tbody>
          @for (a of d.alerts; track a.id) {
            <tr>
              <td><a [routerLink]="['alerts', a.id] | jcLink" [queryParams]="ctx.keep()">{{ a.rule }}</a></td>
              <td>{{ a.subjectKey }}</td><td>{{ a.severity }}</td><td>{{ a.state }}</td>
              <td class="jc-row-actions"><button mat-button type="button" (click)="removeAlert(a.id)">Remove</button></td>
            </tr>
          } @empty { <tr><td class="jc-muted">None. Open an alert and use “Open an incident”, or add one through the API.</td></tr> }
        </tbody></table>
      </section>
      @if (d.incident.state !== 'Resolved') {
        <section class="jc-card" data-testid="incident-suggestions">
          <h2>Suggested alerts</h2>
          <jc-suggestions [suggestions]="suggestions.value()" [withAlert]="true" [incident]="d.incident.id" (decided)="decided()" />
          @if (suggestions.error()) { <p class="jc-error">{{ suggestionsError() }}</p> }
        </section>
      }
      <section class="jc-card" data-testid="incident-events">
        <h2>What happened</h2>
        <table class="jc-table"><tbody>
          @for (e of d.events; track $index) {
            <tr>
              <td class="jc-nowrap">{{ e.at | date: 'MMM d, HH:mm:ss' }}</td>
              <td>{{ e.actor }}</td>
              <td><strong>{{ describe(e) }}</strong>@if (e.text) { <div>{{ e.text }}</div> }</td>
            </tr>
          }
        </tbody></table>
      </section>
    }`,
})
export class IncidentPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  readonly id = input.required<string>();
  protected text = '';
  protected readonly sending = signal(false);
  protected readonly actionError = signal('');

  protected readonly detail = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.incident(params) });
  protected readonly error = computed(() => problemText(this.detail.error()));
  /** A resolved incident is suggested nothing, so its page doesn't ask. */
  protected readonly suggestions = apiResource({
    params: () => (this.detail.value() && this.detail.value()!.incident.state !== 'Resolved' ? this.id() : undefined),
    stream: ({ params }) => this.api.incidentSuggestions(params),
  });
  protected readonly suggestionsError = computed(() => problemText(this.suggestions.error()));

  protected decided() { this.detail.reload(); this.suggestions.reload(); }

  protected moves(state: string): string[] { return MOVES[state] ?? []; }

  protected describe(e: IncidentEvent): string {
    switch (e.type) {
      case 'created': return 'Opened';
      case 'state': return `${e.from} → ${e.to}`;
      case 'note': return 'Note';
      case 'alert_added': return 'Alert added';
      case 'alert_removed': return 'Alert removed';
      default: return e.type;
    }
  }

  private run<T>(request: Observable<T>) {
    this.sending.set(true);
    this.actionError.set('');
    request.subscribe({
      next: () => { this.sending.set(false); this.text = ''; this.detail.reload(); },
      error: e => { this.sending.set(false); this.actionError.set((e as { status?: number }).status === 403 ? 'This needs the operator or admin role.' : problemText(e)); },
    });
  }

  protected move(to: string) { this.run(this.api.moveIncident(this.id(), to, this.text)); }
  protected note() { this.run(this.api.addIncidentNote(this.id(), this.text)); }
  protected removeAlert(alertId: string) { this.run(this.api.removeIncidentAlert(this.id(), alertId)); }
}
