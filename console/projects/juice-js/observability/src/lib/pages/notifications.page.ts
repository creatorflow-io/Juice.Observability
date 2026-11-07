import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { Observable, forkJoin, map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Channel, EscalationPolicy, EscalationStep } from '../core/models';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

const list = (text: string) => text.split(',').map(s => s.trim()).filter(Boolean);

interface ChannelForm {
  id: string | null; key: string; kind: string; url: string; secretRef: string; recipients: string; ruleKeys: string; environments: string; minimumSeverity: string; groupWindowSeconds: number; firing: boolean; resolved: boolean; enabled: boolean;
}

interface PolicyForm { editing: boolean; key: string; name: string; ruleKeys: string; environments: string; minimumSeverity: string; stopOnAcknowledge: boolean; enabled: boolean; steps: string; }

const NEW_CHANNEL: ChannelForm = { id: null, key: '', kind: 'Slack', url: '', secretRef: '', recipients: '', ruleKeys: '', environments: '', minimumSeverity: 'Info', groupWindowSeconds: 0, firing: true, resolved: true, enabled: true };
const NEW_POLICY: PolicyForm = { editing: false, key: '', name: '', ruleKeys: '', environments: '', minimumSeverity: 'Critical', stopOnAcknowledge: true, enabled: true, steps: '' };

/**
 * Where notifications go: channels with their failures and history, the private destinations they may reach, and who is told as an alert stays open. Changing any of these
 * needs the admin role. A channel's address and secret are never shown: only the name of where the secret is, to admins.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar],
  template: `
    <jc-context-bar><h1>Notifications</h1></jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error">{{ error() }}</p> }

    <section class="jc-card">
      <h2>Channels <button mat-button type="button" data-testid="channel-new" (click)="channel.set(newChannel())">New channel</button></h2>
      @if (channel(); as f) {
        <form class="jc-form" data-testid="channel-form" (ngSubmit)="saveChannel(f)">
          @if (!f.id) {
            <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Key</mat-label><input matInput name="key" [(ngModel)]="f.key" required></mat-form-field>
            <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Kind</mat-label>
              <mat-select name="kind" [(ngModel)]="f.kind"><mat-option value="Slack">Slack</mat-option><mat-option value="Teams">Teams</mat-option><mat-option value="Email">Email</mat-option><mat-option value="Webhook">Signed webhook</mat-option></mat-select>
            </mat-form-field>
          }
          @if (f.kind === 'Webhook') { <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>{{ f.id ? 'New URL (keeps the current one if empty)' : 'URL' }}</mat-label><input matInput name="url" [(ngModel)]="f.url"></mat-form-field> }
          @if (f.kind === 'Email') { <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Recipients, comma separated</mat-label><input matInput name="recipients" [(ngModel)]="f.recipients"></mat-form-field> }
          @else {
            <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>{{ f.kind === 'Webhook' ? 'Signing secret reference' : 'Reference to the channel URL' }}</mat-label><input matInput name="secretRef" [(ngModel)]="f.secretRef" placeholder="env:JUICE_WEBHOOK_OPS"></mat-form-field>
            <p class="jc-muted jc-small jc-form-actions">{{ f.kind === 'Webhook' ? 'The name of where the signing secret is, set on the host.' : 'A Slack or Teams URL is itself a credential, so it stays on the host: this is the name of where it is.' }} Not the secret itself.</p>
          }
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these rules</mat-label><input matInput name="ruleKeys" [(ngModel)]="f.ruleKeys"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these environments</mat-label><input matInput name="environments" [(ngModel)]="f.environments"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity at least</mat-label>
            <mat-select name="minimumSeverity" [(ngModel)]="f.minimumSeverity"><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Group for (s)</mat-label><input matInput type="number" min="0" max="3600" name="group" [(ngModel)]="f.groupWindowSeconds"></mat-form-field>
          <mat-checkbox name="firing" [(ngModel)]="f.firing">Firing</mat-checkbox>
          <mat-checkbox name="resolved" [(ngModel)]="f.resolved">Resolved</mat-checkbox>
          <mat-checkbox name="enabled" [(ngModel)]="f.enabled">Enabled</mat-checkbox>
          <div class="jc-form-actions">
            <button mat-flat-button type="submit" data-testid="channel-save" [disabled]="saving()">Save</button>
            <button mat-button type="button" (click)="channel.set(null)">Cancel</button>
            @if (channelError()) { <span class="jc-error" data-testid="channel-error">{{ channelError() }}</span> }
          </div>
        </form>
      }
      <table class="jc-table" data-testid="channels">
        <thead><tr><th>Channel</th><th>Kind</th><th>Destination</th><th>Takes</th><th>Failed (24 h)</th><th>Waiting</th><th></th></tr></thead>
        <tbody>
          @for (c of channels(); track c.id) {
            <tr [class.jc-muted]="!c.enabled">
              <td><strong>{{ c.key }}</strong><div class="jc-muted jc-small">@if (!c.enabled) { disabled · }@if (c.managedBy === 'config') { from the webhooks file }</div></td>
              <td>{{ c.kind }}</td>
              <td>{{ c.destination }}</td>
              <td class="jc-small">{{ c.minimumSeverity }} and above@if (c.environments.length) { · {{ c.environments.join(', ') }} }@if (c.ruleKeys.length) { · {{ c.ruleKeys.join(', ') }} }@if (c.groupWindowSeconds) { · grouped {{ c.groupWindowSeconds }} s }</td>
              <td [class.jc-bad]="c.recentFailures > 0">{{ c.recentFailures }}</td>
              <td>{{ c.waiting }}</td>
              <td class="jc-row-actions">
                <button mat-button type="button" [attr.data-testid]="'channel-history-' + c.key" (click)="history.set(history() === c.id ? null : c.id)">History</button>
                @if (c.managedBy !== 'config') { <button mat-button type="button" [attr.data-testid]="'channel-edit-' + c.key" (click)="editChannel(c)">Change</button> }
              </td>
            </tr>
          } @empty { <tr><td colspan="7" class="jc-muted">No channels: alerts fire, but nobody is told.</td></tr> }
        </tbody>
      </table>
      @if (history()) {
        <h3>Delivery history</h3>
        @if (deliveries.error()) { <p class="jc-error">{{ deliveriesError() }}</p> }
        <table class="jc-table" data-testid="channel-history">
          <thead><tr><th>Created</th><th>Event</th><th>Status</th><th>Attempts</th><th>Last result</th></tr></thead>
          <tbody>
            @for (d of deliveries.value()?.items ?? []; track d.id) {
              <tr>
                <td class="jc-nowrap">{{ d.createdAt | date: 'MMM d, HH:mm:ss' }}</td>
                <td>{{ d.event }}@if (d.escalationStep) { step {{ d.escalationStep }} }</td>
                <td [class.jc-bad]="d.status === 'Failed'">{{ d.status }}</td>
                <td>{{ d.attempts }}</td>
                <td class="jc-small">{{ d.lastError ?? (d.lastStatusCode ? 'HTTP ' + d.lastStatusCode : '') }}</td>
              </tr>
            } @empty { <tr><td colspan="5" class="jc-muted">Nothing sent yet.</td></tr> }
          </tbody>
        </table>
      }
    </section>

    <section class="jc-card">
      <h2>Escalation <button mat-button type="button" data-testid="policy-new" (click)="policy.set(newPolicy())">New policy</button></h2>
      @if (policy(); as f) {
        <form class="jc-form" data-testid="policy-form" (ngSubmit)="savePolicy(f)">
          @if (!f.editing) { <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Key</mat-label><input matInput name="key" [(ngModel)]="f.key" required></mat-form-field> }
          <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" required></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Steps, one a line</mat-label><textarea matInput name="steps" rows="3" [(ngModel)]="f.steps" placeholder="5: oncall-slack&#10;15: pager, lead-email"></textarea></mat-form-field>
          <p class="jc-muted jc-small jc-form-actions">Minutes after firing, then the channels to tell if the alert is still open.</p>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these rules</mat-label><input matInput name="ruleKeys" [(ngModel)]="f.ruleKeys"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic"><mat-label>Only these environments</mat-label><input matInput name="environments" [(ngModel)]="f.environments"></mat-form-field>
          <mat-form-field subscriptSizing="dynamic" class="jc-field-sm"><mat-label>Severity at least</mat-label>
            <mat-select name="minimumSeverity" [(ngModel)]="f.minimumSeverity"><mat-option value="Info">Info</mat-option><mat-option value="Warning">Warning</mat-option><mat-option value="Critical">Critical</mat-option></mat-select>
          </mat-form-field>
          <mat-checkbox name="stop" [(ngModel)]="f.stopOnAcknowledge">Stop when acknowledged</mat-checkbox>
          <mat-checkbox name="policyEnabled" [(ngModel)]="f.enabled">Enabled</mat-checkbox>
          <div class="jc-form-actions">
            <button mat-flat-button type="submit" data-testid="policy-save" [disabled]="saving()">Save</button>
            <button mat-button type="button" (click)="policy.set(null)">Cancel</button>
            @if (policyError()) { <span class="jc-error" data-testid="policy-error">{{ policyError() }}</span> }
          </div>
        </form>
      }
      <table class="jc-table" data-testid="policies">
        <thead><tr><th>Policy</th><th>Steps</th><th>Applies to</th><th>Stops when</th><th></th></tr></thead>
        <tbody>
          @for (p of policies(); track p.id) {
            <tr [class.jc-muted]="!p.enabled">
              <td><strong>{{ p.name }}</strong><div class="jc-muted jc-small">{{ p.key }}@if (!p.enabled) { · disabled }</div></td>
              <td class="jc-small">@for (s of p.steps; track s.afterMinutes) { <div>{{ s.afterMinutes }} min: {{ s.endpointKeys.join(', ') }}</div> }</td>
              <td class="jc-small">{{ p.minimumSeverity }} and above@if (p.environments.length) { · {{ p.environments.join(', ') }} }@if (p.ruleKeys.length) { · {{ p.ruleKeys.join(', ') }} }</td>
              <td class="jc-small">{{ p.stopOnAcknowledge ? 'Acknowledged or resolved' : 'Resolved' }}</td>
              <td class="jc-row-actions"><button mat-button type="button" (click)="editPolicy(p)">Change</button></td>
            </tr>
          } @empty { <tr><td colspan="5" class="jc-muted">No policies: a firing alert notifies once.</td></tr> }
        </tbody>
      </table>
    </section>

    <section class="jc-card">
      <h2>Private destinations</h2>
      <p class="jc-muted jc-small">A channel may reach a private address only if it's listed here. Loopback, link-local and cloud metadata addresses are never reachable.</p>
      <form class="jc-form" (ngSubmit)="addAllowed()">
        <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>CIDR or host name</mat-label><input matInput name="destination" data-testid="allow-destination" [(ngModel)]="destination"></mat-form-field>
        <mat-form-field subscriptSizing="dynamic" class="jc-field-md"><mat-label>Ports (443 if empty)</mat-label><input matInput name="ports" data-testid="allow-ports" [(ngModel)]="ports"></mat-form-field>
        <button mat-stroked-button type="submit" data-testid="allow-add" [disabled]="saving()">Allow</button>
        @if (allowError()) { <span class="jc-error" data-testid="allow-error">{{ allowError() }}</span> }
      </form>
      <table class="jc-table" data-testid="allowlist">
        <tbody>
          @for (a of allowed(); track a.destination) {
            <tr>
              <td><strong>{{ a.destination }}</strong> @if (a.managedBy === 'config') { <span class="jc-muted jc-small">from the webhooks file</span> }</td>
              <td>{{ a.ports.length ? a.ports.join(', ') : '443' }}</td>
              <td class="jc-row-actions">@if (a.managedBy !== 'config') { <button mat-button type="button" (click)="removeAllowed(a.destination)">Remove</button> }</td>
            </tr>
          } @empty { <tr><td class="jc-muted">None: channels reach public addresses only.</td></tr> }
        </tbody>
      </table>
    </section>`,
})
export class NotificationsPage {
  private readonly api = inject(ApiService);
  protected readonly channel = signal<ChannelForm | null>(null);
  protected readonly policy = signal<PolicyForm | null>(null);
  protected readonly history = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly channelError = signal('');
  protected readonly policyError = signal('');
  protected readonly allowError = signal('');
  protected destination = '';
  protected ports = '';

  protected readonly data = apiResource({
    stream: () => forkJoin({
      channels: this.api.channels().pipe(map(p => p.items)),
      policies: this.api.escalationPolicies().pipe(map(p => p.items)),
      allowlist: this.api.allowlist().pipe(map(p => p.items)),
    }),
  });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly channels = computed(() => this.data.value()?.channels ?? []);
  protected readonly policies = computed(() => this.data.value()?.policies ?? []);
  protected readonly allowed = computed(() => this.data.value()?.allowlist ?? []);
  protected readonly deliveries = apiResource({ params: () => this.history() ?? undefined, stream: ({ params }) => this.api.deliveries(params) });
  protected readonly deliveriesError = computed(() => problemText(this.deliveries.error()));

  protected newChannel(): ChannelForm { this.channelError.set(''); return { ...NEW_CHANNEL }; }
  protected newPolicy(): PolicyForm { this.policyError.set(''); return { ...NEW_POLICY }; }

  protected editChannel(c: Channel) {
    this.channelError.set('');
    this.channel.set({
      id: c.id, key: c.key, kind: c.kind, url: '', secretRef: c.secretRef ?? '', recipients: (c.recipients ?? []).join(', '), ruleKeys: c.ruleKeys.join(', '), environments: c.environments.join(', '),
      minimumSeverity: c.minimumSeverity, groupWindowSeconds: c.groupWindowSeconds, firing: c.events.includes('alert.firing'), resolved: c.events.includes('alert.resolved'), enabled: c.enabled,
    });
  }

  private run<T>(request: Observable<T>, error: (message: string) => void, done: () => void) {
    this.saving.set(true);
    error('');
    request.subscribe({
      next: () => { this.saving.set(false); done(); this.data.reload(); },
      error: e => { this.saving.set(false); error((e as { status?: number }).status === 403 ? 'This needs the admin role.' : problemText(e)); },
    });
  }

  protected saveChannel(f: ChannelForm) {
    const events = [f.firing ? 'alert.firing' : '', f.resolved ? 'alert.resolved' : ''].filter(Boolean);
    const body = {
      key: f.key, kind: f.kind, url: f.kind === 'Webhook' && f.url.trim() ? f.url.trim() : undefined, secretRef: f.kind === 'Email' ? undefined : f.secretRef.trim(), recipients: f.kind === 'Email' ? list(f.recipients) : undefined,
      events, ruleKeys: list(f.ruleKeys), environments: list(f.environments).map(e => e.toLowerCase()), minimumSeverity: f.minimumSeverity, groupWindowSeconds: f.groupWindowSeconds || 0, enabled: f.enabled,
    };
    this.run(f.id ? this.api.updateChannel(f.id, body) : this.api.createChannel(body), m => this.channelError.set(m), () => this.channel.set(null));
  }

  protected editPolicy(p: EscalationPolicy) {
    this.policyError.set('');
    this.policy.set({
      editing: true, key: p.key, name: p.name, ruleKeys: p.ruleKeys.join(', '), environments: p.environments.join(', '), minimumSeverity: p.minimumSeverity, stopOnAcknowledge: p.stopOnAcknowledge, enabled: p.enabled,
      steps: p.steps.map(s => `${s.afterMinutes}: ${s.endpointKeys.join(', ')}`).join('\n'),
    });
  }

  /** `5: oncall, pager` per line. A line that doesn't read goes to the server as no channels, which says what's wrong. */
  private parseSteps(text: string): EscalationStep[] {
    return text.split('\n').map(l => l.trim()).filter(Boolean).map(line => {
      const [minutes, ...rest] = line.split(':');
      return { afterMinutes: Number(minutes.trim()), endpointKeys: list(rest.join(':')) };
    });
  }

  protected savePolicy(f: PolicyForm) {
    const body = {
      key: f.key, name: f.name, enabled: f.enabled, ruleKeys: list(f.ruleKeys), environments: list(f.environments).map(e => e.toLowerCase()), minimumSeverity: f.minimumSeverity,
      stopOnAcknowledge: f.stopOnAcknowledge, steps: this.parseSteps(f.steps),
    };
    this.run(f.editing ? this.api.updateEscalationPolicy(f.key, body) : this.api.createEscalationPolicy(body), m => this.policyError.set(m), () => this.policy.set(null));
  }

  protected addAllowed() {
    this.run(this.api.putAllowlist(this.destination.trim(), list(this.ports).map(Number)), m => this.allowError.set(m), () => { this.destination = ''; this.ports = ''; });
  }

  protected removeAllowed(destination: string) {
    this.run(this.api.removeAllowlist(destination), m => this.allowError.set(m), () => undefined);
  }
}
