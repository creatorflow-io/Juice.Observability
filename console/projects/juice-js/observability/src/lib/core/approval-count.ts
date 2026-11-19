import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, effect, inject, signal, untracked } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { Subscription, filter, fromEvent, timer, switchMap } from 'rxjs';
import { MenuBadgeService } from '@juice-js/layout';
import { ApiService } from './api.service';

/**
 * How many action requests wait for the signed-in user to decide, refreshed every 30 seconds from whatever page is open, and at once
 * when a page made or decided one ({@link refresh}). It starts only once a page has fetched the tenant: before that the user may still be
 * signing in, and an early call would fail, along with the page's own call sharing it. It stops on an error (signed out, or no rights)
 * until the next navigation, so a signed-out tab doesn't keep asking. A tab out of sight doesn't ask either; it asks at once on coming back.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalCount {
  static readonly Every = 30_000;

  private readonly api = inject(ApiService);
  private readonly document = inject(DOCUMENT);
  private polling: Subscription | null = null;
  /** Requests the signed-in user can approve or reject now. */
  readonly forYou = signal(0);

  constructor() {
    effect(() => { if (this.api.tenantKnown() && !untracked(() => this.polling)) untracked(() => this.start()); });
    const navigations = inject(Router).events.pipe(filter(e => e instanceof NavigationEnd))
      .subscribe(() => { if (!this.polling && this.api.tenantKnown()) this.start(); });
    // Back in sight: what waits may have changed meanwhile.
    const shown = fromEvent(this.document, 'visibilitychange').pipe(filter(() => !this.document.hidden))
      .subscribe(() => { if (this.polling) this.start(); });
    inject(DestroyRef).onDestroy(() => { navigations.unsubscribe(); shown.unsubscribe(); this.polling?.unsubscribe(); });
  }

  /** Asks again now, for a page that just changed what waits. */
  refresh() { if (this.api.tenantKnown()) this.start(); }

  private start() {
    this.polling?.unsubscribe();
    this.polling = timer(0, ApprovalCount.Every).pipe(filter(() => !this.document.hidden), switchMap(() => this.api.waitingRequests())).subscribe({
      next: w => this.forYou.set(w.forYou),
      error: () => { this.forYou.set(0); this.polling = null; },
    });
  }
}

/** The menu id of the Approvals entry in {@link observabilityMenus}, where {@link ApprovalMenuBadge} shows the count. */
export const APPROVALS_MENU_ID = 'approvals';

/**
 * Shows {@link ApprovalCount} on the Approvals entry of juice-layout's side menu (by its menu id, {@link APPROVALS_MENU_ID}). The menu
 * draws it: beside the label, on the icon while folded, and on the header of the closed group it sits in.
 */
@Injectable({ providedIn: 'root' })
export class ApprovalMenuBadge {
  constructor() {
    const count = inject(ApprovalCount), badges = inject(MenuBadgeService);
    effect(() => {
      const n = count.forYou();
      badges.set(APPROVALS_MENU_ID, { count: n, hint: `${n} request${n === 1 ? '' : 's'} waiting for your approval` });
    });
    inject(DestroyRef).onDestroy(() => badges.clear(APPROVALS_MENU_ID));
  }
}
