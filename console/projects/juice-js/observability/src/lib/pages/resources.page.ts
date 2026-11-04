import { LinkPipe } from '../core/options';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { apiResource } from '../shared/resource';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { SearchablePage } from '@juice-js/layout';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { AgoPipe } from '../shared/format';
import { ContextBar } from '../shared/ui';
import { problemText } from '../shared/telemetry';
import { screenPages } from '../shared/paging';

const TYPES = ['host', 'container', 'k8s-pod', 'database', 'messaging-destination'];

/**
 * Where services run and what they use (Console requirements §11): hosts, containers, pods, databases and
 * destinations, as discovery reconciled them. Never deleted automatically; renames come only from configured aliases.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkPipe, RouterLink, MatPaginatorModule, MatProgressBarModule, ContextBar, AgoPipe],
  template: `
    <jc-context-bar><h1>Resources</h1>
      @for (t of types; track t) {
        <a class="jc-chip" [class.jc-selected]="t === (type() ?? 'host')" [routerLink]="['resources'] | jcLink" [queryParams]="ctx.keep({ type: t })">{{ t }}</a>
      }
    </jc-context-bar>
    @if (resources.isLoading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (resources.error()) { <p class="jc-error">{{ error() }}</p> }
    @if (capped()) { <p class="jc-notice">Showing the first 5,000 only. Narrow the list with the search box or a filter.</p> }
    <table class="jc-table">
      <thead><tr><th>Name</th><th>Key</th><th>First seen</th><th>Last seen</th></tr></thead>
      <tbody>
        @for (r of pages.visible(); track r.id) {
          <tr><td><a [routerLink]="['resources', r.id] | jcLink" [queryParams]="ctx.keep()">{{ r.displayName }}</a></td>
              <td class="jc-mono jc-small">{{ r.key }}</td><td>{{ r.firstSeen | ago }}</td><td>{{ r.lastSeen | ago }}</td></tr>
        } @empty { <tr><td colspan="4" class="jc-muted">None seen.</td></tr> }
      </tbody>
    </table>
    @if (pages.needed()) {
      <mat-paginator [length]="rows().length" [pageSize]="pages.size()" [pageIndex]="pages.index()" [pageSizeOptions]="pages.sizes"
                     (page)="pages.change($event)" aria-label="Pages" />
    }`,
})
export class ResourcesPage implements SearchablePage {
  protected readonly ctx = inject(ContextService);
  private readonly api = inject(ApiService);
  protected readonly types = TYPES;
  readonly type = input<string>();
  private readonly filter = signal('');

  protected readonly resources = apiResource({
    params: () => this.type() ?? 'host',
    stream: ({ params }) => this.api.resources(params),
  });
  protected readonly error = computed(() => problemText(this.resources.error()));
  protected readonly capped = computed(() => (this.resources.value()?.nextCursor ?? null) !== null);
  protected readonly rows = computed(() => (this.resources.value()?.items ?? [])
    .filter(r => !this.filter() || `${r.displayName} ${r.key}`.toLowerCase().includes(this.filter().toLowerCase())));

  protected readonly pages = screenPages(this.rows);

  onSearch(query: string) { this.filter.set(query); }
}
