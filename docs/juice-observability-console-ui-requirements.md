# Juice Observability Console --- UI Requirements

**Status:** MVP requirement\
**Audience:** Claude / implementation agent\
**Product:** Juice Observability

------------------------------------------------------------------------

## 1. Product Decision

Juice Observability must have its own native **Juice Console** as the
primary product UI.

Grafana is **optional** and exists only as an external
visualization/integration surface for customers who already operate
Grafana.

Therefore:

-   Juice Observability must work without Grafana.
-   Grafana must not be a mandatory runtime dependency.
-   The Juice domain model and API must not depend on Grafana.
-   The Console is part of the MVP.
-   MVP is not complete until the core observability investigation
    workflow can be completed entirely through Juice Console.
-   Do not build Juice Console as a generic Grafana replacement. Build a
    native Juice Observability investigation experience.

------------------------------------------------------------------------

## 2. Console Technology

The Console should be implemented with:

-   **Angular**
-   **TypeScript**

Recommended supporting technologies may include:

-   RxJS for reactive state/data flows
-   Cytoscape.js or equivalent for dependency topology
-   Apache ECharts or equivalent for metrics/time-series visualization
-   D3/SVG/Canvas where custom visualization is required
-   AG Grid or equivalent for large tabular datasets
-   OIDC/OAuth2 for authentication

The Console should have a small, purpose-built Juice design system
rather than coupling the entire UX to a generic UI framework.

------------------------------------------------------------------------

## 3. Architecture Boundary

The Console must not query ClickHouse or PostgreSQL directly.

The intended architecture is:

``` text
Application
    |
    | OTLP
    v
OpenTelemetry Collector
    |
    v
ClickHouse
    ^
    |
Juice API
    ^
    |
Angular Juice Console
```

Control/domain data is stored in PostgreSQL where appropriate.

The Console communicates through the **Juice API**, which owns the
observability domain model and query abstraction.

The Console should know about concepts such as:

-   Service
-   Environment
-   Instance
-   Resource
-   Dependency
-   Trace
-   Span
-   Operation
-   Health
-   Rule
-   Alert

It should not depend on ClickHouse table names, SQL schemas, or
storage-specific implementation details.

------------------------------------------------------------------------

# 4. Core UX Principle

The Console is primarily an **investigation tool**, not a dashboard
viewer.

The main workflow is:

``` text
Discover
   ↓
Investigate
   ↓
Correlate
   ↓
Diagnose
```

A typical investigation should look like:

``` text
Service error rate increased
        ↓
Identify affected operation
        ↓
Find representative error trace
        ↓
Open trace waterfall
        ↓
Identify slow/error downstream span
        ↓
Open downstream service / DB / MQ dependency
        ↓
Inspect related logs and metrics
        ↓
Navigate back to service/dependency/trace
```

The user should not be forced to return to a dashboard or top-level menu
between these steps.

------------------------------------------------------------------------

# 5. P0 --- Trace Search and Discovery

Trace must be a first-class citizen of the Console.

Users must be able to search/filter traces by relevant dimensions,
including:

-   Time range
-   Service
-   Environment
-   Operation
-   Trace ID
-   Span ID
-   Status
-   Error
-   Duration
-   HTTP/RPC attributes where available
-   Messaging attributes where available
-   Resource
-   Instance
-   Dependency

The UI should support narrowing from a broad service/time-window search
into a specific trace.

Trace search results should provide enough information to quickly
identify useful traces:

-   Trace ID
-   Root operation
-   Service
-   Start time
-   Duration
-   Status
-   Span count
-   Error indication
-   Relevant resource/instance information

------------------------------------------------------------------------

# 6. P0 --- Trace Detail / Waterfall

Trace detail is one of the core MVP screens.

The trace view should provide:

### Timeline

-   Start/end time
-   Duration
-   Relative timing
-   Parent/child hierarchy
-   Parallel execution visibility

### Span information

For each span:

-   Span name
-   Span kind
-   Service
-   Environment
-   Instance/resource
-   Start time
-   Duration
-   Status
-   Trace ID
-   Span ID
-   Parent span
-   Attributes
-   Events
-   Links
-   Exceptions/errors
-   Instrumentation scope where available

### Interaction

Users should be able to:

-   Expand/collapse span hierarchy
-   Select a span
-   Inspect span details
-   Identify the slowest/error spans
-   Navigate to the owning service
-   Navigate to downstream dependencies
-   Open related logs
-   Open related metrics

The trace UI must remain usable for large traces and should avoid
rendering every detail expanded by default.

------------------------------------------------------------------------

# 7. P0 --- Trace ↔ Log ↔ Metric Correlation

Correlation between telemetry types is a core MVP capability.

From a trace/span, users should be able to navigate to:

-   Related logs around the span time window
-   Related metrics around the span/service time window
-   Related service/instance/resource information

Where telemetry contains correlation identifiers, the Console should use
them.

Examples:

``` text
Trace
  → Span
      → Logs around span
      → Metrics around service/span
      → Dependency
```

And the reverse direction should also be supported where possible:

``` text
Log
  → Trace / Span

Metric anomaly
  → Relevant service / operation
  → Related traces
```

Correlation should be based on actual telemetry relationships and
identifiers, not UI-only assumptions.

------------------------------------------------------------------------

# 8. P0 --- Service Explorer and Service Detail

The Console must provide a service-centric view.

Important domain rule:

> A Service is environment-independent.

For example:

``` text
orders / staging
orders / production
```

represent one logical service with separate environment-specific health
and instances.

The service detail page should expose:

-   Overview
-   Health
-   Environments
-   Instances
-   Operations
-   Dependencies
-   Traces
-   Logs
-   Metrics

The user should be able to move from a service directly into
investigation without losing the current context.

------------------------------------------------------------------------

# 9. P0 --- Dependency Topology

The Console must provide a dependency topology view.

The topology should support dependencies between services and
infrastructure/resources such as:

-   Services
-   Databases
-   Messaging systems
-   Other observable resources

Users should be able to:

-   Zoom/pan
-   Select nodes
-   Select dependency edges
-   Navigate to the related service/resource
-   Open representative traces
-   Inspect dependency metrics

Selecting a dependency edge should expose useful evidence such as:

-   Request/operation count
-   Error rate
-   Latency/P95
-   Last seen
-   Representative traces

## Dependency evidence

Dependencies are not always certain.

The platform explicitly supports dependency evidence/confidence:

-   `observed`
-   `inferred`

The UI must make this distinction visible.

Do not present inferred dependencies as if they were confirmed
observations.

------------------------------------------------------------------------

# 10. P0 --- Health

The Console must expose service and instance health.

Health states:

-   Healthy
-   Degraded
-   Unhealthy
-   Offline

Health should explain **why** the state exists.

The UI should expose affected instances and relevant signals.

Current Phase-0 health defaults:

-   Evaluation interval: **30 seconds**
-   Freshness grace: **5 minutes**
-   Minimum samples: **3**
-   If \>= 50% of active instances are unhealthy → service is
    **Unhealthy**
-   If any instance is unhealthy → service is **Degraded**
-   Error rate \>= 1% → **Degraded**
-   Error rate \>= 5% → **Unhealthy**
-   Error-rate evaluation window: **5 minutes**
-   Application rollup uses weighted children
-   Optional children do not count
-   No worst-child-wins rule

Missing data must be represented as **no data**, not zero.

------------------------------------------------------------------------

# 11. P0 --- Instance and Resource Explorer

Users need to understand where a service is actually running.

The Console should support navigation such as:

``` text
Application
  → Service
      → Environment
          → Instance
              → Resource
```

Relevant resource types include:

-   Host
-   Container
-   Kubernetes Pod
-   Kubernetes Workload
-   Messaging destination
-   Database

Resource identity follows the platform reconciliation rules.

Important behavior:

-   Services/resources are not automatically deleted.
-   Instances become inactive after 15 minutes without telemetry.
-   Inactive instances may be pruned after 30 days.
-   Resource aliases are configuration-driven; the platform must not
    guess renames.

------------------------------------------------------------------------

# 12. P1 --- Operation Explorer

For each service, provide an operation-level view.

Useful fields include:

-   Operation name
-   Request/throughput count
-   Error rate
-   P50
-   P95
-   P99
-   Recent activity

An operation should provide direct navigation to representative traces.

Example:

``` text
Service: Payment
    |
    +-- POST /payments
    |      error rate: 4.2%
    |      p95: 820ms
    |
    +-- GET /payments/{id}
```

The operation view should be an investigation entry point, not merely a
statistics table.

------------------------------------------------------------------------

# 13. P1 --- Global Search

The Console should provide a global search entry point.

At minimum, support direct lookup for:

-   Trace ID
-   Span ID
-   Service

Where appropriate, extend search to other high-value identifiers.

A user who already has a Trace ID should be able to reach the trace
directly without navigating through dashboards.

------------------------------------------------------------------------

# 14. P1 --- Rules and Alerts

The Console should expose configured observability rules and their
current state.

The UI should show:

-   Rule name
-   Rule type
-   Scope
-   Current state
-   Current value
-   Threshold
-   Severity
-   Duration
-   Last evaluation
-   Recovery state/history where available

Rules are typed templates defined by deployment configuration.

MVP rule templates include:

-   `error_rate`
-   `latency_p95`
-   `throughput_absent`
-   `mediator_errors`
-   `consumer_failures`
-   `delivery_failures`
-   `host_resource`

Users must not provide arbitrary SQL as the rule definition.

------------------------------------------------------------------------

# 15. P2 --- Dashboards

Dashboards are useful, but they are not the center of the product.

The primary product experience should remain:

``` text
Service
  → Health
  → Operations
  → Dependencies
  → Traces
  → Logs
  → Metrics
```

The Console may provide overview dashboards for orientation, but custom
dashboarding is not a prerequisite for the core investigation workflow.

Custom dashboards are therefore **P2**, not P0.

------------------------------------------------------------------------

# 16. P2 --- Advanced Analytics

Advanced analytics can follow after the investigation workflow is solid.

Examples:

-   Advanced aggregation
-   Comparative analysis
-   Historical trend analysis
-   Advanced anomaly detection
-   Custom analytics views

These should not delay the core trace/service/dependency investigation
experience.

------------------------------------------------------------------------

# 17. Navigation Model

The Console should make context persistent during investigation.

For example:

``` text
Service: Order
Environment: Production
Operation: POST /orders
        ↓
Trace: abc123
        ↓
Span: PaymentClient.Call
        ↓
Dependency: Payment
        ↓
Trace / Logs / Metrics
```

The user should be able to move between these entities without losing:

-   Time range
-   Environment
-   Service context
-   Selected operation
-   Investigation context

Deep links should be supported where practical so that a trace, service,
dependency, or alert can be shared/bookmarked.

------------------------------------------------------------------------

# 18. Data and Privacy Boundaries

The Console must respect the platform's telemetry data classification.

The platform distinguishes:

### Restricted

Business payloads and credentials must not be ingested.

### Confidential

May include:

-   Application log/exception messages
-   URLs
-   User/tenant IDs intentionally logged by applications

These require retention and privacy consideration.

### Internal

Examples:

-   Span names
-   Metric values
-   Resource attributes

The UI must not introduce a new path that bypasses these
ingestion/storage rules.

------------------------------------------------------------------------

# 19. Retention Context

Current retention decisions:

-   Traces: **30 days**
-   Logs: **30 days**
-   Metrics: **90 days**

The UI should handle data absence gracefully when users query outside
the available retention window.

Again:

> Missing data means no data, not zero.

------------------------------------------------------------------------

# 20. Grafana Integration

Grafana remains an optional integration.

Required principles:

-   Juice Console must work without Grafana.
-   Grafana must not own the Juice domain model.
-   Juice API must remain the integration boundary.
-   Existing customer Grafana installations should be able to consume
    Juice observability data where supported.
-   The Console may provide links such as "Open in Grafana" for
    customers who configure the integration.

Grafana versioning and deployment isolation are implementation details
of the optional integration, not core Juice product requirements.

------------------------------------------------------------------------

# 21. MVP Acceptance Test

The most important acceptance test is an end-to-end investigation
without Grafana.

Given a system with approximately 5--10 services:

1.  User sees that a service has an elevated error rate.
2.  User opens the affected service.
3.  User identifies the affected operation.
4.  User finds a representative error trace.
5.  User opens the trace waterfall.
6.  User identifies the problematic downstream span.
7.  User navigates to the downstream service, database, or messaging
    dependency.
8.  User inspects related logs.
9.  User inspects related metrics.
10. User can navigate back to the original service/trace/dependency
    context.

All of this must be possible using:

``` text
OTLP
  → Collector
  → ClickHouse
  → Juice API
  → Juice Console
```

with **no Grafana dependency**.

If this workflow cannot be completed, the Juice Observability MVP should
not be considered complete.

------------------------------------------------------------------------

# 22. MVP Priority Summary

  Priority   Capability
  ---------- ----------------------------------
  P0         Trace search/filtering
  P0         Trace waterfall/detail
  P0         Trace ↔ Log ↔ Metric correlation
  P0         Service explorer/detail
  P0         Dependency topology
  P0         Health
  P0         Instance/resource explorer
  P1         Operation explorer
  P1         Global search
  P1         Rules/alerts
  P2         Custom dashboards
  P2         Advanced analytics

------------------------------------------------------------------------

# 23. Implementation Guidance for Claude

When implementing the Console:

1.  Treat the requirements above as product requirements, not
    suggestions.
2.  Keep the UI domain-oriented and independent from ClickHouse schema.
3.  Keep Juice API as the only backend boundary for the Console.
4.  Optimize the UX for investigation rather than dashboard consumption.
5.  Treat trace navigation and correlation as first-class workflows.
6.  Preserve context while navigating between services, traces, spans,
    dependencies, logs, and metrics.
7.  Do not make Grafana a hidden runtime dependency.
8.  Do not build arbitrary user-SQL functionality into the Console.
9.  Keep health, dependency evidence, resource identity, and
    service/environment semantics aligned with Phase-0 decisions.
10. Prefer incremental vertical slices that produce a working
    investigation journey over building isolated UI pages.

The first useful vertical slice should be:

``` text
Service
  → Operation
  → Trace
  → Span
  → Dependency
  → Related Logs/Metrics
```

This slice should work end-to-end through the Juice API and Juice
Console before expanding into secondary dashboard features.
