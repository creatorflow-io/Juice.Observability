import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { OBSERVABILITY_OPTIONS, normalizeBasePath } from '../core/options';

function page(path: string, title: string, load: Routes[number]['loadComponent']): Routes[number] {
  return { path, title: `${title} · Juice Observability`, loadComponent: load };
}

/** The library module a page belongs to: it is shown only when that module is installed (`core` always is). */
export type PageModule = 'core' | 'slos' | 'agents';

/**
 * Every page of the library in the order {@link OBSERVABILITY_ROUTES} has always had, tagged with the module it belongs to. The order is the
 * old one so the exported route list is unchanged; each module's own list is the pages tagged with its name.
 */
export const PAGES: readonly { module: PageModule; route: Routes[number] }[] = [
  { module: 'core', route: page('', 'Overview', () => import('../pages/overview.page').then(m => m.OverviewPage)) },
  { module: 'core', route: page('services', 'Services', () => import('../pages/services.page').then(m => m.ServicesPage)) },
  { module: 'core', route: page('services/:id', 'Service', () => import('../pages/service.page').then(m => m.ServicePage)) },
  { module: 'core', route: page('traces', 'Traces', () => import('../pages/traces.page').then(m => m.TracesPage)) },
  { module: 'core', route: page('traces/:traceId', 'Trace', () => import('../pages/trace.page').then(m => m.TracePage)) },
  { module: 'core', route: page('topology', 'Dependencies', () => import('../pages/topology.page').then(m => m.TopologyPage)) },
  { module: 'core', route: page('logs', 'Logs', () => import('../pages/logs.page').then(m => m.LogsPage)) },
  { module: 'core', route: page('metrics', 'Metrics', () => import('../pages/metrics.page').then(m => m.MetricsPage)) },
  { module: 'core', route: page('resources', 'Resources', () => import('../pages/resources.page').then(m => m.ResourcesPage)) },
  { module: 'core', route: page('resources/:id', 'Resource', () => import('../pages/resource.page').then(m => m.ResourcePage)) },
  { module: 'core', route: page('alerts', 'Alerts', () => import('../pages/alerts.page').then(m => m.AlertsPage)) },
  { module: 'core', route: page('alerts/:id', 'Alert', () => import('../pages/alert.page').then(m => m.AlertPage)) },
  { module: 'core', route: page('rules', 'Rules', () => import('../pages/rules.page').then(m => m.RulesPage)) },
  { module: 'core', route: page('escalation', 'Escalation selection', () => import('../pages/notifications.page').then(m => m.NotificationsPage)) },
  // The old Webhooks page and any deep link under it open Escalation selection, under the host's base path.
  { module: 'core', route: { path: 'notifications', children: [{ path: '**', redirectTo: () => { const base = normalizeBasePath(inject(OBSERVABILITY_OPTIONS).basePath); return `${base === '/' ? '' : base}/escalation`; } }] } },
  { module: 'core', route: page('silences', 'Silences', () => import('../pages/silences.page').then(m => m.SilencesPage)) },
  { module: 'core', route: page('incidents', 'Incidents', () => import('../pages/incidents.page').then(m => m.IncidentsPage)) },
  { module: 'core', route: page('incidents/:id', 'Incident', () => import('../pages/incident.page').then(m => m.IncidentPage)) },
  { module: 'agents', route: page('agents', 'Agents', () => import('../pages/agents.page').then(m => m.AgentsPage)) },
  { module: 'agents', route: page('agents/:id', 'Agent', () => import('../pages/agent.page').then(m => m.AgentPage)) },
  { module: 'agents', route: page('approvals', 'Approvals', () => import('../pages/approvals.page').then(m => m.ApprovalsPage)) },
  { module: 'slos', route: page('slos', 'SLOs', () => import('../pages/slos.page').then(m => m.SlosPage)) },
  { module: 'slos', route: page('slos/:key', 'SLO', () => import('../pages/slo.page').then(m => m.SloPage)) },
  { module: 'core', route: page('timeline', 'Timeline', () => import('../pages/timeline.page').then(m => m.TimelinePage)) },
  { module: 'core', route: page('load-runs', 'Load runs', () => import('../pages/load-runs.page').then(m => m.LoadRunsPage)) },
  { module: 'core', route: page('integrations', 'Integrations', () => import('../pages/integrations.page').then(m => m.IntegrationsPage)) },
  { module: 'core', route: page('integrations/:id', 'Integration', () => import('../pages/integration.page').then(m => m.IntegrationPage)) },
];

export const pagesOf = (module: PageModule): Routes => PAGES.filter(p => p.module === module).map(p => p.route);
