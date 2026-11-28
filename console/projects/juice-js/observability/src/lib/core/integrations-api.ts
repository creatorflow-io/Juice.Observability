import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, switchMap } from 'rxjs';
import { ApiService, idempotencyKey } from './api.service';
import { Page } from './models';
import { OBSERVABILITY_OPTIONS } from './options';

/** The integration instances API (integration core I1): its own client over the caller's tenant, so ApiService and the models stay as they are. */
export type FieldKind = 'string' | 'integer' | 'number' | 'boolean' | 'enum' | 'stringList' | 'duration';

export interface FieldDto {
  name: string; kind: FieldKind; label: string; required: boolean;
  maxLength?: number | null; min?: number | null; max?: number | null; allowed?: string[] | null; pattern?: string | null; maxItems?: number | null;
}
export interface SchemaDto { version: number; fields: FieldDto[]; }
export interface SecretSpecDto { name: string; label: string; required: boolean; }

export interface IntegrationPluginDto {
  key: string; displayName: string; version: string; capabilities: string[]; config: SchemaDto; scope: SchemaDto; secrets: SecretSpecDto[];
  requiredPermissions: string[]; defaultIntervalSeconds: number; minIntervalSeconds: number;
}

export type IntegrationState = 'Configured' | 'Validated' | 'Enabled' | 'Degraded' | 'Disabled' | 'Failed';
export type Tri = 'ok' | 'failing' | 'unknown';

export interface IntegrationHealthDto {
  runtime: string; connectivity: string; authentication: string; collection: Tri; freshness: 'ok' | 'stale' | 'unknown'; freshnessSeconds: number | null;
  lastFailure: string | null; lastFailureDetail: string | null; consecutiveFailures: number;
}
export interface ValidationCheckDto { name: string; passed: boolean; detail: string | null; }
export interface LastValidationDto { passed: boolean; checks: ValidationCheckDto[]; at: string; }

export interface IntegrationInstanceDto {
  id: string; pluginKey: string; pluginName: string | null; name: string; state: IntegrationState; stateReason: string | null; intervalSeconds: number;
  configVersion: number; capabilities: string[]; health: IntegrationHealthDto;
  lastSuccessAt: string | null; lastRunAt: string | null; nextRunAt: string | null;
  config: Record<string, unknown> | null; scope: Record<string, unknown> | null; secretRefs: Record<string, string> | null; lastValidation: LastValidationDto | null;
  createdBy: string; createdAt: string; updatedBy: string; updatedAt: string; etag: string;
}

export interface IntegrationInstanceRequest {
  pluginKey?: string; name: string; config: Record<string, unknown>; scope?: Record<string, unknown>; secretRefs?: Record<string, string>; intervalSeconds?: number;
}
export interface ValidateResponse { instance: IntegrationInstanceDto; report: { passed: boolean; checks: ValidationCheckDto[] }; }

@Injectable({ providedIn: 'root' })
export class IntegrationsApi {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(ApiService).tenant$;
  private readonly base = `${inject(OBSERVABILITY_OPTIONS).apiBase}/api/v1`;

  private url(tenantId: string, path: string) { return `${this.base}/tenants/${tenantId}/integrations${path}`; }
  private ifMatch(etag: string) { return { 'If-Match': etag.startsWith('"') ? etag : `"${etag}"` }; }

  plugins(): Observable<IntegrationPluginDto[]> {
    return this.tenant.pipe(switchMap(t => this.http.get<IntegrationPluginDto[]>(this.url(t.id, '/plugins'))));
  }
  instances(query: { state?: string; plugin?: string; limit?: number; cursor?: string | null } = {}): Observable<Page<IntegrationInstanceDto>> {
    let params = new HttpParams();
    for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== '') params = params.set(k, String(v));
    return this.tenant.pipe(switchMap(t => this.http.get<Page<IntegrationInstanceDto>>(this.url(t.id, '/instances'), { params })));
  }
  instance(id: string): Observable<IntegrationInstanceDto> {
    return this.tenant.pipe(switchMap(t => this.http.get<IntegrationInstanceDto>(this.url(t.id, `/instances/${id}`))));
  }
  create(body: IntegrationInstanceRequest): Observable<IntegrationInstanceDto> {
    return this.tenant.pipe(switchMap(t => this.http.post<IntegrationInstanceDto>(this.url(t.id, '/instances'), body, { headers: { 'Idempotency-Key': idempotencyKey() } })));
  }
  /** With the instance's etag (If-Match): 412 when it changed since it was read. */
  update(id: string, body: IntegrationInstanceRequest, etag: string): Observable<IntegrationInstanceDto> {
    return this.tenant.pipe(switchMap(t => this.http.put<IntegrationInstanceDto>(this.url(t.id, `/instances/${id}`), body, { headers: this.ifMatch(etag) })));
  }
  delete(id: string, etag: string): Observable<void> {
    return this.tenant.pipe(switchMap(t => this.http.delete<void>(this.url(t.id, `/instances/${id}`), { headers: this.ifMatch(etag) })));
  }
  validate(id: string): Observable<ValidateResponse> {
    return this.tenant.pipe(switchMap(t => this.http.post<ValidateResponse>(this.url(t.id, `/instances/${id}/validate`), {})));
  }
  enable(id: string): Observable<IntegrationInstanceDto> {
    return this.tenant.pipe(switchMap(t => this.http.post<IntegrationInstanceDto>(this.url(t.id, `/instances/${id}/enable`), {})));
  }
  disable(id: string): Observable<IntegrationInstanceDto> {
    return this.tenant.pipe(switchMap(t => this.http.post<IntegrationInstanceDto>(this.url(t.id, `/instances/${id}/disable`), {})));
  }
}
