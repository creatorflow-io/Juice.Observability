import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
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
import { ActionRequest } from '../core/models';
import { AgoPipe } from '../shared/format';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/**
 * Requests waiting for approval, across every agent (roadmap Phase 8), and the ones decided lately. Admins approve or reject; under a
 * second-person policy, not their own. Approving signs the command and the agent runs it at its next poll.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Approvals</h1></jc-context-bar>
    @if (pending.isLoading() && !pending.value()) { <mat-progress-bar mode="indeterminate" /> }
    @if (pending.error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card" data-testid="approvals-pending">
      <h2>Waiting</h2>
      @for (r of pending.value()?.items ?? []; track r.id) {
        <div class="jc-list-row" [attr.data-testid]="'approval-' + r.id">
          <div>
            <strong>{{ r.action }}</strong> on <a [routerLink]="['agents', r.agentId] | jcLink" [queryParams]="ctx.keep()">{{ r.hostname }}</a>
            <span class="jc-badge" [class.jc-health-unhealthy]="r.risk === 'High'" [class.jc-health-degraded]="r.risk !== 'High'">{{ r.risk }} risk</span>
            @if (describe(r); as d) { <div class="jc-mono jc-small">{{ d }}</div> }
            <div class="jc-small">{{ r.requestedBy }}, {{ r.requestedAt | ago }}: {{ r.reason }}</div>
            <div class="jc-muted jc-small">{{ r.policy === 'SecondPerson' ? 'Needs an admin other than ' + r.requestedBy : 'Needs an admin' }}. Expires {{ r.expiresAt | date: 'HH:mm' }}.</div>
          </div>
          <form class="jc-form" (ngSubmit)="approve(r)">
            <mat-form-field subscriptSizing="dynamic" class="jc-field-md"><mat-label>Comment</mat-label>
              <input matInput [name]="'comment-' + r.id" [attr.data-testid]="'approval-comment-' + r.id" maxlength="500" [ngModel]="comments()[r.id] ?? ''" (ngModelChange)="comment(r.id, $event)">
            </mat-form-field>
            <button mat-flat-button type="submit" [attr.data-testid]="'approve-' + r.id" [disabled]="busy() === r.id">Approve</button>
            <button mat-stroked-button type="button" [attr.data-testid]="'reject-' + r.id" [disabled]="busy() === r.id" (click)="reject(r)">Reject</button>
            @if (errors()[r.id]; as e) { <span class="jc-error" [attr.data-testid]="'approval-error-' + r.id">{{ e }}</span> }
          </form>
        </div>
      } @empty { <p class="jc-muted" data-testid="approvals-none">Nothing is waiting for approval.</p> }
    </section>

    <section class="jc-card">
      <h2>Decided lately</h2>
      <table class="jc-table" data-testid="approvals-decided">
        <thead><tr><th>Asked</th><th>Action</th><th>By</th><th>Decision</th></tr></thead>
        <tbody>
          @for (r of decided(); track r.id) {
            <tr>
              <td class="jc-nowrap">{{ r.requestedAt | date: 'MMM d, HH:mm' }}</td>
              <td><strong>{{ r.action }}</strong> on <a [routerLink]="['agents', r.agentId] | jcLink" [queryParams]="ctx.keep()">{{ r.hostname }}</a></td>
              <td>{{ r.requestedBy }}</td>
              <td>{{ r.state }}@if (r.decidedBy) { by {{ r.decidedBy }} }@if (r.command) { · command {{ r.command.state }} }@if (r.comment) { <div class="jc-small">{{ r.comment }}</div> }</td>
            </tr>
          } @empty { <tr><td colspan="4" class="jc-muted">None yet.</td></tr> }
        </tbody>
      </table>
    </section>`,
})
export class ApprovalsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly busy = signal<string | null>(null);
  protected readonly comments = signal<Record<string, string>>({});
  protected readonly errors = signal<Record<string, string>>({});

  protected readonly pending = apiResource({ stream: () => this.api.actionRequests({ state: 'Pending' }) });
  private readonly recent = apiResource({ stream: () => this.api.actionRequests({ limit: 30 }) });
  protected readonly decided = computed(() => (this.recent.value()?.items ?? []).filter(r => r.state !== 'Pending'));
  protected readonly error = computed(() => problemText(this.pending.error()));

  constructor() {
    // Others ask and decide too: look again every 10 seconds.
    effect(onCleanup => {
      this.pending.value();
      const timer = setTimeout(() => { this.pending.reload(); this.recent.reload(); }, 10_000);
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected describe(r: ActionRequest): string {
    return Object.entries(r.parameters ?? {}).map(([k, v]) => `${k}=${typeof v === 'string' ? v : JSON.stringify(v)}`).join(', ');
  }

  protected comment(id: string, text: string) { this.comments.update(c => ({ ...c, [id]: text })); }

  private decide(r: ActionRequest, call: Observable<ActionRequest>) {
    this.busy.set(r.id);
    this.errors.update(e => ({ ...e, [r.id]: '' }));
    call.subscribe({
      next: () => { this.busy.set(null); this.pending.reload(); this.recent.reload(); },
      error: e => {
        this.busy.set(null);
        const status = (e as { status?: number }).status;
        const text = status === 403 && r.policy === 'SecondPerson' ? `An admin other than ${r.requestedBy} has to approve this.`
          : status === 403 ? 'Deciding needs the admin role.' : problemText(e);
        this.errors.update(x => ({ ...x, [r.id]: text }));
      },
    });
  }

  protected approve(r: ActionRequest) { this.decide(r, this.api.approveRequest(r.id, (this.comments()[r.id] ?? '').trim())); }

  protected reject(r: ActionRequest) {
    const comment = (this.comments()[r.id] ?? '').trim();
    if (!comment) { this.errors.update(e => ({ ...e, [r.id]: 'Say why in the comment.' })); return; }
    this.decide(r, this.api.rejectRequest(r.id, comment));
  }
}
