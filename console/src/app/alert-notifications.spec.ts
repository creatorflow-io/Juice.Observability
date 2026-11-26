import { AuthGuard } from '@juice-js/auth';
import { routes } from './app-routing.module';
import { Menus } from '@juice-js/core';
import { ALERT_NOTIFICATIONS_MENU_ID, observabilityMenus } from '@juice-js/observability';
import { NOTIFICATIONS_ADMIN_PAGES, NOTIFICATIONS_ADMIN_ROUTES, notificationsAdminMenus } from '@juice-js/notifications-admin';

const flat = (menus: Menus): Menus => menus.flatMap(m => [m, ...flat(m.children ?? [])]);

describe('Alerts › Notifications (phase 5, M48)', () => {
  const notifications = notificationsAdminMenus('/alerts/notifications', 1, NOTIFICATIONS_ADMIN_PAGES.embedded);
  const menus = observabilityMenus('/', 1, notifications);

  it('adds one group after Alerts with the embedded pages in one level, and labels the webhooks page Webhooks', () => {
    const ids = menus.map(m => m.id);
    expect(ids.indexOf(ALERT_NOTIFICATIONS_MENU_ID)).toBe(ids.indexOf('alerts') + 1);
    const group = menus.find(m => m.id === ALERT_NOTIFICATIONS_MENU_ID)!;
    expect(group.children!.every(c => !c.children?.length)).toBe(true);
    expect(group.children!.map(c => c.id.replace('notifications-admin-', ''))).toEqual([...NOTIFICATIONS_ADMIN_PAGES.embedded]);
    expect(group.children!.every(c => String(c.routerLink).startsWith('/alerts/notifications'))).toBe(true);
    expect(group.routerLink).toBe(group.children![0].routerLink);
    expect(flat(menus).find(m => m.id === 'notifications')!.label).toBe('Webhooks');
    expect(flat(observabilityMenus('/')).find(m => m.id === 'notifications')!.label).toBe('Notifications');
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
