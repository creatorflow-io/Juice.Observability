import { Injectable, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Params, Router } from '@angular/router';
import { filter, map, startWith } from 'rxjs';

export const RANGES: Record<string, number> = {
  '15m': 15 * 60_000, '1h': 3_600_000, '6h': 6 * 3_600_000, '24h': 24 * 3_600_000, '7d': 7 * 24 * 3_600_000,
};

/** Search ranges are at most 24 hours (the API's bound); series and operations take up to 7 days. */
export const MAX_SEARCH_MS = RANGES['24h'];

/**
 * The investigation context that follows the user between pages (Console requirements §17): environment and time
 * range. It lives in the URL (`env`, and `range` or `from`/`to`), so every page is a deep link and the browser's back
 * button restores it.
 */
@Injectable({ providedIn: 'root' })
export class ContextService {
  private readonly router = inject(Router);

  // Created on first use, after the first navigation may already have ended: start from the current URL.
  private readonly params = toSignal(this.router.events.pipe(
    filter(e => e instanceof NavigationEnd),
    startWith(null),
    map(() => this.leaf().snapshot.queryParams),
  ), { initialValue: {} as Params });

  readonly environment = computed(() => (this.params()['env'] as string | undefined) ?? '');
  readonly range = computed(() => {
    const p = this.params();
    return p['from'] && p['to'] ? 'custom' : ((p['range'] as string | undefined) ?? '1h');
  });
  readonly absolute = computed(() => {
    const p = this.params();
    return p['from'] && p['to'] ? { from: p['from'] as string, to: p['to'] as string } : null;
  });

  /** The window as ISO strings, evaluated now for a relative range. */
  window(maxMs?: number): { from: string; to: string } {
    const absolute = this.absolute();
    if (absolute) return absolute;
    const to = Date.now();
    const span = Math.min(RANGES[this.range()] ?? RANGES['1h'], maxMs ?? Number.MAX_SAFE_INTEGER);
    return { from: new Date(to - span).toISOString(), to: new Date(to).toISOString() };
  }

  /** Query parameters for a link: the context plus the target page's own. */
  keep(extra: Params = {}): Params {
    const p = this.params();
    const context: Params = {};
    for (const key of ['env', 'range', 'from', 'to']) if (p[key]) context[key] = p[key];
    return { ...context, ...extra };
  }

  /** Changes the context on the current page, keeping its other parameters. */
  set(change: { env?: string | null; range?: string | null; from?: string | null; to?: string | null }) {
    const queryParams: Params = { ...change };
    if (change.range) Object.assign(queryParams, { from: null, to: null });
    void this.router.navigate([], { relativeTo: this.leaf(), queryParams, queryParamsHandling: 'merge' });
  }

  /** A time window centred on a moment, for "around this span" links. */
  around(at: string, durationMs: number, paddingMs = 5 * 60_000): Params {
    const start = new Date(at).getTime();
    return this.keep({
      from: new Date(start - paddingMs).toISOString(),
      to: new Date(start + durationMs + paddingMs).toISOString(),
      range: null,
    });
  }

  private leaf(): ActivatedRoute {
    let route = this.router.routerState.root;
    while (route.firstChild) route = route.firstChild;
    return route;
  }
}
