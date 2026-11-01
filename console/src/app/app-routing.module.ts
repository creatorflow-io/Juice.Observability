import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PageComponent, PageNotfoundComponent } from '@juice-js/layout';
import { MenuModule, Menus } from '@juice-js/core';
import { AuthGuard } from '@juice-js/auth';

function page(path: string, title: string, load: Routes[number]['loadComponent']): Routes[number] {
  return { path, title: `${title} · Juice Observability`, canActivate: [AuthGuard], loadComponent: load };
}

/**
 * Investigation first (Console requirements §4): services, traces, topology, logs and metrics; alerts and rules after.
 * Detail pages are deep links (§17) and carry the environment and time range in their query parameters.
 */
const routes: Routes = [
  {
    path: '',
    component: PageComponent,
    children: [
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
      { path: 'auth', loadChildren: () => import('@juice-js/auth').then(m => m.AuthRoutingModule) },
      { path: '**', component: PageNotfoundComponent },
    ],
  },
];

/** The side menu (juice-layout): investigation pages first. */
const menus: Menus = [
  { id: 'overview', label: 'Overview', icon: 'dashboard', routerLink: '/', order: 1 },
  { id: 'services', label: 'Services', icon: 'hub', routerLink: '/services', order: 2 },
  { id: 'traces', label: 'Traces', icon: 'timeline', routerLink: '/traces', order: 3 },
  { id: 'topology', label: 'Dependencies', icon: 'account_tree', routerLink: '/topology', order: 4 },
  { id: 'logs', label: 'Logs', icon: 'notes', routerLink: '/logs', order: 5 },
  { id: 'metrics', label: 'Metrics', icon: 'show_chart', routerLink: '/metrics', order: 6 },
  { id: 'resources', label: 'Resources', icon: 'dns', routerLink: '/resources', order: 7 },
  { id: 'alerts', label: 'Alerts', icon: 'notifications', routerLink: '/alerts', order: 8 },
  { id: 'rules', label: 'Rules', icon: 'rule', routerLink: '/rules', order: 9 },
];

@NgModule({
  imports: [RouterModule.forRoot(routes, { bindToComponentInputs: true }), MenuModule.forRoot(menus)],
  exports: [RouterModule, MenuModule],
})
export class AppRoutingModule { }
