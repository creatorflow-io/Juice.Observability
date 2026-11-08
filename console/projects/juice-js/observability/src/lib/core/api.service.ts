import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EMPTY, Observable, expand, map, reduce, shareReplay, switchMap } from 'rxjs';
import { OBSERVABILITY_OPTIONS } from './options';
import {
  AlertDetail, AlertNotifications, Alert, AllowlistEntry, Application, Channel, ChannelTestResult, Delivery, EscalationPolicy, IncidentDetail, IncidentEvent, IncidentSummary, Silence, DependencyEvidence, DependencyStatsPage, DeploymentEnvironment, LoadRun, LoadRunResults, Related, Timeline, Health, LogRecord, MetricInfo, Operation,
  Page, ResourceDetail, ResourceSummary, Rule, SearchHit, Series, Capacity, ServiceStats, Tenant, TelemetryPage, Topology, Trace,
  TraceSummary,
} from './models';

export type Query = Record<string, string | number | boolean | string[] | null | undefined>;

/**
 * The Console's only backend: the Juice API (Console requirements §3). One deployment serves one tenant, the one the
 * signed-in user's token names.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(OBSERVABILITY_OPTIONS).apiBase}/api/v1`;

  /** The caller's tenant, fetched once. */
  readonly tenant$: Observable<Tenant> = this.http.get<Page<Tenant>>(`${this.base}/tenants`).pipe(
    map(page => {
      if (page.items.length === 0) throw new Error('No tenant is authorized for this user.');
      return page.items[0];
    }),
    shareReplay(1),
  );

  private get<T>(path: string, query: Query = {}): Observable<T> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value === null || value === undefined || value === '') continue;
      for (const item of Array.isArray(value) ? value : [value]) params = params.append(key, String(item));
    }
    return this.tenant$.pipe(switchMap(t => this.http.get<T>(`${this.base}/tenants/${t.id}${path}`, { params })));
  }

  /** A change. `Idempotency-Key` on what can be retried, so a repeated click or a lost response doesn't do it twice. */
  private send<T>(method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown, headers: Record<string, string> = {}): Observable<T> {
    return this.tenant$.pipe(switchMap(t => this.http.request<T>(method, `${this.base}/tenants/${t.id}${path}`, { body, headers })));
  }
  private once<T>(path: string, body: unknown): Observable<T> { return this.send<T>('POST', path, body, { 'Idempotency-Key': crypto.randomUUID() }); }

  /** Every page of a cursor-paged list (bounded: at most `maxPages` requests). */
  private all<T>(path: string, query: Query = {}, maxPages = 25): Observable<Page<T>> {
    let pages = 0;
    return this.get<Page<T>>(path, { ...query, limit: 200 }).pipe(
      expand(page => page.nextCursor && ++pages < maxPages ? this.get<Page<T>>(path, { ...query, limit: 200, cursor: page.nextCursor }) : EMPTY),
      reduce((all, page) => ({ items: [...all.items, ...page.items], nextCursor: page.nextCursor }), { items: [] as T[], nextCursor: null as string | null }),
    );
  }

  environments() { return this.get<Page<DeploymentEnvironment>>('/environments'); }
  applications() { return this.get<Page<Application>>('/applications', { limit: 200 }); }
  resources(type?: string) { return this.all<ResourceSummary>('/resources', { type }); }
  resource(id: string) { return this.get<ResourceDetail>(`/resources/${id}`); }
  health(query: Query) { return this.all<Health>('/health', query); }
  topology(environment?: string | null) { return this.get<Topology>('/topology', { environment }); }

  rules() { return this.get<Page<Rule>>('/rules', { limit: 200 }); }
  createRule(definition: unknown) { return this.send<Rule>('POST', '/rules', definition); }
  /** With the rule's etag, so a change made since it was read isn't overwritten. */
  updateRule(key: string, definition: unknown, etag: string) { return this.send<Rule>('PUT', `/rules/${key}`, definition, { 'If-Match': `"${etag}"` }); }

  channels() { return this.get<Page<Channel>>('/webhooks'); }
  createChannel(body: unknown) { return this.send<Channel>('POST', '/webhooks', body); }
  updateChannel(id: string, body: unknown) { return this.send<Channel>('PUT', `/webhooks/${id}`, body); }
  deliveries(id: string, query: Query = {}) { return this.get<Page<Delivery>>(`/webhooks/${id}/deliveries`, { limit: 50, ...query }); }
  testChannel(id: string) { return this.send<ChannelTestResult>('POST', `/webhooks/${id}/test`); }
  retryDelivery(channelId: string, deliveryId: string) { return this.send<Delivery>('POST', `/webhooks/${channelId}/deliveries/${deliveryId}/retry`); }
  allowlist() { return this.get<Page<AllowlistEntry>>('/webhooks/allowlist'); }
  putAllowlist(destination: string, ports: number[]) { return this.send<AllowlistEntry>('PUT', '/webhooks/allowlist', { destination, ports }); }
  removeAllowlist(destination: string) { return this.send<void>('DELETE', `/webhooks/allowlist?destination=${encodeURIComponent(destination)}`); }

  escalationPolicies() { return this.get<Page<EscalationPolicy>>('/escalation-policies'); }
  createEscalationPolicy(body: unknown) { return this.send<EscalationPolicy>('POST', '/escalation-policies', body); }
  updateEscalationPolicy(key: string, body: unknown) { return this.send<EscalationPolicy>('PUT', `/escalation-policies/${key}`, body); }

  silences(query: Query = {}) { return this.get<Page<Silence>>('/silences', { limit: 200, ...query }); }
  createSilence(body: unknown) { return this.once<Silence>('/silences', body); }
  cancelSilence(id: string) { return this.send<void>('DELETE', `/silences/${id}`); }

  incidents(query: Query = {}) { return this.get<Page<IncidentSummary>>('/incidents', { limit: 100, ...query }); }
  incident(id: string) { return this.get<IncidentDetail>(`/incidents/${id}`); }
  createIncident(body: unknown) { return this.once<IncidentSummary>('/incidents', body); }
  moveIncident(id: string, to: string, comment: string) { return this.once<IncidentSummary>(`/incidents/${id}/transitions`, { to, comment }); }
  addIncidentNote(id: string, text: string) { return this.once<IncidentEvent>(`/incidents/${id}/notes`, { text }); }
  removeIncidentAlert(id: string, alertId: string) { return this.send<void>('DELETE', `/incidents/${id}/alerts/${alertId}`); }
  alertNotifications(id: string) { return this.get<AlertNotifications>(`/alerts/${id}/notifications`); }

  /** One page of alerts, newest first; pass the previous page's `nextCursor` as `cursor` for the next. */
  alerts(query: Query) { return this.get<Page<Alert>>('/alerts', { limit: 100, ...query }); }
  /** Every alert matching the query (bounded, like the other complete lists). */
  allAlerts(query: Query) { return this.all<Alert>('/alerts', query); }
  alert(id: string) { return this.get<AlertDetail>(`/alerts/${id}`); }
  acknowledge(id: string, comment: string): Observable<Alert> {
    return this.tenant$.pipe(switchMap(t => this.http.post<Alert>(`${this.base}/tenants/${t.id}/alerts/${id}/acknowledgement`,
      { comment }, { headers: { 'Idempotency-Key': crypto.randomUUID() } })));
  }

  traces(query: Query) { return this.get<TelemetryPage<TraceSummary>>('/telemetry/traces', query); }
  trace(traceId: string) { return this.get<Trace>(`/telemetry/traces/${traceId}`); }
  logs(query: Query) { return this.get<TelemetryPage<LogRecord>>('/telemetry/logs', query); }
  operations(query: Query) { return this.get<TelemetryPage<Operation>>('/telemetry/operations', query); }
  metrics(query: Query) { return this.get<TelemetryPage<MetricInfo>>('/telemetry/metrics', query); }
  series(query: Query) { return this.get<Series>('/telemetry/metrics/series', query); }
  serviceStats(query: Query) { return this.get<ServiceStats>('/telemetry/service-stats', query); }
  capacity(query: Query) { return this.get<Capacity>('/telemetry/capacity', query); }
  timeline(query: Query) { return this.get<Timeline>('/timeline', query); }
  relatedToAlert(id: string) { return this.get<Related>(`/alerts/${id}/related`); }
  loadRuns(query: Query) { return this.get<Page<LoadRun>>('/load-runs', query); }
  loadRunResults(id: string) { return this.get<LoadRunResults>(`/load-runs/${id}/results`); }
  dependencyStats(query: Query) { return this.get<DependencyStatsPage>('/telemetry/dependencies', query); }
  dependency(id: string, query: Query) { return this.get<DependencyEvidence>(`/telemetry/dependencies/${id}`, query); }
  search(q: string) { return this.get<TelemetryPage<SearchHit>>('/search', { q }); }
}
