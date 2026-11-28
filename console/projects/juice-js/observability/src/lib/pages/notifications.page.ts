import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ObservabilityLinks } from '../core/options';
import { EscalationPolicy } from '../core/models';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

const list = (text: string) => text.split(',').map(s => s.trim()).filter(Boolean);

interface PolicyForm { editing: boolean; key: string; name: string; ruleKeys: string; environments: string; minimumSeverity: string; enabled: boolean; }

const NEW_POLICY: PolicyForm = { editing: false, key: '', name: '', ruleKeys: '', environments: '', minimumSeverity: 'Critical', enabled: true };

/**
 * Escalation selection: which alerts take which escalation policy. What a policy does (its steps, who is told, when it stops) is on the
 * Notifications Escalation Policies page; a selection here needs a policy with the same key there. Changing a selection needs the admin role.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar, RouterLink],
  template: `
    <jc-context-bar><h1>Escalation selection</h1></jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card">
      <h2>Selections <button mat-button type="button" data-testid="policy-new" (click)="policy.set(newPolicy())">New selection</button></h2>
      <p class="jc-muted jc-small">Which alerts escalate. What an escalation does is set on the Notifications Escalation Policies page.</p>
      @if (policy(); as f) {
        <form class="jc-form" data-testid="policy-form" (ngSubmit)="savePolicy(f)">
          @if (!f.editing) { <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Key</mat-label><input matInput name="key" [(ngModel)]="f.key" required></mat-form-field> }
          <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" required></mat-form-field>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these rules</mat-label><input matInput name="ruleKeys" [(ngModel)]="f.ruleKeys"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these environments</mat-label><input matInput name="environments" [(ngModel)]="f.environments"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity at least</mat-label>
            <mat-select name="minimumSeverity" [(ngModel)]="f.minimumSeverity"><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
          </mat-form-field>
          <mat-checkbox name="policyEnabled" [(ngModel)]="f.enabled">Enabled</mat-checkbox>
          <div class="jc-form-actions">
            <button mat-flat-button type="submit" data-testid="policy-save" [disabled]="saving()">Save</button>
            <button mat-button type="button" (click)="policy.set(null)">Cancel</button>
            @if (policyError()) { <span class="jc-error" data-testid="policy-error">{{ policyError() }}</span> }
          </div>
        </form>
      }
      <table class="jc-table" data-testid="policies">
        <thead><tr><th>Selection</th><th>Applies to</th><th>Steps</th><th>Changed</th><th></th></tr></thead>
        <tbody>
          @for (p of policies(); track p.id) {
            <tr [class.jc-muted]="!p.enabled">
              <td><strong>{{ p.name }}</strong><div class="jc-muted jc-small">{{ p.key }}@if (!p.enabled) { · disabled }</div></td>
              <td class="jc-small">{{ p.minimumSeverity }} and above@if (p.environments.length) { · {{ p.environments.join(', ') }} }@if (p.ruleKeys.length) { · {{ p.ruleKeys.join(', ') }} }</td>
              <td class="jc-small">
                @if (steps(); as link) {
                  @if (link.href) { <a [href]="link.href" [attr.data-testid]="'steps-' + p.key">Steps</a> }
                  @else { <a [routerLink]="link.route" [attr.data-testid]="'steps-' + p.key">Steps</a> }
                }
                @if (!p.pipelinePolicyExists) { <span class="jc-bad" [attr.data-testid]="'no-pipeline-policy-' + p.key">No policy with this key in Notifications: alerts notify once</span> }
              </td>
              <td class="jc-small jc-nowrap">{{ p.updatedAt | date: 'MMM d, HH:mm' }}</td>
              <td class="jc-row-actions"><button mat-button type="button" (click)="editPolicy(p)">Change</button></td>
            </tr>
          } @empty { <tr><td colspan="5" class="jc-muted">No selections: a firing alert notifies once.</td></tr> }
        </tbody>
      </table>
    </section>`,
})
export class NotificationsPage {
  private readonly api = inject(ApiService);
  private readonly links = inject(ObservabilityLinks);
  protected readonly policy = signal<PolicyForm | null>(null);
  protected readonly saving = signal(false);
  protected readonly policyError = signal('');

  protected readonly data = apiResource({ stream: () => this.api.escalationPolicies().pipe(map(p => p.items)) });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly policies = computed(() => this.data.value() ?? []);

  /** The Notifications Escalation Policies page, when the host mounted the notification pages. */
  protected steps() {
    const l = this.links.admin('escalation-policies');
    return l ? { href: 'href' in l ? l.href : null, route: 'route' in l ? l.route : [] } : null;
  }

  protected newPolicy(): PolicyForm { this.policyError.set(''); return { ...NEW_POLICY }; }

  protected editPolicy(p: EscalationPolicy) {
    this.policyError.set('');
    this.policy.set({ editing: true, key: p.key, name: p.name, ruleKeys: p.ruleKeys.join(', '), environments: p.environments.join(', '), minimumSeverity: p.minimumSeverity, enabled: p.enabled });
  }

  protected savePolicy(f: PolicyForm) {
    const body = { key: f.key, name: f.name, enabled: f.enabled, ruleKeys: list(f.ruleKeys), environments: list(f.environments).map(e => e.toLowerCase()), minimumSeverity: f.minimumSeverity };
    this.saving.set(true);
    this.policyError.set('');
    (f.editing ? this.api.updateEscalationPolicy(f.key, body) : this.api.createEscalationPolicy(body)).subscribe({
      next: () => { this.saving.set(false); this.policy.set(null); this.data.reload(); },
      error: e => { this.saving.set(false); this.policyError.set((e as { status?: number }).status === 403 ? 'This needs the admin role.' : problemText(e)); },
    });
  }
}
