import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { distinctUntilChanged, forkJoin, map, skip } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { IntegrationInstanceDto, IntegrationPluginDto, IntegrationsApi } from '../core/integrations-api';
import { LinkPipe, OBSERVABILITY_CAN_ADMIN, ObservabilityLinks } from '../core/options';
import { AgoPipe } from '../shared/format';
import { IntegrationForm, cap } from '../shared/integration-form';
import { apiResource } from '../shared/resource';
import { problemText } from '../shared/telemetry';
import { ContextBar } from '../shared/ui';

/** The badge class of an instance state: Enabled is healthy, Degraded warns, Failed is unhealthy; the states before it are plain. */
export function integrationStateClass(state: string): string {
  switch (state) {
    case 'Enabled': return 'jc-health-healthy';
    case 'Degraded': return 'jc-health-degraded';
    case 'Failed': return 'jc-health-unhealthy';
    case 'Disabled': return 'jc-health-offline';
    default: return 'jc-health-none';
  }
}

/** One line on how an instance is doing: collection, then freshness. Plugin text is never part of it. */
export function integrationHealthText(i: IntegrationInstanceDto): string {
  const h = i.health;
  const parts = [`collection ${h.collection}`, `data ${h.freshness === 'ok' ? 'fresh' : h.freshness}`];
  if (h.consecutiveFailures > 0) parts.push(`${h.consecutiveFailures} failed run${h.consecutiveFailures === 1 ? '' : 's'} in a row`);
  return parts.join(' · ');
}

/**
 * Integrations (integration core I1): the instances an admin configured, each bound to a plugin installed in this deployment, with its state and
 * health. An admin creates one here and manages it on its page; others see the list only. Plugin and admin text is shown as text.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, ContextBar, AgoPipe, IntegrationForm],
  template: `
    <jc-context-bar>
      <h1>Integrations</h1>
      <span class="jc-spacer"></span>
      @if (admin() && plugins().length && !creating()) { <button mat-flat-button data-testid="integration-new" (click)="creating.set(true)">New instance</button> }
    </jc-context-bar>
    @if (data.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (data.error()) { <p class="jc-error" data-testid="integrations-error">{{ error() }}</p> }

    @if (data.hasValue() && plugins().length === 0) {
      <p class="jc-muted" data-testid="integrations-none">No integration plugins are installed. Plugins are built into the platform's host, so there is nothing to add here until one is.</p>
    }

    @if (creating() && admin() && selected(); as plugin) {
      @if (plugins().length > 1) {
        <label class="jc-small">Plugin
          <select data-testid="integration-plugin" [ngModel]="pluginKey()" (ngModelChange)="pluginKey.set($event)">
            @for (p of plugins(); track p.key) { <option [value]="p.key">{{ cap(p.displayName) }} ({{ p.key }})</option> }
          </select>
        </label>
      }
      @for (p of [plugin]; track p.key) {
        <jc-integration-form [plugin]="p" (saved)="created($event)" (cancelled)="creating.set(false)" (reload)="creating.set(false)" />
      }
    }

    @if (plugins().length) {
      <table class="jc-table" data-testid="integrations">
        <thead><tr><th>Name</th><th>Plugin</th><th>State</th><th>Health</th><th>Last success</th><th>Next run</th></tr></thead>
        <tbody>
          @for (i of instances(); track i.id) {
            <tr [attr.data-testid]="'integration-' + i.id">
              <td><a [routerLink]="['integrations', i.id] | jcLink" data-testid="integration-link"><strong>{{ i.name }}</strong></a></td>
              <td>
                @if (i.pluginName) { {{ i.pluginName }} <span class="jc-muted jc-small jc-mono">{{ i.pluginKey }}</span> }
                @else { <span class="jc-mono">{{ i.pluginKey }}</span> <span class="jc-badge jc-health-unhealthy" data-testid="plugin-missing">not installed</span> }
              </td>
              <td><span class="jc-badge" data-testid="integration-state" [class]="'jc-badge ' + stateClass(i.state)">{{ i.state }}</span>
                @if (i.stateReason) { <div class="jc-small jc-muted" data-testid="integration-reason">{{ i.stateReason }}</div> }</td>
              <td class="jc-small">{{ healthText(i) }}</td>
              <td class="jc-nowrap">{{ i.lastSuccessAt | ago }}</td>
              <td class="jc-nowrap">{{ i.nextRunAt | ago }}</td>
            </tr>
          } @empty { <tr><td colspan="6" class="jc-muted">No instances yet.{{ admin() ? ' Use New instance to connect one.' : '' }}</td></tr> }
        </tbody>
      </table>
    }
    @if (!admin() && plugins().length) { <p class="jc-muted jc-small" data-testid="integrations-readonly">Changing integrations needs the admin role. You can see their state and health.</p> }`,
})
export class IntegrationsPage {
  private readonly api = inject(IntegrationsApi);
  private readonly tenants = inject(ApiService).tenant$;
  protected readonly cap = cap;
  private readonly router = inject(Router);
  private readonly links = inject(ObservabilityLinks);
  private readonly canAdmin = inject(OBSERVABILITY_CAN_ADMIN);
  protected readonly admin = () => this.canAdmin();
  protected readonly creating = signal(false);
  protected readonly pluginKey = signal('');
  protected readonly stateClass = integrationStateClass;
  protected readonly healthText = integrationHealthText;

  protected readonly data = apiResource({ stream: () => forkJoin({ plugins: this.api.plugins(), instances: this.api.instances({ limit: 200 }) }) });
  protected readonly error = computed(() => problemText(this.data.error()));
  protected readonly plugins = computed<IntegrationPluginDto[]>(() => this.data.value()?.plugins ?? []);
  protected readonly instances = computed(() => this.data.value()?.instances.items ?? []);
  protected readonly selected = computed(() => this.plugins().find(p => p.key === this.pluginKey()) ?? this.plugins()[0] ?? null);

  constructor() {
    // A different tenant starts afresh: no form, draft or plugin choice from the last one stays open.
    this.tenants.pipe(map(t => t.id), distinctUntilChanged(), skip(1), takeUntilDestroyed()).subscribe(() => {
      this.creating.set(false);
      this.pluginKey.set('');
      this.data.reload();
    });
  }

  protected created(i: IntegrationInstanceDto) {
    this.creating.set(false);
    this.router.navigate(this.links.to('integrations', i.id));
  }
}
