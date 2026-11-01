import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { SearchablePage } from '@juice-js/layout';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';

const TYPES = ['host', 'container', 'k8s-pod', 'database', 'messaging-destination'];

/**
 * Where services run and what they use (Console requirements §11): hosts, containers, pods, databases and
 * destinations, as discovery reconciled them. Never deleted automatically; renames come only from configured aliases.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Resources</h1>
      @for (t of types; track t) {
        <a class="jc-chip" [class.jc-selected]="t === (type() ?? 'host')" routerLink="/resources" [queryParams]="ctx.keep({ type: t })">{{ t }}</a>
      }
    </jc-context-bar>
    @if (resources.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (resources.error()) { <p class="jc-error">{{ error() }}</p> }
    <table class="jc-table">
      <thead><tr><th>Name</th><th>Key</th><th>First seen</th><th>Last seen</th></tr></thead>
      <tbody>
        @for (r of rows(); track r.id) {
          <tr><td><a [routerLink]="['/resources', r.id]" [queryParams]="ctx.keep()">{{ r.displayName }}</a></td>
              <td class="jc-mono jc-small">{{ r.key }}</td><td>{{ r.firstSeen | ago }}</td><td>{{ r.lastSeen | ago }}</td></tr>
        } @empty { <tr><td colspan="4" class="jc-muted">None seen.</td></tr> }
      </tbody>
    </table>`,
})
export class ResourcesPage implements SearchablePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly types = TYPES;
  readonly type = input<string>();
  private readonly filter = signal('');

  protected readonly resources = rxResource({
    params: () => this.type() ?? 'host',
    stream: ({ params }) => this.api.resources(params).pipe(map(p => p.items)),
  });
  protected readonly error = computed(() => problemText(this.resources.error()));
  protected readonly rows = computed(() => (this.resources.value() ?? [])
    .filter(r => !this.filter() || `${r.displayName} ${r.key}`.toLowerCase().includes(this.filter().toLowerCase())));

  onSearch(query: string) { this.filter.set(query); }
}
