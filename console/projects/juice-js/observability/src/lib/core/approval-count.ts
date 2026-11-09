import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { Subscription, filter, timer, switchMap } from 'rxjs';
import { ApiService } from './api.service';

/**
 * How many action requests wait for the signed-in user to decide, refreshed every 30 seconds from whatever page is open, and at once
 * when a page made or decided one ({@link refresh}). It starts only once a page has fetched the tenant: before that the user may still be
 * signing in, and an early call would fail, along with the page's own call sharing it. It stops on an error (signed out, or no rights)
 * until the next navigation, so a signed-out tab doesn't keep asking.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalCount {
  static readonly Every = 30_000;

  private readonly api = inject(ApiService);
  private polling: Subscription | null = null;
  /** Requests the signed-in user can approve or reject now. */
  readonly forYou = signal(0);

  constructor() {
    effect(() => { if (this.api.tenantKnown() && !untracked(() => this.polling)) untracked(() => this.start()); });
    const navigations = inject(Router).events.pipe(filter(e => e instanceof NavigationEnd))
      .subscribe(() => { if (!this.polling && this.api.tenantKnown()) this.start(); });
    inject(DestroyRef).onDestroy(() => { navigations.unsubscribe(); this.polling?.unsubscribe(); });
  }

  /** Asks again now, for a page that just changed what waits. */
  refresh() { if (this.api.tenantKnown()) this.start(); }

  private start() {
    this.polling?.unsubscribe();
    this.polling = timer(0, ApprovalCount.Every).pipe(switchMap(() => this.api.waitingRequests())).subscribe({
      next: w => this.forYou.set(w.forYou),
      error: () => { this.forYou.set(0); this.polling = null; },
    });
  }
}

/**
 * Shows {@link ApprovalCount} on the Approvals entry of juice-layout's side menu, found by its icon. juice-layout's menu has no badge of its
 * own, so this adds one beside the label and puts it back whenever the menu is drawn again.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalMenuBadge {
  static readonly Icon = 'approval';

  private readonly count = inject(ApprovalCount);
  private observer: MutationObserver | null = null;

  constructor() {
    if (typeof document === 'undefined' || typeof MutationObserver === 'undefined') return;
    effect(() => this.apply(this.count.forYou()));
    let scheduled = false;
    this.observer = new MutationObserver(() => {
      if (scheduled) return;
      scheduled = true;
      requestAnimationFrame(() => { scheduled = false; this.apply(this.count.forYou()); });
    });
    this.observer.observe(document.body, { childList: true, subtree: true });
    inject(DestroyRef).onDestroy(() => this.observer?.disconnect());
  }

  private apply(count: number) {
    for (const icon of Array.from(document.querySelectorAll<HTMLElement>('juice-nav-menu mat-icon'))) {
      if (icon.textContent?.trim() !== ApprovalMenuBadge.Icon) continue;
      const item = icon.closest<HTMLElement>('mat-list-item, .mat-mdc-list-item');
      const title = item?.querySelector<HTMLElement>('[matListItemTitle], .mdc-list-item__primary-text');
      if (!item || !title) continue;
      let badge = title.querySelector<HTMLElement>('.jc-menu-badge');
      if (count <= 0) { badge?.remove(); item.removeAttribute('title'); continue; }
      if (!badge) {
        badge = document.createElement('span');
        badge.className = 'jc-menu-badge';
        badge.setAttribute('data-testid', 'approvals-badge');
        badge.style.cssText = 'display:inline-block;min-width:1.25rem;margin-left:.5rem;padding:0 .375rem;border-radius:999px;font-size:.75rem;'
          + 'line-height:1.25rem;text-align:center;font-weight:600;background:var(--mat-sys-error,#b3261e);color:var(--mat-sys-on-error,#fff)';
        title.appendChild(badge);
      }
      const text = count > 99 ? '99+' : String(count);
      if (badge.textContent !== text) badge.textContent = text;
      const hint = `${count} request${count === 1 ? '' : 's'} waiting for your approval`;
      if (item.getAttribute('title') !== hint) item.setAttribute('title', hint);
    }
  }
}
