import { Routes } from '@angular/router';
import { Menus } from '@juice-js/core';
import { APPROVALS_MENU_ID } from './core/approval-count';
import { normalizeBasePath } from './core/options';
import { PAGES } from './modules/core-pages';

/**
 * The pages, investigation first (Console requirements §4): services, traces, topology, logs and metrics; alerts and
 * rules after. Detail pages are deep links (§17) and carry the environment and time range in their query parameters.
 * The host mounts them at its `basePath` (see {@link provideJuiceObservability}) and adds its own guards.
 */
export const OBSERVABILITY_ROUTES: Routes = PAGES.map(p => p.route);

/**
 * The side menu entries (juice-layout), linking under `basePath`. The pages an investigation starts from sit at the top
 * with a `shortcut` for the shell's go-to keys; the rest are grouped (Explore, Automation, Configure).
 *
 * A group links to its first page and keeps its pages' own links (`replaceChildRouterLink: false`): juice-layout only
 * opens and closes the group on a click (it has no page of its own).
 * Group ids carry an `observability-` prefix, as the host's menu sits beside them.
 *
 * `alertNotifications` (phase 5, M48): the menu of the notification pages the host mounted, shown as the Alerts › Notifications group after
 * Alerts; the escalation selection page links to its Escalation Policies page.
 */
export function observabilityMenus(basePath = '/', order = 1, alertNotifications?: Menus): Menus {
  const base = normalizeBasePath(basePath);
  const link = (path: string) => (base === '/' ? `/${path}` : path ? `${base}/${path}` : base);
  const group = (id: string, label: string, icon: string, children: Menus): Menus[number] =>
    ({ id, label, icon, routerLink: children[0].routerLink, replaceChildRouterLink: false, children });
  const menus: Menus = [
    { id: 'overview', label: 'Overview', icon: 'dashboard', routerLink: link(''), shortcut: 'g o' },
    { id: 'services', label: 'Services', icon: 'hub', routerLink: link('services'), shortcut: 'g s' },
    { id: 'traces', label: 'Traces', icon: 'timeline', routerLink: link('traces'), shortcut: 'g t' },
    { id: 'logs', label: 'Logs', icon: 'notes', routerLink: link('logs'), shortcut: 'g l' },
    { id: 'metrics', label: 'Metrics', icon: 'show_chart', routerLink: link('metrics'), shortcut: 'g m' },
    { id: 'alerts', label: 'Alerts', icon: 'notifications', routerLink: link('alerts'), shortcut: 'g a' },
    ...(alertNotifications?.length ? [alertNotificationsGroup(alertNotifications)] : []),
    { id: 'incidents', label: 'Incidents', icon: 'emergency', routerLink: link('incidents'), shortcut: 'g i' },
    group('observability-explore', 'Explore', 'explore', [
      { id: 'topology', label: 'Dependencies', icon: 'account_tree', routerLink: link('topology') },
      { id: 'resources', label: 'Resources', icon: 'dns', routerLink: link('resources') },
      { id: 'slos', label: 'SLOs', icon: 'track_changes', routerLink: link('slos') },
      { id: 'timeline', label: 'Timeline', icon: 'history', routerLink: link('timeline') },
      { id: 'load-runs', label: 'Load runs', icon: 'speed', routerLink: link('load-runs') },
    ]),
    group('observability-automation', 'Automation', 'precision_manufacturing', [
      { id: 'agents', label: 'Agents', icon: 'smart_toy', routerLink: link('agents') },
      { id: APPROVALS_MENU_ID, label: 'Approvals', icon: 'approval', routerLink: link('approvals') },
    ]),
    group('observability-configure', 'Configure', 'tune', [
      { id: 'rules', label: 'Rules', icon: 'rule', routerLink: link('rules') },
      { id: 'silences', label: 'Silences', icon: 'notifications_paused', routerLink: link('silences') },
      { id: 'escalation', label: 'Escalation selection', icon: 'campaign', routerLink: link('escalation') },
    ]),
  ];
  return menus.map((menu, index) => ({ ...menu, order: order + index }));
}

/** The menu id of the Alerts › Notifications group {@link observabilityMenus} adds when the host mounts the notification pages. */
export const ALERT_NOTIFICATIONS_MENU_ID = 'observability-alert-notifications';

/**
 * The Alerts › Notifications group (phase 5, M48): the notification pages the host mounted (`@juice-js/notifications-admin`'s menu), in one
 * level, as juice-layout shows one. Their own groups are flattened in order; ids get a `notifications-admin-` prefix and lose their go-to
 * keys, which would clash with the platform's own (g o, g i, g s…).
 */
export function alertNotificationsGroup(items: Menus): Menus[number] {
  const flat = items.flatMap(item => (item.children?.length ? item.children : [item]));
  const children: Menus = flat.map(({ shortcut: _, children: __, ...item }) => ({ ...item, id: `notifications-admin-${item.id}` }));
  return { id: ALERT_NOTIFICATIONS_MENU_ID, label: 'Alert notifications', icon: 'campaign', routerLink: children[0].routerLink,
    replaceChildRouterLink: false, children };
}
