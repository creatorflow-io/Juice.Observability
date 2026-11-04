import { DestroyRef, Signal, computed, inject, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { apiResource } from './resource';
import { PageEvent } from '@angular/material/paginator';
import { Observable, map } from 'rxjs';
import { Query } from '../core/api.service';

/** A page from an API list that pages with a cursor: alerts, traces, logs. */
export interface CursorPage<T> { items: T[]; nextCursor?: string | null; }

/**
 * A newest-first list read page by page with the API's cursor. The first page loads whenever `params` change; `more()`
 * appends the next page, asked with exactly the query of the first one (its time window included), so the list grows
 * without shifting, skipping or repeating rows. Call it in an injection context, such as a field initializer.
 */
export function cursorList<P, T>(params: () => P, query: (params: P) => Query, fetch: (query: Query) => Observable<CursorPage<T>>) {
  const destroyRef = inject(DestroyRef);
  const first = apiResource({
    params,
    stream: ({ params }) => {
      const q = query(params);
      return fetch(q).pipe(map(page => ({ page, query: q })));
    },
  });
  // Pages after the first; reset whenever the first page reloads.
  const rest = linkedSignal({ source: first.value, computation: () => ({ items: [] as T[], cursor: undefined as string | null | undefined }) });
  const loadingMore = signal(false);
  const moreError = signal<unknown>(null);
  const items = computed(() => [...(first.value()?.page.items ?? []), ...rest().items]);
  const cursor = computed(() => rest().cursor !== undefined ? rest().cursor! : first.value()?.page.nextCursor ?? null);

  function more() {
    const current = first.value();
    const next = cursor();
    if (!current || !next || loadingMore()) return;
    loadingMore.set(true);
    moreError.set(null);
    fetch({ ...current.query, cursor: next }).pipe(takeUntilDestroyed(destroyRef)).subscribe({
      next: page => rest.update(r => ({ items: [...r.items, ...page.items], cursor: page.nextCursor ?? null })),
      error: e => { moreError.set(e); loadingMore.set(false); },
      complete: () => loadingMore.set(false),
    });
  }

  return { first, items, hasMore: computed(() => cursor() !== null), loadingMore, moreError: moreError.asReadonly(), more };
}

/**
 * Pages a list already in memory for display (the inventory lists, which the Console sorts and filters itself). Goes
 * back to the first page whenever the rows change, for example when a filter narrows them.
 */
export function screenPages<T>(rows: Signal<T[]>, sizes = [50, 100, 200]) {
  const size = signal(sizes[0]);
  const index = linkedSignal({ source: rows, computation: () => 0 });
  return {
    sizes,
    size: size.asReadonly(),
    index: index.asReadonly(),
    visible: computed(() => rows().slice(index() * size(), (index() + 1) * size())),
    /** More rows than one page: show the paginator. */
    needed: computed(() => rows().length > sizes[0]),
    change(event: PageEvent) { size.set(event.pageSize); index.set(event.pageIndex); },
  };
}
