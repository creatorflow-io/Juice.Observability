import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, distinctUntilChanged, forkJoin, map, skip } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { IntegrationInstanceDto, IntegrationsApi, ValidationCheckDto } from '../core/integrations-api';
import { LinkPipe, OBSERVABILITY_CAN_ADMIN, ObservabilityLinks } from '../core/options';
import { AgoPipe } from '../shared/format';
import { IntegrationForm } from '../shared/integration-form';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';
import { integrationHealthText, integrationStateClass } from './integrations.page';

const VALIDATABLE = ['Configured', 'Validated', 'Disabled', 'Failed'];
const DELETABLE = ['Configured', 'Validated', 'Disabled'];

/**
 * One integration instance: state, health and the last validation for everyone; for an admin also its configuration and secret references
 * (never a secret value) and the controls to edit, validate, enable, disable and delete it. Every change carries the instance's etag, so a
 * change made by someone else since it was read is refused and the page says to reload. Plugin text (failure detail, check detail) is untrusted
 * and only ever interpolated.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, DatePipe, MatButtonModule, MatProgressBarModule, ContextBar, AgoPipe, IntegrationForm],
  template: `
    <jc-context-bar>
      <h1>{{ item()?.name ?? 'Integration' }}</h1>
      <span class="jc-spacer"></span>
      <a mat-button [routerLink]="['integrations'] | jcLink">All integrations</a>
    </jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error" data-testid="integration-error">{{ error() }}</p> }

    @if (item(); as i) {
      <section class="jc-card">
        <p>
          <span class="jc-badge" data-testid="integration-state" [class]="'jc-badge ' + stateClass(i.state)">{{ i.state }}</span>
          @if (i.stateReason) { <span data-testid="integration-reason"> {{ i.stateReason }}</span> }
        </p>
        <p class="jc-small">
          Plugin
          @if (i.pluginName) { <strong>{{ i.pluginName }}</strong> <span class="jc-mono jc-muted">{{ i.pluginKey }}</span> }
          @else { <span class="jc-mono">{{ i.pluginKey }}</span> <span class="jc-badge jc-health-unhealthy" data-testid="plugin-missing">not installed</span>
            <span class="jc-muted"> The host no longer includes this plugin, so the instance cannot run or be changed until it does.</span> }
          · every {{ i.intervalSeconds }} s · config version {{ i.configVersion }}
        </p>
        <p class="jc-small jc-muted">Created by {{ i.createdBy }} on {{ i.createdAt | date: 'MMM d, HH:mm' }} · changed by {{ i.updatedBy }} {{ i.updatedAt | ago }}</p>
      </section>

      @if (admin()) {
        <div class="jc-row" data-testid="integration-actions">
          <button mat-button data-testid="integration-edit" [disabled]="busy() || !i.pluginName" (click)="editing.set(!editing())">Edit</button>
          @if (canValidate(i)) { <button mat-button data-testid="integration-validate" [disabled]="busy()" (click)="validate(i)">Validate</button> }
          @if (canEnable(i)) { <button mat-flat-button data-testid="integration-enable" [disabled]="busy() || !i.pluginName" (click)="run(enable(i))">Enable</button> }
          @if (i.state !== 'Disabled') { <button mat-button data-testid="integration-disable" [disabled]="busy()" (click)="run(disable(i))">Disable</button> }
          @if (canDelete(i)) {
            @if (!confirmingDelete()) { <button mat-button data-testid="integration-delete" [disabled]="busy()" (click)="confirmingDelete.set(true)">Delete</button> }
            @else {
              <span class="jc-small">Delete {{ i.name }} and its checkpoints?</span>
              <button mat-flat-button data-testid="integration-delete-confirm" [disabled]="busy()" (click)="remove(i)">Delete</button>
              <button mat-button (click)="confirmingDelete.set(false)">Keep</button>
            }
          }
        </div>
      }
      @if (stale()) {
        <div class="jc-notice" role="alert" data-testid="stale-reload">
          <p>This instance was changed by someone else since you opened it. Reload to see the current version.</p>
          <button mat-flat-button data-testid="stale-reload-button" (click)="reload()">Reload</button>
        </div>
      }
      @if (actionError()) { <p class="jc-error" role="alert" data-testid="integration-action-error">{{ actionError() }}</p> }

      @if (admin() && editing() && plugin(); as p) {
        <jc-integration-form [plugin]="p" [instance]="i" (saved)="saved()" (cancelled)="editing.set(false)" (reload)="reload()" />
      }

      <section class="jc-card" data-testid="integration-health">
        <h2>Health</h2>
        <table class="jc-table">
          <tbody>
            <tr><th>Runs</th><td>{{ i.health.runtime }}</td></tr>
            <tr><th>Connectivity</th><td>{{ i.health.connectivity }}</td></tr>
            <tr><th>Authentication</th><td>{{ i.health.authentication }}</td></tr>
            <tr><th>Collection</th><td>{{ i.health.collection }}</td></tr>
            <tr><th>Data</th><td>{{ i.health.freshness === 'ok' ? 'fresh' : i.health.freshness }}@if (i.health.freshnessSeconds !== null) { ({{ i.health.freshnessSeconds }} s old) }</td></tr>
            <tr><th>Last success</th><td>{{ i.lastSuccessAt | ago }}</td></tr>
            <tr><th>Last run</th><td>{{ i.lastRunAt | ago }}</td></tr>
            <tr><th>Next run</th><td>{{ i.nextRunAt | ago }}</td></tr>
            <tr><th>Failures in a row</th><td>{{ i.health.consecutiveFailures }}</td></tr>
            @if (i.health.lastFailure) {
              <tr><th>Last failure</th><td data-testid="integration-failure">{{ i.health.lastFailure }}@if (i.health.lastFailureDetail) { : <span data-testid="integration-failure-detail">{{ i.health.lastFailureDetail }}</span> }</td></tr>
            }
          </tbody>
        </table>
        <p class="jc-small jc-muted">{{ summary(i) }}. Failure text comes from the plugin and is shown as written.</p>
      </section>

      @if (checks(); as c) {
        <section class="jc-card" data-testid="integration-report">
          <h2>Validation {{ c.passed ? 'passed' : 'failed' }}@if (c.at) { <span class="jc-muted jc-small"> · {{ c.at | ago }}</span> }</h2>
          <ul>
            @for (k of c.checks; track $index) {
              <li data-testid="integration-check"><span class="jc-badge" [class]="'jc-badge ' + (k.passed ? 'jc-health-healthy' : 'jc-health-unhealthy')">{{ k.passed ? 'pass' : 'fail' }}</span>
                <strong> {{ k.name }}</strong>@if (k.detail) { <span>: {{ k.detail }}</span> }</li>
            }
          </ul>
        </section>
      }

      @if (admin() && i.config) {
        <section class="jc-card" data-testid="integration-config">
          <h2>Configuration</h2>
          <pre class="jc-mono jc-small">{{ json(i.config) }}</pre>
          @if (i.scope && hasKeys(i.scope)) { <h3>Scope</h3><pre class="jc-mono jc-small">{{ json(i.scope) }}</pre> }
          @if (i.secretRefs && hasKeys(i.secretRefs)) {
            <h3>Secret references</h3>
            <ul>@for (r of refs(i); track r[0]) { <li><span class="jc-mono">{{ r[0] }}</span>: <span class="jc-mono">{{ r[1] }}</span></li> }</ul>
          }
        </section>
      } @else if (!admin()) {
        <p class="jc-muted jc-small" data-testid="integration-readonly">Read-only: configuration and secret references are shown to admins only.</p>
      }
    }`,
})
export class IntegrationPage {
  private readonly api = inject(IntegrationsApi);
  private readonly tenants = inject(ApiService).tenant$;
  private readonly router = inject(Router);
  private readonly links = inject(ObservabilityLinks);
  private readonly canAdmin = inject(OBSERVABILITY_CAN_ADMIN);
  readonly id = input.required<string>();
  protected readonly admin = () => this.canAdmin();
  protected readonly stateClass = integrationStateClass;
  protected readonly editing = signal(false);
  protected readonly busy = signal(false);
  protected readonly stale = signal(false);
  protected readonly confirmingDelete = signal(false);
  protected readonly actionError = signal('');
  /** The report of the validation just run; before one, the instance's last. */
  private readonly fresh = signal<{ passed: boolean; checks: ValidationCheckDto[]; at: string } | null>(null);

  protected readonly data = apiResource({
    params: () => this.id(),
    stream: ({ params }) => forkJoin({ instance: this.api.instance(params), plugins: this.api.plugins() }),
  });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly item = computed(() => this.data.value()?.instance ?? null);
  protected readonly plugin = computed(() => this.data.value()?.plugins.find(p => p.key === this.item()?.pluginKey) ?? null);
  protected readonly checks = computed(() => this.fresh() ?? this.item()?.lastValidation ?? null);

  constructor() {
    // A different tenant starts afresh: no open form or draft, refusal, stale notice, report or delete confirmation from the last one.
    this.tenants.pipe(map(t => t.id), distinctUntilChanged(), skip(1), takeUntilDestroyed()).subscribe(() => {
      this.busy.set(false);
      this.confirmingDelete.set(false);
      this.reload();
    });
  }

  protected summary(i: IntegrationInstanceDto) { return integrationHealthText(i); }
  protected canValidate(i: IntegrationInstanceDto) { return VALIDATABLE.includes(i.state) && !!i.pluginName; }
  protected canEnable(i: IntegrationInstanceDto) { return i.state === 'Validated' || i.state === 'Disabled'; }
  protected canDelete(i: IntegrationInstanceDto) { return DELETABLE.includes(i.state); }
  protected json(o: unknown) { return JSON.stringify(o, null, 2); }
  protected hasKeys(o: object) { return Object.keys(o).length > 0; }
  protected refs(i: IntegrationInstanceDto) { return Object.entries(i.secretRefs ?? {}); }
  protected enable(i: IntegrationInstanceDto) { return this.api.enable(i.id); }
  protected disable(i: IntegrationInstanceDto) { return this.api.disable(i.id); }

  protected reload() {
    this.stale.set(false);
    this.editing.set(false);
    this.actionError.set('');
    this.fresh.set(null);
    this.data.reload();
  }

  protected saved() {
    this.editing.set(false);
    this.fresh.set(null);
    this.data.reload();
  }

  private fail(e: unknown) {
    this.busy.set(false);
    const status = (e as { status?: number }).status;
    if (status === 412) { this.stale.set(true); return; }
    this.actionError.set(status === 403 ? 'Changing integrations needs the admin role in this deployment’s tenant.'
      : status === 428 ? 'Reload the instance before changing it.' : problemText(e));
  }

  protected run(call: Observable<unknown>) {
    this.busy.set(true);
    this.actionError.set('');
    this.stale.set(false);
    call.subscribe({ next: () => { this.busy.set(false); this.fresh.set(null); this.data.reload(); }, error: e => this.fail(e) });
  }

  protected validate(i: IntegrationInstanceDto) {
    this.busy.set(true);
    this.actionError.set('');
    this.stale.set(false);
    this.api.validate(i.id).subscribe({
      next: r => { this.busy.set(false); this.fresh.set({ passed: r.report.passed, checks: r.report.checks, at: new Date().toISOString() }); this.data.reload(); },
      error: e => this.fail(e),
    });
  }

  protected remove(i: IntegrationInstanceDto) {
    this.busy.set(true);
    this.actionError.set('');
    this.api.delete(i.id, i.etag).subscribe({
      next: () => { this.busy.set(false); this.router.navigate(this.links.to('integrations')); },
      error: e => { this.confirmingDelete.set(false); this.fail(e); },
    });
  }
}
