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
  definition: Record<string, unknown>; loadedAt: string; managedBy: string; etag: string;
}

export interface Channel {
  id: string; key: string; kind: string; destination: string; events: string[]; ruleKeys: string[]; environments: string[]; recipients: string[] | null; secretRef: string | null;
  groupWindowSeconds: number; minimumSeverity: string; enabled: boolean; managedBy: string; loadedAt: string; recentFailures: number; waiting: number;
}
export interface DeliveryAttempt { number: number; at: string; statusCode: number | null; error: string | null; durationMs: number; }
/** A Juice Agent. `online` means it called in within the last 2 minutes; a revoked agent is never online. */
export interface Agent {
  id: string; hostname: string; operatingSystem: string; version: string; status: string; resourceId: string | null; online: boolean; lastSeenAt: string | null;
  certificateNotAfter: string; enrolledBy: string; enrolledAt: string; revokedAt: string | null; revokedBy: string | null; revokeReason: string | null;
}
/** The token is in this response only: the platform keeps its hash. */
export interface EnrollmentToken { id: string; token: string; purpose: string | null; expiresAt: string; }
/** One parameter of an action, with its bounds (already narrowed to the host's where the agent reported them). */
export interface ActionParameter {
  name: string; type: 'string' | 'integer' | 'boolean'; required: boolean; description: string | null; minimum: number | null; maximum: number | null;
  maxLength: number | null; pattern: string | null; allowed: string[] | null; default: string | null;
}
/** An action an agent can be asked for. `permitted` is null when the agent doesn't report what its host allows (an older agent). */
export interface AgentAction {
  name: string; description: string; changing: boolean; custom: boolean; parameters: ActionParameter[]; permitted: boolean | null; limits: string[];
  /** Low, Medium or High. */
  risk: string;
  /** What the platform's policy needs before it runs: None, Admin or SecondPerson. */
  approval: string;
}
/** A health subject an action should make healthy: Service or Application (with environmentId), Instance or Host. */
export interface ActionTarget { type: string; id: string; environmentId?: string | null; }

/** Requests waiting for a decision: all of them, and those the signed-in user can decide. */
export interface ActionRequestsWaiting { total: number; forYou: number; }

/** A request to run an action, under the approval policy for its risk. `state` is Pending, Issued, Rejected, Cancelled or Expired. */
export interface ActionRequest {
  id: string; agentId: string; hostname: string; action: string; parameters: Record<string, unknown>; risk: string; policy: string; reason: string;
  requestedBy: string; requestedAt: string; expiresAt: string; state: string; decidedBy: string | null; decidedAt: string | null; comment: string | null;
  /** The alert that prompted it, and what it should make healthy: checked before it runs, watched for recovery after. */
  alertId?: string | null; target?: ActionTarget | null;
  /** None without a target; Verifying until health from telemetry newer than the action says Verified, or NotVerified. */
  verification?: string; verificationNote?: string | null; verifiedAt?: string | null;
  command: AgentCommand | null;
}
export interface AgentActions { reportedAt: string | null; items: AgentAction[]; }
export interface AgentPackage { version: string; sha256: string; size: number; signedBy: string; addedAt: string; }
export interface AgentCommand {
  id: string; agentId: string; action: string; parameters: Record<string, unknown>; state: string; issuedBy: string; issuedAt: string; expiresAt: string;
  deliveredAt: string | null; deliveryCount: number; completedAt: string | null; reason: string | null; result: Record<string, unknown> | null; durationMs: number | null;
}

export interface Delivery {
  id: string; alertId: string | null; actionRequestId?: string | null; event: string; status: string; attempts: number; createdAt: string; firstAttemptAt: string | null; deliveredAt: string | null; nextAttemptAt: string | null;
  lastStatusCode: number | null; lastError: string | null; history: DeliveryAttempt[]; suppressedBy: string | null; groupedInto: string | null; escalationStep: number;
}
/** What a test send did; id is the webhook-id it carried. */
export interface ChannelTestResult { id: string; delivered: boolean; statusCode: number | null; error: string | null; durationMs: number; }
export interface AllowlistEntry { destination: string; ports: number[]; managedBy: string; loadedAt: string; }
export interface EscalationStep { afterMinutes: number; endpointKeys: string[]; }
export interface EscalationPolicy {
  id: string; key: string; name: string; enabled: boolean; ruleKeys: string[]; environments: string[]; minimumSeverity: string; stopOnAcknowledge: boolean; steps: EscalationStep[]; updatedAt: string;
}
export interface Silence {
  id: string; kind: string; comment: string; ruleKeys: string[]; service: string | null; environment: string | null; host: string | null; maximumSeverity: string | null; allAlerts: boolean;
  startsAt: string; endsAt: string; repeatWeekly: boolean; active: boolean; createdBy: string; createdAt: string; cancelledAt: string | null; cancelledBy: string | null; held: number;
}
export interface IncidentSummary { id: string; title: string; severity: string; state: string; createdBy: string; createdAt: string; updatedAt: string; resolvedAt: string | null; }
export interface IncidentEvent { at: string; actor: string; type: string; from: string | null; to: string | null; text: string | null; alertId: string | null; }
export interface IncidentDetail { incident: IncidentSummary; alerts: Alert[]; events: IncidentEvent[]; openAlerts: number; }
export interface AlertNotifications {
  deliveries: { endpointKey: string; kind: string; delivery: Delivery; }[];
  escalations: { policy: string; name: string; state: string; stepsSent: number; steps: number; nextAt: string | null; stopReason: string | null; endedAt: string | null; }[];
  silences: Silence[]; incidents: IncidentSummary[];
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

/** What a service was measured to need, per instance. A unit of work is a request or a message. */
export interface Capacity {
  stepSeconds: number; samples: number; instances: number; truncated: boolean; requestSource: 'metrics' | 'spans' | 'none';
  hosts: string[]; coresPerInstance: number | null; peakPerSecond: number; responseMs: number | null;
  responseLowLoadMs: number | null; responseHighLoadMs: number | null; peakQueue: number | null;
  /** `fitted` separates idle CPU from CPU per unit; `average` is CPU over work in the busiest samples, so it can read high. */
  cpuModel: 'fitted' | 'average' | 'none'; cpuSecondsPerUnit: number | null; idleCores: number | null; peakCores: number | null;
  /** `fitted` is idle plus growth per unit in flight; `peak` is only the highest seen. */
  memoryModel: 'fitted' | 'peak' | 'none'; idleMemoryBytes: number | null; memoryBytesPerInFlight: number | null; peakMemoryBytes: number | null;
  /** Near 1, a consumer was busy nearly all the time: if it handles one message at a time, it was at its limit whatever CPU says. */
  peakMessagesPerSecond: number; peakMessagesInFlight: number;
  /** Null when the service created and delivered no outbox messages in the window. */
  outbox: OutboxBacklog | null;
}

/** `undelivered` is created minus delivered in the window, so it includes rolled-back messages. The delay is the p95 from creating to delivering, in the newest minutes. */
export interface OutboxBacklog { createdPerSecond: number; deliveredPerSecond: number; undelivered: number; recentDelaySeconds: number | null; fallingBehind: boolean; }

export interface TimelineEntry { at: string; type: 'change' | 'health' | 'alert'; title: string; detail: string | null; state: string | null; id: string | null; serviceId: string | null; environment: string | null; }
export interface Timeline { from: string; to: string; entries: TimelineEntry[]; truncated: boolean; }
export interface CorrelationReason { code: string; text: string; points: number; }
export interface CorrelatedItem { type: string; id: string; at: string; title: string; detail: string | null; kind: string | null; scope: string; score: number; reasons: CorrelationReason[]; }
/** What happened near an alert: correlation, never a proven cause. */
export interface Related { relation: string; note: string; breachStartedAt: string; items: CorrelatedItem[]; }

export interface LoadRun { id: string; name: string; environment: string | null; startedAt: string; endedAt: string | null; startedBy: string; }
export interface LoadRunPoint {
  at: string; perSecond: number; errorRate: number; p95Ms: number | null; queue: number | null; consumerBusy: number;
  created: number; delivered: number; backlog: number;
}
/** Where a service started to struggle. */
export interface LoadRunLimit { reason: string; at: string; perSecond: number; value: number; baseline: number | null; }
export interface LoadRunService {
  serviceId: string | null; name: string; points: LoadRunPoint[]; peakPerSecond: number; p95AtPeakMs: number | null;
  errorRateAtPeak: number; baselineP95Ms: number | null; limit: LoadRunLimit | null;
}
export interface LoadRunResults { run: LoadRun; stepSeconds: number; services: LoadRunService[]; truncated: boolean; }

export interface DependencyStats { dependencyId: string; count: number; errorRate: number; }
export interface DependencyStatsPage { items: DependencyStats[]; truncated: boolean; }

export interface DependencyEvidence {
  dependencyId: string; relation: string; confidence: string; count: number; errorRate: number; p95Ms: number | null;
  lastSeen: string | null; slowestTraces: string[]; errorTraces: string[];
}

export interface SearchHit { kind: 'trace' | 'span' | 'service'; id: string; title: string; traceId: string | null; }
