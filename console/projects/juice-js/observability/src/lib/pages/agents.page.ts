import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { Agent, EnrollmentToken } from '../core/models';
import { AgoPipe, copyText } from '../shared/format';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/**
 * Juice Agents (roadmap Phase 7): which hosts have one, whether it's calling in, its version and when its certificate runs out. An admin creates the
 * one-time token a new agent enrolls with; it's shown here once, since the platform keeps only its hash.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar>
      <h1>Agents</h1>
      <span class="jc-spacer"></span>
      <mat-checkbox [ngModel]="showAll()" (ngModelChange)="showAll.set($event)" data-testid="agents-all">Include revoked</mat-checkbox>
    </jc-context-bar>
    @if (agents.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (agents.error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card">
      <h2>Enroll an agent</h2>
      <form class="jc-form" data-testid="enroll-form" (ngSubmit)="createToken()">
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>For (host or purpose)</mat-label><input matInput name="purpose" data-testid="enroll-purpose" [(ngModel)]="purpose" maxlength="200"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Valid for (min)</mat-label><input matInput type="number" min="5" max="1440" name="minutes" [(ngModel)]="minutes"></mat-form-field>
        <button mat-flat-button type="submit" data-testid="enroll-create" [disabled]="creating()">Create token</button>
        @if (tokenError()) { <span class="jc-error" data-testid="enroll-error">{{ tokenError() }}</span> }
      </form>
      @if (token(); as t) {
        <div class="jc-notice" data-testid="enroll-token">
          <p>Give this to the agent as <span class="jc-mono">Agent:EnrollmentTokenFile</span>. It works once, until {{ t.expiresAt | date: 'MMM d, HH:mm' }}, and <strong>won't be shown again</strong>.</p>
          <p class="jc-row"><span class="jc-mono" data-testid="enroll-token-value">{{ t.token }}</span>
            <button mat-button type="button" data-testid="enroll-copy" (click)="copy(t.token)">{{ copied() === 'copied' ? 'Copied' : copied() === 'selected' ? 'Selected: press Ctrl+C' : 'Copy' }}</button>
            <button mat-button type="button" (click)="token.set(null)">Done</button></p>
        </div>
      }
      <p class="jc-muted jc-small">The token only lets an agent enroll. What it may do is set on its host, in <span class="jc-mono">Allowlist:Actions</span>: an empty list permits nothing.</p>
    </section>

    <table class="jc-table" data-testid="agents">
      <thead><tr><th>Host</th><th>State</th><th>Last seen</th><th>Version</th><th>Certificate until</th><th>Enrolled</th></tr></thead>
      <tbody>
        @for (a of agents.value()?.items ?? []; track a.id) {
          <tr [class.jc-muted]="a.status !== 'Active'">
            <td><a [routerLink]="['agents', a.id] | jcLink" [queryParams]="ctx.keep()" [attr.data-testid]="'agent-' + a.hostname"><strong>{{ a.hostname }}</strong></a>
              <div class="jc-muted jc-small">{{ a.operatingSystem }}</div></td>
            <td>
              <span class="jc-badge" [class.jc-health-healthy]="a.online" [class.jc-health-offline]="!a.online && a.status === 'Active'" [class.jc-health-none]="a.status !== 'Active'">{{ state(a) }}</span>
              @if (a.status !== 'Active' && a.revokeReason) { <div class="jc-small">{{ a.revokeReason }}</div> }
            </td>
            <td class="jc-nowrap">{{ a.lastSeenAt | ago }}</td>
            <td class="jc-mono jc-small">{{ a.version }}</td>
            <td class="jc-nowrap" [class.jc-bad]="a.status === 'Active' && expiring(a)">{{ a.certificateNotAfter | date: 'MMM d, HH:mm' }}</td>
            <td class="jc-small">{{ a.enrolledAt | date: 'MMM d' }} by {{ a.enrolledBy }}</td>
          </tr>
        } @empty { <tr><td colspan="6" class="jc-muted">No agents{{ showAll() ? '' : ' active' }}. Create a token above and start one on a host.</td></tr> }
      </tbody>
    </table>`,
})
export class AgentsPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly showAll = signal(false);
  protected readonly creating = signal(false);
  protected readonly tokenError = signal('');
  protected readonly token = signal<EnrollmentToken | null>(null);
  protected readonly copied = signal<'' | 'copied' | 'selected'>('');
  protected purpose = '';
  protected minutes = 60;

  protected readonly agents = apiResource({ params: () => this.showAll(), stream: ({ params }) => this.api.agents(params) });
  protected readonly error = computed(() => problemText(this.agents.error()));

  protected state(a: Agent): string { return a.status !== 'Active' ? a.status : a.online ? 'Online' : 'Not calling in'; }

  /** An active agent renews when a third of its certificate is left; one with less than a day left has missed its renewals. */
  protected expiring(a: Agent): boolean { return new Date(a.certificateNotAfter).getTime() - Date.now() < 86_400_000; }

  protected createToken() {
    this.creating.set(true);
    this.tokenError.set('');
    this.copied.set('');
    this.api.createEnrollmentToken(this.purpose.trim(), this.minutes || 60).subscribe({
      next: t => { this.creating.set(false); this.token.set(t); this.purpose = ''; },
      error: e => { this.creating.set(false); this.tokenError.set((e as { status?: number }).status === 403 ? 'Enrolling an agent needs the admin role.' : problemText(e)); },
    });
  }

  protected copy(text: string) {
    copyText(text, document.querySelector('[data-testid=enroll-token-value]')).then(done => this.copied.set(done));
  }
}
