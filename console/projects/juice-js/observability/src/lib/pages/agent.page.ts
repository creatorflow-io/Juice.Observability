import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ApprovalCount } from '../core/approval-count';
import { ContextService } from '../core/context.service';
import { ActionParameter, AgentAction, AgentCommand, EnrollmentToken } from '../core/models';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { AgoPipe, ReasonPipe, copyText } from '../shared/format';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/** What the actions return (Juice.Observability.Agent's Diagnostics). */
interface HostSummary {
  hostname: string; operatingSystem: string; architecture: string; uptimeSeconds: number; processorCount: number; loadAverage: number[] | null;
  memoryTotalBytes: number | null; memoryAvailableBytes: number | null; agentVersion: string;
}
interface Filesystem { mountPoint: string; format: string; totalBytes: number; freeBytes: number; availableBytes: number; }
interface DiskUsage { filesystems: Filesystem[]; truncated: boolean; }
interface TcpReachability { host: string; port: number; reachable: boolean; address: string | null; elapsedMs: number; error: string | null; }

const WAITING = ['Pending', 'Delivered'];
const LABELS: Record<string, string> = {
  'host.summary': 'Host summary', 'disk.usage': 'Disk usage', 'tcp.reachability': 'TCP reachability', 'agent.update': 'Update the agent',
  'service.restart': 'Restart a service', 'files.cleanup': 'Clean up old files',
};
const PARAMETER_LABELS: Record<string, string> = {
  pathPrefix: 'Only under this path', host: 'Host', port: 'Port', timeoutMs: 'Wait (ms)', service: 'Service', folder: 'Folder',
  keepHours: 'Keep files from the last (hours)', dryRun: 'Dry run: only report what would go',
};

function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86_400);
  const hours = Math.floor((seconds % 86_400) / 3600);
  return days > 0 ? `${days} d ${hours} h` : hours > 0 ? `${hours} h ${Math.floor((seconds % 3600) / 60)} min` : `${Math.floor(seconds / 60)} min`;
}

/**
 * One Juice Agent: whether it's calling in, what it was asked and what it answered. Operators run the read-only diagnostics; admins update and
 * revoke it. The platform signs each command, and the agent's own allowlist on its host still decides: a refusal there shows as `Rejected`.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, ContextBar, AgoPipe, ReasonPipe],
  template: `
    <jc-context-bar>
      <a [routerLink]="['agents'] | jcLink" [queryParams]="ctx.keep()" class="jc-crumb">Agents</a><span class="jc-crumb-sep">›</span>
      <h1>{{ agent.value()?.hostname ?? '…' }}</h1>
    </jc-context-bar>
    @if (agent.isLoading() && !agent.value()) { <mat-progress-bar mode="indeterminate" /> }
    @if (agent.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (agent.value(); as a) {
      <div class="jc-summary" data-testid="agent-summary">
        <span class="jc-badge" data-testid="agent-state" [class.jc-health-healthy]="a.online" [class.jc-health-offline]="!a.online && a.status === 'Active'" [class.jc-health-none]="a.status !== 'Active'">
          {{ a.status !== 'Active' ? a.status : a.online ? 'Online' : 'Not calling in' }}</span>
        <span>{{ a.operatingSystem }}</span>
        <span>version <span class="jc-mono" data-testid="agent-version">{{ a.version }}</span></span>
        <span class="jc-muted">last seen {{ a.lastSeenAt | ago }}</span>
        @if (a.resourceId) { <a [routerLink]="['resources', a.resourceId] | jcLink" [queryParams]="ctx.keep()">Host resource</a> }
      </div>
      <p class="jc-muted jc-small">Enrolled {{ a.enrolledAt | date: 'MMM d, y, HH:mm' }} by {{ a.enrolledBy }}. Certificate valid until {{ a.certificateNotAfter | date: 'MMM d, HH:mm' }}; the agent renews it when a third is left.</p>
      @if (a.status !== 'Active') {
        <p class="jc-notice" data-testid="agent-revoked">Revoked {{ a.revokedAt | date: 'MMM d, y, HH:mm' }} by {{ a.revokedBy }}: {{ a.revokeReason }}. The platform refuses its certificate for good.</p>
        @if (successor(); as s) {
          <p data-testid="agent-successor">{{ a.hostname }} enrolled again {{ s.enrolledAt | date: 'MMM d, HH:mm' }} as <a [routerLink]="['agents', s.id] | jcLink" [queryParams]="ctx.keep()">a new agent</a>.</p>
        } @else {
          <section class="jc-card" data-testid="reenroll">
            <h2>Bring this host back</h2>
            <p class="jc-muted jc-small">The host comes back as a new agent, with a new key: this one stays revoked, with its history. It needs a new token from an admin, put on the host.</p>
            <button mat-flat-button type="button" data-testid="reenroll-create" [disabled]="creating()" (click)="reenroll(a.hostname)">Create a token for {{ a.hostname }}</button>
            @if (reenrollError()) { <span class="jc-error" data-testid="reenroll-error"> {{ reenrollError() }}</span> }
            @if (token(); as t) {
              <div class="jc-notice" data-testid="reenroll-token">
                <p>Replace the token in the agent's token file (<span class="jc-mono">Agent:EnrollmentTokenFile</span>) with this one. It works once, until {{ t.expiresAt | date: 'MMM d, HH:mm' }}, and <strong>won't be shown again</strong>.</p>
                <p class="jc-row"><span class="jc-mono" data-testid="reenroll-token-value">{{ t.token }}</span>
                  <button mat-button type="button" (click)="copy(t.token)">{{ copied() === 'copied' ? 'Copied' : copied() === 'selected' ? 'Selected: press Ctrl+C' : 'Copy' }}</button></p>
                <p>The agent notices it within seconds, sets its old identity aside and enrolls again: nothing else on the host changes. An agent from before 9 November doesn't: clear its data directory's identity files and restart it.</p>
              </div>
            }
          </section>
        }
      } @else {
        <section class="jc-card">
          <h2>Ask the agent</h2>
          <form class="jc-form" data-testid="command-form" (ngSubmit)="issue()">
            <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Action</mat-label>
              <mat-select name="action" data-testid="command-action" [ngModel]="action()" (ngModelChange)="choose($event)">
                @for (o of actionList(); track o.name) {
                  <mat-option [value]="o.name" [disabled]="o.permitted === false" [attr.data-testid]="'action-' + o.name">
                    {{ label(o.name) }}@if (o.custom) { <span class="jc-muted"> · custom</span> }@if (o.permitted === false) { <span class="jc-muted"> · not allowed on this host</span> }
                  </mat-option>
                }
              </mat-select>
            </mat-form-field>
            @if (action() === 'agent.update') {
              <mat-form-field subscriptSizing="dynamic" class="jc-field-md"><mat-label>Version</mat-label>
                <mat-select name="version" data-testid="command-version" [(ngModel)]="version">
                  @for (p of packages.value()?.items ?? []; track p.version) { <mat-option [value]="p.version">{{ p.version }}</mat-option> }
                </mat-select>
              </mat-form-field>
              @if (packages.value()?.items?.length === 0) { <span class="jc-muted jc-small">No signed packages are offered: see Agents:PackageDirectory.</span> }
            } @else {
              @for (p of selected()?.parameters ?? []; track p.name) {
                @if (p.type === 'boolean') {
                  <mat-checkbox [name]="p.name" [attr.data-testid]="'command-param-' + p.name" [ngModel]="values()[p.name] === true" (ngModelChange)="set(p.name, $event)">{{ title(p) }}</mat-checkbox>
                } @else if (p.allowed?.length) {
                  <mat-form-field subscriptSizing="dynamic" [class.jc-field-md]="p.type !== 'string'" [class.jc-field-wide]="p.type === 'string'"><mat-label>{{ title(p) }}</mat-label>
                    <mat-select [name]="p.name" [attr.data-testid]="'command-param-' + p.name" [ngModel]="values()[p.name] ?? ''" (ngModelChange)="set(p.name, $event)">
                      @if (!p.required) { <mat-option value="">Any</mat-option> }
                      @for (v of p.allowed; track v) { <mat-option [value]="v">{{ v }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                } @else {
                  <mat-form-field subscriptSizing="dynamic" [class.jc-field-sm]="p.type === 'integer'" [class.jc-field-wide]="p.type === 'string'"><mat-label>{{ title(p) }}</mat-label>
                    <input matInput [type]="p.type === 'integer' ? 'number' : 'text'" [name]="p.name" [attr.data-testid]="'command-param-' + p.name" [attr.min]="p.minimum" [attr.max]="p.maximum"
                           [attr.maxlength]="p.maxLength" [placeholder]="p.description ?? ''" [ngModel]="values()[p.name] ?? ''" (ngModelChange)="set(p.name, $event)">
                  </mat-form-field>
                }
              }
            }
            @if (needsApproval()) {
              <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Why (for whoever approves it)</mat-label>
                <input matInput name="requestReason" data-testid="request-reason" maxlength="500" [(ngModel)]="requestReason">
              </mat-form-field>
              @if (selected()?.changing && agent.value()?.resourceId) {
                <mat-checkbox name="watchHost" data-testid="request-watch-host" [(ngModel)]="watchHost">Fix this host's health: run only while it's unhealthy, and confirm recovery after</mat-checkbox>
              }
            }
            <div class="jc-form-actions">
              <button mat-flat-button type="submit" data-testid="command-issue" [disabled]="issuing() || selected()?.permitted === false">{{ needsApproval() ? 'Request' : action() === 'agent.update' ? 'Update' : 'Run' }}</button>
              @if (issueError()) { <span class="jc-error" data-testid="command-error">{{ issueError() }}</span> }
              @if (issued()) { <span class="jc-muted" data-testid="command-issued">{{ issued() }}</span> }
            </div>
            @if (selected(); as s) {
              <div class="jc-muted jc-small jc-form-actions" data-testid="command-hint">
                <div>{{ s.description }} <span data-testid="command-approval">{{ s.risk }} risk: {{ approvalText(s.approval) }}.</span></div>
                @if (s.limits.length) { <div data-testid="command-limits">On this host: {{ s.limits.join('; ') }}.</div> }
              </div>
            }
          </form>
          @if (actions.value(); as list) {
            @if (!list.reportedAt) {
              <p class="jc-notice" data-testid="actions-not-reported">This agent doesn't report what its host allows (it's older than this platform): the list shows the built-in actions, and one its host doesn't permit comes back <strong>Rejected</strong>.</p>
            } @else {
              <p class="jc-muted jc-small">As the agent reported {{ list.reportedAt | ago }}, when it started. Its host still checks every command.</p>
            }
          }
        </section>
      }

      @if (requestList().length) {
        <section class="jc-card" data-testid="requests">
          <h2>Requests</h2>
          <table class="jc-table">
            <thead><tr><th>Asked</th><th>Action</th><th>By, and why</th><th>State</th><th>Decided</th><th></th></tr></thead>
            <tbody>
              @for (r of requestList(); track r.id) {
                <tr [attr.data-testid]="'request-' + r.id">
                  <td class="jc-nowrap">{{ r.requestedAt | date: 'MMM d, HH:mm:ss' }}</td>
                  <td><strong>{{ r.action }}</strong><div class="jc-muted jc-small">{{ r.risk }} risk</div></td>
                  <td>{{ r.requestedBy }}<div class="jc-small">{{ r.reason }}</div></td>
                  <td class="jc-nowrap"><span class="jc-badge" [class.jc-health-degraded]="r.state === 'Pending'" [class.jc-health-healthy]="r.state === 'Issued'"
                      [class.jc-health-none]="r.state !== 'Pending' && r.state !== 'Issued'" data-testid="request-state">{{ r.state === 'Pending' ? 'Waiting for approval' : r.state }}</span>
                    @if (r.state === 'Pending') { <div class="jc-muted jc-small">until {{ r.expiresAt | date: 'HH:mm' }}</div> }
                    @if (r.verification && r.verification !== 'None') {
                      <div class="jc-small" data-testid="request-verification" [class.jc-error]="r.verification === 'NotVerified'" [title]="r.verificationNote ?? ''">{{ verificationText(r.verification) }}</div>
                    }</td>
                  <td class="jc-small">@if (r.decidedBy) { {{ r.decidedBy }}, {{ r.decidedAt | date: 'HH:mm' }} }@if (r.comment) { <div>{{ r.comment }}</div> }</td>
                  <td class="jc-row-actions">@if (r.state === 'Pending') { <button mat-button type="button" [attr.data-testid]="'request-cancel-' + r.id" (click)="cancelRequest(r.id)">Cancel</button> }</td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      }

      <section class="jc-card" data-testid="commands">
        <h2>Commands</h2>
        @if (commands.error()) { <p class="jc-error">{{ commandsError() }}</p> }
        <table class="jc-table">
          <thead><tr><th>Issued</th><th>Action</th><th>By</th><th>State</th><th>Answer</th></tr></thead>
          <tbody>
            @for (c of allCommands(); track c.id) {
              <tr [attr.data-testid]="'command-' + c.id">
                <td class="jc-nowrap">{{ c.issuedAt | date: 'MMM d, HH:mm:ss' }}</td>
                <td><strong>{{ c.action }}</strong>@if (describe(c); as d) { <div class="jc-small jc-mono">{{ d }}</div> }</td>
                <td>{{ c.issuedBy }}</td>
                <td class="jc-nowrap">
                  <span class="jc-badge" [class.jc-health-healthy]="c.state === 'Succeeded'" [class.jc-health-unhealthy]="c.state === 'Failed' || c.state === 'Rejected'"
                        [class.jc-health-degraded]="waiting(c)" [class.jc-health-none]="c.state === 'Expired' || c.state === 'Cancelled'" data-testid="command-state">{{ c.state }}</span>
                  @if (c.durationMs !== null) { <div class="jc-muted jc-small">{{ c.durationMs }} ms</div> }
                </td>
                <td class="jc-small" data-testid="command-answer">
                  @if (c.reason) { <div [class.jc-bad]="c.state !== 'Succeeded'">{{ c.reason | reason }}</div> }
                  @if (c.state === 'Succeeded' && c.result) {
                    @switch (c.action) {
                      @case ('host.summary') {
                        @if (summary(c); as h) {
                          <div>{{ h.hostname }} · {{ h.operatingSystem }} {{ h.architecture }} · up {{ uptime(h.uptimeSeconds) }}</div>
                          <div>{{ h.processorCount }} CPUs@if (h.loadAverage) { · load {{ h.loadAverage.join(' ') }} }@if (h.memoryTotalBytes) { · memory {{ bytes(h.memoryAvailableBytes) }} of {{ bytes(h.memoryTotalBytes) }} available }</div>
                        }
                      }
                      @case ('disk.usage') {
                        @if (disk(c); as d) {
                          <table class="jc-kv" data-testid="disk-result">
                            @for (f of fullestFirst(d, expanded().has(c.id)); track f.mountPoint) {
                              <tr [class.jc-bad]="low(f)">
                                <td class="jc-mono">{{ f.mountPoint }}</td><td>{{ f.format }}</td><td class="jc-nowrap">{{ bytes(f.availableBytes) }} free of {{ bytes(f.totalBytes) }}</td>
                              </tr>
                            } @empty { <tr><td class="jc-muted">No filesystems there.</td></tr> }
                          </table>
                          @if (d.filesystems.length > shown) {
                            <button mat-button type="button" [attr.data-testid]="'disk-toggle-' + c.id" (click)="toggle(c.id)">
                              {{ expanded().has(c.id) ? 'Show the fullest ' + shown : 'Show all ' + d.filesystems.length }}</button>
                          }
                          @if (d.truncated) { <div class="jc-muted">More filesystems than the agent reports at once.</div> }
                        }
                      }
                      @case ('tcp.reachability') {
                        @if (tcp(c); as t) {
                          @if (t.reachable) { <div>Reachable at {{ t.address }} in {{ t.elapsedMs }} ms</div> }
                          @else { <div class="jc-bad">Not reachable after {{ t.elapsedMs }} ms: {{ t.error }}</div> }
                        }
                      }
                      @default {
                        @if (flat(c.result); as rows) {
                          <table class="jc-kv" data-testid="custom-result">@for (r of rows; track r[0]) { <tr><td>{{ r[0] }}</td><td class="jc-mono">{{ r[1] }}</td></tr> }</table>
                        } @else { <pre class="jc-mono" style="white-space: pre-wrap; margin: 0" data-testid="custom-result">{{ json(c.result) }}</pre> }
                      }
                    }
                  }
                  @if (waiting(c)) { <div class="jc-muted">{{ c.state === 'Pending' ? 'Waiting for the agent to call in' : 'Handed to the agent' }}, until {{ c.expiresAt | date: 'HH:mm:ss' }}</div> }
                </td>
              </tr>
            } @empty { <tr><td colspan="5" class="jc-muted">Nothing asked yet.</td></tr> }
          </tbody>
        </table>
        @if (nextCursor()) { <button mat-button type="button" data-testid="commands-older" (click)="loadOlder()">Older</button> }
      </section>

      @if (a.status === 'Active') {
        <section class="jc-card">
          <h2>Revoke</h2>
          <p class="jc-muted jc-small">The agent stops at its next call: the platform refuses its certificate from then on, and commands still waiting are cancelled. It can't be undone; the host would enroll again with a new token.</p>
          <form class="jc-form" (ngSubmit)="revoke()">
            <mat-form-field subscriptSizing="dynamic" class="jc-field-wide"><mat-label>Why</mat-label><input matInput name="reason" data-testid="revoke-reason" [(ngModel)]="reason" maxlength="500" (ngModelChange)="confirming.set(false)"></mat-form-field>
            @if (confirming()) {
              <button mat-flat-button color="warn" type="submit" data-testid="revoke-confirm" [disabled]="revoking()">Revoke {{ a.hostname }}</button>
              <button mat-button type="button" (click)="confirming.set(false)">Cancel</button>
            } @else {
              <button mat-stroked-button type="submit" data-testid="revoke" [disabled]="!reason.trim()">Revoke</button>
            }
            @if (revokeError()) { <span class="jc-error" data-testid="revoke-error">{{ revokeError() }}</span> }
          </form>
        </section>
      }
    }`,
})
export class AgentPage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  private readonly approvalCount = inject(ApprovalCount);
  readonly id = input.required<string>();
  protected readonly action = signal('host.summary');
  /** The form's values for the chosen action, by parameter name. */
  protected readonly values = signal<Record<string, unknown>>({});
  protected readonly issuing = signal(false);
  protected readonly issueError = signal('');
  protected readonly confirming = signal(false);
  protected readonly revoking = signal(false);
  protected readonly revokeError = signal('');
  protected version = '';
  protected reason = '';
  protected requestReason = '';
  /** Aim the request at this agent's host: checked unhealthy before it runs, and watched for recovery after. */
  protected watchHost = false;
  /** What happened to the last request, when it didn't simply run. */
  protected readonly issued = signal('');
  protected readonly needsApproval = computed(() => (this.selected()?.approval ?? 'None') !== 'None');
  protected readonly requests = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.actionRequests({ agentId: params, limit: 20 }) });
  protected readonly requestList = computed(() => this.requests.value()?.items ?? []);
  protected readonly creating = signal(false);
  protected readonly reenrollError = signal('');
  protected readonly token = signal<EnrollmentToken | null>(null);
  protected readonly copied = signal<'' | 'copied' | 'selected'>('');

  /** For a revoked agent: the active one its host enrolled as since, if any. */
  private readonly everyone = apiResource({ params: () => this.agent.value()?.status === 'Revoked' || undefined, stream: () => this.api.agents(true) });
  protected readonly successor = computed(() => {
    const revoked = this.agent.value();
    if (!revoked?.revokedAt) return undefined;
    return (this.everyone.value()?.items ?? []).find(a => a.status === 'Active' && a.hostname === revoked.hostname && Date.parse(a.enrolledAt) > Date.parse(revoked.revokedAt!));
  });

  /** What the agent reported it can run, with its host's bounds: the menu and the form come from it, custom actions included. */
  protected readonly actions = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.agentActions(params) });
  protected readonly actionList = computed(() => this.actions.value()?.items ?? []);
  protected readonly selected = computed<AgentAction | undefined>(() => this.actionList().find(a => a.name === this.action()));

  protected readonly agent = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.agent(params) });
  protected readonly error = computed(() => problemText(this.agent.error()));
  protected readonly commands = apiResource({ params: () => this.id(), stream: ({ params }) => this.api.agentCommands(params) });
  protected readonly commandsError = computed(() => problemText(this.commands.error()));
  /** Asked for only once someone picks an update, since only admins can use it. */
  protected readonly packages = apiResource({ params: () => this.action() === 'agent.update' || undefined, stream: () => this.api.agentPackages() });
  private readonly older = signal<{ items: AgentCommand[]; cursor: string | null } | null>(null);
  protected readonly allCommands = computed(() => {
    const first = this.commands.value()?.items ?? [];
    const seen = new Set(first.map(c => c.id));
    return [...first, ...(this.older()?.items ?? []).filter(c => !seen.has(c.id))];
  });
  protected readonly nextCursor = computed(() => this.older() ? this.older()!.cursor : this.commands.value()?.nextCursor ?? null);

  constructor() {
    // Once the list is in, start on an action the host permits, with its defaults filled in.
    effect(() => {
      const list = this.actionList();
      if (list.length === 0) return;
      const current = list.find(a => a.name === untracked(this.action));
      if (!current || current.permitted === false) this.choose((list.find(a => a.permitted !== false) ?? list[0]).name);
      else if (Object.keys(untracked(this.values)).length === 0) this.choose(current.name);
    });
    // While a command waits, look again every 2 seconds, and at the agent too, since an update changes its version. While a request waits for
    // approval, every 5: someone else decides it.
    effect(onCleanup => {
      const running = this.allCommands().some(c => this.waiting(c));
      const deciding = this.requestList().some(r => r.state === 'Pending');
      if (!running && !deciding) return;
      const timer = setTimeout(() => { this.commands.reload(); this.agent.reload(); this.requests.reload(); }, running ? 2000 : 5000);
      onCleanup(() => clearTimeout(timer));
    });
  }

  protected waiting(c: AgentCommand): boolean { return WAITING.includes(c.state) && new Date(c.expiresAt).getTime() > Date.now(); }

  protected describe(c: AgentCommand): string {
    const p = c.parameters ?? {};
    switch (c.action) {
      case 'disk.usage': return p['pathPrefix'] ? `under ${p['pathPrefix']}` : 'all filesystems';
      case 'tcp.reachability': return `${p['host']}:${p['port']}, wait ${p['timeoutMs']} ms`;
      case 'agent.update': return `to ${p['version']}`;
      default: return Object.entries(p).map(([k, v]) => `${k}=${typeof v === 'string' ? v : JSON.stringify(v)}`).join(', ');
    }
  }

  /** The built-ins by name as people say them; a custom action by its own name. */
  protected label(name: string): string { return LABELS[name] ?? name; }

  protected title(p: ActionParameter): string {
    const words = PARAMETER_LABELS[p.name] ?? p.name.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase());
    return p.required || p.type === 'boolean' ? words : `${words} (optional)`;
  }

  protected choose(name: string) {
    this.action.set(name);
    this.issueError.set('');
    const defaults: Record<string, unknown> = {};
    for (const p of this.actionList().find(a => a.name === name)?.parameters ?? []) {
      if (p.default !== null) defaults[p.name] = p.type === 'integer' ? Number(p.default) : p.type === 'boolean' ? p.default === 'true' : p.default;
      else if (p.required && p.allowed?.length === 1) defaults[p.name] = p.allowed[0];
    }
    this.values.set(defaults);
  }

  protected set(name: string, value: unknown) { this.values.update(v => ({ ...v, [name]: value })); }

  /** A result of plain values reads as a table; anything nested as JSON. */
  protected flat(result: unknown): [string, string][] | null {
    if (!result || typeof result !== 'object' || Array.isArray(result)) return null;
    const entries = Object.entries(result as Record<string, unknown>);
    return entries.every(([, v]) => v === null || ['string', 'number', 'boolean'].includes(typeof v)) ? entries.map(([k, v]) => [k, v === null ? '—' : String(v)]) : null;
  }

  protected summary(c: AgentCommand) { return c.result as unknown as HostSummary; }
  protected disk(c: AgentCommand) { return c.result as unknown as DiskUsage; }
  protected tcp(c: AgentCommand) { return c.result as unknown as TcpReachability; }
  /** A whole host can have dozens of mounts: the fullest come first, and the rest on request. */
  protected readonly shown = 8;
  protected readonly expanded = signal(new Set<string>());
  protected toggle(id: string) { this.expanded.update(s => { const next = new Set(s); if (!next.delete(id)) next.add(id); return next; }); }
  protected fullestFirst(d: DiskUsage, all: boolean): Filesystem[] {
    const free = (f: Filesystem) => (f.totalBytes > 0 ? f.availableBytes / f.totalBytes : 1);
    const sizable = (f: Filesystem) => Number(f.totalBytes >= 1024 ** 3);
    const sorted = [...d.filesystems].sort((a, b) => Number(this.low(b)) - Number(this.low(a)) || sizable(b) - sizable(a) || free(a) - free(b));
    return all ? sorted : sorted.slice(0, this.shown);
  }
  /** Under 10 % free. Small read-only images (ISO, modules, squashfs) are always full by design, so only filesystems of 1 GB or more count. */
  protected low(f: Filesystem): boolean { return f.totalBytes >= 1024 ** 3 && f.availableBytes / f.totalBytes < 0.1; }
  protected bytes(n: number | null) { return formatBytes(n); }
  protected uptime(s: number) { return formatUptime(s); }
  protected json(v: unknown) { return JSON.stringify(v, null, 2); }

  /** The form's values, typed as the action declares them; an empty optional one left out. The API and the agent check them again. */
  private parameters(): Record<string, unknown> {
    if (this.action() === 'agent.update') return { version: this.version };
    const values = this.values();
    const out: Record<string, unknown> = {};
    for (const p of this.selected()?.parameters ?? []) {
      const v = values[p.name];
      if (p.type === 'boolean') { if (v === true || p.required) out[p.name] = v === true; continue; }
      if (v === undefined || v === null || v === '') continue;
      out[p.name] = p.type === 'integer' ? Number(v) : String(v).trim();
    }
    return out;
  }

  /** Whether it fixed what it was for: only health from telemetry after the action says so, never the agent. */
  protected verificationText(verification: string): string {
    switch (verification) {
      case 'Verifying': return 'Watching health for recovery';
      case 'Verified': return 'Recovery confirmed by health';
      case 'NotVerified': return 'Recovery not confirmed';
      default: return verification;
    }
  }

  protected approvalText(policy: string): string {
    switch (policy) {
      case 'None': return 'runs when asked';
      case 'Admin': return 'needs an admin: an admin\'s request runs at once, an operator\'s waits for an admin to approve it';
      default: return 'needs a second person: an admin other than whoever asks approves it';
    }
  }

  /** What needs no one else runs; what needs approval becomes a request, which runs once approved. */
  protected issue() {
    const action = this.action();
    this.issuing.set(true);
    this.issueError.set('');
    this.issued.set('');
    const failed = (e: unknown) => {
      this.issuing.set(false);
      this.issueError.set((e as { status?: number }).status === 403 ? 'Asking an agent needs the operator or admin role.' : problemText(e));
    };
    if (!this.needsApproval()) {
      this.api.issueCommand(this.id(), action, this.parameters()).subscribe({ next: () => { this.issuing.set(false); this.commands.reload(); }, error: failed });
      return;
    }
    const host = this.watchHost && this.selected()?.changing ? this.agent.value()?.resourceId : null;
    this.api.requestAction(this.id(), action, this.parameters(), this.requestReason.trim(), host ? { type: 'Host', id: host } : null).subscribe({
      next: r => {
        this.issuing.set(false);
        this.requestReason = '';
        this.issued.set(r.state !== 'Issued' ? 'Requested: it runs once approved, on the Approvals page.' : r.policy === 'None' ? 'Needs no approval as asked: running.' : 'Approved by your role: running.');
        this.requests.reload();
        this.commands.reload();
        this.approvalCount.refresh();
      },
      error: failed,
    });
  }

  protected cancelRequest(id: string) {
    this.api.cancelRequest(id).subscribe({ next: () => { this.requests.reload(); this.approvalCount.refresh(); }, error: e => this.issueError.set(problemText(e)) });
  }

  protected loadOlder() {
    const cursor = this.nextCursor();
    if (!cursor) return;
    this.api.agentCommands(this.id(), cursor).subscribe({
      next: page => this.older.update(o => ({ items: [...(o?.items ?? []), ...page.items], cursor: page.nextCursor })),
      error: e => this.issueError.set(problemText(e)),
    });
  }

  /** A token for this host to enroll again with. Admins only; shown once. */
  protected reenroll(hostname: string) {
    this.creating.set(true);
    this.reenrollError.set('');
    this.copied.set('');
    this.api.createEnrollmentToken(`re-enroll ${hostname}`, 60).subscribe({
      next: t => { this.creating.set(false); this.token.set(t); },
      error: e => { this.creating.set(false); this.reenrollError.set((e as { status?: number }).status === 403 ? 'Enrolling an agent needs the admin role.' : problemText(e)); },
    });
  }

  protected copy(text: string) {
    copyText(text, document.querySelector('[data-testid=reenroll-token-value]')).then(done => this.copied.set(done));
  }

  protected revoke() {
    if (!this.confirming()) { this.confirming.set(true); return; }
    this.revoking.set(true);
    this.revokeError.set('');
    this.api.revokeAgent(this.id(), this.reason.trim()).subscribe({
      next: () => { this.revoking.set(false); this.confirming.set(false); this.agent.reload(); this.commands.reload(); },
      error: e => { this.revoking.set(false); this.revokeError.set((e as { status?: number }).status === 403 ? 'Revoking an agent needs the admin role.' : problemText(e)); },
    });
  }
}
