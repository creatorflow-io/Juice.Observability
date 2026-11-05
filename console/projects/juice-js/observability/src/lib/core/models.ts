// The Juice API's contracts, as the Console uses them (docs: Console requirements §3). Domain concepts only: no
// storage names.

export interface Page<T> { items: T[]; nextCursor: string | null; }
export interface TelemetryPage<T> { items: T[]; truncated: boolean; nextCursor?: string | null; }

export interface Tenant { id: string; key: string; displayName: string; }
export interface DeploymentEnvironment { id: string; key: string; type: string; firstSeen: string; lastSeen: string; }
export interface Application { id: string; key: string; displayName: string; services: number; firstSeen: string; lastSeen: string; }
export interface ResourceSummary { id: string; type: string; key: string; displayName: string; firstSeen: string; lastSeen: string; }

export type HealthState = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Critical' | 'Offline' | 'Unknown';

export interface Health {
  subjectType: string; subjectId: string; subjectName: string; environment: string | null; state: HealthState;
  reasons: string[]; policyVersion: string; evaluatedAt: string; evidenceFrom: string; evidenceTo: string;
  lastEvidenceAt: string | null; since: string; stale: boolean;
}

export interface Instance {
  id: string; environment: string; instanceKey: string; keySource: string; serviceVersion: string | null; state: string;
  hostResourceId: string | null; firstSeen: string; lastSeen: string; health: HealthState | null;
}

export interface IntegrationLink { integration: string; title: string; url: string; }

export interface ResourceDetail {
  id: string; type: string; key: string; displayName: string; firstSeen: string; lastSeen: string;
  namespace: string | null; name: string | null; applicationId: string | null; attributes: Record<string, string>;
  instances: Instance[]; instancesTruncated: boolean; health: Health[]; links: IntegrationLink[];
}

export interface TopologyNode { id: string; type: string; displayName: string; health: HealthState | null; }
export interface TopologyEdge {
  id: string; source: string; target: string; relation: string; confidence: string; evidenceSource: string;
  spanCount: number; firstSeen: string; lastSeen: string;
}
export interface Topology { environment: string | null; nodes: TopologyNode[]; edges: TopologyEdge[]; truncated: boolean; }

export interface Alert {
  id: string; rule: string; subjectKey: string; subjectType: string; subjectId: string | null; environment: string | null;
  severity: string; state: string; breachStartedAt: string | null; firedAt: string | null; resolvedAt: string | null;
  lastEvaluatedAt: string; lastValue: number | null; noData: boolean; acknowledgedAt: string | null; acknowledgedBy: string | null;
}
export interface AlertTransition { from: string | null; to: string; at: string; value: number | null; reason: string; }
export interface AlertDetail {
  alert: Alert; evidence: Record<string, string>; acknowledgeComment: string | null; history: AlertTransition[];
  subjectHealth: Health[]; links: IntegrationLink[];
}
export interface Rule {
  key: string; name: string; template: string; severity: string; enabled: boolean;
  definition: Record<string, unknown>; loadedAt: string;
}

export interface TraceSummary {
  traceId: string; startedAt: string; durationMs: number; rootService: string; rootOperation: string;
  environment: string | null; rootInstance: string | null; rootHost: string | null; spanCount: number; errorCount: number;
  services: string[];
}
export interface SpanEvent { name: string; at: string; attributes: Record<string, string>; }
export interface SpanTarget { kind: 'service' | 'resource'; id: string; name: string; }
export interface Span {
  spanId: string; parentSpanId: string | null; serviceId: string | null; service: string; environment: string | null; instance: string | null;
  host: string | null; name: string; kind: string; startedAt: string; durationMs: number; status: string;
  statusMessage: string | null; scope: string | null; scopeVersion: string | null; attributes: Record<string, string>;
  events: SpanEvent[]; links: { traceId: string; spanId: string }[]; target: SpanTarget | null; instanceId: string | null;
}
export interface Trace { traceId: string; spans: Span[]; truncated: boolean; }

export interface LogRecord {
  timestamp: string; severity: string; severityNumber: number; service: string; environment: string | null;
  instance: string | null; body: string; bodyTruncated: boolean; traceId: string | null; spanId: string | null;
  attributes: Record<string, string>;
}

export interface Operation {
  name: string; kind: string; count: number; requestsPerSecond: number; errorRate: number;
  p50Ms: number; p95Ms: number; p99Ms: number; lastSeen: string;
}

export interface MetricInfo { name: string; type: string; unit: string; description: string; }
export interface Point { at: string; value: number | null; }
export interface Series { name: string; type: string; unit: string; aggregation: string; stepSeconds: number; points: Point[]; }
export interface ServiceStatsPoint { at: string; requestsPerSecond: number; messagesPerSecond: number; errorRate: number; p95Ms: number | null; }
export interface ServiceStats { stepSeconds: number; points: ServiceStatsPoint[]; }

export interface DependencyEvidence {
  dependencyId: string; relation: string; confidence: string; count: number; errorRate: number; p95Ms: number | null;
  lastSeen: string | null; slowestTraces: string[]; errorTraces: string[];
}

export interface SearchHit { kind: 'trace' | 'span' | 'service'; id: string; title: string; traceId: string | null; }
