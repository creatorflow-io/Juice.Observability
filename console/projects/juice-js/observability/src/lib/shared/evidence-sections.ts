import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ContextService } from '../core/context.service';
import { EvidenceItem, EvidenceSectionDto } from '../core/models';
import { LinkPipe } from '../core/options';
import { InstalledModules, OBSERVABILITY_MODULES } from '../modules/installed-modules';
import { apiResource } from './resource';

/** Whether a concrete relative route (`slos/api-latency`) matches a route pattern (`slos/:key`): same segments, a `:name` segment takes any one. */
export function routeMatches(route: string, pattern: string): boolean {
  const a = route.split('/');
  const b = pattern.split('/');
  return a.length === b.length && b.every((segment, i) => (segment.startsWith(':') ? a[i].length > 0 : segment === a[i]));
}

/** Two route patterns of the same shape: the same literal segments, and a parameter wherever either has one. */
function samePattern(a: string, b: string): boolean {
  const x = a.split('/');
  const y = b.split('/');
  return x.length === y.length && x.every((segment, i) => segment === y[i] || (segment.startsWith(':') && y[i].startsWith(':')));
}

const SEGMENT = /^[A-Za-z0-9._~-]{1,128}$/;
const QUERY_KEY = /^[A-Za-z0-9_.-]{1,32}$/;

/** The router link of an evidence item, or null: only a route of an installed module's manifest, that its library in this build defines, is a link. */
export function evidenceLink(item: EvidenceItem, patterns: readonly string[]): { segments: string[]; query: Record<string, string> } | null {
  const link = item.link;
  if (!link || typeof link.route !== 'string') return null;
  const segments = link.route.split('/');
  if (!segments.every(s => SEGMENT.test(s) && s !== '.' && s !== '..') || !patterns.some(p => routeMatches(link.route, p))) return null;
  const query: Record<string, string> = {};
  for (const [key, value] of Object.entries(link.query ?? {}).slice(0, 8)) {
    if (QUERY_KEY.test(key) && typeof value === 'string' && value.length <= 256) query[key] = value;
  }
  return { segments, query };
}

const WEEK_MS = 7 * 24 * 3_600_000;
const SEVERITIES = new Set(['info', 'warning', 'critical']);

/**
 * What modules add to a service or an alert page (core 2.0 M3, E9): sections the server's contributors made, each with its own state. Everything is text:
 * a link shows only when it points to a route of an installed module, and a section that failed, timed out or was invalid shows one generic line, never
 * what came with it. When the call fails altogether (an older server, no right) nothing shows. On an alert page the core's own sections are left out:
 * the page shows them in its own blocks.
 */
@Component({
  selector: 'jc-evidence-sections',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, LinkPipe],
  template: `
    @for (s of shown(); track s.key) {
      <section class="jc-card jc-evidence" [attr.data-testid]="'evidence-' + s.key">
        @if (s.state === 'ok') {
          <h2>{{ s.title }}</h2>
          @if (s.note) { <p class="jc-muted">{{ s.note }}</p> }
          @for (item of s.items; track $index) {
            <div class="jc-list-row">
              @if (severity(item); as severity) { <span class="jc-badge" [class.jc-health-unhealthy]="severity === 'critical'" [class.jc-health-degraded]="severity === 'warning'">{{ severity }}</span> }
              @if (link(item); as l) {
                <a [routerLink]="l.segments | jcLink" [queryParams]="l.query">{{ item.label }}</a>
              } @else { <span>{{ item.label }}</span> }
              @if (item.value) { <span>{{ item.value }}</span> }
              @if (item.at) { <span class="jc-muted">{{ item.at | date: 'MMM d, HH:mm:ss' }}</span> }
            </div>
          }
          @if (s.truncated) { <p class="jc-muted">Only the first items are shown.</p> }
        } @else {
          <p class="jc-muted" data-testid="evidence-unavailable">This section is unavailable.</p>
        }
      </section>
    }`,
})
export class EvidenceSections {
  private readonly api = inject(ApiService);
  private readonly ctx = inject(ContextService);
  private readonly installed = inject(InstalledModules);
  private readonly libraries = inject(OBSERVABILITY_MODULES, { optional: true }) ?? [];

  readonly target = input.required<'alert' | 'service'>();
  readonly subjectId = input.required<string>();
  /** The alert page draws the core's sections itself. */
  readonly skipCore = input(false);

  private readonly response = apiResource({
    params: () => ({ target: this.target(), id: this.subjectId(), env: this.ctx.environment(), range: this.ctx.range(), abs: this.ctx.absolute() }),
    stream: ({ params }) => (params.target === 'alert'
      ? this.api.alertEvidence(params.id)
      : this.api.environments().pipe(
        map(page => page.items.find(e => e.key === params.env)?.id ?? null),
        catchError(() => of(null)),
        // The window is the page's, at most 7 days (the API's bound).
        switchMap(environmentId => this.api.serviceEvidence(params.id, { environmentId, ...this.ctx.window(WEEK_MS) })))
    ).pipe(catchError(() => of(null))),
  });

  /** The routes a link may point to: a route of an installed module's manifest that the module's library in this build defines too. */
  private readonly patterns = computed(() => {
    const patterns: string[] = [];
    for (const server of this.installed.modules()) {
      const library = this.libraries.find(l => l.name === server.name);
      if (!library || !server.console) continue;
      const defined = library.routes.map(r => r.path ?? '');
      for (const route of server.console.routes) if (route.path && defined.some(d => samePattern(d, route.path))) patterns.push(route.path);
    }
    return patterns;
  });

  protected readonly shown = computed<EvidenceSectionDto[]>(() => (this.response.value()?.items ?? [])
    .filter(s => s && typeof s.key === 'string' && !(this.skipCore() && s.module === 'core')));

  protected link(item: EvidenceItem) { return evidenceLink(item, this.patterns()); }
  protected severity(item: EvidenceItem): string | null { return item.severity && SEVERITIES.has(item.severity) ? item.severity : null; }
}
