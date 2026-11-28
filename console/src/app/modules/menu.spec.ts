import { Menus } from '@juice-js/core';
import { NOTIFICATIONS_ADMIN_PAGES, notificationsAdminMenus } from '@juice-js/notifications-admin';
import { APPROVALS_MENU_ID, BUILTIN_MODULES, CORE_CONSOLE_MODULES, FALLBACK_MODULES, ObservabilityConsoleModule, composeModules, menusFromManifests, observabilityMenus } from '@juice-js/observability';
import snapshot from './menu.snapshot.json';
import { PING_LIBRARY, PING_SERVER, StubPage, entry, flat, server } from './testing';

/** Plain data, as the snapshot is: functions and undefined members drop out. */
const plain = (menus: Menus) => JSON.parse(JSON.stringify(menus));
/** The old static menu is frozen: pages added since (Integrations) are in the manifests only, so the snapshot is compared without them. */
const legacy = (menus: Menus) => menus.map(m => (m.children ? { ...m, children: m.children.filter(c => c.id !== 'integrations') } : m));
const notifications = () => notificationsAdminMenus('/alerts/notifications', 1, NOTIFICATIONS_ADMIN_PAGES.embedded);
const build = (servers = BUILTIN_MODULES, libraries: readonly ObservabilityConsoleModule[] = CORE_CONSOLE_MODULES, extra: object = {}) =>
  menusFromManifests({ servers, libraries, ...extra });

describe('Console menu from manifests (core 2.0 M3, E7)', () => {
  it('menu_from_builtin_manifests_equals_base_snapshot', () => {
    // The snapshot was taken from observabilityMenus before manifests existed: ids, labels, icons, links, order, shortcuts, groups.
    expect(plain(build(BUILTIN_MODULES, CORE_CONSOLE_MODULES, { alertNotifications: notifications() }))).toEqual(snapshot.host);
    expect(plain(build())).toEqual(snapshot.plain);
    expect(plain(build(BUILTIN_MODULES, CORE_CONSOLE_MODULES, { basePath: '/observability/', order: 5 }))).toEqual(snapshot.mounted);
  });

  it('observabilityMenus_output_unchanged', () => {
    expect(plain(observabilityMenus('/', 1, notifications()))).toEqual(plain(legacy(snapshot.host as Menus)));
    expect(plain(observabilityMenus('/', 1))).toEqual(plain(legacy(snapshot.plain as Menus)));
    expect(plain(observabilityMenus('/observability/', 5))).toEqual(plain(legacy(snapshot.mounted as Menus)));
    // the approvals badge finds its entry by this id
    expect(APPROVALS_MENU_ID).toBe('approvals');
    expect(flat(build()).some(m => m.id === APPROVALS_MENU_ID)).toBe(true);
  });

  it('core_only_no_dead_links (menu)', () => {
    const menus = build(FALLBACK_MODULES);
    const ids = flat(menus).map(m => m.id);
    for (const gone of ['slos', 'agents', 'approvals', 'observability-automation']) expect(ids).not.toContain(gone);
    expect(ids).toContain('observability-explore');
    expect(ids).toContain('observability-configure');
    // every link is a route the core library defines
    const paths = CORE_CONSOLE_MODULES[0].routes.map(r => `/${r.path}`);
    for (const link of flat(menus).map(m => String(m.routerLink))) expect(paths).toContain(link);
  });

  it('test_module_menu_route_and_slots_appear (menu)', () => {
    const menus = build([...BUILTIN_MODULES, PING_SERVER], [...CORE_CONSOLE_MODULES, PING_LIBRARY]);
    const explore = menus.find(m => m.id === 'observability-explore')!;
    // the server's order (150) puts it after Load runs (140)
    expect(explore.children!.map(c => c.id)).toEqual(['topology', 'resources', 'slos', 'timeline', 'load-runs', 'ping']);
    const ping = explore.children!.find(c => c.id === 'ping')!;
    expect(ping).toMatchObject({ label: 'ping', routerLink: '/ping', icon: 'wifi' });
    // the library's go-to shortcut, by key
    expect(flat(menus).find(m => m.id === 'ping')!.shortcut).toBe('g p');
    // the server's order places a top-level entry among the core's
    const top = build([...BUILTIN_MODULES, server('ping', [entry('ping', 'ping', 35)])], [...CORE_CONSOLE_MODULES, PING_LIBRARY]);
    expect(top.map(m => m.id).slice(0, 5)).toEqual(['overview', 'services', 'traces', 'ping', 'logs']);
    expect(top.map(m => m.order)).toEqual(top.map((_, i) => i + 1));
  });

  it('server_module_without_library_shows_nothing', () => {
    const menus = build([...BUILTIN_MODULES, PING_SERVER, server('ghost', [entry('ghost', 'ghost', 1)])], [...CORE_CONSOLE_MODULES, PING_LIBRARY]);
    expect(flat(menus).some(m => m.id === 'ghost')).toBe(false);
    expect(plain(build([...BUILTIN_MODULES, server('ghost', [entry('ghost', 'ghost', 1)])]))).toEqual(snapshot.plain);
  });

  it('entry_route_missing_in_library_dropped_with_warning', () => {
    const warnings: string[] = [];
    const menus = build([...BUILTIN_MODULES, server('ping', [entry('ping', 'ping', 150, 'explore'), entry('lost', 'lost', 151, 'explore')], ['ping', 'lost'])],
      [...CORE_CONSOLE_MODULES, PING_LIBRARY], { warn: (m: string) => warnings.push(m) });
    expect(flat(menus).some(m => m.id === 'lost')).toBe(false);
    expect(flat(menus).some(m => m.id === 'ping')).toBe(true);
    expect(warnings).toEqual([expect.stringContaining("no route 'lost'")]);
  });

  it('library_route_colliding_with_core_skipped', () => {
    const rogue: ObservabilityConsoleModule = { name: 'rogue', routes: [
      { path: 'alerts/x', component: StubPage }, { path: 'auth', component: StubPage }, { path: '', component: StubPage }, { path: ':any', component: StubPage },
      { path: 'slos', component: StubPage }, { path: 'rogue', component: StubPage }] };
    const { modules, warnings } = composeModules([...CORE_CONSOLE_MODULES, rogue]);
    expect(modules.find(m => m.name === 'rogue')!.routes.map(r => r.path)).toEqual(['rogue']);
    expect(warnings.length).toBe(5);
    // and the menu drops an entry for a skipped route
    const menus = build([...BUILTIN_MODULES, server('rogue', [entry('rogue-alerts', 'alerts/x', 1), entry('rogue', 'rogue', 2)], ['alerts/x', 'rogue'])],
      [...CORE_CONSOLE_MODULES, rogue]);
    expect(flat(menus).map(m => m.id)).toContain('rogue');
    expect(flat(menus).map(m => m.id)).not.toContain('rogue-alerts');
    // a module taking a name twice, or another module's first segment
    const twin: ObservabilityConsoleModule = { name: 'twin', routes: [{ path: 'rogue/y', component: StubPage }] };
    expect(composeModules([...CORE_CONSOLE_MODULES, rogue, twin]).modules.find(m => m.name === 'twin')!.routes).toEqual([]);
  });

  it('renders manifest text as data: a label with markup stays a string', () => {
    const menus = build([...BUILTIN_MODULES, server('ping', [{ ...entry('ping', 'ping', 150), label: '<img src=x onerror=alert(1)>' }], ['ping'])],
      [...CORE_CONSOLE_MODULES, PING_LIBRARY]);
    expect(flat(menus).find(m => m.id === 'ping')!.label).toBe('<img src=x onerror=alert(1)>');
  });

  it('an operate-only entry shows only to an operator; an unknown group is dropped', () => {
    const operate = server('ping', [entry('ping', 'ping', 150, 'explore', { policy: 'platform.operate' }), entry('odd', 'ping', 151, 'mine')], ['ping']);
    const ids = (canOperate: boolean) => flat(build([...BUILTIN_MODULES, operate], [...CORE_CONSOLE_MODULES, PING_LIBRARY], { canOperate: () => canOperate })).map(m => m.id);
    expect(ids(true)).toContain('ping');
    expect(ids(false)).not.toContain('ping');
    expect(ids(true)).not.toContain('odd');
  });
});
