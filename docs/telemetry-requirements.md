# Telemetry requirements: what a service must send

The platform works with any service instrumented with OpenTelemetry, in any language. It does not need Juice. It reads
standard [semantic conventions](https://opentelemetry.io/docs/specs/semconv/) from what the Collector stores in
ClickHouse, so what you get depends on what your services send. This page lists what each feature reads, what happens
when it is missing, and what only Juice services provide.

## 1. Getting telemetry in

- Send OTLP (gRPC `4317` or HTTP `4318`) to **the platform's Collector**, over TLS, with `Authorization: Bearer <token>`
  (a line of `deploy/local/secrets/otlp-tokens`). The Collector is the trust boundary: it removes any tenant attribute a
  client sends, stamps the deployment's tenant, and redacts credential-like attributes. Telemetry written to ClickHouse
  any other way is not seen.
- If you already run a Collector (or an agent such as Grafana Alloy), add an OTLP exporter to the platform's Collector:

  ```yaml
  extensions:
    bearertokenauth/juice:
      token: ${env:JUICE_OTLP_TOKEN}
  exporters:
    otlp/juice:                      # otlp_grpc/juice on Collector 0.15x and later
      endpoint: collector.example.internal:4317
      tls:
        ca_file: /etc/juice/ca.crt   # the deployment's CA, when not publicly trusted
      auth:
        authenticator: bearertokenauth/juice
  service:
    extensions: [bearertokenauth/juice]
    pipelines:
      traces:  { exporters: [otlp/juice] }
      metrics: { exporters: [otlp/juice] }
      logs:    { exporters: [otlp/juice] }
  ```

  Keep your Collector's own processors; don't rename the attributes below.

## 2. Resource attributes

Set them through the SDK (`OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`) or with the Collector's `resource` or
`resourcedetection` processors.

| Attribute | Needed for | When it is missing |
|---|---|---|
| `service.name` | everything about a service | the telemetry belongs to no service (host-only) |
| `service.namespace` | grouping services into an **application** | the service goes in the application `default` |
| `deployment.environment.name` | per-environment health, rules, topology and SLOs | the environment is `unspecified`. The older `deployment.environment` is **not** read |
| `service.instance.id` | instances and instance health | the instance is `host.name:process.pid`, or `host.name`, marked *derived*; several replicas on one host without a pid merge into one |
| `service.version` | version-change events on the timeline | no change events from deployments |
| `host.name`, `host.id` | hosts and which instances run on them | no host resource |
| `container.id` | containers | no container resource |
| `k8s.pod.uid`, `k8s.namespace.name`, `k8s.pod.name` | pods | no pod resource |
| `process.pid` | telling replicas apart when `service.instance.id` is absent | see `service.instance.id` |

Environment names are matched case-insensitively. `production`/`prod`/`prd`/`live`, `staging`/`stage`/`stg`/`uat`/`preprod`,
`development`/`dev`/`local` and `test`/`testing`/`qa`/`ci` get their type; any other name is *other*.

Renamed services are mapped with `Discovery:Aliases`.

## 3. Spans

| What the platform does | What it reads |
|---|---|
| Counts requests and messages (health, error rate, latency, throughput, SLOs, operations) | spans of kind **Server** or **Consumer** |
| Counts errors (health, ErrorRate) | span status `Error` on those spans |
| Draws a service → service dependency (*observed*) | a **Client** or **Producer** span whose child **Server** or **Consumer** span is in another service: needs W3C trace context propagated between services (and across queues) |
| Draws a dependency on a database (*inferred*) | Client spans with `db.system.name` (or the older `db.system`), and `db.namespace` (or `db.name`), `server.address` |
| Draws a dependency on a queue or topic (*inferred*) | Producer or Consumer spans with `messaging.system` and `messaging.destination.name` |
| SLO good/bad and health-check exclusion | `http.response.status_code` (or the older `http.status_code`), `http.route`, `url.path` |

Set span kinds correctly. A service that marks its incoming requests as *Internal* has no requests and no error rate;
one that marks outgoing calls as *Internal* has no dependencies. Calls to a service that isn't instrumented show only
as a resource dependency when they are a database or a queue.

## 4. Metrics

| Feature | Metric | Comes from |
|---|---|---|
| Host health, HostMemory rule | `system.memory.usage` (with `state`) | the Collector's `hostmetrics` receiver on each host |
| Capacity view: requests | `http.server.request.duration` | most HTTP server instrumentations |
| Capacity view: CPU, memory and cores per instance | `dotnet.process.cpu.time`, `dotnet.process.memory.working_set`, `dotnet.process.cpu.count` | .NET 9+ runtime metrics **only**: the generic `process.cpu.time` and `process.memory.usage` are not read, so a non-.NET service has no CPU or memory in the capacity view |
| Capacity view and load runs: thread pool queue | `dotnet.thread_pool.queue.length` | .NET 9+ runtime metrics only |

## 5. Logs

Logs need `TraceId` and `SpanId` to be linked to their traces; most SDKs' log bridges set them when a log is written
inside a span. Severity is read from the log record's severity number.

## 6. What only Juice services provide

These are the Juice [signal pack](signal-packs.md), version 1; a deployment without Juice services can switch it off
(`Signals:Juice:Enabled=false`).

| Feature | Reads | Without it |
|---|---|---|
| MediatorErrors rule | `juice.mediator.errors`, `juice.mediator.requests` | the rule observes nothing |
| ConsumerFailures rule | `juice.messaging.consumed` with `juice.dispatch.result` | the rule observes nothing |
| DeliveryFailureRate rule | `delivery_failure_total`, `delivery_attempt_total` (Juice outbox delivery) | the rule observes nothing |
| Outbox backlog and delivery delay (capacity view, load runs) | Juice's `create` and `deliver` spans (`ScopeName = 'Juice'`, `juice.delivery.outcome`) | no backlog line; the rest of the view works |
| A failed message dispatch counted as a bad SLO request | `juice.dispatch.result = failure` on a Consumer span | only the span status counts |

Everything else (discovery, topology, health, the ErrorRate, LatencyP95, ThroughputAbsent, HostMemory and SloBurn
rules, alerts, incidents, notifications, search, change correlation and the agent) works on any service that meets
sections 2–5.

## 7. Checking a service

After a few minutes of traffic, the service should appear under its application in the Console's Services page, with
an environment that is not `unspecified`, its instances, and its dependencies in the topology. If it doesn't:

| Symptom | Likely cause |
|---|---|
| Not there at all | not sent through the platform's Collector (token, TLS) or no `service.name` |
| Under `default` | no `service.namespace` |
| Environment `unspecified` | no `deployment.environment.name` (or only the older `deployment.environment`) |
| One instance where there are several | no `service.instance.id` and no `process.pid` |
| No requests, health *Unknown* | no Server or Consumer spans |
| Missing arrows in the topology | trace context not propagated, or wrong span kinds |
| No host health | no `hostmetrics` receiver |

## 8. Checked against the OpenTelemetry Demo

On 19 November 2026 the [OpenTelemetry Demo](https://github.com/open-telemetry/opentelemetry-demo) 3.1.0 (with Kafka) ran
unmodified against the local stack, its own Collector forwarding to the platform's as in section 1. Its services are
written in Go, Java, .NET, Node.js, Python, C++, Rust, Ruby and PHP; none use Juice.

| What | Result |
|---|---|
| Discovery | the application `opentelemetry-demo` with all 17 services, each with its instance (`service.instance.id`), host and version, within a minute |
| Topology | the demo's real call graph: 25 *observed* service calls (checkout → cart, currency, email, payment, product-catalog, shipping; frontend → seven services; shipping → quote; …), the Kafka topic `orders` (checkout publishes; accounting and fraud-detection consume) and the PostgreSQL and Valkey databases, *inferred* |
| Health | every service evaluated in every language; the application rolled up (*Degraded*, one service failing) |
| Logs | linked to their traces for every service except Kafka's broker (no trace id at all) and about half of checkout's |
| Capacity view | the .NET services (cart, accounting) have CPU and memory per instance; the others have requests and latency only (section 4) |

What it showed:

- **No environment.** The demo sets `service.namespace` but not `deployment.environment.name`, so everything is in
  `unspecified`. Add it with the SDK's `OTEL_RESOURCE_ATTRIBUTES` or a `resource` processor in your Collector.
- **Your Collector's own telemetry becomes a service.** The demo's Collector reports itself as `otelcol-contrib`
  (no namespace, so in the application `default`). Leave the Collector's `service.telemetry` out of the pipeline that
  forwards to the platform, or give it a namespace of its own.
- **Error status without an HTTP code counts as an error.** About 7 % of the Envoy proxy's server spans had status
  `Error` and no `http.response.status_code`, and health made it *Unhealthy* (and its caller, the load generator,
  `dependency_failed`). Health counts span status only; SLOs also look at the code. If a proxy marks client resets or
  cancellations as errors, filter or fix those spans in your Collector.
