import { Injectable, InjectionToken, Injector, computed, effect, inject, runInInjectionContext, signal, untracked } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { Menus } from '@juice-js/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ModulesResponse, ServerModule } from '../core/models';
import { ObservabilityConsoleModule } from './console-module';
import builtin from './builtin-manifests.json';

/** The libraries compiled into this Console build ({@link provideObservabilityModules}). */
export const OBSERVABILITY_MODULES = new InjectionToken<readonly ObservabilityConsoleModule[]>('OBSERVABILITY_MODULES');

/** What the host tells the modules' providers. */
export interface ObservabilityModulesOptions {
  /** Whether a session exists. Without one a module route matches and the host's guard sends the user to sign in; `/modules` isn't asked. */
  signedIn?: () => boolean;
  /** The menu of the notification pages the host mounted: shown as the Alerts › Notifications group (see `observabilityMenus`). */
  alertNotifications?: Menus;
  /** The first menu `order` (the host's own entries come before or after). Default 1. */
  menuOrder?: number;
}

export const OBSERVABILITY_MODULES_OPTIONS = new InjectionToken<ObservabilityModulesOptions>('OBSERVABILITY_MODULES_OPTIONS', { providedIn: 'root', factory: () => ({}) });

/** The internal modules' manifests as the server serves them (a .NET test pins this file to the served answer): what shows before `/modules` arrives. */
export const BUILTIN_MODULES: readonly ServerModule[] = (builtin as unknown as { items: ServerModule[] }).items;

/** When `/modules` fails: the core alone, with no module entries. */
export const FALLBACK_MODULES: readonly ServerModule[] = BUILTIN_MODULES.filter(m => m.name === 'core');

/**
 * The modules the server says the deployment runs, asked once and shared. Until the answer arrives the menu is the one the Console always had
 * (the core's own modules). When the request fails, only `core` is installed, and the next navigation asks again.
 */
@Injectable({ providedIn: 'root' })
export class InstalledModules {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly options = inject(OBSERVABILITY_MODULES_OPTIONS);
  private readonly injector = inject(Injector);

  private readonly _state = signal<'loading' | 'loaded' | 'failed'>('loading');
  private readonly _modules = signal<readonly ServerModule[]>(BUILTIN_MODULES);
  private inflight: Promise<void> | null = null;
  private failedAt: number | null = null;

  readonly state = this._state.asReadonly();
  /** The modules as listed, in the server's order. */
  readonly modules = this._modules.asReadonly();
  readonly names = computed<ReadonlySet<string>>(() => new Set(this._modules().map(m => m.name)));

  constructor() {
    // A tenant is known once a page has called the API: the user is signed in, so the answer is worth asking for.
    effect(() => { if (this.api.tenantKnown() && untracked(() => this._state()) === 'loading') void untracked(() => this.load()); });
    // A failed answer is asked for again on the next navigation (not the one that saw it fail).
    this.router.events.subscribe(e => {
      if (e instanceof NavigationEnd && this._state() === 'failed' && e.id !== this.failedAt && this.signedIn()) void this.load();
    });
  }

  /** Whether a session exists, as the host says (its function may `inject`). Without one given, whether a page has fetched the tenant. */
  signedIn(): boolean {
    const answer = this.options.signedIn;
    return answer ? runInInjectionContext(this.injector, answer) : this.api.tenantKnown();
  }

  /** Asks the server once: concurrent callers share the request. After a failure the next navigation asks again, never the same one. */
  load(): Promise<void> {
    if (this.inflight) return this.inflight;
    if (this._state() === 'loaded') return Promise.resolve();
    const navigation = this.router.currentNavigation()?.id ?? null;
    if (this._state() === 'failed' && navigation !== null && navigation === this.failedAt) return Promise.resolve();
    this.inflight = firstValueFrom(this.api.modules())
      .then((answer: ModulesResponse) => {
        this._modules.set(Array.isArray(answer?.items) ? answer.items : FALLBACK_MODULES);
        this._state.set('loaded');
        this.failedAt = null;
      })
      .catch((error: { status?: number }) => {
        // No session (401): not a failure of the server. The host's guard sends the user to sign in; ask again once there is one.
        if (error?.status === 401) return;
        this._modules.set(FALLBACK_MODULES);
        this._state.set('failed');
        this.failedAt = navigation;
      })
      .finally(() => { this.inflight = null; });
    return this.inflight;
  }
}
