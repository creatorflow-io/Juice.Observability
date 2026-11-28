import { TestBed } from '@angular/core/testing';
import { AuthGuard } from '@juice-js/auth';
import { routes } from './app-routing.module';
import { Menus } from '@juice-js/core';
import { ALERT_NOTIFICATIONS_MENU_ID, OBSERVABILITY_OPTIONS, OBSERVABILITY_ROUTES, ObservabilityLinks, observabilityMenus } from '@juice-js/observability';
import { NOTIFICATIONS_ADMIN_PAGES, NOTIFICATIONS_ADMIN_ROUTES, notificationsAdminMenus } from '@juice-js/notifications-admin';

const flat = (menus: Menus): Menus => menus.flatMap(m => [m, ...flat(m.children ?? [])]);

describe('Alerts › Notifications (phase 5, M48)', () => {
  const notifications = notificationsAdminMenus('/alerts/notifications', 1, NOTIFICATIONS_ADMIN_PAGES.embedded);
  const menus = observabilityMenus('/', 1, notifications);

  it('adds one group after Alerts with the embedded pages in one level, with the escalation selection page in Configure', () => {
    const ids = menus.map(m => m.id);
    expect(ids.indexOf(ALERT_NOTIFICATIONS_MENU_ID)).toBe(ids.indexOf('alerts') + 1);
    const group = menus.find(m => m.id === ALERT_NOTIFICATIONS_MENU_ID)!;
    expect(group.children!.every(c => !c.children?.length)).toBe(true);
    expect(group.children!.map(c => c.id.replace('notifications-admin-', ''))).toEqual([...NOTIFICATIONS_ADMIN_PAGES.embedded]);
    expect(group.children!.every(c => String(c.routerLink).startsWith('/alerts/notifications'))).toBe(true);
    expect(group.routerLink).toBe(group.children![0].routerLink);
    expect(flat(menus).find(m => m.id === 'escalation')!.label).toBe('Escalation selection');
    expect(flat(menus).some(m => m.id === 'notifications')).toBe(false);
    expect(observabilityMenus('/').some(m => m.id === ALERT_NOTIFICATIONS_MENU_ID)).toBe(false);
  });

  it('keeps every menu id and go-to key unique across both menus', () => {
    const all = flat(menus);
    const ids = all.map(m => m.id);
    expect(new Set(ids).size).toBe(ids.length);
    const keys = all.map(m => m.shortcut).filter(Boolean);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('mounts the notification pages before the observability routes (alerts/:id would take them), each behind sign-in', () => {
    const children = routes[0].children!;
    const paths = children.map(r => r.path);
    expect(paths.indexOf('alerts/notifications')).toBeLessThan(paths.indexOf('alerts/:id'));
    const mounted = children.find(r => r.path === 'alerts/notifications')!.children!;
    expect(mounted.length).toBe(NOTIFICATIONS_ADMIN_ROUTES.length);
    expect(mounted.every(r => r.canActivate?.[0] === AuthGuard)).toBe(true);
  });
});

describe('Alerts › links to the notifications admin (M50)', () => {
  const links = (notificationsLink: string | null) => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: OBSERVABILITY_OPTIONS, useValue: { apiBase: '', basePath: '/', notificationsLink } }] });
    return TestBed.inject(ObservabilityLinks);
  };

  it('links the correlation of an alert under the mounted pages, `go` picking the tenant', () => {
    expect(links('/alerts/notifications').correlation('alert:0193')).toEqual({ route: ['/alerts/notifications', 'go', 'correlations', 'observability', 'alert:0193'] });
  });

  it('keeps a configured absolute console base as written: mixed-case host, port and trailing slashes', () => {
    expect(links('https://Console.Example.COM:8443/Admin//').correlation('alert:0193'))
      .toEqual({ href: 'https://Console.Example.COM:8443/Admin/go/correlations/observability/alert%3A0193' });
  });

  it('gives no link without a mounted base or a key', () => {
    expect(links(null).correlation('alert:1')).toBeNull();
    expect(links('/n').correlation(undefined)).toBeNull();
  });

  it('keeps the old webhooks page address working by redirecting to the escalation selection', () => {
    const redirect = OBSERVABILITY_ROUTES.find(r => r.path === 'notifications')!;
    expect(redirect.redirectTo).toBe('escalation');
    expect(OBSERVABILITY_ROUTES.some(r => r.path === 'escalation')).toBe(true);
  });
});
