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
 * own, so this adds one beside the label, and on the header of the menu group it sits in while that group is closed, and puts it back
 * whenever the menu is drawn again. With the menu folded to icons, it sits on the icon.
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
    const schedule = () => {
      if (scheduled) return;
      scheduled = true;
      requestAnimationFrame(() => { scheduled = false; this.apply(this.count.forYou()); });
    };
    // The menu drawn again (anywhere in the page, as the shell may be rebuilt).
    this.observer = new MutationObserver(schedule);
    this.observer.observe(document.body, { childList: true, subtree: true });
    // Within the menu only: a group opening or closing (its class) moves the count.
    this.classes = new MutationObserver(schedule);
    // The menu folding, unfolding or coming into view moves the icon a folded menu's count sits on: measure again.
    if (typeof ResizeObserver !== 'undefined') this.resizes = new ResizeObserver(schedule);
    inject(DestroyRef).onDestroy(() => { this.observer?.disconnect(); this.classes?.disconnect(); this.resizes?.disconnect(); });
  }

  private classes: MutationObserver | null = null;
  private resizes: ResizeObserver | null = null;
  /** The side menu being watched for classes and size: the one on screen. */
  private watched: Element | null = null;

  private apply(count: number) {
    const menu = document.querySelector('juice-nav-menu');
    if (menu !== this.watched) {
      // A new menu (the shell rebuilt): stop watching the old one, so it isn't kept.
      this.classes?.disconnect();
      this.resizes?.disconnect();
      if (menu) {
        this.classes?.observe(menu, { subtree: true, attributes: true, attributeFilter: ['class'] });
        this.resizes?.observe(menu);
      }
      this.watched = menu;
    }
    const counted = new Set<HTMLElement>();
    for (const icon of Array.from(document.querySelectorAll<HTMLElement>('juice-nav-menu mat-icon'))) {
      if (icon.textContent?.trim() !== ApprovalMenuBadge.Icon) continue;
      const item = icon.closest<HTMLElement>('mat-list-item, .mat-mdc-list-item');
      if (!item) continue;
      this.badge(item, count, 'approvals-badge');
      counted.add(item);
      // Inside a menu group (Automation) the entry is hidden while the group is closed: then the group's header counts.
      const panel = icon.closest<HTMLElement>('mat-expansion-panel');
      const header = panel?.querySelector<HTMLElement>('mat-expansion-panel-header .mat-mdc-list-item');
      if (panel && header) {
        this.badge(header, panel.classList.contains('mat-expanded') ? 0 : count, 'approvals-group-badge');
        counted.add(header);
      }
    }
    // A count left on an entry that no longer holds Approvals (the menu changed for this user): take it away.
    // (Also a hint left where no badge could be drawn.)
    const marks = 'juice-nav-menu .jc-menu-badge, juice-nav-menu [title$="waiting for your approval"]';
    for (const stale of Array.from(document.querySelectorAll<HTMLElement>(marks))) {
      const item = stale.closest<HTMLElement>('mat-list-item, .mat-mdc-list-item');
      if (item && !counted.has(item)) this.badge(item, 0, '');
    }
  }

  /**
   * The count beside the item's label; on its icon's corner when the label isn't drawn (the side menu folded to icons).
   * No count: no badge and no hint.
   */
  private badge(item: HTMLElement, count: number, testId: string) {
    // The hint goes with the count, wherever (and whether) the badge can be drawn.
    const hint = count > 0 ? `${count} request${count === 1 ? '' : 's'} waiting for your approval` : null;
    if (hint === null) item.removeAttribute('title');
    else if (item.getAttribute('title') !== hint) item.setAttribute('title', hint);

    const title = item.querySelector<HTMLElement>('[matListItemTitle], .mdc-list-item__primary-text');
    const icon = item.querySelector<HTMLElement>('mat-icon');
    const host = title ?? (icon ? item : null);
    let badge = item.querySelector<HTMLElement>('.jc-menu-badge');
    if (badge && (count <= 0 || badge.parentElement !== host)) { badge.remove(); badge = null; }
    if (count <= 0 || !host) return;
    if (!badge) {
      badge = document.createElement('span');
      badge.className = 'jc-menu-badge';
      badge.setAttribute('data-testid', testId);
      const look = 'min-width:1.25rem;padding:0 .375rem;border-radius:999px;font-size:.75rem;line-height:1.25rem;text-align:center;'
        + 'font-weight:600;background:var(--mat-sys-error,#b3261e);color:var(--mat-sys-on-error,#fff);box-sizing:border-box';
      badge.style.cssText = title
        ? `display:inline-block;margin-left:.5rem;${look}`
        // Folded to icons (no label): small, on the icon's top right corner, inside the row (which clips).
        : `position:absolute;transform:scale(.75);transform-origin:top left;pointer-events:none;${look}`;
      host.appendChild(badge);
    }
    const text = count > 99 ? '99+' : String(count);
    if (badge.textContent !== text) badge.textContent = text;
    if (!title && icon) {
      if (getComputedStyle(item).position === 'static') item.style.position = 'relative';
      const row = item.getBoundingClientRect(), mark = icon.getBoundingClientRect();
      // Not laid out (the menu hidden): place it on a later pass, which the menu's size change brings.
      if (row.width === 0 || mark.width === 0) return;
      const left = `${Math.round(mark.right - row.left - 10)}px`, top = `${Math.max(0, Math.round(mark.top - row.top - 6))}px`;
      if (badge.style.left !== left) badge.style.left = left;
      if (badge.style.top !== top) badge.style.top = top;
    }
  }
}
