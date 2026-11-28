import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Router, provideRouter } from '@angular/router';
import { MenuItem, MenuService, TenantService } from '@juice-js/core';
import {
  BUILTIN_MODULES, CORE_CONSOLE_MODULES, FALLBACK_MODULES, InstalledModules, ModuleMenuService, ObservabilityConsoleModule, installed, menusFromManifests,
  evidenceLink, observabilityModuleRoutes, provideJuiceObservability, provideObservabilityModules,
} from '@juice-js/observability';
import { NotFoundPage, PING_LIBRARY, PING_SERVER, StubPage, flat, settle, stubbed } from './testing';

const MODULES_URL = '/api/v1/modules';
const WITH_PING = { items: [...BUILTIN_MODULES, PING_SERVER] };

function setup(signedIn: boolean, withOption = true) {
  const state = { signedIn, guardPasses: signedIn, guardCalls: 0 };
  const libraries = stubbed([...CORE_CONSOLE_MODULES, PING_LIBRARY]);
  const authGuard: CanActivateFn = () => { state.guardCalls++; return state.guardPasses ? true : inject(Router).parseUrl('/auth/signin'); };
  TestBed.configureTestingModule({
    providers: [
      // The host's shape: the libraries' routes each behind sign-in, the sign-in page and the not-found page after them.
      provideRouter([{ path: '', children: [
        ...observabilityModuleRoutes(libraries).map(route => ({ ...route, canActivate: [authGuard] })),
        { path: 'auth/signin', component: StubPage },
        { path: '**', component: NotFoundPage },
      ] }]),
      provideHttpClient(), provideHttpClientTesting(),
      provideJuiceObservability({ approvalCount: false }),
      provideObservabilityModules(libraries, withOption ? { signedIn: () => state.signedIn } : {}),
      { provide: TenantService, useValue: { getTenantIdentifier: async () => null } },
    ],
  });
  const router = TestBed.inject(Router);
  const http = TestBed.inject(HttpTestingController);
  const leaf = () => { let r = router.routerState.snapshot.root; while (r.firstChild) r = r.firstChild; return r.routeConfig?.path; };
  /** Navigates; when the guard asks for /modules, answers it. */
  async function go(url: string, answer?: { status: number; body?: unknown } | object) {
    const navigation = router.navigateByUrl(url);
    await settle(2);
    if (answer) {
      const request = http.expectOne(MODULES_URL);
      if ('status' in answer) request.flush(answer.body ?? {}, { status: answer.status as number, statusText: 'error' });
      else request.flush(answer);
    }
    await navigation;
    await settle(1);
    return leaf();
  }
  return { state, router, http, leaf, go, libraries };
}

describe('Module routes and the installed modules (core 2.0 M3, E7)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('test_module_menu_route_and_slots_appear (routes)', async () => {
    const { go } = setup(true);
    expect(await go('/ping', WITH_PING)).toBe('ping');
    expect(await go('/ping/42')).toBe('ping/:id');
    expect(await go('/slos')).toBe('slos');
    expect(await go('/services')).toBe('services');
  });

  it('library_without_server_module_not_matched', async () => {
    const { go, http } = setup(true);
    // the build holds ping's library, but the server doesn't list the module
    expect(await go('/ping', { items: BUILTIN_MODULES })).toBe('**');
    http.expectNone(MODULES_URL);
    expect(await go('/ping/42')).toBe('**');
  });

  it('core_only_no_dead_links (routes)', async () => {
    const { go, libraries } = setup(true);
    // /modules says [core]: the core's pages match, slos and agents don't
    expect(await go('/slos', { items: FALLBACK_MODULES })).toBe('**');
    for (const path of ['/agents', '/approvals', '/ping']) expect(await go(path)).toBe('**');
    const menus = menusFromManifests({ servers: FALLBACK_MODULES, libraries });
    const links = flat(menus).map(m => String(m.routerLink));
    expect(links.length).toBeGreaterThan(8);
    for (const link of links) expect([link, await go(link)]).not.toEqual([link, '**']);
    // an evidence link follows the same rule: a route of an installed module only
    const patterns = FALLBACK_MODULES.flatMap(m => m.console!.routes.map(r => r.path));
    expect(evidenceLink({ label: 'a', link: { route: 'alerts/abc' } }, patterns)).not.toBeNull();
    expect(evidenceLink({ label: 'a', link: { route: 'slos/api' } }, patterns)).toBeNull();
  });

  it('modules_failure_falls_back_to_core_retries_next_navigation', async () => {
    const { go, http } = setup(true);
    const modules = TestBed.inject(InstalledModules);
    expect(await go('/ping', { status: 500 })).toBe('**');
    expect(modules.state()).toBe('failed');
    expect([...modules.names()]).toEqual(['core']);
    // the same navigation asked once; the core's pages still work
    expect(await go('/services')).toBe('services');
    // the next navigation asks again, and now answers
    const next = TestBed.inject(Router).navigateByUrl('/logs');
    await settle(2);
    http.expectOne(MODULES_URL).flush(WITH_PING);
    await next;
    await settle(2);
    expect(modules.state()).toBe('loaded');
    expect(await go('/ping')).toBe('ping');
  });

  it('concurrent_guards_share_one_request', async () => {
    setup(true);
    const http = TestBed.inject(HttpTestingController);
    const run = (name: string) => TestBed.runInInjectionContext(() => installed(name)({} as never, [] as never, {} as never)) as Promise<boolean>;
    const all = Promise.all([run('ping'), run('ping'), run('slos'), run('ghost')]);
    await settle(2);
    http.expectOne(MODULES_URL).flush(WITH_PING);
    expect(await all).toEqual([true, true, true, false]);
  });

  it('signed_out_deep_link_to_module_route_goes_to_signin', async () => {
    const { go, http, state, router } = setup(false);
    expect(await go('/ping')).toBe('auth/signin');
    expect(router.url).toBe('/auth/signin');
    expect(state.guardCalls).toBe(1);
    // nothing was asked of the server without a session
    http.expectNone(MODULES_URL);
  });

  it('signed_out_deep_link_without_signedIn_option_defers_to_signin', async () => {
    // No  option: no page has fetched the tenant, so there is no session as far as the library knows
    const { go, http, state, router } = setup(false, false);
    expect(await go('/ping')).toBe('auth/signin');
    expect(router.url).toBe('/auth/signin');
    expect(state.guardCalls).toBe(1);
    http.expectNone(MODULES_URL);
    expect(TestBed.inject(InstalledModules).state()).toBe('loading');
  });

  it('a_401_from_modules_defers_to_signin_and_is_not_a_failure', async () => {
    const { go, state, router } = setup(true);
    const modules = TestBed.inject(InstalledModules);
    // the session looks valid to the library but the server says 401; the host's guard decides
    state.guardPasses = false;
    expect(await go('/ping', { status: 401 })).toBe('auth/signin');
    expect(router.url).toBe('/auth/signin');
    expect(modules.state()).toBe('loading');
    expect(modules.names().has('slos')).toBe(true); // still the built-in menu, not the core-only fallback
  });

  it('menu_updates_when_modules_arrive_after_shell_built_on_unguarded_route', async () => {
    const { router, http } = setup(true);
    const service = TestBed.inject(MenuService);
    expect(service).toBeInstanceOf(ModuleMenuService);
    // the shell is built on a page that needs no sign-in and asks nothing
    await router.navigateByUrl('/auth/signin');
    const seen: MenuItem[][] = [];
    service.getMenu().subscribe(items => seen.push(items));
    await settle();
    const ids = (items: MenuItem[]) => flat(items).map(m => m.id);
    expect(seen.length).toBeGreaterThan(0);
    expect(ids(seen[seen.length - 1])).toContain('slos');
    expect(ids(seen[seen.length - 1])).not.toContain('ping');
    // /modules arrives
    void TestBed.inject(InstalledModules).load();
    http.expectOne(MODULES_URL).flush(WITH_PING);
    await settle();
    expect(ids(seen[seen.length - 1])).toContain('ping');
    // and when it is [core], the entries of the other modules go
    expect(ids(seen[seen.length - 1])).toContain('slos');
  });
});
