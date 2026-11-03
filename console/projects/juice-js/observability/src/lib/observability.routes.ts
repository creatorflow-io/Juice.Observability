import { Routes } from '@angular/router';
import { Menus } from '@juice-js/core';
import { normalizeBasePath } from './core/options';

function page(path: string, title: string, load: Routes[number]['loadComponent']): Routes[number] {
  return { path, title: `${title} · Juice Observability`, loadComponent: load };
}

/**
 * The pages, investigation first (Console requirements §4): services, traces, topology, logs and metrics; alerts and
 * rules after. Detail pages are deep links (§17) and carry the environment and time range in their query parameters.
 * The host mounts them at its `basePath` (see {@link provideJuiceObservability}) and adds its own guards.
 */
export const OBSERVABILITY_ROUTES: Routes = [
  page('', 'Overview', () => import('./pages/overview.page').then(m => m.OverviewPage)),
  page('services', 'Services', () => import('./pages/services.page').then(m => m.ServicesPage)),
  page('services/:id', 'Service', () => import('./pages/service.page').then(m => m.ServicePage)),
  page('traces', 'Traces', () => import('./pages/traces.page').then(m => m.TracesPage)),
  page('traces/:traceId', 'Trace', () => import('./pages/trace.page').then(m => m.TracePage)),
  page('topology', 'Dependencies', () => import('./pages/topology.page').then(m => m.TopologyPage)),
  page('logs', 'Logs', () => import('./pages/logs.page').then(m => m.LogsPage)),
  page('metrics', 'Metrics', () => import('./pages/metrics.page').then(m => m.MetricsPage)),
  page('resources', 'Resources', () => import('./pages/resources.page').then(m => m.ResourcesPage)),
  page('resources/:id', 'Resource', () => import('./pages/resource.page').then(m => m.ResourcePage)),
  page('alerts', 'Alerts', () => import('./pages/alerts.page').then(m => m.AlertsPage)),
  page('alerts/:id', 'Alert', () => import('./pages/alert.page').then(m => m.AlertPage)),
  page('rules', 'Rules', () => import('./pages/rules.page').then(m => m.RulesPage)),
];

/** The side menu entries (juice-layout), investigation pages first, linking under `basePath`. */
export function observabilityMenus(basePath = '/', order = 1): Menus {
  const base = normalizeBasePath(basePath);
  const link = (path: string) => (base === '/' ? `/${path}` : path ? `${base}/${path}` : base);
  return [
    { id: 'overview', label: 'Overview', icon: 'dashboard', routerLink: link('') },
    { id: 'services', label: 'Services', icon: 'hub', routerLink: link('services') },
    { id: 'traces', label: 'Traces', icon: 'timeline', routerLink: link('traces') },
    { id: 'topology', label: 'Dependencies', icon: 'account_tree', routerLink: link('topology') },
    { id: 'logs', label: 'Logs', icon: 'notes', routerLink: link('logs') },
    { id: 'metrics', label: 'Metrics', icon: 'show_chart', routerLink: link('metrics') },
    { id: 'resources', label: 'Resources', icon: 'dns', routerLink: link('resources') },
    { id: 'alerts', label: 'Alerts', icon: 'notifications', routerLink: link('alerts') },
    { id: 'rules', label: 'Rules', icon: 'rule', routerLink: link('rules') },
  ].map((menu, index) => ({ ...menu, order: order + index }));
}
